using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Platform.Inventory;

internal static class StockSourceRevisionBson
{
    internal static string Kind(string name) => name switch
    {
        "stockSourceRevisionProposals" => "stock.source.revision.proposal",
        "stockSourceRevisionDecisions" => "stock.source.revision.decision",
        "stockSourceRevisions" => "stock.source.revision",
        _ => throw Invalid()
    };
    internal static BsonDocument Header(string name, long id) => new()
        { { "_id", id }, { "schemaVersion", 1 }, { "kind", Kind(name) } };
    private static bool Positive(BsonDocument row, string name) => row[name].IsInt64 && row[name].AsInt64 > 0;
    private static void Header(BsonDocument row, string collection, int count)
    {
        if (row.ElementCount != count || row.Names.Distinct(StringComparer.Ordinal).Count() != count ||
            !Positive(row, "_id") || !row["schemaVersion"].IsInt32 || row["schemaVersion"].AsInt32 != 1 || row["kind"] != Kind(collection))
            throw Invalid();
    }
    private static void Metadata(BsonDocument row)
    {
        if (!Positive(row, "auditId") || !StockSourceSetupPolicy.Text(row["reason"].AsString, 1000) ||
            !StockSourceSetupPolicy.Text(row["correlationId"].AsString, 128)) throw Invalid();
    }
    internal static void Proposal(BsonDocument row)
    {
        Guard(() =>
        {
            Header(row, "stockSourceRevisionProposals", 13); Metadata(row);
            if (!Positive(row, "poolId") || !Positive(row, "basePoolRevision") || row["basePoolRevision"].AsInt64 == long.MaxValue ||
                !row["revision"].IsInt64 || row["revision"].AsInt64 != 1 || !Positive(row, "proposedBy")) throw Invalid();
            _ = StockSourceSetupBson.Time(row["proposedAt"]);
            StockSourceRevisionPolicy.Validate(StockSourceSetupBson.ReadInput(row["baseInput"].AsBsonDocument),
                StockSourceSetupBson.ReadInput(row["input"].AsBsonDocument));
        });
    }
    internal static void Decision(BsonDocument row, BsonDocument proposal)
    {
        Guard(() =>
        {
            Header(row, "stockSourceRevisionDecisions", 15); Metadata(row); Proposal(proposal);
            if (!Positive(row, "proposalId") || row["proposalId"] != proposal["_id"] ||
                !Positive(row, "poolId") || row["poolId"] != proposal["poolId"] ||
                !Positive(row, "basePoolRevision") || row["basePoolRevision"] != proposal["basePoolRevision"] ||
                !row["revision"].IsInt64 || row["revision"] != proposal["revision"] || !Positive(row, "decidedBy") ||
                !StockSourceSetupPolicy.Text(row["reviewReference"].AsString, 160) ||
                StockSourceSetupBson.Time(row["decidedAt"]) < StockSourceSetupBson.Time(proposal["proposedAt"])) throw Invalid();
            var decision = row["decision"].AsString;
            if (decision is not ("approve" or "reject" or "withdraw") ||
                (decision == "withdraw") != (row["decidedBy"] == proposal["proposedBy"]) ||
                (decision == "approve" ? !Positive(row, "poolRevision") || row["poolRevision"].AsInt64 != checked(proposal["basePoolRevision"].AsInt64 + 1) :
                    !row["poolRevision"].IsBsonNull)) throw Invalid();
        });
    }
    internal static void Revision(BsonDocument row, BsonDocument proposal, BsonDocument decision)
    {
        Guard(() =>
        {
            Header(row, "stockSourceRevisions", 8); Decision(decision, proposal);
            if (decision["decision"] != "approve" || !Positive(row, "poolId") || row["poolId"] != proposal["poolId"] ||
                !Positive(row, "poolRevision") || row["poolRevision"] != decision["poolRevision"] ||
                !Positive(row, "proposalId") || row["proposalId"] != proposal["_id"] ||
                !Positive(row, "decisionId") || row["decisionId"] != decision["_id"] ||
                !row["input"].Equals(proposal["input"])) throw Invalid();
        });
    }
    internal static BsonDocument Audit(BsonDocument row, ActorContext actor, string operation, DateTimeOffset at) => new()
    {
        { "_id", row["auditId"].AsInt64 }, { "schemaVersion", 1 }, { "kind", "stock.source.revision.audit" },
        { "operation", operation }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
        { "createdAt", at.UtcDateTime }, { "record", row.DeepClone() }
    };
    private static void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or KeyNotFoundException or FormatException or OverflowException)
        { throw Invalid(); }
    }
    internal static DomainValidationException Invalid() => new("stock_source_revision_unavailable", "Source revision history is not usable");
}
