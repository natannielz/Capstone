using System.Collections.Concurrent;
using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;
using B = UnitToko.Platform.Inventory.StockReleaseBson;

namespace UnitToko.Platform.Inventory;

public sealed class StockReleaseService
{
    private const string ProposePermission = "stock.release.propose", ApprovePermission = "stock.release.approve";
    private readonly IMongoDatabase database; private readonly MongoUnitOfWork unit; private readonly IdAllocator ids; private readonly IClock clock;
    private readonly AuthorizationStore authorization; private readonly StockReleaseSource source; private readonly StockSourceSetupService sources; private readonly LotValueStore values;
    private readonly ConcurrentDictionary<IClientSessionHandle, Context> inFlight = new();
    public StockReleaseService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, CatalogAuthoringService catalog,
        PurchaseOrderService orders, StockSourceSetupService sources, ProcurementStockValuationService valuations) {
        this.database = database; this.unit = unit; this.ids = ids; this.clock = clock; this.sources = sources;
        authorization = new(database, unit, clock); source = new(database, unit, clock, catalog, orders, sources, valuations); values = new(database, ids, clock);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string permission, StockReleaseInput input) {
        internal ActorContext Actor = actor; internal AuthorizationSnapshot Authority = authority; internal IClientSessionHandle Tx = tx;
        internal long Number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; internal string Permission = permission; internal StockReleaseInput Input = input;
        internal StockReleaseSourceProof? Source; internal StockReleaseExpectation? Expected; internal PersonAuthorityProof? People;
        internal string? Operation, Key;
        internal HistoryReads? History;
        internal long? HeldRemaining, QualityRemaining;
        internal Dictionary<long, BsonDocument>? FencedScopes;
        internal BsonDocument? Proposal, Decision; internal InventoryBalanceDocument? Before, After; internal DateTimeOffset? SessionExpiry;
        internal ResourceScope Resource => new(FulfillmentOrganizationId: Input.Location.OrganizationId.Value, ShopId: Input.Location.ShopId.Value,
            WarehouseId: Input.Location.WarehouseId.Value, ChannelId: Input.Location.ChannelId.Value);
    }
    // Per owning operation/transaction attempt only. It retains typed immutable
    // ancestry readers; never decisions, effects, policy or current availability.
    private sealed class HistoryReads(StockReleaseService owner, IClientSessionHandle tx) {
        private readonly long number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        private readonly Dictionary<long, HeldReceiptOriginProof> origins = [];
        private readonly Dictionary<long, ProcurementStockValuationService.ReleaseOriginReader> readers = [];
        internal void Require(StockReleaseService expectedOwner, IClientSessionHandle current) {
            if (!ReferenceEquals(owner, expectedOwner) || !ReferenceEquals(tx, current) || !current.IsInTransaction ||
                current.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw B.Broken();
            new InventoryRepository(owner.database).RequireSession(current);
        }
        internal void Seed(HeldReceiptOriginProof origin) {
            Require(owner, tx); origin.Require(owner.database, tx);
            if (origins.TryGetValue(origin.ReceiptId, out var existing) && !ReferenceEquals(existing, origin)) throw B.Broken();
            origins[origin.ReceiptId] = origin;
        }
        internal async Task<HeldReceiptOriginProof> OriginAsync(long receiptId, CancellationToken ct) {
            Require(owner, tx);
            if (!origins.TryGetValue(receiptId, out var origin)) {
                origin = await owner.source.CaptureOriginAsync(tx, receiptId, ct); Seed(origin);
            }
            origin.Require(owner.database, tx); return origin;
        }
        internal ProcurementStockValuationService.ReleaseOriginReader Reader(HeldReceiptOriginProof origin) {
            Seed(origin);
            if (!readers.TryGetValue(origin.ReceiptId, out var reader)) readers.Add(origin.ReceiptId, reader = owner.source.CreateOriginReader(tx, origin));
            return reader;
        }
    }
    private HistoryReads Reads(Context context) {
        var read = context.History ??= new HistoryReads(this, context.Tx); read.Require(this, context.Tx); return read;
    }
    private Task AuthenticateHistoryAsync(Context context, IMongoDatabase owner, IClientSessionHandle tx, InventorySource selected, string hash, CancellationToken ct) =>
        AuthenticateSourceAsync(owner, tx, selected, hash, Reads(context), ct);
    private async Task StorageAsync(CancellationToken ct) { await StockReleaseSchemas.RequireStorageAsync(database, ct); await LotValueStore.RequireStorageAsync(database, ct); }
    private void Current(Context context) {
        new InventoryRepository(database).RequireSession(context.Tx);
        if (context.Tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != context.Number) throw B.Broken();
        context.Source?.Require(database, context.Tx); context.People?.RequireCurrent(context.Tx, Now);
        if (context.SessionExpiry is { } expiry && Now >= expiry) throw new AuthorizationDeniedException("stale_or_inactive_session"); Require(context);
    }
    private string[] PermissionsFor(Context context) => context.Permission == ApprovePermission ? [ApprovePermission, Permissions.ProcurementStockValuationDecide] : [ProposePermission];
    private AuthorizationQuery[] Require(Context context) => PermissionsFor(context).Select(permission => {
        var query = ScopePolicy.Compile(context.Actor, context.Authority, permission, Now); AuthorizationStore.Require(query);
        if (!ScopePolicy.IsResourceActive(context.Resource, context.Authority.Directory) || !query.Matches(context.Resource)) throw new AuthorizationDeniedException("scope_denied");
        return query;
    }).ToArray();
    private async Task FenceAsync(Context context, CancellationToken ct) {
        Current(context); var clauses = Require(context).SelectMany(query => query.Clauses.Where(clause => clause.Matches(context.Resource))).ToArray();
        var nodes = ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct().ToArray();
        var firstScopeWrite = context.FencedScopes is null;
        if (!firstScopeWrite) {
            var captured = context.FencedScopes ?? throw B.Broken();
            if (!captured.Keys.ToHashSet().SetEquals(nodes.Select(node => node.Id))) throw new AuthorizationDeniedException("scope_fence_changed");
            foreach (var node in nodes) {
                var actual = await Rows("authorizationScopes").Find(context.Tx, new BsonDocument("_id", node.Id)).SingleOrDefaultAsync(ct);
                if (actual is null || actual != captured[node.Id] || AuthorizationBson.Node(actual) != node) throw new AuthorizationDeniedException("scope_fence_changed");
            }
        }
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            firstScopeWrite ? nodes : [], [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        if (firstScopeWrite) {
            var captured = new Dictionary<long, BsonDocument>();
            foreach (var node in nodes) {
                // Only successful genuine scope-fence writes can establish this
                // private epoch. Every later fence compares the complete actual row,
                // including its fence, before fresh actor/grant/session checks.
                var actual = await Rows("authorizationScopes").Find(context.Tx, new BsonDocument("_id", node.Id)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
                if (AuthorizationBson.Node(actual) != node) throw new AuthorizationDeniedException("scope_fence_changed");
                captured.Add(node.Id, actual.DeepClone().AsBsonDocument);
            }
            context.FencedScopes = captured; context.History = null;
        }
        if (context.People is { } proof) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct);
        if (context.Actor.Session is { } session) {
            var live = await Rows("identitySessions").Find(context.Tx, SessionPersistence.LiveFilter(session, context.Actor.UserId,
                context.Actor.ProviderIssuer, context.Actor.ProviderSubject, context.Actor.GrantVersion, Now)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("stale_or_inactive_session");
            context.SessionExpiry = new(new[] { live["idleExpiresAt"].ToUniversalTime(), live["absoluteExpiresAt"].ToUniversalTime() }.Min());
        }
        Current(context);
        ct.ThrowIfCancellationRequested();
    }
    private async Task PeopleAsync(Context context, CancellationToken ct) {
        var users = context.Proposal is null ? new[] { context.Actor.UserId } : new[] { context.Actor.UserId, context.Proposal["actorId"].AsInt64 };
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, users.Distinct().ToImmutableArray(), ct);
        if (proof.Evidence.Any(person => person.Verification != PersonVerification.Verified || person.PersonId <= 0 || person.Version <= 0)) throw new AuthorizationDeniedException("person_evidence_invalid");
        if (context.Proposal is { } proposal) {
            var maker = R.Person(proposal["people"][0]); var people = proof.Evidence.ToDictionary(person => person.UserId);
            if (!people.TryGetValue(maker.UserId, out var currentMaker) || currentMaker.PersonId != maker.PersonId ||
                !PersonIndependencePolicy.AreIndependent(people[context.Actor.UserId], currentMaker, maker)) throw new AuthorizationDeniedException("independent_person_required");
        }
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); context.People = proof;
        context.History = null;
    }
    private async Task PrepareAsync(Context context, CancellationToken ct) {
        context.HeldRemaining = null; context.QualityRemaining = null;
        Current(context); await new StockSourceTopologyFence(database).FenceAsync(context.Tx, context.Input.Location.OrganizationId.Value, ct);
        var selected = new StockReleaseSelection(context.Input.ReceiptId, context.Input.QualityAllocationId, context.Input.ValuationAllocationId);
        Task Authenticate(IMongoDatabase owner, IClientSessionHandle tx, InventorySource value, string hash, CancellationToken token) => AuthenticateHistoryAsync(context, owner, tx, value, hash, token);
        // The raw predicate only triggers a genuine owner read; it proves no quantity or authority.
        if (await Rows(LotValueStore.HeldHeads).Find(context.Tx, new BsonDocument {
            { "_id", selected.ValuationAllocationId }, { "held.allocationId", selected.ValuationAllocationId },
            { "remainingPackages", new BsonDocument("$lt", context.Input.Packages) },
            { "$and", new BsonArray { LotValueStore.Validator(LotValueStore.HeldHeads) } }
        }).AnyAsync(ct)) {
            var exhausted = await values.ReadHeldAsync(context.Tx, selected.ValuationAllocationId, Authenticate, ct);
            Current(context);
            if (exhausted is not null && exhausted.Key.Location != context.Input.Location) throw B.Broken();
            if (exhausted is not null && context.Input.Packages > exhausted.RemainingPackages) throw B.Changed();
        }
        StockReleaseSourceProof facts;
        using (SupplierDocumentMatchDiagnostics.Begin("release.current-source")) facts = await source.ReadCurrentAsync(context.Tx, selected, ct);
        if (!facts.IsCurrent || facts.Key.Location != context.Input.Location) throw B.Broken();
        using (SupplierDocumentMatchDiagnostics.Begin("release.activation")) await sources.RequireActivatedPoolAsync(context.Tx, facts.Pool, ct);
        var before = InventoryBson.ReadBalance(facts.Balance); LotValueBalance available; LotValueBalance? held;
        using (SupplierDocumentMatchDiagnostics.Begin("release.available-value")) available = await values.ReadForReleaseAsync(context.Tx, facts, Authenticate, ct);
        using (SupplierDocumentMatchDiagnostics.Begin("release.held-value")) held = await values.ReadHeldAsync(context.Tx, selected.ValuationAllocationId, Authenticate, ct);
        var remaining = held?.RemainingPackages ?? facts.ValuedPackages;
        long qualityRemaining;
        using (SupplierDocumentMatchDiagnostics.Begin("release.remaining-quality")) qualityRemaining = facts.AcceptedPackages - await ReleasedQualityAsync(context, selected.QualityAllocationId, ct);
        if (context.Input.Packages > remaining || context.Input.Packages > before.Balance.Held || context.Input.Packages > qualityRemaining) throw B.Changed();
        context.HeldRemaining = remaining; context.QualityRemaining = qualityRemaining;
        context.Source = facts; context.Before = before; context.After = before with { Balance = StockBalancePolicy.ReleaseHold(before.Balance, before.Balance.Revision, context.Input.Packages) };
        context.Expected = new(before.Balance.Revision, facts.Pool["revision"].AsInt64, facts.Pool["fence"].AsInt64, held?.Revision ?? 0, available.Revision);
        Current(context);
    }
    private string Binding(Context context, StockReleaseDecision? action = null) => CatalogRepository.Digest(new BsonDocument { { "actorId", context.Actor.UserId },
        { "actorGrantVersion", context.Actor.GrantVersion }, { "input", B.Input(context.Input) }, { "expected", B.Expected(context.Expected ?? throw B.Broken()) },
        { "source", (context.Source ?? throw B.Broken()).Snapshot }, { "proposalHash", context.Proposal?["snapshotHash"] ?? BsonNull.Value },
        { "action", action is null ? BsonNull.Value : action.Value.ToString().ToLowerInvariant() } });
    private static void Metadata(AuthorizationCommandMetadata value) { AuthorizationStore.ValidateMetadata(value); InventoryLedger.RequireText(value.Reason, 1000);
        if (value.IdempotencyKey.Length > 128 || value.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw StockReleaseInput.Invalid(); }
    private CommandAuthorization Command(Context context) {
        var approval = context.Permission == ApprovePermission ? new ApprovalRequest(1, 1, ProposedBy: context.Proposal!["actorId"].AsInt64) : null;
        return new(ScopePolicy.Authorize(context.Actor, context.Authority, context.Permission, context.Resource, Now, approval),
            ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory), context.Permission, new(context.Resource, approval));
    }
    public async Task<BsonDocument> PreviewAsync(ActorContext actor, StockReleaseInput input, CancellationToken ct = default) {
        input.Validate(); await StorageAsync(ct); return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = new Context(actor, authority, tx, ProposePermission, input); await PeopleAsync(context, token); await PrepareAsync(context, token); await FenceAsync(context, token);
            return new BsonDocument { { "input", B.Input(input) }, { "expected", B.Expected(context.Expected!) }, { "source", context.Source!.Snapshot },
                { "before", InventoryBson.Balance(context.Before!) }, { "after", InventoryBson.Balance(context.After!) }, { "binding", Binding(context) }, { "observedAt", Now.UtcDateTime } };
        }, ct);
    }
    private BsonDocument Row(Context context, long id, long auditId, AuthorizationCommandMetadata metadata, object body, string binding, bool decision) {
        var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "kind", decision ? "stock.release.decision" : "stock.release.proposal" },
            { "input", B.Input(context.Input) }, { "expected", B.Expected(context.Expected!) }, { "source", context.Source!.Snapshot },
            { "before", InventoryBson.Balance(context.Before!) }, { "after", InventoryBson.Balance(context.After!) }, { "binding", binding },
            { "people", new BsonArray(context.People!.Evidence.OrderBy(person => person.UserId).Select(R.Person)) }, { "actorId", context.Actor.UserId },
            { "actorGrantVersion", context.Actor.GrantVersion }, { "recordedAt", Now.UtcDateTime }, { "requestKey", metadata.IdempotencyKey },
            { "bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason) }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "auditId", auditId } };
        return row;
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, string operation, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(tx, B.Audit(row, operation), cancellationToken: ct); await Rows("outboxEvents").InsertOneAsync(tx, B.Outbox(row, operation), cancellationToken: ct);
    }
    public async Task<BsonDocument> ProposeAsync(ActorContext actor, StockReleaseInput input, StockReleaseExpectation expected, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input.Validate(); expected.Validate(); Metadata(metadata); await StorageAsync(ct); var id = (await ids.NextAsync(B.Proposals, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = new { input, expected, binding }; Context? context = null; BsonDocument? fresh = null;
        return await authorization.ExecuteCommandAsync(actor, B.Propose, metadata, body, async (authority, tx, token) => {
            fresh = null; context = new(actor, authority, tx, ProposePermission, input) { Operation = B.Propose, Key = metadata.IdempotencyKey }; await PeopleAsync(context, token); return Command(context);
        }, async (tx, token) => {
            var active = context ?? throw B.Broken(); active.History = null;
            // ExecuteCommandAsync fences scope rows after authorization and before
            // this callback. Capture ancestry only after those owned mutations.
            await FenceAsync(active, token);
            await PrepareAsync(active, token); if (active.Expected != expected || Binding(active) != binding) throw B.Changed(); await FenceAsync(active, token);
            var row = Row(active, id, audit, metadata, body, binding, false); row.Add("snapshotHash", B.Hash(row));
            await Rows(B.Proposals).InsertOneAsync(tx, row, cancellationToken: token); await EffectsAsync(tx, row, B.Propose, token); fresh = row; return B.Result(row);
        }, ct, async (completedAuthority, tx, token) => { var active = context ?? throw B.Broken(); active.History = null;
            var row = fresh ?? await ForKeyAsync(tx, actor.UserId, B.Propose, metadata.IdempotencyKey, token) ?? throw B.Broken();
            _ = await ProposalAsync(tx, row["_id"].AsInt64, token, reads: Reads(active)); await FenceAsync(active, token); }, validateCurrent: () => Current(context ?? throw B.Broken()));
    }
    private async Task<Context> ProposalContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, CancellationToken ct) {
        var reads = new HistoryReads(this, tx);
        var proposal = await ProposalAsync(tx, id, ct, reads: reads); var context = new Context(actor, authority, tx, ApprovePermission, B.Input(proposal["input"])) { Proposal = proposal, History = reads };
        Require(context); await RequireMakerAsync(context, ct); await PeopleAsync(context, ct); context.History = null; return context;
    }
    private async Task RequireMakerAsync(Context context, CancellationToken ct) {
        var proposal = context.Proposal ?? throw B.Broken(); var identity = await Rows("identities").Find(context.Tx, new BsonDocument("_id", proposal["actorId"])).SingleOrDefaultAsync(ct);
        if (identity is null || identity["status"] != "active" || identity["grantVersion"] != proposal["actorGrantVersion"]) throw new AuthorizationDeniedException("original_maker_authority_changed");
        var maker = new ActorContext(identity["_id"].AsInt64, identity["issuer"].AsString, identity["subject"].AsString, identity["grantVersion"].AsInt64,
            new DateTimeOffset(proposal["recordedAt"].ToUniversalTime()), FactorAssurance.Unknown);
        var current = await authorization.LoadAsync(maker, context.Tx, ct); var query = ScopePolicy.CompileParticipantGrants(maker, current, ProposePermission, Now); AuthorizationStore.Require(query);
        if (!query.Matches(context.Resource)) throw new AuthorizationDeniedException("original_maker_scope_changed");
        var clauses = query.Clauses.Where(clause => clause.Matches(context.Resource)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(maker, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            current.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), current.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        context.History = null;
    }
    private async Task PrepareRejectAsync(Context context, CancellationToken ct) {
        await new StockSourceTopologyFence(database).FenceAsync(context.Tx, context.Input.Location.OrganizationId.Value, ct);
        var proposal = context.Proposal ?? throw B.Broken(); var reads = Reads(context); var origin = await reads.OriginAsync(context.Input.ReceiptId, ct);
        context.Source = await source.ReadOriginAsync(context.Tx, Selection(proposal), origin, reads.Reader(origin), ct);
        context.Expected = B.Expected(proposal["expected"]); context.Before = InventoryBson.ReadBalance(proposal["before"].AsBsonDocument);
        context.After = context.Before; Current(context);
    }
    public async Task<BsonDocument> PreviewDecisionAsync(ActorContext actor, long proposalId, StockReleaseDecision decision, string policy, CancellationToken ct = default) {
        if (proposalId <= 0 || !Enum.IsDefined(decision) || policy != LotValuePolicy.ProportionalFloorPolicy) throw StockReleaseInput.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, token);
            if (await Rows(B.Decisions).Find(tx, new BsonDocument("proposalId", proposalId)).AnyAsync(token)) throw B.Changed();
            if (decision == StockReleaseDecision.Approve) await PrepareAsync(context, token); else await PrepareRejectAsync(context, token);
            await FenceAsync(context, token);
            return new BsonDocument { { "proposal", context.Proposal! }, { "expected", B.Expected(context.Expected!) }, { "policy", policy },
                { "before", InventoryBson.Balance(context.Before!) }, { "after", InventoryBson.Balance(context.After!) }, { "source", context.Source!.Snapshot },
                { "decision", decision.ToString().ToLowerInvariant() }, { "binding", Binding(context, decision) }, { "observedAt", Now.UtcDateTime } };
        }, ct);
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long proposalId, long expectedRevision, StockReleaseDecision decision,
        string policy, StockReleaseExpectation expected, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (proposalId <= 0 || expectedRevision != 1 || !Enum.IsDefined(decision) || policy != LotValuePolicy.ProportionalFloorPolicy) throw StockReleaseInput.Invalid(); expected.Validate(); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Decisions, ct)).Value; var audit = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = new { proposalId, expectedRevision, decision, policy, expected, binding }; Context? context = null; BsonDocument? fresh = null;
        try {
            return await authorization.ExecuteCommandAsync(actor, B.Decide, metadata, body, async (authority, tx, token) => {
                fresh = null; context = await ProposalContextAsync(actor, authority, tx, proposalId, token); context.Operation = B.Decide; context.Key = metadata.IdempotencyKey; return Command(context);
            }, async (tx, token) => {
                var active = context ?? throw B.Broken(); active.History = null;
                await FenceAsync(active, token);
                if (await Rows(B.Decisions).Find(tx, new BsonDocument("proposalId", proposalId)).AnyAsync(token)) throw B.Changed();
                if (decision == StockReleaseDecision.Approve) await PrepareAsync(active, token); else await PrepareRejectAsync(active, token);
                if (active.Expected != expected || Binding(active, decision) != binding || active.Input.Policy != policy || active.Source!.Snapshot != active.Proposal!["source"]) throw B.Changed();
                await FenceAsync(active, token); if (decision == StockReleaseDecision.Reject) active.After = active.Before;
                var row = Row(active, id, audit, metadata, body, binding, true); row.Add("proposalId", proposalId); row.Add("proposalHash", active.Proposal!["snapshotHash"]);
                row.Add("decision", decision.ToString().ToLowerInvariant()); row.Add("phase", "preparing"); row.Add("movementId", BsonNull.Value);
                row.Add("valueTransitionId", BsonNull.Value); row.Add("valueTransitionHash", BsonNull.Value); row.Add("transferredValue", BsonNull.Value);
                row.Add("causeHash", B.CauseHash(row)); row.Add("snapshotHash", B.Hash(row)); active.Decision = row; inFlight[tx] = active;
                await Rows(B.Decisions).InsertOneAsync(tx, row, cancellationToken: token);
                if (decision == StockReleaseDecision.Approve) {
                    var reader = new LedgerSource(this, active); var ledger = new InventoryLedger(database, clock, [reader]);
                    var movement = await ledger.ApplyAsync(tx, actor, B.Source(id, active.Source!.PoolId), 1, new(metadata.Reason, metadata.CorrelationId), token);
                    // Physical ownership changed within this attempt. Start fresh ancestor
                    // captures for post-write value history, retaining no pre-write view.
                    active.History = new HistoryReads(this, tx);
                    var proof = await HeldStockReleaseValueProof.CreateAsync(this, database, tx, id, token); var value = await values.ReleaseAsync(tx, proof, token);
                    row["movementId"] = movement.Id; row["valueTransitionId"] = value.TransitionId; row["valueTransitionHash"] = value.TransitionHash; row["transferredValue"] = value.TransferredValue.ToString();
                }
                row["phase"] = "committed"; row["snapshotHash"] = B.Hash(row);
                if ((await Rows(B.Decisions).ReplaceOneAsync(tx, new BsonDocument { { "_id", id }, { "phase", "preparing" } }, row, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
                await EffectsAsync(tx, row, B.Decide, token); fresh = row; return B.Result(row);
            }, ct, async (completedAuthority, tx, token) => {
                var active = context ?? throw B.Broken(); active.History = null;
                var row = fresh ?? await ForKeyAsync(tx, actor.UserId, B.Decide, metadata.IdempotencyKey, token) ?? throw B.Broken();
                _ = await DecisionAsync(tx, row["_id"].AsInt64, token, reads: Reads(active)); await FenceAsync(active, token);
            }, validateCurrent: () => Current(context ?? throw B.Broken()));
        } finally { if (context is not null) inFlight.TryRemove(context.Tx, out _); }
    }
    internal static bool IsLedgerReleaseSource(IInventorySourceReader reader) => reader is LedgerSource;
    private sealed class LedgerSource(StockReleaseService owner, Context context) : IInventorySourceReader {
        public string Namespace => B.Namespace; public IMongoDatabase Database => owner.database;
        public async Task<InventorySourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, InventorySource value, long revision, CancellationToken ct) {
            owner.Current(context); var row = context.Decision ?? throw B.Broken();
            if (!ReferenceEquals(context.Tx, tx) || actor != context.Actor || revision != 1 || value != B.Source(row["_id"].AsInt64, context.Source!.PoolId) ||
                row["phase"] != "preparing" || row["decision"] != "approve" || row["causeHash"] != B.CauseHash(row) ||
                await owner.Rows(B.Decisions).Find(tx, row).SingleOrDefaultAsync(ct) is null) throw B.Broken();
            await owner.sources.RequireActivatedPoolAsync(tx, context.Source.Pool, ct); await owner.FenceAsync(context, ct);
            var before = context.Before ?? throw B.Broken();
            return new(value, 1, new(before.PhysicalPoolId, before.Balance.Key.Location, context.Expected!.PoolRevision),
                [new(before.Id, before.PhysicalPoolId, before.Balance.Key, before.Balance.Revision, InventoryBalanceAction.ReleaseHold, context.Input.Packages)]);
        }
    }
    internal async Task<(BsonDocument Row, StockReleaseSourceProof Source, InventoryBalanceDocument Before, InventoryBalanceDocument After, StockReleaseExpectation Expected)> ValueAuthorityAsync(
        IMongoDatabase owner, IClientSessionHandle tx, long decisionId, CancellationToken ct) {
        if (!ReferenceEquals(owner.Client, database.Client) || owner.DatabaseNamespace != database.DatabaseNamespace || !inFlight.TryGetValue(tx, out var context)) throw B.Broken();
        Current(context); var row = context.Decision ?? throw B.Broken();
        if (row["_id"] != decisionId || row["phase"] != "preparing" || row["decision"] != "approve" || row["causeHash"] != B.CauseHash(row) ||
            await Rows(B.Decisions).Find(tx, row).SingleOrDefaultAsync(ct) is null) throw B.Broken();
        await FenceAsync(context, ct); return (row.DeepClone().AsBsonDocument, context.Source!, context.Before!, context.After!, context.Expected!);
    }
    internal void RequireValueAuthority(IMongoDatabase owner, IClientSessionHandle tx, long decisionId, string hash) {
        if (!ReferenceEquals(owner.Client, database.Client) || owner.DatabaseNamespace != database.DatabaseNamespace || !inFlight.TryGetValue(tx, out var context)) throw B.Broken();
        Current(context); var row = context.Decision ?? throw B.Broken();
        if (row["_id"] != decisionId || row["phase"] != "preparing" || row["decision"] != "approve" || row["causeHash"] != hash || B.CauseHash(row) != hash) throw B.Broken();
    }
    private async Task<BsonDocument> ProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct, HeldReceiptOriginProof? retainedOrigin = null, HistoryReads? reads = null) {
        var row = await Rows(B.Proposals).Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray { StockReleaseSchemas.Validator(B.Proposals) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (row["snapshotHash"] != B.Hash(row) || B.Input(row["input"]).Policy != LotValuePolicy.ProportionalFloorPolicy) throw B.Broken();
        var receiptId = row["input"]["receiptId"].AsInt64;
        if (retainedOrigin is not null && retainedOrigin.ReceiptId != receiptId) throw B.Broken();
        reads?.Require(this, tx);
        var origin = retainedOrigin ?? (reads is null ? await source.CaptureOriginAsync(tx, receiptId, ct) : await reads.OriginAsync(receiptId, ct));
        var proof = reads is null ? await source.ReadOriginAsync(tx, Selection(row), origin, ct)
            : await source.ReadOriginAsync(tx, Selection(row), origin, reads.Reader(origin), ct);
        if (proof.Snapshot != row["source"]) throw B.Broken();
        var input = B.Input(row["input"]); var expected = B.Expected(row["expected"]); var before = InventoryBson.ReadBalance(row["before"].AsBsonDocument);
        var after = InventoryBson.ReadBalance(row["after"].AsBsonDocument); var maker = R.Person(row["people"][0]);
        if (row["people"].AsBsonArray.Count != 1 || maker.UserId != row["actorId"].AsInt64 || maker.Verification != PersonVerification.Verified || maker.PersonId <= 0 || maker.Version <= 0 ||
            before.Id != proof.BalanceId || before.PhysicalPoolId != proof.PoolId || before.Balance.Key != proof.Key || input.Location != proof.Key.Location ||
            expected.BalanceRevision != before.Balance.Revision || after != (before with { Balance = StockBalancePolicy.ReleaseHold(before.Balance, before.Balance.Revision, input.Packages) }) ||
            row["bodyHash"] != AuthorizationStore.CommandHash(new { input, expected, binding = row["binding"].AsString }, row["reason"].AsString)) throw B.Broken();
        await EffectsProofAsync(tx, row, B.Propose, ct); return row;
    }
    private static StockReleaseSelection Selection(BsonDocument row) => new(row["input"]["receiptId"].AsInt64, row["input"]["qualityAllocationId"].AsInt64, row["input"]["valuationAllocationId"].AsInt64);
    private async Task<BsonDocument> DecisionAsync(IClientSessionHandle tx, long id, CancellationToken ct, HeldReceiptOriginProof? retainedOrigin = null, HistoryReads? reads = null) {
        var row = await Rows(B.Decisions).Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray { StockReleaseSchemas.Validator(B.Decisions) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (row["phase"] != "committed" || row["snapshotHash"] != B.Hash(row) || row["causeHash"] != B.CauseHash(row)) throw B.Broken();
        var proposal = await ProposalAsync(tx, row["proposalId"].AsInt64, ct, retainedOrigin, reads);
        if (row["proposalHash"] != proposal["snapshotHash"] || row["input"] != proposal["input"] || row["source"] != proposal["source"]) throw B.Broken();
        var people = row["people"].AsBsonArray.Select(R.Person).ToDictionary(person => person.UserId); var maker = R.Person(proposal["people"][0]);
        if (people.Count != 2 || !people.TryGetValue(maker.UserId, out var original) || original.PersonId != maker.PersonId ||
            !people.TryGetValue(row["actorId"].AsInt64, out var approver) || !PersonIndependencePolicy.AreIndependent(approver, original, maker) ||
            row["recordedAt"].CompareTo(proposal["recordedAt"]) < 0 || row["recordedAt"].ToUniversalTime() > Now.UtcDateTime) throw B.Broken();
        var before = InventoryBson.ReadBalance(row["before"].AsBsonDocument); var after = InventoryBson.ReadBalance(row["after"].AsBsonDocument);
        var input = B.Input(row["input"]); var expected = B.Expected(row["expected"]); var decision = row["decision"] == "approve" ? StockReleaseDecision.Approve : StockReleaseDecision.Reject;
        var expectedBinding = CatalogRepository.Digest(new BsonDocument { { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] },
            { "input", row["input"] }, { "expected", row["expected"] }, { "source", row["source"] }, { "proposalHash", proposal["snapshotHash"] }, { "action", row["decision"] } });
        if (row["binding"] != expectedBinding || row["bodyHash"] != AuthorizationStore.CommandHash(new { proposalId = row["proposalId"].AsInt64,
            expectedRevision = 1L, decision, policy = input.Policy, expected, binding = row["binding"].AsString }, row["reason"].AsString) ||
            before.Id != proposal["before"]["_id"].AsInt64 || before.Balance.Key != InventoryBson.ReadBalance(proposal["before"].AsBsonDocument).Balance.Key ||
            expected.BalanceRevision != before.Balance.Revision) throw B.Broken();
        if (row["decision"] == "approve") {
            if (after != (before with { Balance = StockBalancePolicy.ReleaseHold(before.Balance, before.Balance.Revision, row["input"]["packages"].AsInt64) })) throw B.Broken();
            await AuthenticateTransitionLinkAsync(tx, row, ct);
        } else if (after != before || !row["movementId"].IsBsonNull || !row["valueTransitionId"].IsBsonNull || !row["valueTransitionHash"].IsBsonNull || !row["transferredValue"].IsBsonNull) throw B.Broken();
        await EffectsProofAsync(tx, row, B.Decide, ct); return row;
    }
    private async Task AuthenticateTransitionLinkAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        var stock = B.Source(row["_id"].AsInt64, row["before"]["physicalPoolId"].AsInt64);
        var movement = await Rows("movements").Find(tx, new BsonDocument { { "_id", row["movementId"] },
            { "$and", new BsonArray { InventoryMovementSchemas.Validator() } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var leg = movement["legs"].AsBsonArray.SingleOrDefault() ?? throw B.Broken();
        if (movement["sourceEvent"] != stock.EventKey || movement["actorId"] != row["actorId"] || movement["actorGrantVersion"] != row["actorGrantVersion"] ||
            movement["sourceRevision"] != 1L || movement["reason"] != row["reason"] || movement["correlationId"] != row["correlationId"] ||
            movement["physicalPoolId"] != stock.Occurrence || movement["source"]["namespace"] != stock.Namespace ||
            movement["source"]["documentId"] != stock.DocumentId || movement["source"]["eventKind"] != stock.EventKind || movement["source"]["occurrence"] != stock.Occurrence ||
            leg["action"] != "ReleaseHold" || leg["before"] != row["before"] || leg["after"] != row["after"] || leg["packages"] != row["input"]["packages"] ||
            movement["recordedAt"].CompareTo(row["recordedAt"]) < 0) throw B.Broken();
        var transition = await Rows(LotValueStore.Transitions).Find(tx, new BsonDocument { { "_id", row["valueTransitionId"] },
            { "$and", new BsonArray { LotValueStore.Validator(LotValueStore.Transitions) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (transition["transitionHash"] != row["valueTransitionHash"] || transition["transitionHash"] != LotValueStore.Hash(transition) ||
            transition["sourceEvent"] != stock.EventKey || transition["sourceHash"] != row["causeHash"] || transition["operation"] != "release" ||
            transition["packages"] != row["input"]["packages"] || transition["held"]["allocationId"] != row["input"]["valuationAllocationId"] ||
            transition["held"]["allocationHash"] != row["source"]["valuationAllocationHash"] || transition["transferredValue"] != row["transferredValue"] ||
            transition["held"]["acquisitionPackages"] != row["source"]["valuedPackages"] || transition["held"]["acquisitionValue"] != row["source"]["capitalizedValue"] ||
            transition["poolId"] != stock.Occurrence || transition["policy"] != row["input"]["policy"] || transition["key"] != row["source"]["stockKey"] ||
            transition["source"]["namespace"] != stock.Namespace || transition["source"]["documentId"] != stock.DocumentId ||
            transition["source"]["eventKind"] != stock.EventKind || transition["source"]["occurrence"] != stock.Occurrence ||
            transition["recordedAt"] != row["recordedAt"] || transition["availableId"] != row["before"]["_id"]) throw B.Broken();
        LotValueStore.RequireTransition(transition);
        var expected = B.Expected(row["expected"]); var before = InventoryBson.ReadBalance(row["before"].AsBsonDocument);
        var after = InventoryBson.ReadBalance(row["after"].AsBsonDocument);
        if (movement["poolRevision"] != expected.PoolRevision || leg["balanceId"] != before.Id ||
            movement["recordedAt"].ToUniversalTime() > Now.UtcDateTime || transition["held"]["balanceId"] != before.Id ||
            transition["held"]["key"] != row["source"]["stockKey"] ||
            transition["heldBefore"]["revision"] != expected.HeldValueRevision || transition["heldAfter"]["revision"] != checked(expected.HeldValueRevision + 1) ||
            transition["availableBefore"]["revision"] != expected.LotValueRevision || transition["availableAfter"]["revision"] != checked(expected.LotValueRevision + 1) ||
            transition["availableBefore"]["remainingPackages"].AsInt64 != checked(before.Balance.OnHand - before.Balance.Held - before.Balance.Expired) ||
            transition["availableAfter"]["remainingPackages"].AsInt64 != checked(after.Balance.OnHand - after.Balance.Held - after.Balance.Expired)) throw B.Broken();
        if (expected.HeldValueRevision == 0 && (transition["heldBefore"]["remainingPackages"] != row["source"]["valuedPackages"].ToInt64() ||
            transition["heldBefore"]["remainingValue"] != row["source"]["capitalizedValue"])) throw B.Broken();
        if (expected.LotValueRevision == 0 && (transition["availableBefore"]["remainingPackages"] != 0L || transition["availableBefore"]["remainingValue"] != "0.00")) throw B.Broken();
        await RequirePreviousValueAsync(tx, transition, "previousHeld", "heldBefore", expected.HeldValueRevision, true, ct);
        await RequirePreviousValueAsync(tx, transition, "previousAvailable", "availableBefore", expected.LotValueRevision, false, ct);
    }
    private async Task RequirePreviousValueAsync(IClientSessionHandle tx, BsonDocument transition, string linkField, string beforeField,
        long revision, bool held, CancellationToken ct) {
        var link = transition[linkField]; if (revision == 0) { if (!link.IsBsonNull) throw B.Broken(); return; }
        if (!link.IsBsonDocument || link["transitionId"].AsInt64 >= transition["_id"].AsInt64) throw B.Broken();
        var previous = await Rows(LotValueStore.Transitions).Find(tx, new BsonDocument { { "_id", link["transitionId"] },
            { "$and", new BsonArray { LotValueStore.Validator(LotValueStore.Transitions) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (previous["transitionHash"] != link["transitionHash"] || previous["transitionHash"] != LotValueStore.Hash(previous) ||
            previous["poolId"] != transition["poolId"] || previous["recordedAt"].CompareTo(transition["recordedAt"]) > 0) throw B.Broken();
        LotValueStore.RequireTransition(previous);
        if (held) {
            if (previous["operation"] != "release" || previous["held"] != transition["held"] || previous["heldAfter"] != transition[beforeField]) throw B.Broken();
        } else if (previous["availableId"] == transition["availableId"]) {
            if (previous["key"] != transition["key"] || previous["availableAfter"] != transition[beforeField]) throw B.Broken();
        } else if (previous["destinationId"] != transition["availableId"] || previous["destinationKey"] != transition["key"] || previous["destinationAfter"] != transition[beforeField]) throw B.Broken();
    }
    internal async Task AuthenticateSourceAsync(IMongoDatabase owner, IClientSessionHandle tx, InventorySource selected, string hash, CancellationToken ct) {
        var reads = inFlight.TryGetValue(tx, out var context) ? Reads(context) : new HistoryReads(this, tx);
        await AuthenticateSourceAsync(owner, tx, selected, hash, reads, ct);
    }
    private async Task AuthenticateSourceAsync(IMongoDatabase owner, IClientSessionHandle tx, InventorySource selected, string hash, HistoryReads reads, CancellationToken ct) {
        if (!ReferenceEquals(owner.Client, database.Client) || owner.DatabaseNamespace != database.DatabaseNamespace || selected.Namespace != B.Namespace ||
            selected.EventKind != "releaseHeld") throw B.Broken();
        reads.Require(this, tx); var row = await DecisionAsync(tx, selected.DocumentId, ct, reads: reads);
        if (row["decision"] != "approve" || row["causeHash"] != hash || selected != B.Source(selected.DocumentId, row["before"]["physicalPoolId"].AsInt64)) throw B.Broken();
    }
    /// <summary>Immutable owning-command authentication for the held movement reader.
    /// The caller supplies its already authenticated typed receipt genesis; no current
    /// held balance or availability is inferred by this historical link.</summary>
    internal static async Task RequireMovementSourceAsync(IMongoDatabase database, MongoUnitOfWork unit, IClock clock,
        IClientSessionHandle tx, BsonDocument movement, HeldReceiptOriginProof origin, CancellationToken ct) {
        origin.Require(database, tx);
        var ids = new IdAllocator(database); var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var requests = new ProcurementRequestService(database, unit, ids, clock, catalog);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog, requests, new SupplierService(database, unit, ids, clock, catalog));
        var sources = new StockSourceSetupService(database, unit, ids, clock); var receiving = new ProcurementReceivingService(database, unit, ids, clock, catalog, orders, requests);
        var valuations = new ProcurementStockValuationService(database, unit, ids, clock, catalog, orders, receiving, sources);
        var owner = new StockReleaseService(database, unit, ids, clock, catalog, orders, sources, valuations); await owner.StorageAsync(ct);
        var source = movement["source"].AsBsonDocument;
        if (source["namespace"] != B.Namespace || source["eventKind"] != "releaseHeld" || movement["sourceRevision"] != 1L) throw B.Broken();
        var reads = new HistoryReads(owner, tx); reads.Seed(origin);
        var decision = await owner.DecisionAsync(tx, source["documentId"].AsInt64, ct, origin, reads);
        if (decision["decision"] != "approve" || decision["input"]["receiptId"] != origin.ReceiptId || decision["movementId"] != movement["_id"] ||
            source["occurrence"] != decision["before"]["physicalPoolId"] || movement["poolRevision"] != decision["expected"]["poolRevision"] ||
            await owner.Rows("movements").Find(tx, new BsonDocument("_id", movement["_id"])).SingleOrDefaultAsync(ct) != movement) throw B.Broken();
        // DecisionAsync already proves the selected owner source and balance/key
        // through its exact proposal. Only read-only link/effect checks intervened.
        await origin.RequireAsync(database, tx, ct);
    }
    private async Task<long> ReleasedQualityAsync(Context context, long qualityId, CancellationToken ct) {
        var tx = context.Tx; await RequireOwnerMembershipAsync(context, ct);
        var rows = await Rows(B.Decisions).Find(tx, new BsonDocument { { "input.qualityAllocationId", qualityId }, { "decision", "approve" } }).Limit(4097).ToListAsync(ct);
        if (rows.Count > 4096) throw B.Broken(); long total = 0;
        foreach (var row in rows) { var proved = await DecisionAsync(tx, row["_id"].AsInt64, ct, reads: Reads(context)); total = checked(total + proved["input"]["packages"].AsInt64); } return total;
    }
    private async Task RequireOwnerMembershipAsync(Context context, CancellationToken ct) {
        await RequireOwnerMembershipAsync(context.Tx, context, ct);
    }
    private async Task RequireOwnerMembershipAsync(IClientSessionHandle tx, Context? current, CancellationToken ct) {
        foreach (var (table, operation) in new[] { (B.Proposals, B.Propose), (B.Decisions, B.Decide) }) {
            var rows = await Rows(table).Find(tx, new BsonDocument()).Limit(4097).ToListAsync(ct); if (rows.Count > 4096) throw B.Broken();
            foreach (var (effect, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") }) {
                var marker = effect switch {
                    "auditEvents" => new BsonDocument("record.kind", table == B.Proposals ? "stock.release.proposal" : "stock.release.decision"),
                    "outboxEvents" => new BsonDocument("sourceKey", new BsonRegularExpression("^" + System.Text.RegularExpressions.Regex.Escape(operation) + ":")),
                    _ => new BsonDocument { { "result.valueTransitionId", new BsonDocument("$exists", true) },
                        { "result.movementId", new BsonDocument("$exists", true) }, { "result.transferredValue", new BsonDocument("$exists", true) },
                        { "result.status", table == B.Proposals ? "proposed" : new BsonDocument("$in", new BsonArray { "approved", "rejected" }) } }
                };
                var effects = await Rows(effect).Find(tx, new BsonDocument("$or", new BsonArray { new BsonDocument(field, operation), marker })).Limit(4098).ToListAsync(ct);
                if (effect == "idempotencyRecords") {
                    var pending = effects.Where(row => row["result"] == new BsonDocument()).ToArray();
                    if (pending.Length > 1 || pending.Any(row => current is null || current.Operation != operation || current.Key != row["key"] || row["actorId"] != current.Actor.UserId)) throw B.Broken();
                    effects.RemoveAll(row => pending.Any(own => own == row));
                }
                if (effects.Count != rows.Count) throw B.Broken();
            }
            foreach (var row in rows) {
                if (row["snapshotHash"] != B.Hash(row) || table == B.Decisions && row["phase"] != "committed") throw B.Broken();
                await EffectsProofAsync(tx, row, operation, ct);
            }
        }
    }
    // The outer source reader holds the organization's topology lane. Every
    // release writer joins that lane; there is no inferred empty owner/head.
    internal async Task<(long MembershipCount, long OpenCount)> ReadObligationAsync(IClientSessionHandle tx, long poolId, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx); if (poolId <= 0) throw B.Broken(); await StorageAsync(ct);
        await RequireOwnerMembershipAsync(tx, null, ct);
        var proposals = await Rows(B.Proposals).Find(tx, new BsonDocument()).Limit(4097).ToListAsync(ct);
        var decisions = await Rows(B.Decisions).Find(tx, new BsonDocument()).Limit(4097).ToListAsync(ct);
        if (proposals.Count > 4096 || decisions.Count > 4096) throw B.Broken();
        var reads = new HistoryReads(this, tx);
        foreach (var proposal in proposals) _ = await ProposalAsync(tx, proposal["_id"].AsInt64, ct, reads: reads);
        foreach (var decision in decisions) _ = await DecisionAsync(tx, decision["_id"].AsInt64, ct, reads: reads);
        var byProposal = decisions.ToDictionary(row => row["proposalId"].AsInt64);
        var approved = decisions.Where(row => row["decision"] == "approve").ToDictionary(row => row["_id"].AsInt64);
        foreach (var (table, link) in new[] { ("movements", "movementId"), (LotValueStore.Transitions, "valueTransitionId") }) {
            var sourceFilter = new BsonDocument("$or", new BsonArray { new BsonDocument("source.namespace", B.Namespace),
                new BsonDocument("sourceEvent", new BsonRegularExpression("^" + System.Text.RegularExpressions.Regex.Escape(B.Namespace) + ":")),
                new BsonDocument(table == "movements" ? "legs.action" : "operation", table == "movements" ? "ReleaseHold" : "release") });
            var effects = await Rows(table).Find(tx, sourceFilter).Limit(4097).ToListAsync(ct);
            if (effects.Count > 4096 || effects.Count != approved.Count) throw B.Broken();
            foreach (var effect in effects) {
                if (!effect.TryGetValue("source", out var value) || !value.IsBsonDocument || !value.AsBsonDocument.TryGetValue("documentId", out var id) ||
                    !id.IsInt64 || !approved.TryGetValue(id.AsInt64, out var decision) || effect["_id"] != decision[link]) throw B.Broken();
                // DecisionAsync authenticates the precise owning movement and finite value
                // transition, not just this namespace or its stored hash.
                await AuthenticateTransitionLinkAsync(tx, decision, ct);
            }
        }
        var members = proposals.LongCount(row => row["source"]["poolId"] == poolId) + decisions.LongCount(row => row["source"]["poolId"] == poolId);
        var open = proposals.LongCount(row => row["source"]["poolId"] == poolId && !byProposal.ContainsKey(row["_id"].AsInt64));
        return (members, open);
    }
    private async Task EffectsProofAsync(IClientSessionHandle tx, BsonDocument row, string operation, CancellationToken ct) {
        if (await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) != B.Audit(row, operation)) throw B.Broken();
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (receipt["bodyHash"] != row["bodyHash"] || receipt["result"] != B.Result(row)) throw B.Broken();
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", operation + ":" + row["_id"].AsInt64)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (effect["kind"] != operation || effect["payload"] != B.Result(row) || effect["createdAt"] != row["recordedAt"]) throw B.Broken();
    }
    private async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string operation, string key, CancellationToken ct) =>
        await Rows(operation == B.Propose ? B.Proposals : B.Decisions).Find(tx, new BsonDocument { { "actorId", actor }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
    public async Task<BsonDocument?> ReadAsync(ActorContext actor, long proposalId, CancellationToken ct = default) {
        await StorageAsync(ct); return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var reads = new HistoryReads(this, tx); var proposal = await ProposalAsync(tx, proposalId, token, reads: reads);
            var context = new Context(actor, authority, tx, ProposePermission, B.Input(proposal["input"])) { History = reads };
            if (!ScopePolicy.Compile(actor, authority, ProposePermission, Now).Matches(context.Resource)) context.Permission = ApprovePermission;
            var decision = await Rows(B.Decisions).Find(tx, new BsonDocument("proposalId", proposalId)).SingleOrDefaultAsync(token);
            if (decision is not null) decision = await DecisionAsync(tx, decision["_id"].AsInt64, token, reads: reads); await FenceAsync(context, token);
            return new BsonDocument { { "proposal", proposal }, { "decision", (BsonValue?)decision ?? BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument?> RecoverAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        if (operation is not (B.Propose or B.Decide) || string.IsNullOrEmpty(key) || key.Length > 128 ||
            key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw StockReleaseInput.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            var found = await ForKeyAsync(tx, actor.UserId, operation, key, token);
            if (found is null) {
                var permission = operation == B.Propose ? ProposePermission : ApprovePermission; var query = ScopePolicy.Compile(actor, authority, permission, Now); AuthorizationStore.Require(query);
                await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, query.Clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
                    authority.Grants.Where(grant => query.Clauses.Any(clause => clause.GrantId == grant.Id)),
                    authority.Memberships.Where(member => query.Clauses.Any(clause => clause.MembershipId == member.Id)), tx, token);
                AuthorizationStore.Require(ScopePolicy.Compile(actor, authority, permission, Now)); return null;
            }
            var reads = new HistoryReads(this, tx);
            var row = operation == B.Propose ? await ProposalAsync(tx, found["_id"].AsInt64, token, reads: reads) : await DecisionAsync(tx, found["_id"].AsInt64, token, reads: reads);
            var context = new Context(actor, authority, tx, operation == B.Propose ? ProposePermission : ApprovePermission, B.Input(row["input"])) { History = reads };
            if (operation == B.Decide) { context.Proposal = await ProposalAsync(tx, row["proposalId"].AsInt64, token, reads: reads); await PeopleAsync(context, token); }
            await FenceAsync(context, token); return B.Result(row);
        }, ct);
    }

    private static ResourceScope DiscoveryResource(StockLocation location) => new(FulfillmentOrganizationId: location.OrganizationId.Value,
        ShopId: location.ShopId.Value, WarehouseId: location.WarehouseId.Value, ChannelId: location.ChannelId.Value);
    private string DiscoveryPermission(ActorContext actor, AuthorizationSnapshot authority, StockLocation location, bool makerOnly) {
        location.Validate(); var resource = DiscoveryResource(location);
        if (ScopePolicy.Compile(actor, authority, ProposePermission, Now).Matches(resource)) return ProposePermission;
        if (!makerOnly && ScopePolicy.Compile(actor, authority, ApprovePermission, Now).Matches(resource) &&
            ScopePolicy.Compile(actor, authority, Permissions.ProcurementStockValuationDecide, Now).Matches(resource)) return ApprovePermission;
        throw new AuthorizationDeniedException("scope_denied");
    }
    private async Task FenceDiscoveryAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ResourceScope[] resources, string[] permissions, CancellationToken ct) {
        var queries = permissions.Select(permission => ScopePolicy.Compile(actor, authority, permission, Now)).ToArray();
        if (!queries.Any(query => query.IsAllowed) || resources.Any(resource => !queries.Any(query => query.Matches(resource)))) throw new AuthorizationDeniedException("scope_denied");
        var clauses = queries.Where(query => query.IsAllowed).SelectMany(query => query.Clauses)
            .Where(clause => resources.Length == 0 || resources.Any(clause.Matches)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(resource => ScopePolicy.RequiredNodes(resource, authority.Directory))).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        if (actor.Session is { } session && !await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId,
            actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, Now)).AnyAsync(ct)) throw new AuthorizationDeniedException("stale_or_inactive_session");
        if (resources.Any(resource => !permissions.Any(permission => ScopePolicy.Compile(actor, authority, permission, Now).Matches(resource))))
            throw new AuthorizationDeniedException("scope_denied");
        ct.ThrowIfCancellationRequested();
    }
    private Task<T> DiscoveryReadAsync<T>(ActorContext actor, StockLocation location, bool makerOnly,
        Func<AuthorizationSnapshot, IClientSessionHandle, CancellationToken, Task<T>> read, CancellationToken ct) =>
        authorization.ReadAsync(actor, async (authority, tx, token) => {
            var permission = DiscoveryPermission(actor, authority, location, makerOnly);
            await new StockSourceTopologyFence(database).FenceAsync(tx, location.OrganizationId.Value, token);
            var result = await read(authority, tx, token);
            var permissions = permission == ApprovePermission ? new[] { ApprovePermission, Permissions.ProcurementStockValuationDecide } : [ProposePermission];
            await FenceDiscoveryAsync(actor, authority, tx, [DiscoveryResource(location)], permissions, token);
            _ = DiscoveryPermission(actor, authority, location, makerOnly); return result;
        }, ct);

    /// <summary>Actual current release assignments and reviewed warehouse labels.
    /// Neither count nor source-setup permission is required to discover release work.</summary>
    public async Task<ImmutableArray<StockSourceWarehouseOption>> LocationsAsync(ActorContext actor, CancellationToken ct = default) {
        await StorageAsync(ct); return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var queries = new[] { ProposePermission, ApprovePermission }.Select(permission => ScopePolicy.Compile(actor, authority, permission, Now)).Where(query => query.IsAllowed).ToArray();
            if (queries.Length == 0) throw new AuthorizationDeniedException("scope_denied");
            var scopes = new HashSet<GrantScope>();
            foreach (var clause in queries.SelectMany(query => query.Clauses)) {
                if (clause.Scope.Kind == GrantScopeKind.FulfillmentWarehouse) scopes.Add(clause.Scope);
                if (clause.Scope.Kind == GrantScopeKind.FulfillmentShop)
                    foreach (var warehouse in authority.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Warehouse &&
                        node.Status == AuthorizationStatus.Active && node.ParentId == clause.Scope.ShopId))
                        scopes.Add(clause.Scope with { Kind = GrantScopeKind.FulfillmentWarehouse, WarehouseId = warehouse.Id });
            }
            var locations = scopes.Select(scope => new StockLocation(new(scope.OrganizationId!.Value), new(scope.ShopId!.Value),
                    new(scope.WarehouseId!.Value), new(scope.ChannelId!.Value)))
                .Where(location => queries.Any(query => query.Matches(DiscoveryResource(location))))
                .OrderBy(location => location.OrganizationId.Value).ThenBy(location => location.ShopId.Value).ThenBy(location => location.WarehouseId.Value).ThenBy(location => location.ChannelId.Value).ToArray();
            if (locations.Length > 256) throw B.Broken();
            var nodes = locations.SelectMany(location => ScopePolicy.RequiredNodes(DiscoveryResource(location), authority.Directory)).Distinct().ToArray();
            var profiles = await Rows("organizationProfiles").Find(tx, new BsonDocument("_id", new BsonDocument("$in", new BsonArray(nodes.Select(node => node.Id))))).ToListAsync(token);
            string Name(long id, ScopeNodeKind kind) {
                var node = authority.Directory.Find(id, kind) ?? throw B.Broken();
                var profile = profiles.SingleOrDefault(row => row.GetValue("_id", BsonNull.Value) == id) ?? throw B.Broken();
                return StockSourceSetupService.ProfileName(profile, node);
            }
            var result = locations.Select(location => new StockSourceWarehouseOption(location,
                Name(location.OrganizationId.Value, ScopeNodeKind.FulfillmentOrganization), Name(location.ShopId.Value, ScopeNodeKind.Shop),
                Name(location.WarehouseId.Value, ScopeNodeKind.Warehouse), Name(location.ChannelId.Value, ScopeNodeKind.Channel))).ToImmutableArray();
            await FenceDiscoveryAsync(actor, authority, tx, locations.Select(DiscoveryResource).ToArray(), [ProposePermission, ApprovePermission], token);
            return result;
        }, ct);
    }
    private static BsonDocument ReleaseLabels(BsonDocument receipt) {
        var key = InventoryHeldReceiptWriter.After(receipt).Balance.Key;
        var line = PurchaseOrderService.FundingOrderView(receipt["source"].AsBsonDocument)["lines"].AsBsonArray.SingleOrDefault(value => value["skuId"] == key.SkuId.Value) ?? throw B.Broken();
        return new() { { "receiptReference", receipt["input"]["receiptReference"] }, { "productName", line["familyName"] },
            { "packagingLabel", line["packageLabel"] }, { "lotReference", receipt["input"]["lotReference"] }, { "expiresOn", receipt["input"]["expiresOn"] } };
    }
    /// <summary>Existing real proposals, newest first. Scope is applied before paging;
    /// an unavailable history is never replaced with a fabricated empty pending list.</summary>
    public async Task<BsonDocument> ListAsync(ActorContext actor, StockLocation location, long afterProposalId = 0, int limit = 25, CancellationToken ct = default) {
        if (afterProposalId is < 0 or >= long.MaxValue - 2 || limit is < 1 or > 100) throw StockReleaseInput.Invalid(); await StorageAsync(ct);
        return await DiscoveryReadAsync(actor, location, false, async (authority, tx, token) => {
            await RequireOwnerMembershipAsync(tx, null, token);
            var filter = new BsonDocument("input.location", StockSourceSetupBson.Location(location));
            if (afterProposalId > 0) filter.Add("_id", new BsonDocument("$lt", afterProposalId));
            var page = await Rows(B.Proposals).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(limit + 1).ToListAsync(token);
            var items = new BsonArray();
            var reads = new HistoryReads(this, tx);
            foreach (var raw in page.Take(limit)) {
                var proposal = await ProposalAsync(tx, raw["_id"].AsInt64, token, reads: reads);
                var decision = await Rows(B.Decisions).Find(tx, new BsonDocument("proposalId", proposal["_id"])).SingleOrDefaultAsync(token);
                if (decision is not null) decision = await DecisionAsync(tx, decision["_id"].AsInt64, token, reads: reads);
                var receipt = await reads.OriginAsync(proposal["input"]["receiptId"].AsInt64, token);
                var item = ReleaseLabels(receipt.Receipt); item.Remove("expiresOn");
                item.Add("proposalId", proposal["_id"]); item.Add("status", decision is null ? "proposed" : decision["decision"] == "approve" ? "approved" : "rejected");
                item.Add("packages", proposal["input"]["packages"]); item.Add("policy", proposal["input"]["policy"]);
                item.Add("recordedAt", proposal["recordedAt"]); item.Add("decidedAt", decision?["recordedAt"] ?? BsonNull.Value); items.Add(item);
            }
            return new BsonDocument { { "items", items }, { "nextAfter", page.Count > limit ? page[limit - 1]["_id"] : BsonNull.Value } };
        }, ct);
    }
    /// <summary>Only genuine current held/quality/posted-value selections are offered.
    /// Each proof has its own authorized transaction because an unavailable ancestry
    /// aborts its owning read. Commands always perform a fresh current preview.</summary>
    public async Task<BsonDocument> SourcesAsync(ActorContext actor, StockLocation location, int limit = 25, CancellationToken ct = default) {
        if (limit is < 1 or > 100) throw StockReleaseInput.Invalid(); await StorageAsync(ct);
        var candidates = await DiscoveryReadAsync(actor, location, true, async (authority, tx, token) => {
            var receipts = await Rows(ProcurementStockReceiptBson.Receipts).Find(tx, new BsonDocument("location", StockSourceSetupBson.Location(location)))
                .Sort(new BsonDocument("_id", -1)).Limit(4097).ToListAsync(token);
            if (receipts.Count > 4096) throw B.Broken();
            var pairs = new List<StockReleaseSelection>(); long missing = 0;
            foreach (var receipt in receipts) {
                var quality = await Rows(ProcurementQualityAcceptanceBson.Allocations).Find(tx, new BsonDocument("receiptId", receipt["_id"]))
                    .Sort(new BsonDocument("_id", -1)).Limit(4097).ToListAsync(token);
                var valuation = await Rows(ProcurementStockValuationBson.Allocations).Find(tx, new BsonDocument("receiptId", receipt["_id"]))
                    .Sort(new BsonDocument("_id", -1)).Limit(4097).ToListAsync(token);
                if (quality.Count > 4096 || valuation.Count > 4096 || (long)quality.Count * valuation.Count + pairs.Count > 4096) throw B.Broken();
                if (quality.Count == 0 || valuation.Count == 0) { missing++; continue; }
                foreach (var q in quality) foreach (var v in valuation) pairs.Add(new(receipt["_id"].AsInt64, q["_id"].AsInt64, v["_id"].AsInt64));
            }
            return (Pairs: pairs, Missing: missing);
        }, ct);
        var items = new BsonArray(); var unavailable = candidates.Missing;
        foreach (var candidate in candidates.Pairs) {
            if (items.Count >= limit) break;
            try {
                var item = await authorization.ReadAsync(actor, async (authority, tx, token) => {
                    _ = DiscoveryPermission(actor, authority, location, true);
                    var input = new StockReleaseInput(location, candidate.ReceiptId, candidate.QualityAllocationId, candidate.ValuationAllocationId, 1, LotValuePolicy.ProportionalFloorPolicy);
                    var context = new Context(actor, authority, tx, ProposePermission, input); await PeopleAsync(context, token); await PrepareAsync(context, token);
                    var facts = context.Source ?? throw B.Broken();
                    // Prepare authenticated both finite remainders in this exact
                    // transaction. No business write intervenes before the final fence.
                    Current(context);
                    var quantity = Math.Min(Math.Min(context.HeldRemaining ?? throw B.Broken(), context.Before!.Balance.Held),
                        context.QualityRemaining ?? throw B.Broken());
                    if (quantity <= 0) throw B.Changed();
                    var result = ReleaseLabels(facts.Receipt); result.Add("receiptId", candidate.ReceiptId); result.Add("qualityAllocationId", candidate.QualityAllocationId);
                    result.Add("valuationAllocationId", candidate.ValuationAllocationId); result.Add("acceptedPackages", facts.AcceptedPackages); result.Add("valuedPackages", facts.ValuedPackages);
                    result.Add("releaseablePackages", quantity); result.Add("capitalizedValue", facts.CapitalizedValue.ToString()); result.Add("policy", LotValuePolicy.ProportionalFloorPolicy);
                    await FenceAsync(context, token); return result;
                }, ct);
                items.Add(item);
            } catch (Exception error) when (error is PersistenceConflictException or DomainValidationException) { unavailable++; }
        }
        // A revocation during independent source proofs must not return the earlier
        // discovery result or reveal scoped candidate counts after authority is lost.
        return await DiscoveryReadAsync(actor, location, true, (authority, tx, token) => Task.FromResult(new BsonDocument {
            { "items", items }, { "unavailableCount", unavailable } }), ct);
    }
}
