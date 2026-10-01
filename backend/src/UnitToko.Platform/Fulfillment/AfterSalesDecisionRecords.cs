using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo.Migrations;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Fulfillment;
internal sealed class AfterSalesDecisionRecords(IMongoDatabase database)
{
    internal const string Collection = "afterSalesCaseDecisions", Operation = "fulfillment.case.decide";
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static BsonDocument Source(BsonDocument root) => new() { { "caseId", root["_id"] }, { "caseHash", root["snapshotHash"] },
        { "invoiceId", root["source"]["invoiceId"] }, { "invoiceBinding", root["input"]["binding"] }, { "requestedBy", root["requestedBy"] },
        { "requestedAt", root["requestedAt"] }, { "lines", root["input"]["lines"].DeepClone() } };
    internal static string Binding(BsonDocument root) => R.Binding(Source(root));
    internal static object Body(long id, long revision, string binding, AfterSalesDecisionInput input) => new { caseId = id, expectedRevision = revision, binding, input };
    internal static BsonDocument Input(AfterSalesDecisionInput input) => new() { { "action", input.Action }, { "publicExplanation", input.PublicExplanation },
        { "lines", new BsonArray(input.Lines.Select(line => new BsonDocument { { "receiptId", line.ReceiptId }, { "skuId", line.SkuId }, { "reportedBaseUnits", line.ReportedBaseUnits } })) } };
    internal static AfterSalesDecisionInput Input(BsonDocument value) => AfterSalesDecisionPolicy.Copy(new(value["action"].AsString, value["publicExplanation"].AsString,
        [.. value["lines"].AsBsonArray.Select(line => new AfterSalesCaseLine(line["receiptId"].AsInt64, line["skuId"].AsInt64, line["reportedBaseUnits"].AsInt64))]));
    internal static BsonDocument Result(BsonDocument row) => new() { { "caseId", row["_id"] }, { "invoiceId", row["source"]["invoiceId"] }, { "revision", 2L },
        { "state", row["state"] }, { "decidedAt", row["decidedAt"] }, { "operation", Operation } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] },
        { "reason", row["reason"] }, { "requestKey", row["requestKey"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["decidedAt"] }, { "result", Result(row) } };
    internal async Task<BsonDocument?> ReadAsync(IClientSessionHandle tx, BsonDocument root, CancellationToken ct) {
        var id = root["_id"].AsInt64; var candidate = await Rows(Collection).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct);
        if (candidate is null) {
            foreach (var (table, path) in new[] { ("idempotencyRecords", "result.caseId"), ("auditEvents", "result.caseId"), ("outboxEvents", "payload.caseId") })
                if (await Rows(table).Find(tx, new BsonDocument { { table == "idempotencyRecords" ? "operation" : "kind", Operation }, { path, id } }).AnyAsync(ct)) throw R.Broken();
            return null;
        }
        var row = await Rows(Collection).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", id) & AfterSalesDecisionSchemas.Validator()).SingleOrDefaultAsync(ct) ?? throw R.Broken();
        var input = Input(row["input"].AsBsonDocument); AfterSalesDecisionPolicy.RequireRequested(input, R.Input(root["input"].AsBsonDocument).Lines);
        if (!InvoiceRecords.Equal(row["input"], Input(input)) || !InvoiceRecords.Equal(row["source"], Source(root)) || row["binding"] != Binding(root) ||
            row["snapshotHash"] != R.Hash(row) || row["bodyHash"] != AuthorizationStore.CommandHash(Body(id, 1, row["binding"].AsString, input), row["reason"].AsString) ||
            row["state"] != AfterSalesDecisionPolicy.State(input) || row["decidedAt"].CompareTo(root["requestedAt"]) < 0) throw R.Broken();
        var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray();
        if (people.Select(person => person.UserId).Distinct().Count() != 2 || !PersonIndependencePolicy.AreIndependent(people.SingleOrDefault(person => person.UserId == row["actorId"].AsInt64),
            people.SingleOrDefault(person => person.UserId == root["requestedBy"].AsInt64))) throw R.Broken();
        foreach (var field in new[] { "requestKey", "reason", "correlationId" }) if (!ReceivingAccountPolicy.Text(row[field].AsString, field == "reason" ? 1000 : 128)) throw R.Broken();
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", Operation + ":" + id)).SingleOrDefaultAsync(ct);
        if (receipt is null || receipt.ElementCount != 9 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || !receipt["createdAt"].IsBsonDateTime ||
            receipt["createdAt"].CompareTo(row["decidedAt"]) > 0 || receipt["bodyHash"] != row["bodyHash"] || !InvoiceRecords.Equal(receipt["result"], Result(row)) ||
            !InvoiceRecords.Equal(receipt.GetValue(PersonApprovalReceipt.Field, BsonNull.Value), PersonApprovalReceipt.Create(Permissions.CaseManage, new(1, 1, RequestedBy: root["requestedBy"].AsInt64))) ||
            audit is null || !InvoiceRecords.Equal(audit, Audit(row)) || effect is null || effect["kind"] != Operation || effect["actorId"] != row["actorId"] ||
            effect["requestKey"] != row["requestKey"] || effect["createdAt"] != row["decidedAt"] || !InvoiceRecords.Equal(effect["payload"], Result(row))) throw R.Broken();
        return row;
    }
    internal async Task RequireCapacityAsync(IClientSessionHandle tx, BsonDocument root, System.Collections.Immutable.ImmutableArray<AfterSalesCaseLine> additional, CancellationToken ct) {
        var invoice = root["source"].AsBsonDocument; var total = new Dictionary<(long Receipt, long Sku), long>(); var decisions = 0;
        // ponytail: verify this invoice's immutable case history; checkpoint only if measured case volume requires it.
        using var cursor = await Rows(R.Collection).Find(tx, new BsonDocument("source.invoiceId", invoice["invoiceId"])).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var candidate in cursor.Current) {
            var peer = await new R(database).ReadAsync(tx, candidate["_id"].AsInt64, ct); if (!InvoiceRecords.Equal(peer["source"], invoice)) throw R.Broken();
            var decision = await ReadAsync(tx, peer, ct); if (decision is null) continue; decisions++;
            foreach (var line in Input(decision["input"].AsBsonDocument).Lines) { var key = (line.ReceiptId, line.SkuId); total[key] = checked(total.GetValueOrDefault(key) + line.ReportedBaseUnits); }
        }
        if (await Rows(Collection).CountDocumentsAsync(tx, new BsonDocument("source.invoiceId", invoice["invoiceId"]), cancellationToken: ct) != decisions) throw R.Broken();
        foreach (var (table, path) in new[] { ("idempotencyRecords", "result.invoiceId"), ("auditEvents", "result.invoiceId"), ("outboxEvents", "payload.invoiceId") })
            if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument { { table == "idempotencyRecords" ? "operation" : "kind", Operation }, { path, invoice["invoiceId"] } }, cancellationToken: ct) != decisions) throw R.Broken();
        foreach (var (item, quantity) in total) {
            var accepted = invoice["lines"].AsBsonArray.SingleOrDefault(line => line["receiptId"] == item.Receipt && line["skuId"] == item.Sku);
            if (accepted is null || quantity > accepted["acceptedBaseUnits"].AsInt64) throw R.Broken();
        }
        foreach (var line in additional) {
            var accepted = invoice["lines"].AsBsonArray.Single(item => item["receiptId"] == line.ReceiptId && item["skuId"] == line.SkuId)["acceptedBaseUnits"].AsInt64;
            if (total.GetValueOrDefault((line.ReceiptId, line.SkuId)) > accepted - line.ReportedBaseUnits) throw Changed("after_sales_return_quantity_exceeded");
        }
    }
    internal static UnitToko.Platform.Mongo.PersistenceConflictException Changed(string code = "after_sales_case_changed") => new(code, "Reload the case and authorized return quantities");
}
