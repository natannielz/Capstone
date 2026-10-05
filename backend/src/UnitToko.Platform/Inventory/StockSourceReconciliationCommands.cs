using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Inventory.StockSourceReconciliationBson;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    public async Task<BsonDocument> ReadReconciliationBaselineAsync(ActorContext actor, long poolId, CancellationToken ct = default)
    {
        if (poolId <= 0) throw B.Unavailable(); await ReconciliationStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            var context = await OperatingPolicyContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourceReconciliationRead, token);
            var baseline = await ReconciliationBaselineAsync(tx, context, token);
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceReconciliationRead, context.Source.Resources, tx, token);
            return baseline;
        }, ct);
    }

    public Task<BsonDocument> ProposeReconciliationAsync(ActorContext actor, StockSourceReconciliationInput input,
        string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        StockSourceReconciliationRules.Validate(input); Metadata(metadata);
        if (binding is not { Length: 64 }) throw B.Changed();
        return ProposeReconciliationCoreAsync(actor, input, binding, metadata, false, ct);
    }
    private async Task<BsonDocument> ProposeReconciliationCoreAsync(ActorContext actor, StockSourceReconciliationInput input,
        string binding, AuthorizationCommandMetadata metadata, bool recoveryOnly, CancellationToken ct)
    {
        await ReconciliationStorageAsync(ct);
        var id = recoveryOnly ? 0 : (await ids.NextAsync(B.Proposals, ct)).Value;
        var auditId = recoveryOnly ? 0 : (await ids.NextAsync("auditEvents", ct)).Value;
        OperatingPolicyContext? captured = null; BsonDocument? timingBaseline = null; var wrote = false;
        return await AliasCommandAsync(actor, B.ProposeOperation, Permissions.StockSourceReconciliationPropose, metadata,
            new { input, binding }, async (snapshot, tx, token) =>
            {
                wrote = false; timingBaseline = null;
                captured = await OperatingPolicyContextAsync(actor, snapshot, tx, input.PoolId, Permissions.StockSourceReconciliationPropose, token);
                return (captured.Source.Resources, null);
            }, async (tx, token) =>
            {
                var context = captured ?? throw B.Unavailable();
                var baseline = await ReconciliationBaselineAsync(tx, context, token);
                if (baseline["poolRevision"].AsInt64 != input.PoolRevision || baseline["poolFence"].AsInt64 != input.PoolFence ||
                    baseline["policyId"].AsInt64 != input.PolicyId || baseline["policyRevision"].AsInt64 != input.PolicyRevision ||
                    baseline["configurationHash"].AsString != input.ConfigurationHash || baseline["binding"].AsString != binding)
                    throw B.Changed();
                var now = Now(); RequireObservationTime(input.Observation, baseline, now);
                timingBaseline = baseline;
                _ = ObservedRows(input.Observation, baseline);
                var observation = B.Observation(input.Observation);
                var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "kind", "stock.source.reconciliation.proposal" },
                    { "poolId", input.PoolId }, { "revision", 1L }, { "baseline", baseline }, { "observation", observation },
                    { "differences", Differences(baseline, observation) }, { "committedPoolFence", checked(input.PoolFence + 1) },
                    { "binding", binding }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                    { "recordedAt", now.UtcDateTime }, { "auditId", auditId }, { "reason", metadata.Reason },
                    { "correlationId", metadata.CorrelationId }, { "requestKey", metadata.IdempotencyKey },
                    { "bodyHash", AuthorizationStore.CommandHash(new { input, binding }, metadata.Reason) } };
                row.Add("snapshotHash", B.Hash(row));
                await FenceRevisionPoolAsync(tx, context.Source.Pool, false, token);
                await Rows(B.Proposals).InsertOneAsync(tx, row, cancellationToken: token);
                await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row, B.ProposeOperation), cancellationToken: token);
                await Rows("outboxEvents").InsertOneAsync(tx, B.Outbox(row, B.ProposeOperation), cancellationToken: token);
                wrote = true;
                return B.Result(row);
            }, ct, recoveryOnly, () => { if (wrote) RequireObservationTime(input.Observation, timingBaseline ?? throw B.Unavailable(), Now()); });
    }

    private static string DecisionBinding(BsonDocument proposal, BsonDocument? baseline, StockSourceReconciliationDecision decision,
        StockSourceReconciliationObservation? observation, long actorId) => B.Hash(new BsonDocument {
            { "proposalHash", proposal["snapshotHash"] }, { "decision", decision.ToString().ToLowerInvariant() },
            { "actorId", actorId }, { "baselineBinding", baseline is null ? BsonNull.Value : baseline["binding"] },
            { "observation", observation is null ? BsonNull.Value : B.Observation(observation) } });

    // A checker cannot complete a proposal after its original maker loses current authority over
    // any mapped channel. This checks grants and membership only; it does not invent fresh maker MFA.
    private async Task<(ActorContext Actor, AuthorizationSnapshot Snapshot)> CurrentReconciliationMakerAsync(
        IClientSessionHandle tx, BsonDocument proposal, ResourceScope[] resources, bool fence, CancellationToken token)
    {
        var makerId = proposal["actorId"].AsInt64;
        var identity = await Rows("identities").Find(tx, F.Eq("_id", makerId)).SingleOrDefaultAsync(token);
        if (identity is null || identity.GetValue("status", BsonNull.Value) != "active" ||
            identity.GetValue("grantVersion", BsonNull.Value) != proposal["actorGrantVersion"] ||
            !identity.GetValue("issuer", BsonNull.Value).IsString || !identity.GetValue("subject", BsonNull.Value).IsString)
            throw new AuthorizationDeniedException("original_maker_authority_changed");
        var maker = new ActorContext(makerId, identity["issuer"].AsString, identity["subject"].AsString,
            identity["grantVersion"].AsInt64, new DateTimeOffset(proposal["recordedAt"].ToUniversalTime()), FactorAssurance.Unknown);
        var snapshot = await authorization.LoadAsync(maker, tx, token);
        RequireCurrentReconciliationMaker(maker, snapshot, resources);
        if (fence)
        {
            var query = ScopePolicy.CompileParticipantGrants(maker, snapshot, Permissions.StockSourceReconciliationPropose, Now());
            var clauses = query.Clauses.Where(clause => resources.Any(clause.Matches)).ToArray();
            await new HistoricalAuthorizationFence(database, authorization).FenceAsync(maker,
                clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(resource =>
                    ScopePolicy.RequiredNodes(resource, snapshot.Directory))).Distinct(), [],
                snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
                snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, token);
        }
        return (maker, snapshot);
    }

    private void RequireCurrentReconciliationMaker(ActorContext maker, AuthorizationSnapshot snapshot, ResourceScope[] resources)
    {
        var query = ScopePolicy.CompileParticipantGrants(maker, snapshot, Permissions.StockSourceReconciliationPropose, Now());
        AuthorizationStore.Require(query);
        if (resources.Any(resource => !query.Matches(resource)))
            throw new AuthorizationDeniedException("original_maker_scope_changed");
    }

    public async Task<BsonDocument> PreviewReconciliationDecisionAsync(ActorContext actor, long proposalId,
        StockSourceReconciliationDecision decision, StockSourceReconciliationObservation? observation, CancellationToken ct = default)
    {
        if (proposalId <= 0 || !Enum.IsDefined(decision) || (decision == StockSourceReconciliationDecision.Confirm) != (observation is not null)) throw B.Unavailable();
        await ReconciliationStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            var proposal = await ReconciliationProposalAsync(tx, proposalId, token);
            var context = await OperatingPolicyContextAsync(actor, snapshot, tx, proposal["poolId"].AsInt64, Permissions.StockSourceReconciliationDecide, token);
            var makerAuthority = await CurrentReconciliationMakerAsync(tx, proposal, context.Source.Resources, false, token);
            var approval = new ApprovalRequest(1, 1, ProposedBy: proposal["actorId"].AsInt64);
            foreach (var resource in context.Source.Resources)
            {
                var allowed = ScopePolicy.Authorize(actor, snapshot, Permissions.StockSourceReconciliationDecide, resource, Now(), approval);
                if (!allowed.Allowed) throw new AuthorizationDeniedException(allowed.Code);
            }
            var proof = await new UnitToko.Platform.Identity.PersonRegistryService(database, clock).ResolveProofsAsync(tx,
                PersonApprovalPolicy.RequiredAccounts(actor.UserId, Permissions.StockSourceReconciliationDecide, approval), token);
            var people = PersonApprovalPolicy.Evaluate(actor.UserId, Permissions.StockSourceReconciliationDecide, approval, proof.Evidence);
            if (!people.Allowed) throw new AuthorizationDeniedException(people.Code);
            await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, token);
            var prior = await Rows(B.Decisions).Find(tx, F.Eq("proposalId", proposalId)).SingleOrDefaultAsync(token);
            if (prior is not null) throw B.Changed();
            BsonDocument? baseline = null; var canConfirm = false; var checkerDifferences = new BsonArray();
            if (decision == StockSourceReconciliationDecision.Confirm)
            {
                baseline = await ReconciliationBaselineAsync(tx, context, token);
                _ = ObservedRows(observation!, baseline);
                RequireObservationTime(observation!, baseline, Now());
                checkerDifferences = Differences(baseline, B.Observation(observation!));
                var makerTime = new DateTimeOffset(proposal["observation"]["observedAt"].ToUniversalTime());
                var makerCurrent = makerTime <= Now() && Now() - makerTime < TimeSpan.FromMilliseconds(baseline["maximumAgeMilliseconds"].AsInt64);
                canConfirm = SameBaseline(proposal, baseline) && proposal["differences"].AsBsonArray.Count == 0 && checkerDifferences.Count == 0 &&
                    makerCurrent && proposal["observation"]["entries"].Equals(B.Observation(observation!)["entries"]);
            }
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceReconciliationDecide, context.Source.Resources, tx, token);
            RequireCurrentReconciliationMaker(makerAuthority.Actor, makerAuthority.Snapshot, context.Source.Resources);
            proof.RequireCurrent(tx, Now());
            return new BsonDocument { { "proposal", ReconciliationProposalView(proposal) }, { "decision", decision.ToString().ToLowerInvariant() },
                { "baseline", (BsonValue?)baseline ?? BsonNull.Value }, { "checkerDifferences", checkerDifferences },
                { "canConfirm", canConfirm }, { "binding", DecisionBinding(proposal, baseline, decision, observation, actor.UserId) },
                { "observedAt", Now().UtcDateTime } };
        }, ct);
    }

    private static bool SameBaseline(BsonDocument proposal, BsonDocument current) =>
        proposal["committedPoolFence"] == current["poolFence"] &&
        proposal["baseline"]["baselineHash"] == current["baselineHash"] &&
        proposal["baseline"]["policyId"] == current["policyId"] &&
        proposal["baseline"]["policyRevision"] == current["policyRevision"] &&
        proposal["baseline"]["configurationHash"] == current["configurationHash"];

    public Task<BsonDocument> DecideReconciliationAsync(ActorContext actor, long proposalId, long expectedRevision,
        StockSourceReconciliationDecision decision, StockSourceReconciliationObservation? observation, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) =>
        DecideReconciliationCoreAsync(actor, proposalId, expectedRevision, decision, observation, binding, metadata, false, ct);

    private async Task<BsonDocument> DecideReconciliationCoreAsync(ActorContext actor, long proposalId, long expectedRevision,
        StockSourceReconciliationDecision decision, StockSourceReconciliationObservation? observation, string binding,
        AuthorizationCommandMetadata metadata, bool recoveryOnly, CancellationToken ct)
    {
        if (proposalId <= 0 || expectedRevision != 1 || !Enum.IsDefined(decision) ||
            (decision == StockSourceReconciliationDecision.Confirm) != (observation is not null) || binding is not { Length: 64 }) throw B.Unavailable();
        if (observation is not null) StockSourceReconciliationRules.Validate(observation);
        Metadata(metadata); await ReconciliationStorageAsync(ct);
        var id = recoveryOnly ? 0 : (await ids.NextAsync(B.Decisions, ct)).Value;
        var auditId = recoveryOnly ? 0 : (await ids.NextAsync("auditEvents", ct)).Value;
        OperatingPolicyContext? captured = null; BsonDocument? proposal = null; BsonDocument? timingBaseline = null;
        ActorContext? maker = null; AuthorizationSnapshot? makerSnapshot = null; var wrote = false;
        return await AliasCommandAsync(actor, B.DecideOperation, Permissions.StockSourceReconciliationDecide, metadata,
            new { proposalId, expectedRevision, decision, observation, binding }, async (snapshot, tx, token) =>
            {
                wrote = false; timingBaseline = null; maker = null; makerSnapshot = null;
                proposal = await ReconciliationProposalAsync(tx, proposalId, token);
                captured = await OperatingPolicyContextAsync(actor, snapshot, tx, proposal["poolId"].AsInt64, Permissions.StockSourceReconciliationDecide, token);
                (maker, makerSnapshot) = await CurrentReconciliationMakerAsync(tx, proposal, captured.Source.Resources, true, token);
                return (captured.Source.Resources, new ApprovalRequest(1, expectedRevision, ProposedBy: proposal["actorId"].AsInt64));
            }, async (tx, token) =>
            {
                var original = proposal ?? throw B.Unavailable(); var context = captured ?? throw B.Unavailable();
                if (await Rows(B.Decisions).Find(tx, F.Eq("proposalId", proposalId)).AnyAsync(token)) throw B.Changed();
                BsonDocument? baseline = null;
                if (decision == StockSourceReconciliationDecision.Confirm)
                {
                    baseline = await ReconciliationBaselineAsync(tx, context, token);
                    timingBaseline = baseline;
                    _ = ObservedRows(observation!, baseline);
                    RequireObservationTime(observation!, baseline, Now());
                    if (!SameBaseline(original, baseline) || original["differences"].AsBsonArray.Count != 0 ||
                        Differences(baseline, B.Observation(observation!)).Count != 0 ||
                        !original["observation"]["entries"].Equals(B.Observation(observation!)["entries"])) throw B.Changed();
                    var originalTime = new DateTimeOffset(original["observation"]["observedAt"].ToUniversalTime());
                    if (originalTime > Now() || Now() - originalTime >= TimeSpan.FromMilliseconds(baseline["maximumAgeMilliseconds"].AsInt64)) throw B.Changed();
                }
                if (DecisionBinding(original, baseline, decision, observation, actor.UserId) != binding) throw B.Changed();
                var now = Now();
                var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "kind", "stock.source.reconciliation.decision" },
                    { "proposalId", proposalId }, { "proposalHash", original["snapshotHash"] }, { "poolId", original["poolId"] },
                    { "revision", 1L }, { "decision", decision.ToString().ToLowerInvariant() },
                    { "observation", observation is null ? BsonNull.Value : B.Observation(observation) },
                    { "baselineHash", baseline is null ? BsonNull.Value : baseline["baselineHash"] },
                    { "binding", binding }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                    { "recordedAt", now.UtcDateTime }, { "auditId", auditId }, { "reason", metadata.Reason },
                    { "correlationId", metadata.CorrelationId }, { "requestKey", metadata.IdempotencyKey },
                    { "bodyHash", AuthorizationStore.CommandHash(new { proposalId, expectedRevision, decision, observation, binding }, metadata.Reason) } };
                row.Add("snapshotHash", B.Hash(row));
                // Both fresh decisions close an outstanding obligation. Fence the current canonical
                // pool even when rejecting an old observation, without changing quantities or authority.
                await FenceRevisionPoolAsync(tx, context.Source.Pool, false, token);
                await Rows(B.Decisions).InsertOneAsync(tx, row, cancellationToken: token);
                await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row, B.DecideOperation), cancellationToken: token);
                await Rows("outboxEvents").InsertOneAsync(tx, B.Outbox(row, B.DecideOperation), cancellationToken: token);
                wrote = true;
                return B.Result(row);
            }, ct, recoveryOnly, () => {
                RequireCurrentReconciliationMaker(maker ?? throw B.Unavailable(), makerSnapshot ?? throw B.Unavailable(),
                    (captured ?? throw B.Unavailable()).Source.Resources);
                if (!wrote || decision != StockSourceReconciliationDecision.Confirm) return;
                var current = timingBaseline ?? throw B.Unavailable();
                RequireObservationTime(observation!, current, Now());
                var makerTime = new DateTimeOffset((proposal ?? throw B.Unavailable())["observation"]["observedAt"].ToUniversalTime());
                if (makerTime > Now() || Now() - makerTime >= TimeSpan.FromMilliseconds(current["maximumAgeMilliseconds"].AsInt64)) throw B.Changed();
            });
    }
}
