using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Fulfillment;
/// <summary>Operational resolution proposal, never stock disposition, credit entitlement, cash
/// reservation or approval. Financial request permissions are additional current scoped grants.</summary>
internal sealed class ReturnResolutionRecords(IMongoDatabase database, IClock clock)
{
    internal const string Collection = "returnResolutionRequests", Operation = "fulfillment.case.resolution_request";
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static BsonDocument Source(BsonDocument inspection) => new() { { "returnId", inspection["_id"] }, { "inspectionHash", inspection["snapshotHash"] },
        { "caseId", inspection["source"]["caseId"] }, { "invoiceId", inspection["source"]["invoiceId"] }, { "requestedBy", inspection["source"]["requestedBy"] },
        { "inspectedBy", inspection["actorId"] }, { "inspectedAt", inspection["inspectedAt"] }, { "lines", new BsonArray(ReturnInspectionRecords.Input(inspection["input"].AsBsonDocument).Lines.Select(line => new BsonDocument {
            { "receiptId", line.ReceiptId }, { "skuId", line.SkuId }, { "inspectedBaseUnits", line.InspectedBaseUnits }, { "identity", line.Identity }, { "condition", line.Condition } })) } };
    internal static string Binding(BsonDocument inspection) => R.Binding(Source(inspection));
    internal static object Body(long id, long revision, string binding, ReturnResolutionInput input) => new { returnId = id, expectedRevision = revision, binding, input };
    internal static BsonDocument Input(ReturnResolutionInput input) => new() { { "action", input.Action }, { "publicNote", input.PublicNote } };
    internal static ReturnResolutionInput Input(BsonDocument value) => ReturnResolutionPolicy.Copy(new(value["action"].AsString, value["publicNote"].AsString));
    internal static BsonDocument Result(BsonDocument row) => new() { { "returnId", row["_id"] }, { "caseId", row["source"]["caseId"] }, { "invoiceId", row["source"]["invoiceId"] },
        { "revision", 1L }, { "requestedAt", row["requestedAt"] }, { "action", row["input"]["action"] }, { "state", "resolution_requested" }, { "operation", Operation } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", Operation }, { "operation", Operation }, { "actorId", row["actorId"] },
        { "actorGrantVersion", row["actorGrantVersion"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] }, { "reason", row["reason"] }, { "requestKey", row["requestKey"] },
        { "correlationId", row["correlationId"] }, { "createdAt", row["requestedAt"] }, { "result", Result(row) } };
    internal async Task<BsonDocument?> ReadAsync(IClientSessionHandle tx, BsonDocument inspection, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw R.Broken();
        var row = await Rows(Collection).Find(tx, new BsonDocument("_id", inspection["_id"])).SingleOrDefaultAsync(ct);
        foreach (var (table, field) in new[] { ("idempotencyRecords", "result.returnId"), ("auditEvents", "result.returnId"), ("outboxEvents", "payload.returnId") })
            if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument { { table == "idempotencyRecords" ? "operation" : "kind", Operation }, { field, inspection["_id"] } }, cancellationToken: ct) != (row is null ? 0 : 1)) throw R.Broken();
        if (row is null) return null;
        if (!await Rows(Collection).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", row["_id"]) & ReturnResolutionSchemas.Validator()).AnyAsync(ct)) throw R.Broken();
        var input = Input(row["input"].AsBsonDocument);
        if (row["requestedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime || !InvoiceRecords.Equal(row["source"], Source(inspection)) || row["binding"] != Binding(inspection) ||
            row["snapshotHash"] != R.Hash(row) || row["bodyHash"] != AuthorizationStore.CommandHash(Body(inspection["_id"].AsInt64, 0, row["binding"].AsString, input), row["reason"].AsString)) throw R.Broken();
        var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray();
        if (people.Select(person => person.UserId).Distinct().Count() != 2 || !PersonIndependencePolicy.AreIndependent(people.SingleOrDefault(person => person.UserId == row["actorId"].AsInt64),
                people.SingleOrDefault(person => person.UserId == row["source"]["requestedBy"].AsInt64))) throw R.Broken();
        foreach (var field in new[] { "requestKey", "reason", "correlationId" }) if (!ReceivingAccountPolicy.Text(row[field].AsString, field == "reason" ? 1000 : 128)) throw R.Broken();
        var saved = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct);
        var audit = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct); var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", Operation + ":" + row["_id"])).SingleOrDefaultAsync(ct);
        if (saved is null || saved.ElementCount != 9 || !saved["_id"].IsObjectId || saved["schemaVersion"] != 1 || !saved["createdAt"].IsBsonDateTime || saved["createdAt"].CompareTo(row["requestedAt"]) > 0 ||
            saved["bodyHash"] != row["bodyHash"] || !InvoiceRecords.Equal(saved["result"], Result(row)) || !InvoiceRecords.Equal(saved.GetValue(PersonApprovalReceipt.Field, BsonNull.Value),
                PersonApprovalReceipt.Create(Permissions.CaseManage, new(1, 1, RequestedBy: row["source"]["requestedBy"].AsInt64))) || audit is null || !InvoiceRecords.Equal(audit, Audit(row)) ||
            effect is null || effect["kind"] != Operation || effect["actorId"] != row["actorId"] || effect["requestKey"] != row["requestKey"] || effect["createdAt"] != row["requestedAt"] || !InvoiceRecords.Equal(effect["payload"], Result(row))) throw R.Broken();
        return row;
    }
    internal async Task RecordAsync(IClientSessionHandle tx, BsonDocument row, BsonDocument inspection, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || !InvoiceRecords.Equal(row["source"], Source(inspection))) throw R.Broken();
        await Rows(Collection).InsertOneAsync(tx, row, cancellationToken: ct); await Rows("auditEvents").InsertOneAsync(tx, Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", Operation }, { "sourceKey", Operation + ":" + row["_id"] },
            { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "payload", Result(row) }, { "createdAt", row["requestedAt"] }, { "dueAt", row["requestedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
}
