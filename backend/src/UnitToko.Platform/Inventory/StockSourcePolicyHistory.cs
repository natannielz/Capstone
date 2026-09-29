using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    private sealed record OperatingPolicyHistory(IReadOnlyDictionary<long, BsonDocument> Views, BsonDocument? Latest);
    private async Task<OperatingPolicyHistory> OperatingPolicyHistoryAsync(IClientSessionHandle tx, SuspensionContext source, CancellationToken ct)
    {
        var poolId = source.Pool["_id"].AsInt64; var views = new Dictionary<long, BsonDocument>(); var accepted = new SortedDictionary<long, BsonDocument>();
        long decisions = 0;
        using var cursor = await Rows(StockSourcePolicyBson.Proposals).Find(tx, F.Eq("poolId", poolId)).Sort(new BsonDocument("_id", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var proposal in cursor.Current)
        {
            StockSourcePolicyBson.Proposal(proposal);
            await RequireOperatingPolicyBindingAsync(tx, proposal, source, ct);
            await OperatingPolicyProvenanceAsync(tx, proposal, null, ct);
            var decision = await Rows(StockSourcePolicyBson.Decisions).Find(tx, F.Eq("proposalId", proposal["_id"])).SingleOrDefaultAsync(ct);
            BsonDocument? policy = null;
            if (decision is not null)
            {
                decisions++; StockSourcePolicyBson.Decision(decision, proposal);
                if (StockSourceSetupBson.Time(decision["decidedAt"]) > Now()) throw StockSourcePolicyBson.Invalid();
                await OperatingPolicyProvenanceAsync(tx, proposal, decision, ct);
                if (decision["decision"] == "approve")
                {
                    await RequireOperatingPolicyBindingAtAsync(tx, proposal, StockSourceSetupBson.Time(decision["decidedAt"]), ct);
                    policy = await Rows(StockSourcePolicyBson.Policies).Find(tx, F.Eq("_id", decision["policyId"])).SingleOrDefaultAsync(ct) ?? throw StockSourcePolicyBson.Invalid();
                    StockSourcePolicyBson.Policy(policy, proposal, decision);
                }
            }
            var view = new BsonDocument { { "proposal", proposal }, { "decision", (BsonValue?)decision ?? BsonNull.Value }, { "policy", (BsonValue?)policy ?? BsonNull.Value } };
            if (!views.TryAdd(proposal["_id"].AsInt64, view) || policy is not null && !accepted.TryAdd(policy["policyRevision"].AsInt64, view)) throw StockSourcePolicyBson.Invalid();
        }
        long revision = 0; BsonDocument? previous = null;
        foreach (var (number, view) in accepted)
        {
            if (number != checked(++revision) || previous is not null &&
                (StockSourceSetupBson.Time(previous["decision"]["decidedAt"]) > StockSourceSetupBson.Time(view["proposal"]["proposedAt"]) ||
                previous["proposal"]["poolRevision"].AsInt64 > view["proposal"]["poolRevision"].AsInt64 ||
                previous["proposal"]["suspensionRevision"].AsInt64 > view["proposal"]["suspensionRevision"].AsInt64)) throw StockSourcePolicyBson.Invalid();
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
        if (await Rows(StockSourcePolicyBson.Decisions).CountDocumentsAsync(tx, F.Eq("poolId", poolId), cancellationToken: ct) != decisions ||
            await Rows(StockSourcePolicyBson.Policies).CountDocumentsAsync(tx, F.Eq("poolId", poolId), cancellationToken: ct) != revision) throw StockSourcePolicyBson.Invalid();
        var auditFilter = F.Eq("record.poolId", poolId) & F.Or(F.Eq("kind", "stock.source.policy.audit"),
            F.In("record.kind", new[] { "stock.source.policy.proposal", "stock.source.policy.decision" }));
        var receiptFilter = F.Eq("result.poolId", poolId) & F.Or(F.In("operation", new[] {
            "stock.source.policy.propose", "stock.source.policy.approve", "stock.source.policy.reject", "stock.source.policy.withdraw" }),
            F.Exists("result.basePolicyRevision"), F.Exists("result.policyId"));
        if (await Rows("auditEvents").CountDocumentsAsync(tx, auditFilter, cancellationToken: ct) != views.Count + decisions ||
            await Rows("idempotencyRecords").CountDocumentsAsync(tx, receiptFilter, cancellationToken: ct) != views.Count + decisions) throw StockSourcePolicyBson.Invalid();
        return new(views, previous);
    }
    private async Task OperatingPolicyProvenanceAsync(IClientSessionHandle tx, BsonDocument proposal, BsonDocument? decision, CancellationToken ct)
    {
        var row = decision ?? proposal; var operation = StockSourcePolicyBson.Prefix + (decision?["decision"].AsString ?? "propose");
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        StockSourcePolicyBson.RequireAudit(audit, row, operation, row[decision is null ? "proposedBy" : "decidedBy"], row[decision is null ? "proposedAt" : "decidedAt"]);
        var receipts = await Rows("idempotencyRecords").Find(tx, F.Eq("operation", operation) & F.Eq("result.poolId", proposal["poolId"]) &
            F.Eq("result.proposalId", proposal["_id"])).Limit(2).ToListAsync(ct);
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
            var stop = await Rows(StockSourceSuspensionSchemas.Collection).Find(tx, F.Eq("poolId", proposal["poolId"]) &
                F.Eq("suspensionRevision", proposal["suspensionRevision"])).SingleOrDefaultAsync(ct) ?? throw StockSourcePolicyBson.Invalid();
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
        if (await Rows("stockSourceRevisionDecisions").Find(tx, F.Eq("poolId", proposal["poolId"]) & F.Eq("decision", "approve") &
            F.Gt("poolRevision", proposal["poolRevision"]) & F.Lt("decidedAt", at.UtcDateTime)).AnyAsync(ct) ||
            await Rows(StockSourceSuspensionSchemas.Collection).Find(tx, F.Eq("poolId", proposal["poolId"]) &
                F.Gt("suspensionRevision", proposal["suspensionRevision"]) & F.Lt("suspendedAt", at.UtcDateTime)).AnyAsync(ct)) throw StockSourcePolicyBson.Invalid();
        var mappings = proposal["configuration"]["mappings"].AsBsonArray.Select(value => value.AsBsonDocument).ToArray();
        // Strictly earlier approvals must be represented at proposal AND acceptance. Equal timestamps do not invent a commit order.
        using var cursor = await Rows("stockSourceAliasDecisions").Find(tx, F.Eq("poolId", proposal["poolId"]) &
            F.Eq("decision", "approve") & F.Lt("decidedAt", at.UtcDateTime)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current)
        {
            var mapping = mappings.SingleOrDefault(item => item["requestedLocation"].Equals(row["requestedLocation"]));
            if (mapping is null || mapping["aliasRevision"].AsInt64 < row["aliasRevision"].AsInt64) throw StockSourcePolicyBson.Invalid();
        }
    }
}
