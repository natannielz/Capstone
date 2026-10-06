using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Inventory.StockSourceActivationBson;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    /// <summary>Called by the authenticated configuration owner, without calling it
    /// recursively. An active Boolean alone is never accepted source authority.</summary>
    private async Task RequireActivationPoolProvenanceAsync(IClientSessionHandle tx, BsonDocument current, CancellationToken ct) {
        await ActivationOwnerStorageAsync(ct);
        var latest = await ActivationHistoryAsync(tx, current, ct); var pool = current["pool"].AsBsonDocument;
        if (latest is null) {
            if (pool["active"].AsBoolean) throw B.Unavailable(); return;
        }
        var expected = B.ReadExpected(latest["expected"].AsBsonDocument);
        var stop = await LatestSuspensionAsync(tx, pool["_id"].AsInt64, current, ct);
        var suspensionRevision = stop?["suspensionRevision"].AsInt64 ?? 0;
        if (suspensionRevision < expected.SuspensionRevision || pool["fence"].AsInt64 < latest["committedPoolFence"].AsInt64) throw B.Unavailable();
        if (pool["active"].AsBoolean) {
            if (suspensionRevision != expected.SuspensionRevision || pool["revision"].AsInt64 != expected.PoolRevision) throw B.Unavailable();
            var aliases = new List<BsonDocument>();
            using var cursor = await Rows("stockSourceAliases").Find(tx, F.Eq("poolId", pool["_id"]))
                .Project(new BsonDocument("requestedLocation", 1)).ToCursorAsync(ct);
            var locations = new HashSet<StockLocation>();
            while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current) locations.Add(InventoryGraph.Location(row["requestedLocation"].AsBsonDocument));
            foreach (var location in locations.OrderBy(value => value.ChannelId.Value)) {
                var alias = await LatestAliasAsync(tx, pool["_id"].AsInt64, location, ct) ?? throw B.Unavailable(); aliases.Add(alias["alias"].AsBsonDocument);
            }
            var config = StockSourceSuspensionBson.Configuration(current, aliases); config["source"]["active"] = false;
            if (B.Hash(config) != expected.ConfigurationHash) throw B.Unavailable();
        } else if (suspensionRevision <= expected.SuspensionRevision || stop is null ||
            StockSourceSetupBson.Time(stop["suspendedAt"]) < StockSourceSetupBson.Time(latest["recordedAt"])) throw B.Unavailable();
    }

    internal async Task RequireActivatedPoolAsync(IClientSessionHandle tx, BsonDocument pool, CancellationToken ct) {
        if (!pool["active"].AsBoolean) throw B.Unavailable();
        var current = await CurrentSourceAsync(tx, pool["_id"].AsInt64, ct);
        if (current["pool"] != pool) throw B.Changed();
        await RequireActivationPoolProvenanceAsync(tx, current, ct);
    }

    private async Task<BsonDocument?> ActivationHistoryAsync(IClientSessionHandle tx, BsonDocument current, CancellationToken ct) {
        var pool = current["pool"].AsBsonDocument; var poolId = pool["_id"].AsInt64; long proposalCount = 0, decisionCount = 0, activationCount = 0;
        var accepted = new SortedDictionary<long, BsonDocument>(); var referenced = new List<BsonDocument>();
        using var cursor = await Rows(B.Proposals).Find(tx, F.Eq("poolId", poolId)).Sort(new BsonDocument("_id", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var raw in cursor.Current) {
            proposalCount++; var proposal = await ActivationProposalAsync(tx, raw["_id"].AsInt64, ct);
            await ActivationPolicyBindingAsync(tx, proposal, current, ct);
            var decision = await ActivationDecisionAsync(tx, proposal, ct); if (decision is null) continue;
            decisionCount++; if (decision["decision"] != "approve") continue;
            var activation = await Rows(B.Activations).Find(tx, F.Eq("_id", decision["activationId"])).SingleOrDefaultAsync(ct) ?? throw B.Unavailable();
            B.Exact(activation, "_id", "schemaVersion", "kind", "poolId", "activationRevision", "proposalId", "decisionId", "proposalHash", "decisionHash",
                "expected", "committedPoolFence", "recordedAt", "snapshotHash"); B.RequireSeal(activation);
            if (activation["kind"] != "stock.source.activation" || activation["_id"] != proposal["_id"] || activation["poolId"] != proposal["poolId"] ||
                activation["activationRevision"] != decision["activationRevision"] || activation["proposalId"] != proposal["_id"] ||
                activation["decisionId"] != decision["_id"] || activation["proposalHash"] != proposal["snapshotHash"] ||
                activation["decisionHash"] != decision["snapshotHash"] || activation["expected"] != decision["expected"] ||
                activation["committedPoolFence"] != decision["committedPoolFence"] || activation["recordedAt"] != decision["recordedAt"] ||
                !accepted.TryAdd(activation["activationRevision"].AsInt64, activation)) throw B.Unavailable();
            referenced.Add(proposal); activationCount++;
        }
        BsonDocument? latest = null; long number = 0;
        foreach (var (revision, activation) in accepted) {
            var expected = B.ReadExpected(activation["expected"].AsBsonDocument);
            if (revision != checked(++number) || expected.ActivationRevision != revision - 1 || expected.PoolRevision > pool["revision"].AsInt64 ||
                expected.PoolFence >= activation["committedPoolFence"].AsInt64 ||
                StockSourceSetupBson.Time(activation["recordedAt"]) > Now()) throw B.Unavailable();
            if (latest is not null) {
                var before = B.ReadExpected(latest["expected"].AsBsonDocument);
                var proposal = referenced.Single(row => row["_id"] == activation["proposalId"]);
                if (expected.SuspensionRevision <= before.SuspensionRevision || expected.PoolRevision < before.PoolRevision ||
                    expected.PoolFence < latest["committedPoolFence"].AsInt64 ||
                    StockSourceSetupBson.Time(latest["recordedAt"]) > StockSourceSetupBson.Time(proposal["recordedAt"])) throw B.Unavailable();
            }
            latest = activation;
        }
        foreach (var proposal in referenced) {
            var expected = B.ReadExpected(proposal["expected"].AsBsonDocument);
            if (expected.ActivationRevision > 0 && (!accepted.TryGetValue(expected.ActivationRevision, out var previous) ||
                StockSourceSetupBson.Time(previous["recordedAt"]) > StockSourceSetupBson.Time(proposal["recordedAt"]))) throw B.Unavailable();
        }
        if (await Rows(B.Decisions).CountDocumentsAsync(tx, F.Eq("poolId", poolId), cancellationToken: ct) != decisionCount ||
            await Rows(B.Activations).CountDocumentsAsync(tx, F.Eq("poolId", poolId), cancellationToken: ct) != activationCount) throw B.Unavailable();
        var operations = new[] { B.Propose, B.Decide };
        if (await Rows("auditEvents").CountDocumentsAsync(tx, F.Eq("kind", "stock.source.activation.audit") & F.Eq("record.poolId", poolId), cancellationToken: ct) != proposalCount + decisionCount ||
            await Rows("idempotencyRecords").CountDocumentsAsync(tx, F.In("operation", operations) & F.Eq("result.poolId", poolId), cancellationToken: ct) != proposalCount + decisionCount ||
            await Rows("outboxEvents").CountDocumentsAsync(tx, F.In("kind", operations) & F.Eq("poolId", poolId), cancellationToken: ct) != proposalCount + decisionCount) throw B.Unavailable();
        return latest;
    }
    private async Task<BsonDocument> ActivationProposalAsync(IClientSessionHandle tx, long proposalId, CancellationToken ct) {
        var row = await Rows(B.Proposals).Find(tx, F.Eq("_id", proposalId)).SingleOrDefaultAsync(ct) ?? throw B.Unavailable();
        B.Exact(row, "_id", "schemaVersion", "kind", "poolId", "revision", "expected", "configuration", "policyHash", "handoff", "obligations", "committedPoolFence",
            "actorId", "actorGrantVersion", "requestKey", "bodyHash", "reason", "correlationId", "auditId", "recordedAt", "snapshotHash"); B.RequireSeal(row);
        var expected = B.ReadExpected(row["expected"].AsBsonDocument); var handoff = B.ReadHandoff(row["handoff"].AsBsonDocument);
        if (row["kind"] != "stock.source.activation.proposal" || row["revision"] != 1L || row["configuration"]["source"]["active"] != false ||
            B.Hash(row["configuration"].AsBsonDocument) != expected.ConfigurationHash || row["committedPoolFence"].AsInt64 != checked(expected.PoolFence + 1) ||
            handoff.ObservedAt > StockSourceSetupBson.Time(row["recordedAt"]) || StockSourceSetupBson.Time(row["recordedAt"]) > Now() ||
            row["bodyHash"] != AuthorizationStore.CommandHash(new { poolId = row["poolId"].AsInt64, expected, handoff }, row["reason"].AsString)) throw B.Unavailable();
        RequireActivationObligations(row["obligations"].AsBsonArray);
        await ActivationEffectsAsync(tx, row, B.Propose, null, ct); return row;
    }
    private async Task<BsonDocument?> ActivationDecisionAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct) {
        var row = await Rows(B.Decisions).Find(tx, F.Eq("proposalId", proposal["_id"])).SingleOrDefaultAsync(ct);
        if (row is null) return null;
        B.Exact(row, "_id", "schemaVersion", "kind", "poolId", "proposalId", "proposalHash", "revision", "decision", "handoff", "expected", "binding",
            "activationId", "activationRevision", "obligations", "committedPoolFence", "actorId", "actorGrantVersion", "requestKey", "bodyHash", "reason",
            "correlationId", "auditId", "recordedAt", "snapshotHash"); B.RequireSeal(row);
        var expected = B.ReadExpected(row["expected"].AsBsonDocument);
        var decision = row["decision"].AsString switch { "approve" => StockSourceSetupDecision.Approve, "reject" => StockSourceSetupDecision.Reject, _ => throw B.Unavailable() };
        var handoff = row["handoff"].IsBsonNull ? null : B.ReadHandoff(row["handoff"].AsBsonDocument);
        if (row["kind"] != "stock.source.activation.decision" || row["poolId"] != proposal["poolId"] || row["proposalId"] != proposal["_id"] ||
            row["proposalHash"] != proposal["snapshotHash"] || row["revision"] != 1L || row["actorId"] == proposal["actorId"] ||
            row["binding"] != ActivationDecisionBinding(proposal, expected, decision, handoff, row["actorId"].AsInt64) ||
            row["bodyHash"] != AuthorizationStore.CommandHash(new { proposalId = proposal["_id"].AsInt64, expectedRevision = 1L, decision, handoff,
                expected, binding = row["binding"].AsString }, row["reason"].AsString) ||
            StockSourceSetupBson.Time(row["recordedAt"]) < StockSourceSetupBson.Time(proposal["recordedAt"]) || StockSourceSetupBson.Time(row["recordedAt"]) > Now()) throw B.Unavailable();
        if (decision == StockSourceSetupDecision.Approve) {
            RequireActivationProposalCurrent(proposal, expected); RequireActivationObligations(row["obligations"].AsBsonArray);
            var maker = B.ReadHandoff(proposal["handoff"].AsBsonDocument);
            var source = StockSourceSetupBson.ReadInput(proposal["configuration"]["source"]["input"].AsBsonDocument);
            var at = StockSourceSetupBson.Time(row["recordedAt"]);
            if (handoff is null || handoff.Reference != maker.Reference || handoff.ObservedAt < maker.ObservedAt || handoff.ObservedAt > at ||
                at - maker.ObservedAt >= TimeSpan.FromMilliseconds(source.MaximumAgeMilliseconds) || at - handoff.ObservedAt >= TimeSpan.FromMilliseconds(source.MaximumAgeMilliseconds) ||
                row["activationId"] != proposal["_id"] || row["activationRevision"] != checked(expected.ActivationRevision + 1) ||
                row["committedPoolFence"].AsInt64 != checked(expected.PoolFence + 2)) throw B.Unavailable();
        } else if (handoff is not null || !row["activationId"].IsBsonNull || !row["activationRevision"].IsBsonNull ||
            row["obligations"].AsBsonArray.Count != 0 || row["committedPoolFence"].AsInt64 != checked(expected.PoolFence + 1)) throw B.Unavailable();
        await ActivationEffectsAsync(tx, row, B.Decide, proposal, ct); return row;
    }
    private static void RequireActivationObligations(BsonArray rows) {
        var domains = Enum.GetValues<StockSourceObligationDomain>(); if (rows.Count != domains.Length) throw B.Unavailable();
        for (var index = 0; index < domains.Length; index++) {
            var row = rows[index].AsBsonDocument; B.Exact(row, "domain", "producerRevision", "complete", "openCount");
            if (row["domain"] != domains[index].ToString() || !row["producerRevision"].IsInt64 || row["producerRevision"].AsInt64 <= 0 ||
                row["complete"] != true || !row["openCount"].IsInt64 || row["openCount"] != 0L) throw B.Unavailable();
        }
    }
    private async Task ActivationEffectsAsync(IClientSessionHandle tx, BsonDocument row, string operation, BsonDocument? proposal, CancellationToken ct) {
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        if (audit is null || audit != B.Audit(row, operation)) throw B.Unavailable();
        var outbox = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", operation + ":" + row["_id"].AsInt64)).SingleOrDefaultAsync(ct);
        if (outbox is null || outbox["kind"] != operation || outbox["poolId"] != row["poolId"] || outbox["actorId"] != row["actorId"] ||
            outbox["requestKey"] != row["requestKey"] || outbox["payload"] != B.Result(row) || outbox["createdAt"] != row["recordedAt"]) throw B.Unavailable();
        var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", row["actorId"]) & F.Eq("operation", operation) & F.Eq("key", row["requestKey"])).SingleOrDefaultAsync(ct);
        if (receipt is null || receipt["bodyHash"] != row["bodyHash"] || receipt["result"] != B.Result(row) || receipt["createdAt"].CompareTo(row["recordedAt"]) > 0) throw B.Unavailable();
        if (proposal is not null && !receipt.GetValue(PersonApprovalReceipt.Field, BsonNull.Value).Equals(PersonApprovalReceipt.Create(
            Permissions.StockSourceApprove, new ApprovalRequest(1, 1, ProposedBy: proposal["actorId"].AsInt64)))) throw B.Unavailable();
        if (proposal is null && receipt.Contains(PersonApprovalReceipt.Field)) throw B.Unavailable();
    }
    private async Task ActivationPolicyBindingAsync(IClientSessionHandle tx, BsonDocument proposal, BsonDocument current, CancellationToken ct) {
        var expected = B.ReadExpected(proposal["expected"].AsBsonDocument); var poolId = proposal["poolId"].AsInt64;
        if (proposal["poolId"] != current["pool"]["_id"] || expected.PoolRevision > current["pool"]["revision"].AsInt64 ||
            proposal["configuration"]["source"]["creationProposalId"] != current["creationProposalId"]) throw B.Unavailable();
        var policy = await Rows(StockSourcePolicyBson.Policies).Find(tx, F.Eq("_id", expected.PolicyId)).SingleOrDefaultAsync(ct) ?? throw B.Unavailable();
        var policyProposal = await Rows(StockSourcePolicyBson.Proposals).Find(tx, F.Eq("_id", policy["proposalId"])).SingleOrDefaultAsync(ct) ?? throw B.Unavailable();
        var policyDecision = await Rows(StockSourcePolicyBson.Decisions).Find(tx, F.Eq("_id", policy["decisionId"])).SingleOrDefaultAsync(ct) ?? throw B.Unavailable();
        StockSourcePolicyBson.Proposal(policyProposal); StockSourcePolicyBson.Decision(policyDecision, policyProposal); StockSourcePolicyBson.Policy(policy, policyProposal, policyDecision);
        await OperatingPolicyProvenanceAsync(tx, policyProposal, null, ct); await OperatingPolicyProvenanceAsync(tx, policyProposal, policyDecision, ct);
        var view = new BsonDocument { { "proposal", policyProposal }, { "decision", policyDecision }, { "policy", policy } };
        if (B.Hash(view) != proposal["policyHash"] || policy["poolId"] != poolId || policy["policyRevision"].AsInt64 != expected.PolicyRevision ||
            policyProposal["configuration"] != proposal["configuration"] || policyProposal["configurationHash"] != expected.ConfigurationHash ||
            policyProposal["suspensionRevision"].AsInt64 != expected.SuspensionRevision || policyProposal["input"]["writer"] != "unitToko" ||
            StockSourceSetupBson.Time(policyDecision["decidedAt"]) > StockSourceSetupBson.Time(proposal["handoff"]["observedAt"])) throw B.Unavailable();
        await RequireOperatingPolicyBindingAtAsync(tx, policyProposal, StockSourceSetupBson.Time(proposal["recordedAt"]), ct);
        var source = StockSourceSetupBson.ReadInput(proposal["configuration"]["source"]["input"].AsBsonDocument);
        if (source.Mode != StockAuthorityMode.WebLedger || proposal["handoff"]["reference"] != source.HandoffReference) throw B.Unavailable();
    }
}
