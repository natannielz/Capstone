using MongoDB.Bson;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Inventory;

internal static class StockSourceReconciliationBson
{
    internal const string Proposals = "stockSourceReconciliationProposals";
    internal const string Decisions = "stockSourceReconciliationDecisions";
    internal const string ProposeOperation = "stock.source.reconciliation.propose";
    internal const string DecideOperation = "stock.source.reconciliation.decide";
    internal static string Hash(BsonDocument row) => StockSourceSuspensionBson.Hash(row);
    internal static BsonDocument Observation(StockSourceReconciliationObservation value)
    {
        StockSourceReconciliationRules.Validate(value);
        return new() { { "observedAt", value.ObservedAt.UtcDateTime }, { "sourceReference", value.SourceReference },
            { "entries", new BsonArray(value.Entries.OrderBy(item => item.BalanceId)
                .Select(item => new BsonDocument { { "balanceId", item.BalanceId }, { "packages", item.Packages } })) } };
    }
    internal static BsonDocument Result(BsonDocument row) => row["kind"] == "stock.source.reconciliation.proposal"
        ? new() { { "proposalId", row["_id"] }, { "decisionId", BsonNull.Value }, { "poolId", row["poolId"] },
            { "revision", 1L }, { "status", "proposed" }, { "recordedAt", row["recordedAt"] } }
        : new() { { "proposalId", row["proposalId"] }, { "decisionId", row["_id"] }, { "poolId", row["poolId"] },
            { "revision", 1L }, { "status", row["decision"] == "confirm" ? "reconciled" : "rejected" }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument Audit(BsonDocument row, string operation) => new() {
        { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", "stock.source.reconciliation.audit" },
        { "operation", operation }, { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "createdAt", row["recordedAt"] }, { "record", row.DeepClone() } };
    internal static BsonDocument Outbox(BsonDocument row, string operation) => new() {
        { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", operation },
        { "sourceKey", operation + ":" + row["_id"].AsInt64 }, { "actorId", row["actorId"] },
        { "requestKey", row["requestKey"] }, { "poolId", row["poolId"] }, { "payload", Result(row) },
        { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] },
        { "status", "pending" }, { "attempts", 0 } };
    internal static PersistenceConflictException Changed() => new("stock_source_reconciliation_changed",
        "The reviewed source, policy, pool or complete physical baseline changed; reload before recording reconciliation");
    internal static PersistenceConflictException Unavailable() => new("stock_source_reconciliation_unavailable",
        "Complete canonical stock and source provenance is unavailable");
}
