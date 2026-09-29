using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    /// <summary>A frozen policy-definition review. It never activates a writer or promises stock.</summary>
    public async Task<BsonDocument> PreviewOperatingPolicyAsync(ActorContext actor, long poolId, CancellationToken ct = default)
    {
        if (poolId <= 0) throw StockSourcePolicyBson.Invalid(); await OperatingPolicyStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            var context = await OperatingPolicyContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourcePolicyPropose, token);
            RequireOperatingPolicyReady(context.Source);
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourcePolicyPropose, context.Source.Resources, tx, token);
            return new BsonDocument
            {
                { "poolId", poolId }, { "poolRevision", context.Source.Pool["revision"] }, { "suspensionRevision", context.SuspensionRevision },
                { "policyRevision", context.PolicyRevision }, { "configurationHash", context.Hash },
                { "configuration", context.Source.Configuration.DeepClone() }
            };
        }, ct);
    }
    public Task<BsonDocument> ProposeOperatingPolicyAsync(ActorContext actor, long poolId, StockSourcePolicyExpectation expected,
        StockSourceOperatingPolicyInput input, AuthorizationCommandMetadata metadata, CancellationToken ct = default) =>
        ProposeOperatingPolicyCoreAsync(actor, poolId, expected, input, metadata, false, ct);
    private async Task<BsonDocument> ProposeOperatingPolicyCoreAsync(ActorContext actor, long poolId, StockSourcePolicyExpectation expected,
        StockSourceOperatingPolicyInput input, AuthorizationCommandMetadata metadata, bool recoveryOnly, CancellationToken ct)
    {
        if (poolId <= 0 || expected is null || input is null) throw StockSourcePolicyBson.Invalid();
        expected.Validate(); Metadata(metadata); await OperatingPolicyStorageAsync(ct);
        var id = recoveryOnly ? 0 : (await ids.NextAsync(StockSourcePolicyBson.Proposals, ct)).Value;
        var auditId = recoveryOnly ? 0 : (await ids.NextAsync("auditEvents", ct)).Value;
        OperatingPolicyContext? captured = null;
        return await AliasCommandAsync(actor, StockSourcePolicyBson.Prefix + "propose", Permissions.StockSourcePolicyPropose, metadata,
            new { poolId, expected, input }, async (snapshot, tx, token) =>
            {
                captured = await OperatingPolicyContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourcePolicyPropose, token);
                return (captured.Source.Resources, null);
            }, async (tx, token) =>
            {
                var context = captured ?? throw StockSourcePolicyBson.Invalid();
                RequireOperatingPolicyExpected(context, expected);
                StockSourceOperatingPolicyRules.Validate(input, StockSourceSetupBson.ReadInput(context.Source.Current["input"].AsBsonDocument));
                if (context.History.Latest is { } latest && latest["proposal"]["configurationHash"] == context.Hash &&
                    latest["proposal"]["suspensionRevision"] == context.SuspensionRevision &&
                    latest["proposal"]["input"].Equals(StockSourcePolicyBson.Input(input)))
                    throw new PersistenceConflictException("stock_source_policy_unchanged", "The operating-policy definition or its reviewed source binding must change");
                var now = Now();
                if (now < context.Source.ApprovedAt || context.Source.Latest is not null &&
                    now < StockSourceSetupBson.Time(context.Source.Latest["suspendedAt"])) throw StockSourcePolicyBson.Invalid();
                var row = StockSourcePolicyBson.Header(StockSourcePolicyBson.Proposals, id);
                row.Add("poolId", poolId); row.Add("poolRevision", expected.PoolRevision); row.Add("suspensionRevision", expected.SuspensionRevision);
                row.Add("basePolicyRevision", expected.PolicyRevision); row.Add("configuration", context.Source.Configuration.DeepClone());
                row.Add("configurationHash", context.Hash); row.Add("input", StockSourcePolicyBson.Input(input));
                row.Add("revision", 1L); row.Add("proposedBy", actor.UserId); row.Add("proposedAt", now.UtcDateTime);
                StockSourceSetupBson.Metadata(row, auditId, metadata); StockSourcePolicyBson.Proposal(row);
                await FenceRevisionPoolAsync(tx, context.Source.Pool, false, token);
                await Rows(StockSourcePolicyBson.Proposals).InsertOneAsync(tx, row, cancellationToken: token);
                await Rows("auditEvents").InsertOneAsync(tx, StockSourcePolicyBson.Audit(row, actor, StockSourcePolicyBson.Prefix + "propose", now), cancellationToken: token);
                return StockSourcePolicyBson.ProposalResult(row);
            }, ct, recoveryOnly);
    }
    public Task<BsonDocument> DecideOperatingPolicyAsync(ActorContext actor, long proposalId, long expectedRevision,
        StockSourceSetupDecision decision, string reviewReference, AuthorizationCommandMetadata metadata, CancellationToken ct = default) =>
        DecideOperatingPolicyCoreAsync(actor, proposalId, expectedRevision, decision, reviewReference, metadata, false, ct);
    private async Task<BsonDocument> DecideOperatingPolicyCoreAsync(ActorContext actor, long proposalId, long expectedRevision,
        StockSourceSetupDecision decision, string reviewReference, AuthorizationCommandMetadata metadata, bool recoveryOnly, CancellationToken ct)
    {
        if (proposalId <= 0 || expectedRevision <= 0 || !Enum.IsDefined(decision) || !StockSourceSetupPolicy.Text(reviewReference, 160)) throw StockSourcePolicyBson.Invalid();
        Metadata(metadata); await OperatingPolicyStorageAsync(ct);
        var id = recoveryOnly ? 0 : (await ids.NextAsync(StockSourcePolicyBson.Decisions, ct)).Value;
        var auditId = recoveryOnly ? 0 : (await ids.NextAsync("auditEvents", ct)).Value;
        long? policyId = !recoveryOnly && decision == StockSourceSetupDecision.Approve ? (await ids.NextAsync(StockSourcePolicyBson.Policies, ct)).Value : null;
        var permission = decision == StockSourceSetupDecision.Withdraw ? Permissions.StockSourcePolicyPropose : Permissions.StockSourcePolicyApprove;
        var name = decision.ToString().ToLowerInvariant(); var operation = StockSourcePolicyBson.Prefix + name;
        OperatingPolicyContext? captured = null; BsonDocument? selected = null;
        return await AliasCommandAsync(actor, operation, permission, metadata, new { proposalId, expectedRevision, decision, reviewReference },
            async (snapshot, tx, token) =>
            {
                var scope = await Rows(StockSourcePolicyBson.Proposals).Find(tx, F.Eq("_id", proposalId)).Project(new BsonDocument("poolId", 1)).SingleOrDefaultAsync(token);
                if (scope is null || !scope.GetValue("poolId", BsonNull.Value).IsInt64) throw new AuthorizationDeniedException("scope_denied");
                captured = await OperatingPolicyContextAsync(actor, snapshot, tx, scope["poolId"].AsInt64, permission, token);
                if (!captured.History.Views.TryGetValue(proposalId, out selected)) throw StockSourcePolicyBson.Invalid();
                var proposal = selected["proposal"].AsBsonDocument;
                if (decision == StockSourceSetupDecision.Withdraw && proposal["proposedBy"].AsInt64 != actor.UserId)
                    throw new AuthorizationDeniedException("stock_source_proposer_required");
                return (captured.Source.Resources, decision == StockSourceSetupDecision.Withdraw ? null :
                    new ApprovalRequest(1, expectedRevision, ProposedBy: proposal["proposedBy"].AsInt64));
            }, async (tx, token) =>
            {
                var context = captured ?? throw StockSourcePolicyBson.Invalid(); var view = selected ?? throw StockSourcePolicyBson.Invalid();
                var proposal = view["proposal"].AsBsonDocument;
                if (proposal["revision"].AsInt64 != expectedRevision) throw OperatingPolicyChanged();
                if (!view["decision"].IsBsonNull) throw new PersistenceConflictException("stock_source_policy_already_decided", "This operating-policy proposal already has a decision");
                if (decision == StockSourceSetupDecision.Approve) RequireOperatingPolicyExpected(context, StockSourcePolicyBson.Expected(proposal));
                var now = Now();
                if (now < StockSourceSetupBson.Time(proposal["proposedAt"])) throw StockSourcePolicyBson.Invalid();
                var row = StockSourcePolicyBson.Header(StockSourcePolicyBson.Decisions, id);
                row.Add("poolId", proposal["poolId"]); row.Add("proposalId", proposalId); row.Add("revision", expectedRevision); row.Add("decision", name);
                row.Add("policyId", policyId.HasValue ? new BsonInt64(policyId.Value) : BsonNull.Value);
                row.Add("policyRevision", policyId.HasValue ? new BsonInt64(checked(context.PolicyRevision + 1)) : BsonNull.Value);
                row.Add("decidedBy", actor.UserId); row.Add("decidedAt", now.UtcDateTime); row.Add("reviewReference", reviewReference);
                StockSourceSetupBson.Metadata(row, auditId, metadata); StockSourcePolicyBson.Decision(row, proposal);
                await FenceRevisionPoolAsync(tx, context.Source.Pool, false, token);
                if (policyId.HasValue)
                {
                    var policy = StockSourcePolicyBson.Header(StockSourcePolicyBson.Policies, policyId.Value);
                    policy.Add("poolId", proposal["poolId"]); policy.Add("policyRevision", row["policyRevision"]); policy.Add("proposalId", proposalId); policy.Add("decisionId", id);
                    StockSourcePolicyBson.Policy(policy, proposal, row);
                    await Rows(StockSourcePolicyBson.Policies).InsertOneAsync(tx, policy, cancellationToken: token);
                }
                await Rows(StockSourcePolicyBson.Decisions).InsertOneAsync(tx, row, cancellationToken: token);
                await Rows("auditEvents").InsertOneAsync(tx, StockSourcePolicyBson.Audit(row, actor, operation, now), cancellationToken: token);
                return StockSourcePolicyBson.DecisionResult(row);
            }, ct, recoveryOnly);
    }
    public async Task<BsonDocument> ReadOperatingPolicyAsync(ActorContext actor, long poolId, CancellationToken ct = default)
    {
        if (poolId <= 0) throw StockSourcePolicyBson.Invalid(); await OperatingPolicyStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            var context = await OperatingPolicyContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourceRead, token);
            var result = context.History.Latest?.DeepClone().AsBsonDocument ??
                new BsonDocument { { "proposal", BsonNull.Value }, { "decision", BsonNull.Value }, { "policy", BsonNull.Value } };
            result.Add("bindingCurrent", context.History.Latest is { } latest &&
                latest["proposal"]["configurationHash"] == context.Hash && latest["proposal"]["suspensionRevision"] == context.SuspensionRevision);
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceRead, context.Source.Resources, tx, token);
            return result;
        }, ct);
    }
    public async Task<BsonDocument?> ReadOperatingPolicyProposalAsync(ActorContext actor, long poolId, long proposalId, CancellationToken ct = default)
    {
        if (poolId <= 0 || proposalId <= 0) throw StockSourcePolicyBson.Invalid(); await OperatingPolicyStorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (snapshot, tx, token) =>
        {
            var context = await OperatingPolicyContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourceRead, token);
            var result = context.History.Views.GetValueOrDefault(proposalId)?.DeepClone().AsBsonDocument;
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceRead, context.Source.Resources, tx, token);
            return result;
        }, ct);
    }
    private sealed record OperatingPolicyContext(SuspensionContext Source, OperatingPolicyHistory History)
    {
        internal string Hash => StockSourceSuspensionBson.Hash(Source.Configuration);
        internal long SuspensionRevision => Source.Latest?["suspensionRevision"].AsInt64 ?? 0;
        internal long PolicyRevision => History.Latest?["policy"]["policyRevision"].AsInt64 ?? 0;
    }
    private async Task<OperatingPolicyContext> OperatingPolicyContextAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        IClientSessionHandle tx, long poolId, string permission, CancellationToken ct)
    {
        var source = await SuspensionContextAsync(actor, snapshot, tx, poolId, permission, ct);
        return new(source, await OperatingPolicyHistoryAsync(tx, source, ct));
    }
    private static void RequireOperatingPolicyReady(SuspensionContext source)
    {
        if (source.Pool["active"].AsBoolean) throw new PersistenceConflictException("stock_source_policy_suspension_required", "The physical pool must be stopped before its operating policy can be reviewed");
        foreach (var mapping in source.Configuration["mappings"].AsBsonArray.Select(value => value.AsBsonDocument))
            if (mapping["enabled"].AsBoolean && mapping["poolRevision"] != source.Pool["revision"])
                throw new PersistenceConflictException("stock_source_policy_mapping_stale", "Every enabled channel mapping must bind the current source settings");
    }
    private static void RequireOperatingPolicyExpected(OperatingPolicyContext context, StockSourcePolicyExpectation expected)
    {
        RequireOperatingPolicyReady(context.Source);
        if (context.Source.Pool["revision"].AsInt64 != expected.PoolRevision || context.SuspensionRevision != expected.SuspensionRevision ||
            context.PolicyRevision != expected.PolicyRevision || context.Hash != expected.ConfigurationHash) throw OperatingPolicyChanged();
    }
    private async Task OperatingPolicyStorageAsync(CancellationToken ct)
    {
        await SuspensionStorageAsync(ct);
        foreach (var name in StockSourcePolicySchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, StockSourcePolicySchemas.Validator(name), StockSourcePolicySchemas.Key(name), ct);
        foreach (var expected in StockSourcePolicySchemas.LookupIndexes())
        {
            var indexes = await (await Rows(expected.Collection).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (!indexes.Any(index => index["key"].AsBsonDocument.Equals(expected.Key) && !index.GetValue("unique", false).AsBoolean &&
                !index.GetValue("sparse", false).AsBoolean && !index.GetValue("hidden", false).AsBoolean && !index.Contains("partialFilterExpression") &&
                !index.Contains("expireAfterSeconds") && (!index.TryGetValue("collation", out var collation) ||
                    collation.IsBsonDocument && collation.AsBsonDocument.Equals(new BsonDocument("locale", "simple")))))
                throw StockSourcePolicyBson.Invalid();
        }
    }
    private static PersistenceConflictException OperatingPolicyChanged() =>
        new("stock_source_policy_changed", "The source, channels, suspension or accepted operating policy changed; review the current version");
}
