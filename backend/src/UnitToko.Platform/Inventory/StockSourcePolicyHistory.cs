using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    private sealed record OperatingPolicyHistory(IReadOnlyDictionary<long, BsonDocument> Views, BsonDocument? Latest)
    {
        public static OperatingPolicyHistory Copy(OperatingPolicyHistory value) =>
            new(value.Views.ToDictionary(item => item.Key, item => item.Value.DeepClone().AsBsonDocument), value.Latest?.DeepClone().AsBsonDocument);
    }
    // The whole policy history of a pool is re-proved for every held receipt and lot a stock decision touches; it is keyed by the
    // exact pool context it is proved against (see RememberedProofAsync).
    private Task<OperatingPolicyHistory> OperatingPolicyHistoryAsync(IClientSessionHandle tx, SuspensionContext source, CancellationToken ct) =>
        RememberedProofAsync(tx, "policy-history:" + StockSourceSuspensionBson.Hash(new BsonDocument {
                { "current", source.Current }, { "configuration", source.Configuration }, { "latest", (BsonValue?)source.Latest ?? BsonNull.Value } }),
            reader => reader.OperatingPolicyHistoryCoreAsync(tx, source, ct), OperatingPolicyHistory.Copy);
    private async Task<OperatingPolicyHistory> OperatingPolicyHistoryCoreAsync(IClientSessionHandle tx, SuspensionContext source, CancellationToken ct)
    {
        var poolId = source.Pool["_id"].AsInt64; var views = new Dictionary<long, BsonDocument>(); var accepted = new SortedDictionary<long, BsonDocument>();
        long decisions = 0;
        var captured = historyCapture is null ? null : new List<BsonDocument>();
        using var cursor = await Rows(StockSourcePolicyBson.Proposals).Find(tx, F.Eq("poolId", poolId)).Sort(new BsonDocument("_id", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var proposal in cursor.Current)
        {
            captured?.Add(proposal);
            StockSourcePolicyBson.Proposal(proposal);
            await RequireOperatingPolicyBindingAsync(tx, proposal, source, ct);
            await OperatingPolicyProvenanceAsync(tx, proposal, null, ct);
            var decision = await HistoryOneAsync(tx, StockSourcePolicyBson.Decisions, F.Eq("proposalId", proposal["_id"]), ct);
            BsonDocument? policy = null;
            if (decision is not null)
            {
                decisions++; StockSourcePolicyBson.Decision(decision, proposal);
                if (StockSourceSetupBson.Time(decision["decidedAt"]) > Now()) throw StockSourcePolicyBson.Invalid();
                await OperatingPolicyProvenanceAsync(tx, proposal, decision, ct);
                if (decision["decision"] == "approve")
                {
                    await RequireOperatingPolicyBindingAtAsync(tx, proposal, StockSourceSetupBson.Time(decision["decidedAt"]), ct);
                    policy = await HistoryOneAsync(tx, StockSourcePolicyBson.Policies, F.Eq("_id", decision["policyId"]), ct) ?? throw StockSourcePolicyBson.Invalid();
                    StockSourcePolicyBson.Policy(policy, proposal, decision);
                }
            }
            var view = new BsonDocument { { "proposal", proposal }, { "decision", (BsonValue?)decision ?? BsonNull.Value }, { "policy", (BsonValue?)policy ?? BsonNull.Value } };
            if (!views.TryAdd(proposal["_id"].AsInt64, view) || policy is not null && !accepted.TryAdd(policy["policyRevision"].AsInt64, view)) throw StockSourcePolicyBson.Invalid();
        }
        HistoryRows(StockSourcePolicyBson.Proposals, F.Eq("poolId", poolId), captured);
        long revision = 0; BsonDocument? previous = null;
        foreach (var (number, view) in accepted)
        {
            if (number != checked(++revision)) throw StockSourcePolicyBson.Invalid();
            if (previous is not null) StockSourcePolicyBson.AcceptedContinuity(
                previous["proposal"].AsBsonDocument, previous["decision"].AsBsonDocument, view["proposal"].AsBsonDocument);
            previous = view;
        }
        foreach (var view in views.Values)
        {
            var proposal = view["proposal"].AsBsonDocument; var baseRevision = proposal["basePolicyRevision"].AsInt64;
            if (baseRevision > revision || baseRevision > 0 && StockSourceSetupBson.Time(accepted[baseRevision]["decision"]["decidedAt"]) >
                StockSourceSetupBson.Time(proposal["proposedAt"])) throw StockSourcePolicyBson.Invalid();
            if (accepted.Values.Any(policy => policy["policy"]["policyRevision"].AsInt64 > baseRevision &&
                StockSourceSetupBson.Time(policy["decision"]["decidedAt"]) < StockSourceSetupBson.Time(proposal["proposedAt"]))) throw StockSourcePolicyBson.Invalid();
        }
        if (await HistoryCountAsync(tx, StockSourcePolicyBson.Decisions, F.Eq("poolId", poolId), ct) != decisions ||
            await HistoryCountAsync(tx, StockSourcePolicyBson.Policies, F.Eq("poolId", poolId), ct) != revision) throw StockSourcePolicyBson.Invalid();
        var auditFilter = F.Eq("record.poolId", poolId) & F.Or(F.Eq("kind", "stock.source.policy.audit"),
            F.In("record.kind", new[] { "stock.source.policy.proposal", "stock.source.policy.decision" }));
        var receiptFilter = F.Eq("result.poolId", poolId) & F.Or(F.In("operation", new[] {
            "stock.source.policy.propose", "stock.source.policy.approve", "stock.source.policy.reject", "stock.source.policy.withdraw" }),
            F.Exists("result.basePolicyRevision"), F.Exists("result.policyId"));
        if (await HistoryCountAsync(tx, "auditEvents", auditFilter, ct) != views.Count + decisions ||
            await HistoryCountAsync(tx, "idempotencyRecords", receiptFilter, ct) != views.Count + decisions) throw StockSourcePolicyBson.Invalid();
        return new(views, previous);
    }
    private async Task OperatingPolicyProvenanceAsync(IClientSessionHandle tx, BsonDocument proposal, BsonDocument? decision, CancellationToken ct)
    {
        var row = decision ?? proposal; var operation = StockSourcePolicyBson.Prefix + (decision?["decision"].AsString ?? "propose");
        var audit = await HistoryOneAsync(tx, "auditEvents", F.Eq("_id", row["auditId"]), ct);
        StockSourcePolicyBson.RequireAudit(audit, row, operation, row[decision is null ? "proposedBy" : "decidedBy"], row[decision is null ? "proposedAt" : "decidedAt"]);
        var filter = F.Eq("operation", operation) & F.Eq("result.poolId", proposal["poolId"]) & F.Eq("result.proposalId", proposal["_id"]);
        var receipts = await Rows("idempotencyRecords").Find(tx, filter).Limit(2).ToListAsync(ct);
        HistoryRows("idempotencyRecords", filter, receipts);
        if (receipts.Count != 1) throw StockSourcePolicyBson.Invalid();
        StockSourcePolicyBson.Receipt(receipts[0], proposal, decision);
    }
    private async Task RequireOperatingPolicyBindingAsync(IClientSessionHandle tx, BsonDocument proposal, SuspensionContext current, CancellationToken ct)
    {
        var at = StockSourceSetupBson.Time(proposal["proposedAt"]); var config = proposal["configuration"].AsBsonDocument;
        var source = config["source"].AsBsonDocument;
        if (at > Now() || proposal["poolId"] != current.Pool["_id"] || proposal["poolRevision"].AsInt64 > current.Pool["revision"].AsInt64 ||
            proposal["suspensionRevision"].AsInt64 > (current.Latest?["suspensionRevision"].AsInt64 ?? 0) ||
            source["creationProposalId"] != current.Current["creationProposalId"]) throw StockSourcePolicyBson.Invalid();
        if (proposal["poolRevision"] == 1L)
        {
            var original = await ViewAsync(tx, await ProposalAsync(tx, source["creationProposalId"].AsInt64, ct), ct);
            if (original["decision"]["poolId"] != proposal["poolId"] || !original["proposal"]["input"].Equals(source["input"]) ||
                original["decision"]["decidedAt"] != source["approvedAt"]) throw StockSourcePolicyBson.Invalid();
        }
        else
        {
            var view = await RevisionViewAsync(tx, source["revisionProposalId"].AsInt64, ct);
            if (view["appliedRevision"].IsBsonNull || view["appliedRevision"]["poolId"] != proposal["poolId"] ||
                view["appliedRevision"]["poolRevision"] != proposal["poolRevision"] || !view["appliedRevision"]["input"].Equals(source["input"]) ||
                view["decision"]["decidedAt"] != source["approvedAt"]) throw StockSourcePolicyBson.Invalid();
        }
        if (proposal["suspensionRevision"].AsInt64 > 0)
        {
            var stop = await HistoryOneAsync(tx, StockSourceSuspensionSchemas.Collection, F.Eq("poolId", proposal["poolId"]) &
                F.Eq("suspensionRevision", proposal["suspensionRevision"]), ct) ?? throw StockSourcePolicyBson.Invalid();
            if (StockSourceSetupBson.Time(stop["suspendedAt"]) > at) throw StockSourcePolicyBson.Invalid();
        }

        var mappings = config["mappings"].AsBsonArray.Select(value => value.AsBsonDocument).ToArray();
        foreach (var mapping in mappings)
        {
            var view = await AliasViewAsync(tx, mapping["proposalId"].AsInt64, ct);
            if (view["alias"].IsBsonNull) throw StockSourcePolicyBson.Invalid();
            var alias = view["alias"].AsBsonDocument;
            foreach (var field in new[] { "aliasRevision", "poolRevision", "requestedLocation", "enabled", "proposalId", "decisionId" })
                if (!alias[field].Equals(mapping[field])) throw StockSourcePolicyBson.Invalid();
            if (alias["_id"] != mapping["aliasId"] || alias["poolId"] != proposal["poolId"] ||
                StockSourceSetupBson.Time(view["decision"]["decidedAt"]) > at) throw StockSourcePolicyBson.Invalid();
        }
        await RequireOperatingPolicyBindingAtAsync(tx, proposal, at, ct);
    }
    private async Task RequireOperatingPolicyBindingAtAsync(IClientSessionHandle tx, BsonDocument proposal, DateTimeOffset at, CancellationToken ct)
    {
        if (await HistoryAnyAsync(tx, "stockSourceRevisionDecisions", F.Eq("poolId", proposal["poolId"]) & F.Eq("decision", "approve") &
            F.Gt("poolRevision", proposal["poolRevision"]) & F.Lt("decidedAt", at.UtcDateTime), ct) ||
            await HistoryAnyAsync(tx, StockSourceSuspensionSchemas.Collection, F.Eq("poolId", proposal["poolId"]) &
                F.Gt("suspensionRevision", proposal["suspensionRevision"]) & F.Lt("suspendedAt", at.UtcDateTime), ct)) throw StockSourcePolicyBson.Invalid();
        var mappings = proposal["configuration"]["mappings"].AsBsonArray.Select(value => value.AsBsonDocument).ToArray();
        // Strictly earlier approvals must be represented at proposal AND acceptance. Equal timestamps do not invent a commit order.
        var filter = F.Eq("poolId", proposal["poolId"]) & F.Eq("decision", "approve") & F.Lt("decidedAt", at.UtcDateTime);
        var captured = historyCapture is null ? null : new List<BsonDocument>();
        using var cursor = await Rows("stockSourceAliasDecisions").Find(tx, filter).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current)
        {
            captured?.Add(row);
            var mapping = mappings.SingleOrDefault(item => item["requestedLocation"].Equals(row["requestedLocation"]));
            if (mapping is null || mapping["aliasRevision"].AsInt64 < row["aliasRevision"].AsInt64) throw StockSourcePolicyBson.Invalid();
        }
        HistoryRows("stockSourceAliasDecisions", filter, captured);
    }
}
