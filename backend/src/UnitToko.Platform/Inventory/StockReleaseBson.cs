using MongoDB.Bson;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Inventory;

internal static class StockReleaseBson
{
    internal const string Proposals = "stockReleaseProposals", Decisions = "stockReleaseDecisions",
        Propose = "inventory.stock-release.propose", Decide = "inventory.stock-release.decide", Namespace = "inventory.stock-release";
    internal static PersistenceConflictException Broken() => new("stock_release_integrity_unavailable", "Complete held-release source, independent decision and quantity/value provenance is required");
    internal static PersistenceConflictException Changed() => new("stock_release_changed", "The selected quantity, value, source or approval context changed");
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name != "snapshotHash")));
    internal static string CauseHash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name is not
        ("snapshotHash" or "causeHash" or "phase" or "movementId" or "valueTransitionId" or "valueTransitionHash" or "transferredValue"))));
    internal static BsonDocument Input(StockReleaseInput value) { value.Validate(); return new() {
        { "location", StockSourceSetupBson.Location(value.Location) }, { "receiptId", value.ReceiptId }, { "qualityAllocationId", value.QualityAllocationId },
        { "valuationAllocationId", value.ValuationAllocationId }, { "packages", value.Packages }, { "policy", value.Policy } }; }
    internal static StockReleaseInput Input(BsonValue row) {
        var value = new StockReleaseInput(InventoryGraph.Location(row["location"].AsBsonDocument), row["receiptId"].AsInt64,
            row["qualityAllocationId"].AsInt64, row["valuationAllocationId"].AsInt64, row["packages"].AsInt64, row["policy"].AsString); value.Validate(); return value;
    }
    internal static BsonDocument Expected(StockReleaseExpectation value) { value.Validate(); return new() { { "balanceRevision", value.BalanceRevision },
        { "poolRevision", value.PoolRevision }, { "poolFence", value.PoolFence }, { "heldValueRevision", value.HeldValueRevision }, { "lotValueRevision", value.LotValueRevision } }; }
    internal static StockReleaseExpectation Expected(BsonValue row) {
        var value = new StockReleaseExpectation(row["balanceRevision"].AsInt64, row["poolRevision"].AsInt64, row["poolFence"].AsInt64,
            row["heldValueRevision"].AsInt64, row["lotValueRevision"].AsInt64); value.Validate(); return value;
    }
    internal static InventorySource Source(long decisionId, long poolId) => new(Namespace, decisionId, "releaseHeld", poolId);
    internal static BsonDocument Result(BsonDocument row) {
        var proposal = row["kind"] == "stock.release.proposal";
        if (!proposal && row["phase"] != "committed") throw Broken();
        return new() { { "proposalId", proposal ? row["_id"] : row["proposalId"] }, { "revision", 1L },
            { "status", proposal ? "proposed" : row["decision"] == "approve" ? "approved" : "rejected" },
            { "decisionId", proposal ? BsonNull.Value : row["_id"] }, { "movementId", proposal ? BsonNull.Value : row["movementId"] },
            { "valueTransitionId", proposal ? BsonNull.Value : row["valueTransitionId"] }, { "transferredValue", proposal ? BsonNull.Value : row["transferredValue"] } };
    }
    internal static BsonDocument Audit(BsonDocument row, string operation) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 },
        { "kind", operation }, { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "createdAt", row["recordedAt"] }, { "record", row.DeepClone() }, { "result", Result(row) } };
    internal static BsonDocument Outbox(BsonDocument row, string operation) => new() { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
        { "kind", operation }, { "sourceKey", operation + ":" + row["_id"].AsInt64 }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] },
        { "location", row["input"]["location"] }, { "payload", Result(row) }, { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } };
}
