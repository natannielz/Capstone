using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    public async Task<BsonDocument?> RecoverOperatingPolicyAsync(ActorContext actor, long poolId, string operation, string key,
        string correlationId, CancellationToken ct = default)
    {
        if (poolId <= 0 || !StockSourceSetupPolicy.Text(key, 200) || !StockSourceSetupPolicy.Text(correlationId, 128) ||
            operation is not ("stock.source.policy.propose" or "stock.source.policy.approve" or "stock.source.policy.reject" or "stock.source.policy.withdraw"))
            throw StockSourcePolicyBson.Invalid();
        await OperatingPolicyStorageAsync(ct);
        var permission = operation is "stock.source.policy.propose" or "stock.source.policy.withdraw" ? Permissions.StockSourcePolicyPropose : Permissions.StockSourcePolicyApprove;
        var recovered = await authorization.ReadAsync<BsonDocument?>(actor, async (snapshot, tx, token) =>
        {
            var context = await OperatingPolicyContextAsync(actor, snapshot, tx, poolId, permission, token);
            var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", actor.UserId) & F.Eq("operation", operation) & F.Eq("key", key)).SingleOrDefaultAsync(token);
            BsonDocument? result = null;
            if (receipt is not null)
            {
                StockSourcePolicyBson.Guard(() =>
                {
                    var saved = receipt["result"].AsBsonDocument;
                    if (!StockSourcePolicyBson.Positive(saved, "poolId") || !StockSourcePolicyBson.Positive(saved, "proposalId")) throw StockSourcePolicyBson.Invalid();
                    if (saved["poolId"].AsInt64 != poolId) throw new AuthorizationDeniedException("scope_denied");
                    if (!context.History.Views.TryGetValue(saved["proposalId"].AsInt64, out var view)) throw StockSourcePolicyBson.Invalid();
                    var decision = operation == "stock.source.policy.propose" ? null : view["decision"].AsBsonDocument;
                    if (decision is not null && operation != StockSourcePolicyBson.Prefix + decision["decision"].AsString) throw StockSourcePolicyBson.Invalid();
                    StockSourcePolicyBson.Receipt(receipt, view["proposal"].AsBsonDocument, decision);
                    result = view.DeepClone().AsBsonDocument;
                });
            }
            await CompleteAuthorityAsync(actor, snapshot, permission, context.Source.Resources, tx, token);
            return result;
        }, ct);
        if (recovered is null) return null;
        var proposal = recovered["proposal"].AsBsonDocument;
        if (operation == "stock.source.policy.propose")
        {
            var result = await ProposeOperatingPolicyCoreAsync(actor, poolId, StockSourcePolicyBson.Expected(proposal),
                StockSourcePolicyBson.ReadInput(proposal["input"].AsBsonDocument, StockSourcePolicyBson.Source(proposal)),
                new(key, proposal["reason"].AsString, correlationId), true, ct);
            if (!result.Equals(StockSourcePolicyBson.ProposalResult(proposal))) throw StockSourcePolicyBson.Invalid();
            return result;
        }
        var row = recovered["decision"].AsBsonDocument;
        var replay = await DecideOperatingPolicyCoreAsync(actor, proposal["_id"].AsInt64, 1,
            Enum.Parse<StockSourceSetupDecision>(row["decision"].AsString, true), row["reviewReference"].AsString,
            new(key, row["reason"].AsString, correlationId), true, ct);
        if (!replay.Equals(StockSourcePolicyBson.DecisionResult(row))) throw StockSourcePolicyBson.Invalid();
        return replay;
    }
}
