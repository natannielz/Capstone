using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo.Migrations;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Inventory;
/// <summary>Immutable physical product/condition assessment. Matching a product or reading a lot label
/// never proves its original stock lot, expiry, cost, resale eligibility or financial entitlement.</summary>
internal sealed class ReturnInspectionRecords(IMongoDatabase database, IClock clock)
{
    internal const string Collection = "returnInspections", Operation = "fulfillment.case.return_inspect";
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static BsonDocument Source(BsonDocument receipt) => new() { { "returnId", receipt["_id"] }, { "returnHash", receipt["snapshotHash"] }, { "caseId", receipt["caseId"] },
        { "invoiceId", receipt["source"]["invoiceId"] }, { "requestedBy", receipt["source"]["requestedBy"] }, { "receivedBy", receipt["actorId"] }, { "receivedAt", receipt["receivedAt"] }, { "lines", receipt["input"]["lines"].DeepClone() } };
    internal static string Binding(BsonDocument receipt) => R.Binding(Source(receipt));
    internal static object Body(long id, long revision, string binding, ReturnedStockInspectionInput input) => new { returnId = id, expectedRevision = revision, binding, input };
    internal static BsonDocument Input(ReturnedStockInspectionInput input) => new() { { "publicNote", input.PublicNote }, { "lines", new BsonArray(input.Lines.Select(line => new BsonDocument {
        { "receiptId", line.ReceiptId }, { "skuId", line.SkuId }, { "inspectedBaseUnits", line.InspectedBaseUnits }, { "identity", line.Identity }, { "condition", line.Condition },
        { "observedIdentity", line.ObservedIdentity }, { "observedLotLabel", line.ObservedLotLabel is null ? BsonNull.Value : new BsonString(line.ObservedLotLabel) } })) } };
    internal static ReturnedStockInspectionInput Input(BsonDocument input) => ReturnedStockInspectionPolicy.Copy(new(input["publicNote"].AsString, [.. input["lines"].AsBsonArray.Select(line =>
        new ReturnedStockInspectionLine(line["receiptId"].AsInt64, line["skuId"].AsInt64, line["inspectedBaseUnits"].AsInt64, line["identity"].AsString, line["condition"].AsString,
            line["observedIdentity"].AsString, line["observedLotLabel"].IsBsonNull ? null : line["observedLotLabel"].AsString))]));
    internal static BsonDocument Result(BsonDocument row) => new() { { "returnId", row["_id"] }, { "caseId", row["source"]["caseId"] }, { "invoiceId", row["source"]["invoiceId"] },
        { "revision", row["revision"] }, { "inspectedAt", row["inspectedAt"] }, { "state", "inspected_quarantine" }, { "operation", Operation } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation }, { "actorId", row["actorId"] },
        { "actorGrantVersion", row["actorGrantVersion"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] }, { "reason", row["reason"] }, { "requestKey", row["requestKey"] },
        { "correlationId", row["correlationId"] }, { "createdAt", row["inspectedAt"] }, { "result", Result(row) } };
    internal async Task<BsonDocument?> ReadAsync(IClientSessionHandle tx, BsonDocument receipt, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw R.Broken();
        var row = await Rows(Collection).Find(tx, new BsonDocument("_id", receipt["_id"])).SingleOrDefaultAsync(ct); var count = row is null ? 0 : 1;
        foreach (var (table, field) in new[] { ("idempotencyRecords", "result.returnId"), ("auditEvents", "result.returnId"), ("outboxEvents", "payload.returnId") })
            if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument { { table == "idempotencyRecords" ? "operation" : "kind", Operation }, { field, receipt["_id"] } }, cancellationToken: ct) != count) throw R.Broken();
        if (row is null) return null;
        if (!await Rows(Collection).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", row["_id"]) & ReturnInspectionSchemas.Validator()).AnyAsync(ct)) throw R.Broken();
        var input = Input(row["input"].AsBsonDocument); ReturnedStockInspectionPolicy.RequireReceipt(input, ReturnQuarantineLedger.Input(receipt["input"].AsBsonDocument));
        if (row["inspectedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime || !InvoiceRecords.Equal(row["source"], Source(receipt)) || row["binding"] != Binding(receipt) || !InvoiceRecords.Equal(row["input"], Input(input)) ||
            row["snapshotHash"] != R.Hash(row) || row["bodyHash"] != AuthorizationStore.CommandHash(Body(receipt["_id"].AsInt64, 0, row["binding"].AsString, input), row["reason"].AsString)) throw R.Broken();
        var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray();
        if (people.Select(person => person.UserId).Distinct().Count() != 2 || !PersonIndependencePolicy.AreIndependent(people.SingleOrDefault(person => person.UserId == row["actorId"].AsInt64),
                people.SingleOrDefault(person => person.UserId == row["source"]["requestedBy"].AsInt64))) throw R.Broken();
        foreach (var field in new[] { "requestKey", "reason", "correlationId" }) if (!ReceivingAccountPolicy.Text(row[field].AsString, field == "reason" ? 1000 : 128)) throw R.Broken();
        var saved = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", Operation + ":" + row["_id"])).SingleOrDefaultAsync(ct);
        if (saved is null || saved.ElementCount != 9 || !saved["_id"].IsObjectId || saved["schemaVersion"] != 1 || !saved["createdAt"].IsBsonDateTime || saved["createdAt"].CompareTo(row["inspectedAt"]) > 0 ||
            saved["bodyHash"] != row["bodyHash"] || !InvoiceRecords.Equal(saved["result"], Result(row)) || !InvoiceRecords.Equal(saved.GetValue(PersonApprovalReceipt.Field, BsonNull.Value),
                PersonApprovalReceipt.Create(Permissions.CaseManage, new(1, 1, RequestedBy: row["source"]["requestedBy"].AsInt64))) || audit is null || !InvoiceRecords.Equal(audit, Audit(row)) ||
            effect is null || effect["kind"] != Operation || effect["actorId"] != row["actorId"] || effect["requestKey"] != row["requestKey"] || effect["createdAt"] != row["inspectedAt"] || !InvoiceRecords.Equal(effect["payload"], Result(row))) throw R.Broken();
        return row;
    }
    internal async Task RecordAsync(IClientSessionHandle tx, BsonDocument row, BsonDocument receipt, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || !InvoiceRecords.Equal(row["source"], Source(receipt))) throw R.Broken();
        ReturnedStockInspectionPolicy.RequireReceipt(Input(row["input"].AsBsonDocument), ReturnQuarantineLedger.Input(receipt["input"].AsBsonDocument));
        await Rows(Collection).InsertOneAsync(tx, row, cancellationToken: ct); await Rows("auditEvents").InsertOneAsync(tx, Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", Operation }, { "sourceKey", Operation + ":" + row["_id"] },
            { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "payload", Result(row) }, { "createdAt", row["inspectedAt"] }, { "dueAt", row["inspectedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
}
