using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Fulfillment;
/// <summary>Independent physical association finding only. Original intake/inspection remains
/// unverified; no usable package balance, stock movement, disposition or finance is created.</summary>
internal sealed class ReturnedLotVerificationRecords(IMongoDatabase database, IClock clock)
{
    internal const string Collection = "returnedLotVerifications", Operation = "fulfillment.case.return_lot_verify";
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static BsonDocument Source(BsonDocument receipt, BsonDocument inspection) => new() {
        { "returnId", receipt["_id"] }, { "returnHash", receipt["snapshotHash"] }, { "inspectionHash", inspection["snapshotHash"] },
        { "caseId", receipt["caseId"] }, { "invoiceId", receipt["source"]["invoiceId"] }, { "requestedBy", receipt["source"]["requestedBy"] }, { "inspectedAt", inspection["inspectedAt"] } };
    internal static string Binding(BsonDocument receipt, BsonDocument inspection) => R.Binding(Source(receipt, inspection));
    internal static object Body(long id, long revision, string binding, ReturnedLotVerificationInput input) => new { returnId = id, expectedRevision = revision, binding, input };
    internal static BsonDocument Input(ReturnedLotVerificationInput input) => new() { { "verificationReference", input.VerificationReference },
        { "confirmPhysicalOriginalLotMatch", input.ConfirmPhysicalOriginalLotMatch }, { "lines", new BsonArray(input.Lines.Select(line => new BsonDocument {
            { "receiptId", line.ReceiptId }, { "skuId", line.SkuId }, { "originalLotId", line.OriginalLotId }, { "verifiedBaseUnits", line.VerifiedBaseUnits } })) } };
    internal static ReturnedLotVerificationInput Input(BsonDocument row) => ReturnedLotVerificationPolicy.Copy(new(row["verificationReference"].AsString,
        row["confirmPhysicalOriginalLotMatch"].AsBoolean, [.. row["lines"].AsBsonArray.Select(line => new ReturnedLotVerificationLine(
            line["receiptId"].AsInt64, line["skuId"].AsInt64, line["originalLotId"].AsInt64, line["verifiedBaseUnits"].AsInt64))]));
    internal static BsonDocument Result(BsonDocument row) => new() { { "returnId", row["_id"] }, { "caseId", row["source"]["caseId"] },
        { "invoiceId", row["source"]["invoiceId"] }, { "revision", 1L }, { "verifiedAt", row["verifiedAt"] }, { "state", row["state"] }, { "operation", Operation } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] },
        { "reason", row["reason"] }, { "requestKey", row["requestKey"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["verifiedAt"] }, { "result", Result(row) } };
    internal static void RequireInput(BsonDocument receipt, BsonDocument inspection, ReturnedLotVerificationInput input)
    {
        input = ReturnedLotVerificationPolicy.Copy(input);
        foreach (var line in input.Lines) {
            var received = receipt["input"]["lines"].AsBsonArray.SingleOrDefault(item => item["receiptId"] == line.ReceiptId && item["skuId"] == line.SkuId);
            var inspected = inspection["input"]["lines"].AsBsonArray.SingleOrDefault(item => item["receiptId"] == line.ReceiptId && item["skuId"] == line.SkuId);
            var stock = receipt["source"]["stock"].AsBsonArray.SingleOrDefault(item => item["receiptId"] == line.ReceiptId && item["skuId"] == line.SkuId);
            if (received is null || inspected is null || stock is null || inspected["identity"] != "matched" || inspected["condition"] != "sealed" ||
                inspected["observedLotLabel"].IsBsonNull || inspected["inspectedBaseUnits"] != received["receivedBaseUnits"] ||
                line.VerifiedBaseUnits > received["receivedBaseUnits"].AsInt64 || line.VerifiedBaseUnits % stock["baseUnitsPerPackage"].AsInt32 != 0)
                throw ReturnedLotVerificationPolicy.Invalid();
            var original = stock["originalLots"].AsBsonArray.SingleOrDefault(item => item["lotId"] == line.OriginalLotId);
            if (original is null || line.VerifiedBaseUnits > checked(original["dispatchedPackages"].AsInt64 * stock["baseUnitsPerPackage"].AsInt32))
                throw ReturnedLotVerificationPolicy.Invalid();
        }
    }
    internal async Task RequireCapacityAsync(IClientSessionHandle tx, BsonDocument receipt, BsonDocument inspection, ReturnedLotVerificationInput input, CancellationToken ct)
    {
        RequireInput(receipt, inspection, input);
        var owner = new AfterSalesStockSourceEvents(database, new(database), clock);
        foreach (var line in input.Lines) {
            var stock = receipt["source"]["stock"].AsBsonArray.Single(item => item["receiptId"] == line.ReceiptId && item["skuId"] == line.SkuId);
            var original = stock["originalLots"].AsBsonArray.Single(item => item["lotId"] == line.OriginalLotId);
            var raw = await Rows(Collection).Find(tx, new BsonDocument { { "_id", new BsonDocument("$ne", receipt["_id"]) },
                { "input.lines", new BsonDocument("$elemMatch", new BsonDocument { { "receiptId", line.ReceiptId }, { "skuId", line.SkuId }, { "originalLotId", line.OriginalLotId } }) } })
                .Limit(AfterSalesStockSourceRecords.MaximumRows + 1).ToListAsync(ct);
            if (raw.Count > AfterSalesStockSourceRecords.MaximumRows) throw AfterSalesStockSourceRecords.Unavailable();
            long used = 0;
            foreach (var candidate in raw) {
                var source = await owner.ReadAsync(tx, "inspect", candidate["_id"].AsInt64, true, ct);
                await RequireRowAsync(tx, candidate, source.Record["return"].AsBsonDocument, source.Record["inspection"].AsBsonDocument, ct);
                used = checked(used + Input(candidate["input"].AsBsonDocument).Lines.Single(item => item.ReceiptId == line.ReceiptId && item.SkuId == line.SkuId).VerifiedBaseUnits);
            }
            if (used > checked(original["dispatchedPackages"].AsInt64 * stock["baseUnitsPerPackage"].AsInt32) - line.VerifiedBaseUnits)
                throw AfterSalesDecisionRecords.Changed("return_original_lot_quantity_exceeded");
        }
    }
    private async Task RequireRowAsync(IClientSessionHandle tx, BsonDocument row, BsonDocument receipt, BsonDocument inspection, CancellationToken ct)
    {
        if (!await Rows(Collection).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", row["_id"]) & ReturnedLotVerificationSchemas.Validator()).AnyAsync(ct)) throw R.Broken();
        var input = Input(row["input"].AsBsonDocument); RequireInput(receipt, inspection, input);
        if (row["verifiedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime || row["source"] != Source(receipt, inspection) || row["binding"] != Binding(receipt, inspection) ||
            row["input"] != Input(input) || row["snapshotHash"] != R.Hash(row) || row["bodyHash"] != AuthorizationStore.CommandHash(Body(receipt["_id"].AsInt64, 0, row["binding"].AsString, input), row["reason"].AsString)) throw R.Broken();
        var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray();
        if (people.Select(person => person.UserId).Distinct().Count() != 2 || !PersonIndependencePolicy.AreIndependent(people.SingleOrDefault(person => person.UserId == row["actorId"].AsInt64),
            people.SingleOrDefault(person => person.UserId == row["source"]["requestedBy"].AsInt64))) throw R.Broken();
        foreach (var field in new[] { "requestKey", "reason", "correlationId" }) if (!ReceivingAccountPolicy.Text(row[field].AsString, field == "reason" ? 1000 : 128)) throw R.Broken();
        var command = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", Operation + ":" + row["_id"])).SingleOrDefaultAsync(ct);
        if (command is null || command.ElementCount != 9 || !command["_id"].IsObjectId || command["schemaVersion"] != 1 || !command["createdAt"].IsBsonDateTime ||
            command["createdAt"].CompareTo(row["verifiedAt"]) > 0 || command["bodyHash"] != row["bodyHash"] || command["result"] != Result(row) ||
            !InvoiceRecords.Equal(command.GetValue(PersonApprovalReceipt.Field, BsonNull.Value), PersonApprovalReceipt.Create(Permissions.CaseManage, new(1, 1, RequestedBy: row["source"]["requestedBy"].AsInt64))) ||
            audit is null || audit != Audit(row) || effect is null || effect["kind"] != Operation || effect["actorId"] != row["actorId"] || effect["requestKey"] != row["requestKey"] ||
            effect["createdAt"] != row["verifiedAt"] || effect["payload"] != Result(row)) throw R.Broken();
    }
    internal async Task<BsonDocument?> ReadAsync(IClientSessionHandle tx, BsonDocument receipt, BsonDocument inspection, CancellationToken ct)
    {
        new InventoryRepository(database).RequireSession(tx);
        var row = await Rows(Collection).Find(tx, new BsonDocument("_id", receipt["_id"])).SingleOrDefaultAsync(ct);
        foreach (var (table, field) in new[] { ("idempotencyRecords", "result.returnId"), ("auditEvents", "result.returnId"), ("outboxEvents", "payload.returnId") })
            if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument { { table == "idempotencyRecords" ? "operation" : "kind", Operation }, { field, receipt["_id"] } }, cancellationToken: ct) != (row is null ? 0 : 1)) throw R.Broken();
        if (row is null) return null;
        await RequireRowAsync(tx, row, receipt, inspection, ct);
        await RequireCapacityAsync(tx, receipt, inspection, Input(row["input"].AsBsonDocument), ct); return row;
    }
    internal async Task RecordAsync(IClientSessionHandle tx, BsonDocument row, BsonDocument receipt, BsonDocument inspection, CancellationToken ct)
    {
        new InventoryRepository(database).RequireSession(tx); await RequireCapacityAsync(tx, receipt, inspection, Input(row["input"].AsBsonDocument), ct);
        if (row["source"] != Source(receipt, inspection)) throw R.Broken();
        await Rows(Collection).InsertOneAsync(tx, row, cancellationToken: ct); await Rows("auditEvents").InsertOneAsync(tx, Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", Operation }, { "sourceKey", Operation + ":" + row["_id"] },
            { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "payload", Result(row) }, { "createdAt", row["verifiedAt"] }, { "dueAt", row["verifiedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
}
