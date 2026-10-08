using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Procurement;

internal static class PurchaseArrivalRecords
{
    internal const string Heads = "purchaseArrivalHeads", Events = "purchaseArrivalEvents", Audits = "purchaseArrivalAudits";
    internal const string Prefix = "procurement.purchase.arrival.";
    internal static string Key(long order, long sku) => $"{order}:{sku}";
    internal static PersistenceConflictException Broken() => new("purchase_arrival_integrity_unavailable", "The recorded arrival history cannot be verified");
    internal static PersistenceConflictException Changed() => new("purchase_arrival_changed", "The purchase order or arrival schedule changed; reload and review");
    internal static BsonDocument Input(PurchaseArrivalInput v) => new() {
        { "orderId", v.OrderId }, { "orderVersion", v.OrderVersion }, { "approvalId", v.ApprovalId }, { "skuId", v.SkuId },
        { "definitionId", v.DefinitionId }, { "expectedRevision", v.ExpectedRevision },
        { "expectedAt", v.ExpectedAt is { } at ? new BsonDateTime(at.UtcDateTime) : BsonNull.Value }, { "confirmationReference", v.ConfirmationReference } };
    internal static PurchaseArrivalInput Input(BsonValue v) => PurchaseArrivalRules.Copy(new(v["orderId"].AsInt64,
        v["orderVersion"].AsInt64, v["approvalId"].AsInt64, v["skuId"].AsInt64, v["definitionId"].AsInt64, v["expectedRevision"].AsInt64,
        v["expectedAt"].IsBsonNull ? null : new(v["expectedAt"].ToUniversalTime(), TimeSpan.Zero), v["confirmationReference"].AsString));
    internal static object Body(BsonDocument row) => row["action"] == "propose"
        ? new { input = Input(row["input"]), binding = row["binding"].AsString }
        : (object)new { proposalId = row["proposalId"].AsInt64, expectedRevision = row["previousRevision"].AsInt64,
            decision = row["action"].AsString, binding = row["binding"].AsString };
    internal static BsonDocument Audit(BsonDocument row) => new() {
        { "_id", row["_id"] }, { "schemaVersion", 1 }, { "headId", row["headId"] }, { "actorId", row["actorId"] }, { "operation", Prefix + row["action"].AsString },
        { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] }, { "eventHash", row["eventHash"] },
        { "location", row["location"].DeepClone() }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument Result(BsonDocument row) => new() { { "eventId", row["_id"] }, { "eventHash", row["eventHash"] },
        { "orderId", row["input"]["orderId"] }, { "skuId", row["input"]["skuId"] }, { "revision", row["revision"] },
        { "state", row["action"] == "propose" ? "pending" : row["action"] == "approve" ? "approved" : "rejected" } };
    internal static string Hash(BsonDocument row) => PurchaseOrderBson.Hash(new(row.Where(field => field.Name != "eventHash")));
    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct)
    {
        foreach (var name in PurchaseArrivalSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            var indexes = definition is null ? [] : (await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            var keys = PurchaseArrivalSchemas.Keys(name);
            if (definition is null || !AccountContactStorage.SafeDefinition(definition, PurchaseArrivalSchemas.Validator(name)) ||
                !AccountContactStorage.SafeIndexes(indexes, keys) || keys.Any(key => !indexes.Any(index => AccountContactStorage.Matches(index, key)))) throw Broken();
        }
    }
}
