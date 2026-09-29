using System.Security.Cryptography;
using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Platform.Inventory;

internal static class StockSourceSuspensionBson
{
    internal const string Operation = "stock.source.suspend";
    internal static BsonDocument Header(long id) => new() { { "_id", id }, { "schemaVersion", 1 }, { "kind", "stock.source.suspension" } };
    internal static string Hash(BsonDocument configuration) => Convert.ToHexString(SHA256.HashData(configuration.ToBson())).ToLowerInvariant();
    internal static BsonDocument Configuration(BsonDocument current, IEnumerable<BsonDocument> aliases)
    {
        var source = new BsonDocument
        {
            { "poolRevision", current["pool"]["revision"] }, { "active", current["pool"]["active"] },
            { "input", StockSourceSetupBson.Input(StockSourceSetupBson.ReadInput(current["input"].AsBsonDocument)) },
            { "approvedAt", current["approvedAt"] }, { "creationProposalId", current["creationProposalId"] },
            { "revisionProposalId", current["revisionProposalId"] }
        };
        var mappings = new BsonArray(aliases.OrderBy(alias => alias["requestedLocation"]["channelId"].AsInt64).Select(alias => new BsonDocument
        {
            { "aliasId", alias["_id"] }, { "aliasRevision", alias["aliasRevision"] }, { "poolRevision", alias["poolRevision"] },
            { "requestedLocation", alias["requestedLocation"].DeepClone() }, { "enabled", alias["enabled"] },
            { "proposalId", alias["proposalId"] }, { "decisionId", alias["decisionId"] }
        }));
        return new() { { "source", source }, { "mappings", mappings } };
    }
    internal static BsonDocument Result(BsonDocument row) => new()
    {
        { "suspensionId", row["_id"] }, { "poolId", row["poolId"] }, { "poolRevision", row["poolRevision"] },
        { "suspensionRevision", row["suspensionRevision"] }, { "status", "suspended" }
    };
    internal static StockSourceSuspensionExpectation Expectation(BsonDocument row) =>
        new(row["poolRevision"].AsInt64, checked(row["suspensionRevision"].AsInt64 - 1), row["configurationHash"].AsString);
    internal static BsonDocument Audit(BsonDocument row, ActorContext actor, DateTimeOffset at) => new()
    {
        { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", "stock.source.suspension.audit" },
        { "operation", Operation }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
        { "createdAt", at.UtcDateTime }, { "record", row.DeepClone() }
    };
    internal static void Require(BsonDocument row) => Guard(() =>
    {
        Exact(row, "_id", "schemaVersion", "kind", "poolId", "poolRevision", "ownerLocation", "physicalKey", "suspensionRevision",
            "configuration", "configurationHash", "suspendedBy", "suspendedAt", "reason", "correlationId", "auditId");
        if (!Positive(row, "_id") || !row["schemaVersion"].IsInt32 || row["schemaVersion"] != 1 || row["kind"] != "stock.source.suspension" ||
            !Positive(row, "poolId") || !Positive(row, "poolRevision") || !Positive(row, "suspensionRevision") ||
            !Positive(row, "suspendedBy") || !Positive(row, "auditId") || !StockSourceSetupPolicy.Key(row["physicalKey"].AsString) ||
            !StockSourceSetupPolicy.Text(row["reason"].AsString, 1000) || !StockSourceSetupPolicy.Text(row["correlationId"].AsString, 128)) throw Invalid();
        Expectation(row).Validate();
        var configuration = row["configuration"].AsBsonDocument;
        Exact(configuration, "source", "mappings");
        var source = configuration["source"].AsBsonDocument;
        Exact(source, "poolRevision", "active", "input", "approvedAt", "creationProposalId", "revisionProposalId");
        if (!Positive(source, "poolRevision") || source["poolRevision"] != row["poolRevision"] || !source["active"].IsBoolean || !Positive(source, "creationProposalId") ||
            (row["poolRevision"] == 1L ? !source["revisionProposalId"].IsBsonNull : !Positive(source, "revisionProposalId"))) throw Invalid();
        var input = StockSourceSetupBson.ReadInput(source["input"].AsBsonDocument);
        if (input.PhysicalKey != row["physicalKey"].AsString || input.Location != InventoryGraph.Location(row["ownerLocation"].AsBsonDocument) ||
            StockSourceSetupBson.Time(source["approvedAt"]) > StockSourceSetupBson.Time(row["suspendedAt"])) throw Invalid();
        long previous = 0;
        foreach (var value in configuration["mappings"].AsBsonArray)
        {
            var alias = value.AsBsonDocument;
            Exact(alias, "aliasId", "aliasRevision", "poolRevision", "requestedLocation", "enabled", "proposalId", "decisionId");
            if (!Positive(alias, "aliasId") || !Positive(alias, "aliasRevision") || !Positive(alias, "poolRevision") ||
                alias["poolRevision"].AsInt64 > row["poolRevision"].AsInt64 || !Positive(alias, "proposalId") || !Positive(alias, "decisionId") ||
                !alias["enabled"].IsBoolean) throw Invalid();
            var requested = InventoryGraph.Location(alias["requestedLocation"].AsBsonDocument);
            StockSourceAliasPolicy.Validate(input.Location, requested);
            if (requested.ChannelId.Value <= previous) throw Invalid();
            previous = requested.ChannelId.Value;
        }
        if (row["configurationHash"].AsString != Hash(configuration)) throw Invalid();
    });
    internal static bool Positive(BsonDocument row, string field) => row[field].IsInt64 && row[field].AsInt64 > 0;
    internal static void Exact(BsonDocument row, params string[] fields)
    {
        if (row.ElementCount != fields.Length || row.Names.Distinct(StringComparer.Ordinal).Count() != fields.Length || !fields.All(row.Contains)) throw Invalid();
    }
    internal static void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or KeyNotFoundException or FormatException or OverflowException)
        { throw Invalid(); }
    }
    internal static DomainValidationException Invalid() => new("stock_source_suspension_unavailable", "Source suspension history is unavailable");
}
