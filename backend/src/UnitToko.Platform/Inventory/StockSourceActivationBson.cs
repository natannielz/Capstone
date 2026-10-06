using MongoDB.Bson;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Inventory;

internal static class StockSourceActivationBson
{
    internal const string Proposals = "stockSourceActivationProposals", Decisions = "stockSourceActivationDecisions",
        Activations = "stockSourceActivations", Propose = "stock.source.activation.propose", Decide = "stock.source.activation.decide";
    internal static string Hash(BsonDocument row) => StockSourceSuspensionBson.Hash(row);
    internal static BsonDocument Header(string table, long id) => new() { { "_id", id }, { "schemaVersion", 1 },
        { "kind", table switch { Proposals => "stock.source.activation.proposal", Decisions => "stock.source.activation.decision",
            Activations => "stock.source.activation", _ => throw Unavailable() } } };
    internal static BsonDocument Expected(StockSourceActivationExpectation value) => new() {
        { "poolRevision", value.PoolRevision }, { "poolFence", value.PoolFence }, { "suspensionRevision", value.SuspensionRevision },
        { "policyId", value.PolicyId }, { "policyRevision", value.PolicyRevision }, { "activationRevision", value.ActivationRevision },
        { "configurationHash", value.ConfigurationHash } };
    internal static StockSourceActivationExpectation ReadExpected(BsonDocument row) {
        Exact(row, "poolRevision", "poolFence", "suspensionRevision", "policyId", "policyRevision", "activationRevision", "configurationHash");
        foreach (var name in row.Names.Where(name => name != "configurationHash")) if (!row[name].IsInt64) throw Unavailable();
        var value = new StockSourceActivationExpectation(row["poolRevision"].AsInt64, row["poolFence"].AsInt64,
            row["suspensionRevision"].AsInt64, row["policyId"].AsInt64, row["policyRevision"].AsInt64,
            row["activationRevision"].AsInt64, row["configurationHash"].AsString);
        value.Validate(); return value;
    }
    internal static BsonDocument Handoff(StockSourceEmptyHandoff value) {
        StockSourceActivationRules.Validate(value);
        return new() { { "reference", value.Reference }, { "observedAt", value.ObservedAt.UtcDateTime },
            { "physicalPackages", value.PhysicalPackages }, { "carriedValueMinorUnits", value.CarriedValueMinorUnits },
            { "emptyPoolConfirmed", value.EmptyPoolConfirmed }, { "previousWriterStopped", value.PreviousWriterStopped },
            { "noUnrecordedStock", value.NoUnrecordedStock } };
    }
    internal static StockSourceEmptyHandoff ReadHandoff(BsonDocument row) {
        Exact(row, "reference", "observedAt", "physicalPackages", "carriedValueMinorUnits", "emptyPoolConfirmed", "previousWriterStopped", "noUnrecordedStock");
        if (!row["physicalPackages"].IsInt64 || !row["carriedValueMinorUnits"].IsInt64) throw Unavailable();
        var value = new StockSourceEmptyHandoff(row["reference"].AsString, StockSourceSetupBson.Time(row["observedAt"]),
            row["physicalPackages"].AsInt64, row["carriedValueMinorUnits"].AsInt64, row["emptyPoolConfirmed"].AsBoolean,
            row["previousWriterStopped"].AsBoolean, row["noUnrecordedStock"].AsBoolean);
        StockSourceActivationRules.Validate(value); return value;
    }
    internal static void Seal(BsonDocument row) => row.Add("snapshotHash", Hash(row));
    internal static void RequireSeal(BsonDocument row) {
        if (row.GetValue("snapshotHash", BsonNull.Value) != Hash(new BsonDocument(row.Where(field => field.Name != "snapshotHash")))) throw Unavailable();
    }
    internal static void Exact(BsonDocument row, params string[] names) {
        if (row.ElementCount != names.Length || row.Names.Distinct(StringComparer.Ordinal).Count() != names.Length || !names.All(row.Contains)) throw Unavailable();
    }
    internal static BsonDocument Result(BsonDocument row) => row["kind"] == "stock.source.activation.proposal"
        ? new() { { "proposalId", row["_id"] }, { "decisionId", BsonNull.Value }, { "activationId", BsonNull.Value },
            { "poolId", row["poolId"] }, { "revision", 1L }, { "status", "proposed" }, { "recordedAt", row["recordedAt"] } }
        : new() { { "proposalId", row["proposalId"] }, { "decisionId", row["_id"] }, { "activationId", row["activationId"] },
            { "poolId", row["poolId"] }, { "revision", 1L }, { "status", row["decision"] == "approve" ? "activated" : "rejected" },
            { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument Audit(BsonDocument row, string operation) => new() {
        { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", "stock.source.activation.audit" }, { "operation", operation },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "createdAt", row["recordedAt"] }, { "record", row.DeepClone() } };
    internal static BsonDocument Outbox(BsonDocument row, string operation) => new() {
        { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "kind", operation },
        { "sourceKey", operation + ":" + row["_id"].AsInt64 }, { "actorId", row["actorId"] },
        { "requestKey", row["requestKey"] }, { "poolId", row["poolId"] }, { "payload", Result(row) },
        { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } };
    internal static PersistenceConflictException Changed() => new("stock_source_activation_changed",
        "The reviewed source, operating policy, suspension, handoff or pool changed");
    internal static PersistenceConflictException Unavailable() => new("stock_source_activation_unavailable",
        "Complete source activation provenance is unavailable");
}
