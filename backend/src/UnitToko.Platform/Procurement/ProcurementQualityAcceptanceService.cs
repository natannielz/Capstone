using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementQualityAcceptanceService
{
    private readonly IMongoDatabase database; private readonly MongoUnitOfWork unit; private readonly IdAllocator ids; private readonly IClock clock;
    private readonly CatalogAuthoringService catalog; private readonly PurchaseOrderService orders; private readonly ProcurementReceivingService receiving;
    private readonly StockSourceSetupService sources; private readonly AuthorizationStore authorization; private readonly ProcurementQualityAcceptanceRecords records;
    public ProcurementQualityAcceptanceService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        CatalogAuthoringService catalog, PurchaseOrderService orders, ProcurementReceivingService receiving, StockSourceSetupService sources) {
        this.database = database; this.unit = unit; this.ids = ids; this.clock = clock; this.catalog = catalog; this.orders = orders; this.receiving = receiving; this.sources = sources;
        authorization = new(database, unit, clock); records = new(database, unit, clock, catalog, orders, sources);
    }
    private IMongoCollection<BsonDocument> Rows(string table) => database.GetCollection<BsonDocument>(table);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private static ResourceScope Resource(ProcurementLocation location) => new(FulfillmentOrganizationId: location.OrganizationId,
        ShopId: location.ShopId, WarehouseId: location.WarehouseId, ChannelId: location.ChannelId);
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ProcurementLocation location, string permission, bool live) {
        internal readonly ActorContext Actor = actor; internal readonly AuthorizationSnapshot Authority = authority; internal readonly IClientSessionHandle Tx = tx;
        internal readonly long Transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        internal readonly ProcurementLocation Location = location; internal readonly string Permission = permission; internal readonly bool Live = live;
        internal ProcurementQualityAcceptanceSources? Source; internal BsonDocument? Proposal; internal ProcurementQualityAcceptanceInput? ObservedInput;
        internal PersonAuthorityProof? People;
        internal ProcurementQualityAcceptanceObservation? CheckerObservation; internal DateTimeOffset? SessionExpiry;
        internal readonly List<(string Table, BsonDocument Row)> Fences = [];
    }
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot authority, string permission) {
        var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query); return query;
    }
    private AuthorizationQuery[] Require(Context context) {
        var permissions = context.Live ? new[] { Permissions.ProcurementRead, Permissions.StockRead, context.Permission } : [context.Permission];
        return permissions.Distinct().Select(permission => {
            var query = Query(context.Actor, context.Authority, permission); var resource = Resource(context.Location);
            if (!ScopePolicy.IsResourceActive(resource, context.Authority.Directory) || !query.Matches(resource))
                throw new AuthorizationDeniedException("scope_denied");
            return query;
        }).ToArray();
    }
    private void Current(Context context) {
        _ = Require(context);
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client) ||
            context.Tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != context.Transaction) throw B.Broken();
        context.People?.RequireCurrent(context.Tx, clock.UtcNow);
        context.Source?.PoolCapture?.RequireCurrent();
        if (context.Live && context.Source is { } source) {
            if (clock.UtcNow >= new DateTimeOffset(source.Policy["expiresAt"].ToUniversalTime()))
                throw B.Changed("procurement_quality_acceptance_policy_unavailable");
            var input = context.ObservedInput ?? (context.Proposal is { } proposal ? B.Input(proposal["input"]) : null);
            if (input is { } maker) {
                ProcurementQualityAcceptanceRecords.ObservationTime(maker.ObservedAt, source.Receipt, source.Policy, Now);
                ProcurementQualityAcceptanceRecords.LotFacts(maker, source.Receipt, Now);
            }
            if (context.CheckerObservation is { } checker)
                ProcurementQualityAcceptanceRecords.ObservationTime(checker.ObservedAt, source.Receipt, source.Policy, Now);
        }
        if (context.SessionExpiry is { } expiry && clock.UtcNow >= expiry) throw new AuthorizationDeniedException("stale_or_inactive_session");
    }
    internal async Task StorageAsync(CancellationToken ct) {
        await orders.RequireReceivingStorageAsync(ct); await sources.RequireHeldReceiptStorageAsync(ct);
        await ProcurementStockReceiptSchemas.RequireDispositionStorageAsync(database, ct);
        foreach (var name in ProcurementStockReceiptSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, ProcurementStockReceiptSchemas.Validator(name), ProcurementStockReceiptSchemas.Keys(name), ct);
        await InventoryRepository.RequireStorageAsync(database, "lots", InventoryLotSchemas.Validator(), new BsonDocument("_id", 1), ct);
        await InventoryRepository.RequireStorageAsync(database, "stockBalances", InventorySchemas.BalanceValidator(), InventorySchemas.BalanceKey(), ct);
        await InventoryRepository.RequireStorageAsync(database, "movements", InventoryMovementSchemas.Validator(), InventoryMovementSchemas.Key(), ct);
        foreach (var name in ProcurementInspectionSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, ProcurementInspectionSchemas.Validator(name), ProcurementInspectionSchemas.Keys(name), ct);
        foreach (var name in SupplierClaimSchemas.Collections.Where(name => name != ProcurementQuarantineDispositionBson.PhysicalAllocations))
            await InventoryRepository.RequireStorageAsync(database, name, SupplierClaimSchemas.Validator(name), SupplierClaimSchemas.Keys(name), ct);
        foreach (var name in ProcurementReceivingSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, ProcurementReceivingSchemas.Validator(name), ProcurementReceivingSchemas.Keys(name), ct);
        foreach (var name in SupplierDocumentSchemas.Collections)
            await SupplierDocumentService.RequirePredecessorDocumentStorageAsync(database, name, ct);
        foreach (var name in ProcurementQualityAcceptanceSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, ProcurementQualityAcceptanceSchemas.Validator(name),
                ProcurementQualityAcceptanceSchemas.Keys(name), ct);
        foreach (var name in ProcurementQualityAcceptanceSchemas.Collections)
            if ((await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression")))
                throw B.Broken();
    }
    private static void AddFence(Context context, string table, BsonDocument row) {
        var prior = context.Fences.SingleOrDefault(value => value.Table == table && value.Row["_id"] == row["_id"]);
        if (prior.Row is not null) { ProcurementQualityAcceptanceRecords.Equal(prior.Row, row); return; }
        context.Fences.Add((table, row));
    }
    private async Task<DateTimeOffset?> SessionAsync(ActorContext actor, IClientSessionHandle tx, CancellationToken ct) {
        if (actor.Session is not { } session) return null;
        var row = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer,
            actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("stale_or_inactive_session");
        return new(new[] { row["idleExpiresAt"].ToUniversalTime(), row["absoluteExpiresAt"].ToUniversalTime() }.Min());
    }
    private async Task FenceAsync(Context context, CancellationToken ct, bool sourceFence = false) {
        Current(context);
        if (sourceFence && context.Source?.PoolCapture is { } capture) await capture.FenceAsync(ct);
        foreach (var (table, row) in context.Fences) {
            if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(table).UpdateOneAsync(context.Tx, row,
                Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
            row["fence"] = row["fence"].AsInt64 + 1;
        }
        var resource = Resource(context.Location); var queries = Require(context);
        var clauses = queries.SelectMany(query => query.Clauses.Where(clause => clause.Matches(resource))).Distinct().ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            ScopePolicy.RequiredNodes(resource, context.Authority.Directory).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        if (context.People is { } proof) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct);
        context.SessionExpiry = await SessionAsync(context.Actor, context.Tx, ct); Current(context); ct.ThrowIfCancellationRequested();
    }
    private CommandAuthorization Command(Context context, BsonDocument? proposal = null) => new(
        ScopePolicy.Authorize(context.Actor, context.Authority, context.Permission, Resource(context.Location), clock.UtcNow,
            proposal is null ? null : new ApprovalRequest(1, 1, RequestedBy: proposal["actorId"].AsInt64)),
        ScopePolicy.RequiredNodes(Resource(context.Location), context.Authority.Directory), context.Permission,
        new(Resource(context.Location), proposal is null ? null : new ApprovalRequest(1, 1,
            RequestedBy: proposal["actorId"].AsInt64)));
    private static void Metadata(AuthorizationCommandMetadata metadata) { AuthorizationStore.ValidateMetadata(metadata);
        if (metadata.IdempotencyKey is not { Length: > 0 and <= 128 } ||
            metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')))
            throw ProcurementQualityAcceptanceRules.Invalid();
        ProcurementStockReceiptRules.Text(metadata.CorrelationId, 128); }
    private static void Binding(string value) => ProcurementRequestBson.RequireBinding(value);
    private async Task<Context> PrepareAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ProcurementQualityAcceptanceInput input, string permission, CancellationToken ct) {
        var context = new Context(actor, authority, tx, input.Location, permission, true); _ = Require(context);
        context.Source = await records.SourcesAsync(tx, input, Now, true, actor, authority, ct);
        context.ObservedInput = input;
        AddFence(context, B.Policies, context.Source.Policy);
        if (context.Source.Capacity.Head is { } head) AddFence(context, B.ReceiptHeads, head);
        Current(context); return context;
    }
    private async Task<(ActorContext Maker, AuthorizationSnapshot Snapshot)> MakerAsync(Context context, BsonDocument proposal,
        bool fence, CancellationToken ct) {
        var id = proposal["actorId"].AsInt64; var identity = await Rows("identities").Find(context.Tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct);
        if (identity is null || identity.GetValue("status", BsonNull.Value) != "active" ||
            !identity.GetValue("grantVersion", BsonNull.Value).IsInt64 ||
            context.Live && identity["grantVersion"] != proposal["actorGrantVersion"] ||
            !identity.GetValue("issuer", BsonNull.Value).IsString || !identity.GetValue("subject", BsonNull.Value).IsString)
            throw new AuthorizationDeniedException("original_maker_authority_changed");
        var maker = new ActorContext(id, identity["issuer"].AsString, identity["subject"].AsString,
            identity["grantVersion"].AsInt64, new DateTimeOffset(proposal["recordedAt"].ToUniversalTime()), FactorAssurance.Unknown);
        var snapshot = await authorization.LoadAsync(maker, context.Tx, ct);
        // Closing a historical proposal without a physical effect does not renew its maker's grants.
        // The stored identity and person evidence still bind that original maker to the checker.
        if (context.Live) RequireMaker(maker, snapshot, context.Location);
        if (fence) {
            var resources = new[] { Resource(context.Location), Resource(R.Location(context.Source!.Pool["location"])) }.Distinct().ToArray();
            var query = ScopePolicy.CompileParticipantGrants(maker, snapshot, Permissions.ProcurementQualityAcceptancePropose, clock.UtcNow);
            var clauses = query.Clauses.Where(clause => resources.Any(clause.Matches)).ToArray();
            await new HistoricalAuthorizationFence(database, authorization).FenceAsync(maker,
                clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(resource => ScopePolicy.RequiredNodes(resource, snapshot.Directory))).Distinct(), [],
                snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
                snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        }
        return (maker, snapshot);
    }
    private void RequireMaker(ActorContext maker, AuthorizationSnapshot snapshot, ProcurementLocation location) {
        foreach (var permission in new[] { Permissions.ProcurementQualityAcceptancePropose, Permissions.ProcurementRead, Permissions.StockRead }) {
            var query = ScopePolicy.CompileParticipantGrants(maker, snapshot, permission, clock.UtcNow); AuthorizationStore.Require(query);
            if (!query.Matches(Resource(location))) throw new AuthorizationDeniedException("original_maker_scope_changed");
        }
    }
    private async Task PeopleAsync(Context context, BsonDocument? proposal, CancellationToken ct) {
        var policy = context.Source?.Policy ?? throw B.Broken();
        var users = new[] { context.Actor.UserId, proposal?["actorId"].AsInt64 ?? context.Actor.UserId,
            policy["proposedBy"].AsInt64, policy["approvedBy"].AsInt64 }.Distinct().Order().ToArray();
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [.. users], ct);
        if (proof.Evidence.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 }))
            throw new AuthorizationDeniedException("person_evidence_invalid");
        var byUser = proof.Evidence.ToDictionary(person => person.UserId);
        foreach (var original in policy["people"].AsBsonArray.Select(R.Person))
            if (byUser[original.UserId].PersonId != original.PersonId) throw new AuthorizationDeniedException("procurement_quality_acceptance_person_changed");
        if (proposal is { }) {
            var maker = proposal["people"].AsBsonArray.Select(R.Person).Single(person => person.UserId == proposal["actorId"].AsInt64);
            if (byUser[maker.UserId].PersonId != maker.PersonId || !PersonIndependencePolicy.AreIndependent(byUser[context.Actor.UserId], byUser[maker.UserId]))
                throw new AuthorizationDeniedException("independent_person_required");
        }
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); context.People = proof;
    }
}
