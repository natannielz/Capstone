using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Inventory;

internal static class StockSourcePolicyBson
{
    internal const string Proposals = "stockSourcePolicyProposals", Decisions = "stockSourcePolicyDecisions", Policies = "stockSourcePolicies";
    internal const string Prefix = "stock.source.policy.";
    internal static string Kind(string collection) => collection switch
    {
        Proposals => Prefix + "proposal", Decisions => Prefix + "decision", Policies => "stock.source.policy", _ => throw Invalid()
    };
    internal static BsonDocument Header(string collection, long id) => new()
        { { "_id", id }, { "schemaVersion", 1 }, { "kind", Kind(collection) } };
    internal static BsonDocument Input(StockSourceOperatingPolicyInput input) => new()
    {
        { "writer", input.Writer == StockMovementWriter.UnitToko ? "unitToko" : "externalSystem" },
        { "externalTenant", (BsonValue?)input.ExternalTenant ?? BsonNull.Value }, { "feedIntervalMilliseconds", input.FeedIntervalMilliseconds },
        { "reconciliationProcedureReference", input.ReconciliationProcedureReference }, { "outageProcedureReference", input.OutageProcedureReference }
    };
    internal static StockSourceOperatingPolicyInput ReadInput(BsonDocument row, StockSourceSetupInput source)
    {
        StockSourceOperatingPolicyInput? result = null;
        Guard(() =>
        {
            Exact(row, "writer", "externalTenant", "feedIntervalMilliseconds", "reconciliationProcedureReference", "outageProcedureReference");
            if (!row["feedIntervalMilliseconds"].IsInt64) throw Invalid();
            result = new(row["writer"].AsString switch
                { "unitToko" => StockMovementWriter.UnitToko, "externalSystem" => StockMovementWriter.ExternalSystem, _ => throw Invalid() },
                row["externalTenant"].IsBsonNull ? null : row["externalTenant"].AsString, row["feedIntervalMilliseconds"].AsInt64,
                row["reconciliationProcedureReference"].AsString, row["outageProcedureReference"].AsString);
            StockSourceOperatingPolicyRules.Validate(result, source);
        });
        return result!;
    }
    internal static StockSourcePolicyExpectation Expected(BsonDocument proposal) => new(proposal["poolRevision"].AsInt64,
        proposal["suspensionRevision"].AsInt64, proposal["basePolicyRevision"].AsInt64, proposal["configurationHash"].AsString);
    internal static StockSourceSetupInput Source(BsonDocument proposal) =>
        StockSourceSetupBson.ReadInput(proposal["configuration"]["source"]["input"].AsBsonDocument);
    internal static void Proposal(BsonDocument row) => Guard(() =>
    {
        Exact(row, "_id", "schemaVersion", "kind", "poolId", "poolRevision", "suspensionRevision", "basePolicyRevision",
            "configuration", "configurationHash", "input", "revision", "proposedBy", "proposedAt", "auditId", "reason", "correlationId");
        RequireHeader(row, Proposals); Metadata(row);
        if (!Positive(row, "poolId") || !Positive(row, "poolRevision") || !Nonnegative(row, "suspensionRevision") ||
            !Nonnegative(row, "basePolicyRevision") || !Positive(row, "revision") || row["revision"] != 1L || !Positive(row, "proposedBy")) throw Invalid();
        Expected(row).Validate();
        var at = StockSourceSetupBson.Time(row["proposedAt"]);
        var config = row["configuration"].AsBsonDocument; Exact(config, "source", "mappings");
        if (StockSourceSuspensionBson.Hash(config) != row["configurationHash"].AsString) throw Invalid();
        var source = config["source"].AsBsonDocument;
        Exact(source, "poolRevision", "active", "input", "approvedAt", "creationProposalId", "revisionProposalId");
        if (!Positive(source, "poolRevision") || source["poolRevision"] != row["poolRevision"] || !source["active"].IsBoolean ||
            source["active"].AsBoolean || !Positive(source, "creationProposalId") ||
            (row["poolRevision"] == 1L ? !source["revisionProposalId"].IsBsonNull : !Positive(source, "revisionProposalId")) ||
            StockSourceSetupBson.Time(source["approvedAt"]) > at) throw Invalid();
        var settings = Source(row);
        ReadInput(row["input"].AsBsonDocument, settings);
        long previous = 0;
        foreach (var value in config["mappings"].AsBsonArray)
        {
            var mapping = value.AsBsonDocument;
            Exact(mapping, "aliasId", "aliasRevision", "poolRevision", "requestedLocation", "enabled", "proposalId", "decisionId");
            if (!Positive(mapping, "aliasId") || !Positive(mapping, "aliasRevision") || !Positive(mapping, "poolRevision") ||
                mapping["poolRevision"].AsInt64 > row["poolRevision"].AsInt64 || !Positive(mapping, "proposalId") || !Positive(mapping, "decisionId") ||
                !mapping["enabled"].IsBoolean || mapping["enabled"].AsBoolean && mapping["poolRevision"] != row["poolRevision"]) throw Invalid();
            var location = InventoryGraph.Location(mapping["requestedLocation"].AsBsonDocument);
            StockSourceAliasPolicy.Validate(settings.Location, location);
            if (location.ChannelId.Value <= previous) throw Invalid();
            previous = location.ChannelId.Value;
        }
    });
    // Accepted versions prove ordering even when database timestamps are equal.
    internal static void AcceptedContinuity(BsonDocument previous, BsonDocument decision, BsonDocument current) => Guard(() =>
    {
        if (StockSourceSetupBson.Time(decision["decidedAt"]) > StockSourceSetupBson.Time(current["proposedAt"]) || previous["poolRevision"].AsInt64 > current["poolRevision"].AsInt64 ||
            previous["suspensionRevision"].AsInt64 > current["suspensionRevision"].AsInt64) throw Invalid();
        if (previous["configurationHash"] == current["configurationHash"] && previous["suspensionRevision"] == current["suspensionRevision"] &&
            previous["input"].Equals(current["input"])) throw Invalid();
        var mappings = current["configuration"]["mappings"].AsBsonArray.Select(value => value.AsBsonDocument)
            .ToDictionary(value => value["requestedLocation"]["channelId"].AsInt64);
        foreach (var value in previous["configuration"]["mappings"].AsBsonArray)
            if (!mappings.TryGetValue(value["requestedLocation"]["channelId"].AsInt64, out var mapping) ||
                mapping["aliasRevision"].AsInt64 < value["aliasRevision"].AsInt64) throw Invalid();
    });
    internal static void Decision(BsonDocument row, BsonDocument proposal) => Guard(() =>
    {
        Exact(row, "_id", "schemaVersion", "kind", "poolId", "proposalId", "revision", "decision", "policyId", "policyRevision",
            "decidedBy", "decidedAt", "reviewReference", "auditId", "reason", "correlationId");
        RequireHeader(row, Decisions); Metadata(row); Proposal(proposal);
        if (!Positive(row, "poolId") || row["poolId"] != proposal["poolId"] || !Positive(row, "proposalId") ||
            row["proposalId"] != proposal["_id"] || !Positive(row, "revision") || row["revision"] != proposal["revision"] ||
            !Positive(row, "decidedBy") || !StockSourceSetupPolicy.Text(row["reviewReference"].AsString, 160) ||
            StockSourceSetupBson.Time(row["decidedAt"]) < StockSourceSetupBson.Time(proposal["proposedAt"])) throw Invalid();
        var decision = row["decision"].AsString;
        if (decision is not ("approve" or "reject" or "withdraw") ||
            (decision == "withdraw") != (row["decidedBy"] == proposal["proposedBy"]) ||
            (decision == "approve" ? !Positive(row, "policyId") || !Positive(row, "policyRevision") ||
                row["policyRevision"].AsInt64 != checked(proposal["basePolicyRevision"].AsInt64 + 1) :
                !row["policyId"].IsBsonNull || !row["policyRevision"].IsBsonNull)) throw Invalid();
    });
    internal static void Policy(BsonDocument row, BsonDocument proposal, BsonDocument decision) => Guard(() =>
    {
        Exact(row, "_id", "schemaVersion", "kind", "poolId", "policyRevision", "proposalId", "decisionId");
        RequireHeader(row, Policies); Decision(decision, proposal);
        if (decision["decision"] != "approve" || row["_id"] != decision["policyId"] || !Positive(row, "poolId") || row["poolId"] != proposal["poolId"] ||
            !Positive(row, "policyRevision") || row["policyRevision"] != decision["policyRevision"] || !Positive(row, "proposalId") ||
            row["proposalId"] != proposal["_id"] || !Positive(row, "decisionId") || row["decisionId"] != decision["_id"]) throw Invalid();
    });
    internal static BsonDocument ProposalResult(BsonDocument proposal) => new()
        { { "proposalId", proposal["_id"] }, { "revision", 1L }, { "poolId", proposal["poolId"] }, { "basePolicyRevision", proposal["basePolicyRevision"] } };
    internal static BsonDocument DecisionResult(BsonDocument decision) => new()
        { { "proposalId", decision["proposalId"] }, { "decisionId", decision["_id"] }, { "poolId", decision["poolId"] },
          { "policyId", decision["policyId"] }, { "policyRevision", decision["policyRevision"] }, { "status", decision["decision"] } };
    internal static BsonDocument Audit(BsonDocument row, ActorContext actor, string operation, DateTimeOffset at) => new()
    {
        { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", "stock.source.policy.audit" },
        { "operation", operation }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
        { "createdAt", at.UtcDateTime }, { "record", row.DeepClone() }
    };
    internal static void RequireAudit(BsonDocument? audit, BsonDocument row, string operation, BsonValue actor, BsonValue at) => Guard(() =>
    {
        if (audit is null) throw Invalid();
        Exact(audit, "_id", "schemaVersion", "kind", "operation", "actorId", "actorGrantVersion", "createdAt", "record");
        if (audit["_id"] != row["auditId"] || !audit["schemaVersion"].IsInt32 || audit["schemaVersion"] != 1 || audit["kind"] != "stock.source.policy.audit" ||
            audit["operation"] != operation || audit["actorId"] != actor || !Positive(audit, "actorGrantVersion") ||
            audit["createdAt"] != at || !audit["record"].Equals(row)) throw Invalid();
    });
    internal static void Receipt(BsonDocument receipt, BsonDocument proposal, BsonDocument? decision) => Guard(() =>
    {
        var needsParticipants = decision is not null && decision["decision"].AsString is "approve" or "reject";
        string[] fields = ["_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "result", "createdAt"];
        Exact(receipt, needsParticipants ? [.. fields, PersonApprovalReceipt.Field] : fields);
        if (needsParticipants && !receipt[PersonApprovalReceipt.Field].Equals(PersonApprovalReceipt.Create(
            Permissions.StockSourcePolicyApprove, new ApprovalRequest(1, 1, ProposedBy: proposal["proposedBy"].AsInt64))))
            throw Invalid();
        var actor = decision?["decidedBy"] ?? proposal["proposedBy"];
        var operation = Prefix + (decision?["decision"].AsString ?? "propose");
        var result = decision is null ? ProposalResult(proposal) : DecisionResult(decision);
        object body = decision is null
            ? new { poolId = proposal["poolId"].AsInt64, expected = Expected(proposal), input = ReadInput(proposal["input"].AsBsonDocument, Source(proposal)) }
            : new { proposalId = proposal["_id"].AsInt64, expectedRevision = 1L,
                decision = Enum.Parse<StockSourceSetupDecision>(decision["decision"].AsString, true), reviewReference = decision["reviewReference"].AsString };
        var row = decision ?? proposal;
        if (!receipt["_id"].IsObjectId || !receipt["schemaVersion"].IsInt32 || receipt["schemaVersion"] != 1 || !Positive(receipt, "actorId") ||
            receipt["actorId"] != actor || receipt["operation"] != operation || !StockSourceSetupPolicy.Text(receipt["key"].AsString, 200) ||
            receipt["bodyHash"] != AuthorizationStore.CommandHash(body, row["reason"].AsString) || !receipt["result"].Equals(result) ||
            StockSourceSetupBson.Time(receipt["createdAt"]) > StockSourceSetupBson.Time(row[decision is null ? "proposedAt" : "decidedAt"])) throw Invalid();
    });
    internal static bool Positive(BsonDocument row, string field) => row[field].IsInt64 && row[field].AsInt64 > 0;
    private static bool Nonnegative(BsonDocument row, string field) => row[field].IsInt64 && row[field].AsInt64 >= 0;
    private static void RequireHeader(BsonDocument row, string collection)
    {
        if (!Positive(row, "_id") || !row["schemaVersion"].IsInt32 || row["schemaVersion"] != 1 || row["kind"] != Kind(collection)) throw Invalid();
    }
    private static void Metadata(BsonDocument row)
    {
        if (!Positive(row, "auditId") || !StockSourceSetupPolicy.Text(row["reason"].AsString, 1000) ||
            !StockSourceSetupPolicy.Text(row["correlationId"].AsString, 128)) throw Invalid();
    }
    internal static void Exact(BsonDocument row, params string[] names)
    {
        if (row.ElementCount != names.Length || row.Names.Distinct(StringComparer.Ordinal).Count() != names.Length || !names.All(row.Contains)) throw Invalid();
    }
    internal static void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or KeyNotFoundException or FormatException or OverflowException)
        { throw Invalid(); }
    }
    internal static DomainValidationException Invalid() => new("stock_source_policy_unavailable", "The stock operating-policy history is unavailable");
}
