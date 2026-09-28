using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Platform.Inventory;

internal static class StockSourceAliasBson
{
    internal static string Kind(string name) => name switch
    {
        "stockSourceAliasProposals" => "stock.source.alias.proposal",
        "stockSourceAliasDecisions" => "stock.source.alias.decision",
        "stockSourceAliases" => "stock.source.alias",
        _ => throw Invalid()
    };
    internal static BsonDocument Header(string name, long id) => new()
        { { "_id", id }, { "schemaVersion", 1 }, { "kind", Kind(name) } };
    private static bool Positive(BsonDocument row, string name) => row[name].IsInt64 && row[name].AsInt64 > 0;
    private static void Header(BsonDocument row, string name, int count)
    {
        if (row.ElementCount != count || row.Names.Distinct(StringComparer.Ordinal).Count() != count ||
            !Positive(row, "_id") || !row["schemaVersion"].IsInt32 || row["schemaVersion"].AsInt32 != 1 ||
            row["kind"] != Kind(name)) throw Invalid();
    }
    private static void Metadata(BsonDocument row)
    {
        if (!Positive(row, "auditId") || !StockSourceSetupPolicy.Text(row["reason"].AsString, 1000) ||
            !StockSourceSetupPolicy.Text(row["correlationId"].AsString, 128)) throw Invalid();
    }
    private static void Binding(BsonDocument row)
    {
        if (!Positive(row, "poolId") || !Positive(row, "poolRevision") || !row["enabled"].IsBoolean ||
            !StockSourceSetupPolicy.Key(row["physicalKey"].AsString)) throw Invalid();
        StockSourceAliasPolicy.Validate(InventoryGraph.Location(row["ownerLocation"].AsBsonDocument),
            InventoryGraph.Location(row["requestedLocation"].AsBsonDocument));
    }
    internal static void Proposal(BsonDocument row) => Guard(() =>
    {
        Header(row, "stockSourceAliasProposals", 16); Binding(row); Metadata(row);
        if (!row["baseAliasRevision"].IsInt64 || row["baseAliasRevision"].AsInt64 < 0 ||
            row["baseAliasRevision"].AsInt64 == long.MaxValue || !row["revision"].IsInt64 || row["revision"].AsInt64 != 1 ||
            !Positive(row, "proposedBy") || row["baseAliasRevision"].AsInt64 == 0 && !row["enabled"].AsBoolean) throw Invalid();
        _ = StockSourceSetupBson.Time(row["proposedAt"]);
    });
    internal static void Decision(BsonDocument row, BsonDocument proposal) => Guard(() =>
    {
        Header(row, "stockSourceAliasDecisions", 16); Metadata(row); Proposal(proposal);
        if (!Positive(row, "proposalId") || row["proposalId"] != proposal["_id"] ||
            !Positive(row, "poolId") || row["poolId"] != proposal["poolId"] ||
            !Positive(row, "poolRevision") || row["poolRevision"] != proposal["poolRevision"] || !row["requestedLocation"].Equals(proposal["requestedLocation"]) ||
            !row["revision"].IsInt64 || row["revision"] != proposal["revision"] || !Positive(row, "decidedBy") ||
            !StockSourceSetupPolicy.Text(row["reviewReference"].AsString, 160) ||
            StockSourceSetupBson.Time(row["decidedAt"]) < StockSourceSetupBson.Time(proposal["proposedAt"])) throw Invalid();
        var outcome = row["decision"].AsString;
        if (outcome is not ("approve" or "reject" or "withdraw") ||
            (outcome == "withdraw") != (row["decidedBy"] == proposal["proposedBy"]) ||
            (outcome == "approve" ? !Positive(row, "aliasRevision") ||
                row["aliasRevision"].AsInt64 != checked(proposal["baseAliasRevision"].AsInt64 + 1) :
                !row["aliasRevision"].IsBsonNull)) throw Invalid();
    });
    internal static void Alias(BsonDocument row, BsonDocument proposal, BsonDocument decision) => Guard(() =>
    {
        Header(row, "stockSourceAliases", 12); Binding(row); Decision(decision, proposal);
        if (decision["decision"] != "approve" || !Positive(row, "aliasRevision") ||
            row["aliasRevision"] != decision["aliasRevision"] || !Positive(row, "proposalId") ||
            row["proposalId"] != proposal["_id"] || !Positive(row, "decisionId") ||
            row["decisionId"] != decision["_id"]) throw Invalid();
        foreach (var name in BindingFields) if (!row[name].Equals(proposal[name])) throw Invalid();
    });
    internal static readonly string[] BindingFields = ["poolId", "poolRevision", "ownerLocation", "requestedLocation", "physicalKey", "enabled"];
    internal static BsonDocument Audit(BsonDocument row, ActorContext actor, string operation, DateTimeOffset at) => new()
    {
        { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", "stock.source.alias.audit" },
        { "operation", operation }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
        { "createdAt", at.UtcDateTime }, { "record", row.DeepClone() }
    };
    private static void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or KeyNotFoundException or FormatException or OverflowException)
        { throw Invalid(); }
    }
    internal static DomainValidationException Invalid() => new("stock_source_alias_unavailable", "The stock channel mapping history is unavailable");
}
