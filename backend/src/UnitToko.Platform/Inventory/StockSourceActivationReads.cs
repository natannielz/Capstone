using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Inventory.StockSourceActivationBson;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    public async Task<BsonDocument?> ReadActivationAsync(ActorContext actor, long poolId, long proposalId, CancellationToken ct = default) {
        if (poolId <= 0 || proposalId <= 0) throw B.Unavailable(); await ActivationStorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (snapshot, tx, token) => {
            var context = await OperatingPolicyContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourceRead, token);
            await ActivationHistoryAsync(tx, context.Source.Current, token);
            var found = await Rows(B.Proposals).Find(tx, F.Eq("_id", proposalId) & F.Eq("poolId", poolId)).AnyAsync(token);
            BsonDocument? result = null;
            if (found) {
                var proposal = await ActivationProposalAsync(tx, proposalId, token);
                result = ActivationView(proposal, await ActivationDecisionAsync(tx, proposal, token));
            }
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceRead, context.Source.Resources, tx, token); return result;
        }, ct);
    }
    public async Task<AuthorizedPage> ListActivationsAsync(ActorContext actor, long poolId, int offset, int limit, CancellationToken ct = default) {
        if (poolId <= 0) throw B.Unavailable(); AuthorizationStore.ValidatePage(offset, limit); await ActivationStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var context = await OperatingPolicyContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourceRead, token);
            await ActivationHistoryAsync(tx, context.Source.Current, token);
            var filter = F.Eq("poolId", poolId); var total = await Rows(B.Proposals).CountDocumentsAsync(tx, filter, cancellationToken: token);
            var selected = await Rows(B.Proposals).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Skip(offset).Limit(limit).ToListAsync(token);
            var items = new List<BsonDocument>();
            foreach (var row in selected) {
                var proposal = await ActivationProposalAsync(tx, row["_id"].AsInt64, token);
                items.Add(ActivationView(proposal, await ActivationDecisionAsync(tx, proposal, token)));
            }
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceRead, context.Source.Resources, tx, token); return new AuthorizedPage(total, items);
        }, ct);
    }
    private static BsonDocument ActivationView(BsonDocument proposal, BsonDocument? decision) => new() {
        { "proposal", proposal.DeepClone() }, { "decision", (BsonValue?)decision?.DeepClone() ?? BsonNull.Value },
        { "status", decision is null ? "proposed" : decision["decision"] == "approve" ? "activated" : "rejected" } };

    /// <summary>Current-authority replay of an existing original-key receipt only.
    /// A missing receipt never creates a proposal, decision, activation or movement.</summary>
    public async Task<BsonDocument?> RecoverActivationAsync(ActorContext actor, long poolId, string operation, string key,
        string correlationId, CancellationToken ct = default) {
        if (poolId <= 0 || operation is not (B.Propose or B.Decide) || !StockSourceSetupPolicy.Text(key, 200) ||
            !StockSourceSetupPolicy.Text(correlationId, 128)) throw B.Unavailable();
        await ActivationStorageAsync(ct); var permission = operation == B.Propose ? Permissions.StockSourcePropose : Permissions.StockSourceApprove;
        var recovered = await authorization.ReadAsync<BsonDocument?>(actor, async (snapshot, tx, token) => {
            var context = await OperatingPolicyContextAsync(actor, snapshot, tx, poolId, permission, token);
            var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", key)).SingleOrDefaultAsync(token);
            BsonDocument? result = null;
            if (receipt is not null) {
                if (!receipt["result"].IsBsonDocument || !receipt["result"]["poolId"].IsInt64 ||
                    receipt["result"]["poolId"].AsInt64 != poolId) throw new AuthorizationDeniedException("scope_denied");
                var proposal = await ActivationProposalAsync(tx, receipt["result"]["proposalId"].AsInt64, token);
                var decision = operation == B.Propose ? null : await ActivationDecisionAsync(tx, proposal, token) ?? throw B.Unavailable();
                var row = decision ?? proposal;
                if (row["actorId"] != actor.UserId || row["requestKey"] != key || row["poolId"] != poolId || receipt["result"] != B.Result(row)) throw B.Unavailable();
                result = ActivationView(proposal, decision);
            }
            await CompleteAuthorityAsync(actor, snapshot, permission, context.Source.Resources, tx, token); return result;
        }, ct);
        if (recovered is null) return null;
        var original = recovered["proposal"].AsBsonDocument;
        if (operation == B.Propose) {
            var result = await ProposeActivationCoreAsync(actor, poolId, B.ReadExpected(original["expected"].AsBsonDocument),
                B.ReadHandoff(original["handoff"].AsBsonDocument), new(key, original["reason"].AsString, correlationId), true, ct);
            if (result != B.Result(original)) throw B.Unavailable(); return result;
        }
        var saved = recovered["decision"].AsBsonDocument;
        var replay = await DecideActivationCoreAsync(actor, original["_id"].AsInt64, 1,
            Enum.Parse<StockSourceSetupDecision>(saved["decision"].AsString, true), saved["handoff"].IsBsonNull ? null : B.ReadHandoff(saved["handoff"].AsBsonDocument),
            B.ReadExpected(saved["expected"].AsBsonDocument), saved["binding"].AsString, new(key, saved["reason"].AsString, correlationId), true, ct);
        if (replay != B.Result(saved)) throw B.Unavailable(); return replay;
    }
}
