using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Inventory.StockSourceActivationBson;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    /// <summary>Current scoped empty-web-ledger review. The seven real readers fence
    /// their own producer state; neither this preview nor a proposal activates stock.</summary>
    public async Task<BsonDocument> PreviewActivationAsync(ActorContext actor, long poolId, CancellationToken ct = default) {
        if (poolId <= 0) throw B.Unavailable(); await ActivationStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var context = await OperatingPolicyContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourcePropose, token);
            var history = await ActivationHistoryAsync(tx, context.Source.Current, token);
            var expected = ActivationExpected(context, history); RequireActivationReady(context);
            await RequireEmptyActivationPoolAsync(tx, context, token);
            var person = await ReadInventoryCountPersonAsync(tx, actor.UserId, token); await FenceInventoryCountPersonAsync(tx, person, token);
            var drained = await ActivationDrainAsync(tx, actor, context.Source.Pool, token);
            expected = expected with { PoolFence = drained.Pool.PoolFence };
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourcePropose, context.Source.Resources, tx, token); person.RequireCurrent(tx, Now());
            return new BsonDocument { { "poolId", poolId }, { "expected", B.Expected(expected) },
                { "configuration", context.Source.Configuration.DeepClone() }, { "policy", context.History.Latest!["proposal"]["input"].DeepClone() },
                { "requiredHandoffReference", StockSourceSetupBson.ReadInput(context.Source.Current["input"].AsBsonDocument).HandoffReference },
                { "obligations", ActivationObligations(drained) }, { "active", false } };
        }, ct);
    }

    public Task<BsonDocument> ProposeActivationAsync(ActorContext actor, long poolId, StockSourceActivationExpectation expected,
        StockSourceEmptyHandoff handoff, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (poolId <= 0 || expected is null) throw B.Unavailable(); expected.Validate(); StockSourceActivationRules.Validate(handoff); Metadata(metadata);
        return ProposeActivationCoreAsync(actor, poolId, expected, handoff, metadata, false, ct);
    }
    private async Task<BsonDocument> ProposeActivationCoreAsync(ActorContext actor, long poolId, StockSourceActivationExpectation expected,
        StockSourceEmptyHandoff handoff, AuthorizationCommandMetadata metadata, bool recoveryOnly, CancellationToken ct) {
        await ActivationStorageAsync(ct);
        var id = recoveryOnly ? 0 : (await ids.NextAsync(B.Proposals, ct)).Value;
        var auditId = recoveryOnly ? 0 : (await ids.NextAsync("auditEvents", ct)).Value;
        OperatingPolicyContext? captured = null; PersonAuthorityProof? person = null; IClientSessionHandle? personTransaction = null; var wrote = false;
        var body = new { poolId, expected, handoff };
        return await AliasCommandAsync(actor, B.Propose, Permissions.StockSourcePropose, metadata, body,
            async (snapshot, tx, token) => {
                wrote = false; captured = await OperatingPolicyContextAsync(actor, snapshot, tx, poolId, Permissions.StockSourcePropose, token);
                personTransaction = tx; person = await ReadInventoryCountPersonAsync(tx, actor.UserId, token); await FenceInventoryCountPersonAsync(tx, person, token);
                return (captured.Source.Resources, null);
            }, async (tx, token) => {
                var context = captured ?? throw B.Unavailable(); var history = await ActivationHistoryAsync(tx, context.Source.Current, token);
                if (ActivationExpected(context, history) != expected) throw B.Changed();
                RequireActivationReady(context); RequireActivationHandoff(handoff, context, Now());
                await RequireEmptyActivationPoolAsync(tx, context, token);
                var drain = await ActivationDrainAsync(tx, actor, context.Source.Pool, token);
                var row = B.Header(B.Proposals, id); row.Add("poolId", poolId); row.Add("revision", 1L);
                row.Add("expected", B.Expected(expected)); row.Add("configuration", context.Source.Configuration.DeepClone());
                row.Add("policyHash", B.Hash(context.History.Latest!));
                row.Add("handoff", B.Handoff(handoff)); row.Add("obligations", ActivationObligations(drain)); row.Add("committedPoolFence", drain.Pool.PoolFence);
                ActivationMetadata(row, actor, metadata, auditId, body); B.Seal(row);
                await Rows(B.Proposals).InsertOneAsync(tx, row, cancellationToken: token);
                await ActivationEffectsWriteAsync(tx, row, B.Propose, token); wrote = true; return B.Result(row);
            }, ct, recoveryOnly, () => {
                if (person is null || personTransaction is null) throw B.Unavailable();
                person.RequireCurrent(personTransaction, Now());
                if (wrote) RequireActivationHandoff(handoff, captured ?? throw B.Unavailable(), Now());
            });
    }

    public async Task<BsonDocument> PreviewActivationDecisionAsync(ActorContext actor, long proposalId, StockSourceSetupDecision decision,
        StockSourceEmptyHandoff? handoff, CancellationToken ct = default) {
        ValidateActivationDecision(proposalId, 1, decision, handoff); await ActivationStorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var context = await ActivationProposalContextAsync(actor, snapshot, tx, proposalId, Permissions.StockSourceApprove, token);
            var proposal = await ActivationProposalAsync(tx, proposalId, token);
            if (await Rows(B.Decisions).Find(tx, F.Eq("proposalId", proposalId)).AnyAsync(token)) throw B.Changed();
            var approval = new ApprovalRequest(1, 1, ProposedBy: proposal["actorId"].AsInt64);
            foreach (var resource in context.Source.Resources) {
                var allowed = ScopePolicy.Authorize(actor, snapshot, Permissions.StockSourceApprove, resource, Now(), approval);
                if (!allowed.Allowed) throw new AuthorizationDeniedException(allowed.Code);
            }
            var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx,
                PersonApprovalPolicy.RequiredAccounts(actor.UserId, Permissions.StockSourceApprove, approval), token);
            var people = PersonApprovalPolicy.Evaluate(actor.UserId, Permissions.StockSourceApprove, approval, proof.Evidence);
            if (!people.Allowed) throw new AuthorizationDeniedException(people.Code);
            await new PersonAuthorizationFence(database, clock).TouchAsync(tx, proof, token);
            await CurrentActivationMakerAsync(tx, proposal, context.Source.Resources, token);
            var history = await ActivationHistoryAsync(tx, context.Source.Current, token); var expected = ActivationExpected(context, history);
            if (decision == StockSourceSetupDecision.Approve) {
                RequireActivationProposalCurrent(proposal, expected); RequireActivationReady(context);
                RequireActivationHandoffs(proposal, handoff!, context, Now()); await RequireEmptyActivationPoolAsync(tx, context, token);
                var drain = await ActivationDrainAsync(tx, actor, context.Source.Pool, token); expected = expected with { PoolFence = drain.Pool.PoolFence };
            }
            await CompleteAuthorityAsync(actor, snapshot, Permissions.StockSourceApprove, context.Source.Resources, tx, token); proof.RequireCurrent(tx, Now());
            return new BsonDocument { { "poolId", proposal["poolId"] }, { "proposal", proposal.DeepClone() },
                { "proposalId", proposalId }, { "expected", B.Expected(expected) }, { "decision", decision.ToString().ToLowerInvariant() },
                { "handoff", handoff is null ? BsonNull.Value : B.Handoff(handoff) }, { "binding", ActivationDecisionBinding(proposal, expected, decision, handoff, actor.UserId) } };
        }, ct);
    }

    public Task<BsonDocument> DecideActivationAsync(ActorContext actor, long proposalId, long expectedRevision,
        StockSourceSetupDecision decision, StockSourceEmptyHandoff? handoff, StockSourceActivationExpectation expected,
        string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        ValidateActivationDecision(proposalId, expectedRevision, decision, handoff); expected.Validate(); Metadata(metadata);
        if (binding is not { Length: 64 }) throw B.Changed();
        return DecideActivationCoreAsync(actor, proposalId, expectedRevision, decision, handoff, expected, binding, metadata, false, ct);
    }
    private async Task<BsonDocument> DecideActivationCoreAsync(ActorContext actor, long proposalId, long expectedRevision,
        StockSourceSetupDecision decision, StockSourceEmptyHandoff? handoff, StockSourceActivationExpectation expected,
        string binding, AuthorizationCommandMetadata metadata, bool recoveryOnly, CancellationToken ct) {
        await ActivationStorageAsync(ct);
        var id = recoveryOnly ? 0 : (await ids.NextAsync(B.Decisions, ct)).Value;
        var auditId = recoveryOnly ? 0 : (await ids.NextAsync("auditEvents", ct)).Value;
        OperatingPolicyContext? captured = null; BsonDocument? selected = null; var wrote = false;
        (ActorContext Actor, AuthorizationSnapshot Snapshot)? maker = null;
        var body = new { proposalId, expectedRevision, decision, handoff, expected, binding };
        return await AliasCommandAsync(actor, B.Decide, Permissions.StockSourceApprove, metadata, body,
            async (snapshot, tx, token) => {
                wrote = false; captured = await ActivationProposalContextAsync(actor, snapshot, tx, proposalId, Permissions.StockSourceApprove, token);
                selected = await ActivationProposalAsync(tx, proposalId, token);
                maker = await CurrentActivationMakerAsync(tx, selected, captured.Source.Resources, token);
                return (captured.Source.Resources, new ApprovalRequest(1, expectedRevision, ProposedBy: selected["actorId"].AsInt64));
            }, async (tx, token) => {
                var context = captured ?? throw B.Unavailable(); var proposal = selected ?? throw B.Unavailable();
                if (proposal["revision"] != expectedRevision || await Rows(B.Decisions).Find(tx, F.Eq("proposalId", proposalId)).AnyAsync(token)) throw B.Changed();
                var history = await ActivationHistoryAsync(tx, context.Source.Current, token);
                if (ActivationExpected(context, history) != expected || ActivationDecisionBinding(proposal, expected, decision, handoff, actor.UserId) != binding) throw B.Changed();
                StockSourceDrainCheck? drain = null;
                if (decision == StockSourceSetupDecision.Approve) {
                    RequireActivationProposalCurrent(proposal, expected); RequireActivationReady(context);
                    RequireActivationHandoffs(proposal, handoff!, context, Now()); await RequireEmptyActivationPoolAsync(tx, context, token);
                    drain = await ActivationDrainAsync(tx, actor, context.Source.Pool, token);
                } else await FenceRevisionPoolAsync(tx, context.Source.Pool, false, token);
                var row = B.Header(B.Decisions, id); row.Add("poolId", proposal["poolId"]); row.Add("proposalId", proposalId);
                row.Add("proposalHash", proposal["snapshotHash"]); row.Add("revision", 1L); row.Add("decision", decision.ToString().ToLowerInvariant());
                row.Add("handoff", handoff is null ? BsonNull.Value : B.Handoff(handoff)); row.Add("expected", B.Expected(expected)); row.Add("binding", binding);
                row.Add("activationId", drain is null ? BsonNull.Value : new BsonInt64(proposalId));
                row.Add("activationRevision", drain is null ? BsonNull.Value : new BsonInt64(checked(expected.ActivationRevision + 1)));
                row.Add("obligations", drain is null ? new BsonArray() : ActivationObligations(drain));
                row.Add("committedPoolFence", checked(expected.PoolFence + (drain is null ? 1 : 2)));
                ActivationMetadata(row, actor, metadata, auditId, body); B.Seal(row);
                if (drain is not null) {
                    var pool = context.Source.Pool.DeepClone().AsBsonDocument; pool["fence"] = drain.Pool.PoolFence;
                    if ((await Rows("inventoryPools").UpdateOneAsync(tx, pool, Builders<BsonDocument>.Update.Set("active", true).Inc("fence", 1L), cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
                    var activation = B.Header(B.Activations, proposalId); activation.Add("poolId", proposal["poolId"]);
                    activation.Add("activationRevision", row["activationRevision"]); activation.Add("proposalId", proposalId); activation.Add("decisionId", id);
                    activation.Add("proposalHash", proposal["snapshotHash"]); activation.Add("decisionHash", row["snapshotHash"]);
                    activation.Add("expected", B.Expected(expected)); activation.Add("committedPoolFence", row["committedPoolFence"]);
                    activation.Add("recordedAt", row["recordedAt"]); B.Seal(activation);
                    await Rows(B.Activations).InsertOneAsync(tx, activation, cancellationToken: token);
                }
                await Rows(B.Decisions).InsertOneAsync(tx, row, cancellationToken: token);
                await ActivationEffectsWriteAsync(tx, row, B.Decide, token); wrote = true; return B.Result(row);
            }, ct, recoveryOnly, () => {
                if (maker is not { } current) throw B.Unavailable();
                RequireActivationMaker(current.Actor, current.Snapshot, (captured ?? throw B.Unavailable()).Source.Resources);
                if (wrote && decision == StockSourceSetupDecision.Approve) RequireActivationHandoffs(selected ?? throw B.Unavailable(), handoff!, captured!, Now());
            });
    }

    private static void ValidateActivationDecision(long proposalId, long revision, StockSourceSetupDecision decision, StockSourceEmptyHandoff? handoff) {
        if (proposalId <= 0 || revision != 1 || decision is not (StockSourceSetupDecision.Approve or StockSourceSetupDecision.Reject) ||
            (decision == StockSourceSetupDecision.Approve) != (handoff is not null)) throw B.Unavailable();
        if (handoff is not null) StockSourceActivationRules.Validate(handoff);
    }
    private async Task<OperatingPolicyContext> ActivationProposalContextAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        IClientSessionHandle tx, long proposalId, string permission, CancellationToken ct) {
        var route = await Rows(B.Proposals).Find(tx, F.Eq("_id", proposalId)).Project(new BsonDocument("poolId", 1)).SingleOrDefaultAsync(ct);
        if (route is null || !route.GetValue("poolId", BsonNull.Value).IsInt64) throw new AuthorizationDeniedException("scope_denied");
        return await OperatingPolicyContextAsync(actor, snapshot, tx, route["poolId"].AsInt64, permission, ct);
    }
    private static void RequireActivationReady(OperatingPolicyContext context) {
        RequireOperatingPolicyReady(context.Source);
        var source = StockSourceSetupBson.ReadInput(context.Source.Current["input"].AsBsonDocument);
        if (source.Mode != StockAuthorityMode.WebLedger) throw new PersistenceConflictException("stock_source_external_capability_unavailable",
            "External source activation requires actual authenticated feed and reservation capabilities");
        if (context.History.Latest is not { } policy || policy["proposal"]["configurationHash"] != context.Hash ||
            policy["proposal"]["suspensionRevision"].AsInt64 != context.SuspensionRevision || policy["proposal"]["input"]["writer"] != "unitToko")
            throw new PersistenceConflictException("stock_source_activation_policy_required", "A current independently approved operating policy is required");
    }
    private static StockSourceActivationExpectation ActivationExpected(OperatingPolicyContext context, BsonDocument? latest) {
        var policy = context.History.Latest ?? throw new PersistenceConflictException("stock_source_activation_policy_required", "A reviewed operating policy is required");
        return new(context.Source.Pool["revision"].AsInt64, context.Source.Pool["fence"].AsInt64, context.SuspensionRevision,
            policy["policy"]["_id"].AsInt64, policy["policy"]["policyRevision"].AsInt64,
            latest?["activationRevision"].AsInt64 ?? 0, context.Hash);
    }
    private static void RequireActivationProposalCurrent(BsonDocument proposal, StockSourceActivationExpectation current) {
        var expected = B.ReadExpected(proposal["expected"].AsBsonDocument);
        if ((expected with { PoolFence = current.PoolFence }) != current || proposal["committedPoolFence"].AsInt64 > current.PoolFence) throw B.Changed();
    }
    private static void RequireActivationHandoff(StockSourceEmptyHandoff handoff, OperatingPolicyContext context, DateTimeOffset now) {
        StockSourceActivationRules.Validate(handoff);
        var source = StockSourceSetupBson.ReadInput(context.Source.Current["input"].AsBsonDocument);
        var latestPolicyAt = StockSourceSetupBson.Time(context.History.Latest!["decision"]["decidedAt"]);
        var stoppedAt = context.Source.Latest is null ? context.Source.ApprovedAt : StockSourceSetupBson.Time(context.Source.Latest["suspendedAt"]);
        if (handoff.Reference != source.HandoffReference || handoff.ObservedAt < latestPolicyAt || handoff.ObservedAt < stoppedAt ||
            handoff.ObservedAt > now || now - handoff.ObservedAt >= TimeSpan.FromMilliseconds(source.MaximumAgeMilliseconds)) throw B.Changed();
    }
    private static void RequireActivationHandoffs(BsonDocument proposal, StockSourceEmptyHandoff checker, OperatingPolicyContext context, DateTimeOffset now) {
        var maker = B.ReadHandoff(proposal["handoff"].AsBsonDocument);
        RequireActivationHandoff(maker, context, now); RequireActivationHandoff(checker, context, now);
        if (checker.Reference != maker.Reference || checker.ObservedAt < maker.ObservedAt) throw B.Changed();
    }
    private static string ActivationDecisionBinding(BsonDocument proposal, StockSourceActivationExpectation expected,
        StockSourceSetupDecision decision, StockSourceEmptyHandoff? handoff, long actorId) => B.Hash(new BsonDocument {
            { "proposalHash", proposal["snapshotHash"] }, { "expected", B.Expected(expected) }, { "decision", decision.ToString().ToLowerInvariant() },
            { "handoff", handoff is null ? BsonNull.Value : B.Handoff(handoff) }, { "actorId", actorId } });
    private async Task RequireEmptyActivationPoolAsync(IClientSessionHandle tx, OperatingPolicyContext context, CancellationToken ct) {
        await InventoryRepository.RequireStorageAsync(database, "stockBalances", InventorySchemas.BalanceValidator(), InventorySchemas.BalanceKey(), ct);
        var locations = new BsonArray(context.Source.Locations.Select(StockSourceSetupBson.Location));
        // Even zero raw/imported rows are refused. No held, available, quarantined or
        // cost-bearing predecessor stock is silently admitted by this initial handoff.
        if (await Rows("stockBalances").Find(tx, F.Eq("physicalPoolId", context.Source.Pool["_id"]) |
            F.In("location", locations)).Limit(1).AnyAsync(ct))
            throw new PersistenceConflictException("stock_source_nonempty_handoff_required", "Existing physical stock requires its own reviewed migration and valuation handoff");
    }
    private async Task<StockSourceDrainCheck> ActivationDrainAsync(IClientSessionHandle tx, ActorContext actor, BsonDocument pool, CancellationToken ct) {
        await new StockSourceTopologyFence(database).FenceAsync(tx, InventoryGraph.Location(pool["location"].AsBsonDocument).OrganizationId.Value, ct);
        IStockSourceObligationReader[] readers = [
            new UnitToko.Platform.Ordering.OrderStockSourceObligationReader(database, unit, ids, clock),
            new UnitToko.Platform.Ordering.ReservationStockSourceObligationReader(database, unit, ids, clock),
            new UnitToko.Platform.Fulfillment.FulfillmentStockSourceObligationReader(database, unit, ids, clock),
            new UnitToko.Platform.Procurement.ProcurementStockSourceObligationReader(database, unit, ids, clock),
            new UnitToko.Platform.Fulfillment.AfterSalesStockSourceObligationReader(database, unit, ids, clock),
            new ReconciliationStockSourceObligationReader(database, unit, ids, clock),
            new InventoryCountStockSourceObligationReader(database, unit, ids, clock) ];
        return await new StockSourceObligationGate(database, readers).RequireDrainedAsync(tx, actor, new(pool["_id"].AsInt64,
            InventoryGraph.Location(pool["location"].AsBsonDocument), pool["revision"].AsInt64, pool["fence"].AsInt64), ct);
    }
    private static BsonArray ActivationObligations(StockSourceDrainCheck drain) => new(drain.Results.Select(row => new BsonDocument {
        { "domain", row.Domain.ToString() }, { "producerRevision", row.ProducerRevision }, { "complete", row.Complete }, { "openCount", row.OpenCount } }));
    private void ActivationMetadata(BsonDocument row, ActorContext actor, AuthorizationCommandMetadata metadata, long auditId, object body) {
        row.Add("actorId", actor.UserId); row.Add("actorGrantVersion", actor.GrantVersion); row.Add("requestKey", metadata.IdempotencyKey);
        row.Add("bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason)); row.Add("reason", metadata.Reason); row.Add("correlationId", metadata.CorrelationId);
        row.Add("auditId", auditId); row.Add("recordedAt", Now().UtcDateTime);
    }
    private async Task ActivationEffectsWriteAsync(IClientSessionHandle tx, BsonDocument row, string operation, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row, operation), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(tx, B.Outbox(row, operation), cancellationToken: ct);
    }
    private async Task<(ActorContext Actor, AuthorizationSnapshot Snapshot)> CurrentActivationMakerAsync(IClientSessionHandle tx,
        BsonDocument proposal, ResourceScope[] resources, CancellationToken ct) {
        var identity = await Rows("identities").Find(tx, F.Eq("_id", proposal["actorId"])).SingleOrDefaultAsync(ct);
        if (identity is null || identity.GetValue("status", "") != "active" || identity.GetValue("grantVersion", BsonNull.Value) != proposal["actorGrantVersion"])
            throw new AuthorizationDeniedException("original_maker_authority_changed");
        var maker = new ActorContext(proposal["actorId"].AsInt64, identity["issuer"].AsString, identity["subject"].AsString,
            identity["grantVersion"].AsInt64, StockSourceSetupBson.Time(proposal["recordedAt"]), FactorAssurance.Unknown);
        var snapshot = await authorization.LoadAsync(maker, tx, ct); RequireActivationMaker(maker, snapshot, resources);
        var query = ScopePolicy.CompileParticipantGrants(maker, snapshot, Permissions.StockSourcePropose, Now());
        var clauses = query.Clauses.Where(clause => resources.Any(clause.Matches)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(maker,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(resource => ScopePolicy.RequiredNodes(resource, snapshot.Directory))).Distinct(), [],
            snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        return (maker, snapshot);
    }
    private void RequireActivationMaker(ActorContext maker, AuthorizationSnapshot snapshot, ResourceScope[] resources) {
        var query = ScopePolicy.CompileParticipantGrants(maker, snapshot, Permissions.StockSourcePropose, Now()); AuthorizationStore.Require(query);
        if (resources.Any(resource => !query.Matches(resource))) throw new AuthorizationDeniedException("original_maker_scope_changed");
    }
}
