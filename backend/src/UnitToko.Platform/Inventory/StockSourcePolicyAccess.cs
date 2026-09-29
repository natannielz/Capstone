using MongoDB.Bson;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    public async Task<BsonDocument> PreviewOperatingPolicyDecisionAsync(ActorContext actor, long poolId, long proposalId,
        StockSourceSetupDecision decision, CancellationToken ct = default)
    {
        if (poolId <= 0 || proposalId <= 0 || !Enum.IsDefined(decision)) throw StockSourcePolicyBson.Invalid();
        await OperatingPolicyStorageAsync(ct);
        var permission = decision == StockSourceSetupDecision.Withdraw ? Permissions.StockSourcePolicyPropose : Permissions.StockSourcePolicyApprove;
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            var context = await OperatingPolicyContextAsync(actor, snapshot, tx, poolId, permission, token);
            if (!context.History.Views.TryGetValue(proposalId, out var view)) throw new AuthorizationDeniedException("scope_denied");
            var proposal = view["proposal"].AsBsonDocument;
            if (decision == StockSourceSetupDecision.Withdraw && proposal["proposedBy"].AsInt64 != actor.UserId)
                throw new AuthorizationDeniedException("stock_source_proposer_required");
            if (!view["decision"].IsBsonNull)
                throw new PersistenceConflictException("stock_source_policy_already_decided", "This operating-policy proposal already has a decision");
            var approval = decision == StockSourceSetupDecision.Withdraw ? null :
                new ApprovalRequest(proposal["revision"].AsInt64, proposal["revision"].AsInt64, ProposedBy: proposal["proposedBy"].AsInt64);
            void RequireAction()
            {
                foreach (var resource in context.Source.Resources)
                {
                    var allowed = ScopePolicy.Authorize(actor, snapshot, permission, resource, Now(), approval);
                    if (!allowed.Allowed) throw new AuthorizationDeniedException(allowed.Code);
                }
            }
            RequireAction();
            PersonAuthorityProof? proof = null;
            if (approval is not null)
            {
                var accounts = PersonApprovalPolicy.RequiredAccounts(actor.UserId, permission, approval);
                if (accounts.IsDefaultOrEmpty) throw new AuthorizationDeniedException("person_evidence_invalid");
                proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, accounts, token);
                var allowed = PersonApprovalPolicy.Evaluate(actor.UserId, permission, approval, proof.Evidence);
                if (!allowed.Allowed) throw new AuthorizationDeniedException(allowed.Code);
                await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, token);
            }
            if (decision == StockSourceSetupDecision.Approve)
                RequireOperatingPolicyExpected(context, StockSourcePolicyBson.Expected(proposal));
            var current = !context.Source.Pool["active"].AsBoolean &&
                proposal["poolRevision"] == context.Source.Pool["revision"] &&
                proposal["suspensionRevision"] == context.SuspensionRevision &&
                proposal["basePolicyRevision"] == context.PolicyRevision && proposal["configurationHash"] == context.Hash &&
                context.Source.Configuration["mappings"].AsBsonArray.All(mapping => !mapping["enabled"].AsBoolean ||
                    mapping["poolRevision"] == context.Source.Pool["revision"]);
            var result = view.DeepClone().AsBsonDocument;
            result.Add("action", decision.ToString().ToLowerInvariant());
            result.Add("bindingCurrent", current);
            // These are review facts, not activation or external-control evidence.
            result.Add("current", new BsonDocument {
                { "poolRevision", context.Source.Pool["revision"] }, { "suspensionRevision", context.SuspensionRevision },
                { "policyRevision", context.PolicyRevision }, { "configurationHash", context.Hash }
            });
            await CompleteAuthorityAsync(actor, snapshot, permission, context.Source.Resources, tx, token);
            proof?.RequireCurrent(tx, Now());
            RequireAction();
            return result;
        }, ct);
    }

    public async Task<AuthorizedPage> ListOperatingPolicyProposalsAsync(ActorContext actor, long poolId, int offset, int limit,
        CancellationToken ct = default)
    {
        if (poolId <= 0) throw StockSourcePolicyBson.Invalid();
        AuthorizationStore.ValidatePage(offset, limit); await OperatingPolicyStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            var context = await OperatingPolicyContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourceRead, token);
            // The response is bounded; complete history validation still precedes it.
            var items = context.History.Views.Values.OrderByDescending(view => view["proposal"]["_id"].AsInt64)
                .Skip(offset).Take(limit).Select(view => view.DeepClone().AsBsonDocument).ToArray();
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceRead, context.Source.Resources, tx, token);
            return new AuthorizedPage(context.History.Views.Count, items);
        }, ct);
    }
}
