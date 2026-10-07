using MongoDB.Bson;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Inventory;

internal static class PackConversionBson
{
    internal const string Conversions = "packConversions", Operation = "inventory.pack-conversion.execute", Namespace = "inventory.pack-conversion";
    internal static PersistenceConflictException Broken() => new("pack_conversion_integrity_unavailable", "The exact approved conversion, physical legs and conserved value cannot be authenticated");
    internal static PersistenceConflictException Changed() => new("pack_conversion_changed", "The selected rule, source quantity, value or authority changed");
    internal static BsonDocument Input(PackConversionInput input) {
        input = PackConversionExecutionPolicy.Copy(input);
        return new() { { "location", StockCountBson.Scope(input.Location) }, { "ruleId", input.RuleId }, { "ruleVersion", input.RuleVersion },
            { "sourceBalanceId", input.SourceBalanceId }, { "steps", input.Steps } };
    }
    internal static PackConversionInput Input(BsonValue row) => PackConversionExecutionPolicy.Copy(new PackConversionInput(
        StockCountBson.Scope(row["location"].AsBsonDocument), row["ruleId"].AsInt64, row["ruleVersion"].AsInt64,
        row["sourceBalanceId"].AsInt64, row["steps"].AsInt32));
    internal static BsonDocument Expected(PackConversionExpectation value) {
        value = PackConversionExecutionPolicy.Copy(value);
        return new() { { "sourceBalanceRevision", value.SourceBalanceRevision }, { "poolRevision", value.PoolRevision },
            { "poolFence", value.PoolFence }, { "lotValueRevision", value.LotValueRevision } };
    }
    internal static PackConversionExpectation Expected(BsonValue row) => PackConversionExecutionPolicy.Copy(new PackConversionExpectation(
        row["sourceBalanceRevision"].AsInt64, row["poolRevision"].AsInt64, row["poolFence"].AsInt64, row["lotValueRevision"].AsInt64));
    internal static InventorySource Source(long id, long poolId) => new(Namespace, id, "convertPackages", poolId);
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name != "snapshotHash")));
    internal static string CauseHash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name is not
        ("phase" or "causeHash" or "snapshotHash" or "movementId" or "valueTransitionId" or "valueTransitionHash" or "transferredValue"))));
    internal static BsonDocument Result(BsonDocument row) {
        if (row["phase"] != "committed") throw Broken();
        return new() { { "conversionId", row["_id"] }, { "revision", 1L }, { "status", "converted" },
            { "sourcePackages", row["quantities"]["sourcePackages"] }, { "destinationPackages", row["quantities"]["destinationPackages"] },
            { "baseUnits", row["quantities"]["baseUnits"] }, { "destinationBalanceId", row["destinationBefore"]["_id"] },
            { "destinationLotId", row["destinationLot"]["_id"] }, { "movementId", row["movementId"] },
            { "valueTransitionId", row["valueTransitionId"] }, { "transferredValue", row["transferredValue"] } };
    }
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 },
        { "kind", Operation }, { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "createdAt", row["recordedAt"] }, { "record", row.DeepClone() }, { "result", Result(row) } };
    internal static BsonDocument Outbox(BsonDocument row) => new() { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
        { "kind", Operation }, { "sourceKey", Operation + ":" + row["_id"].AsInt64 }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] },
        { "location", row["input"]["location"] }, { "payload", Result(row) }, { "createdAt", row["recordedAt"] },
        { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } };
}
