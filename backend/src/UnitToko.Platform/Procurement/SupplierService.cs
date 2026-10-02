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
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Procurement.SupplierBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierService
{
    private readonly IMongoDatabase database;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly CatalogAuthoringService catalog;
    private readonly AuthorizationStore authorization;
    private readonly OrganizationMasterDataService organizations;
    private readonly SupplierRecords records;
    public SupplierService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, CatalogAuthoringService catalog) {
        this.database = database; this.ids = ids; this.clock = clock; this.catalog = catalog; authorization = new(database, unit, clock);
        organizations = new(database, authorization, ids, clock); records = new(database, catalog);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, SupplierOwner owner, string permission, BsonDocument sources, ProcurementLocation? readLocation) {
        internal ActorContext Actor { get; } = actor;
        internal AuthorizationSnapshot Authority { get; } = authority;
        internal IClientSessionHandle Tx { get; } = tx;
        internal SupplierOwner Owner { get; } = owner;
        internal string Permission { get; } = permission;
        internal bool PurchaseOrderConsumer => readLocation is not null && Permission is Permissions.ProcurementPrepare or Permissions.ProcurementApprove;
        internal ResourceScope Resource => new(FulfillmentOrganizationId: Owner.OrganizationId, ShopId: Owner.ShopId, ChannelId: Owner.ChannelId, WarehouseId: readLocation?.WarehouseId);
        internal BsonDocument Sources { get; } = sources;
        internal BsonArray MappingSources { get; set; } = [];
        internal List<(ProcurementLocation Location, ProcurementCatalogCapture Capture)> Catalog { get; } = [];
        internal BsonDocument? Policy { get; set; }
        internal bool Activating { get; set; }
        internal SupplierChangeInput? UnexpiredMappings { get; set; }
        internal BsonDocument? Head { get; set; }
        internal BsonDocument? Proposal { get; set; }
        internal BsonDocument? Observation { get; set; }
        internal List<PersonEvidence> OriginalPeople { get; } = [];
        internal PersonAuthorityProof? People { get; set; }
        internal DateTimeOffset? SessionExpiresAt { get; set; }
    }
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Changed(); }
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata); SupplierRules.Text(metadata.Reason, 1000); SupplierRules.Text(metadata.CorrelationId, 128);
        if (metadata.IdempotencyKey.Length > 128 || metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw SupplierRules.Invalid();
    }
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot authority, string permission) {
        var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query); return query;
    }
    private ScopeClause[] Clauses(Context context) {
        var query = Query(context.Actor, context.Authority, context.Permission);
        var clauses = query.Clauses.Where(clause => clause.Matches(context.Resource) &&
            (context.Permission == Permissions.ProcurementRead || context.PurchaseOrderConsumer || clause.Scope.Kind == GrantScopeKind.FulfillmentShop && clause.Scope == SupplierRules.Scope(context.Owner))).ToArray();
        if (!ScopePolicy.IsResourceActive(context.Resource, context.Authority.Directory) || clauses.Length == 0) throw new AuthorizationDeniedException("scope_denied"); return clauses;
    }
    private async Task StorageAsync(CancellationToken ct) {
        foreach (var name in SupplierSchemas.Collections) {
            await InventoryRepository.RequireStorageAsync(database, name, SupplierSchemas.Validator(name), SupplierSchemas.Keys(name), ct);
            var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression"))) throw B.Broken();
        }
        foreach (var name in SupplierSchemas.Collections.Concat(["auditEvents", "idempotencyRecords", "outboxEvents"])) {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw B.Broken();
            if (name is "auditEvents" or "idempotencyRecords" or "outboxEvents") PaymentEvidenceMigration.RequireIndexes(name, await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct));
        }
    }
    private async Task<BsonDocument> SourceAsync(AuthorizationSnapshot authority, IClientSessionHandle tx, ScopeNodeKind kind, long id, CancellationToken ct) {
        var source = await organizations.ReadAddressProfileAsync(tx, new(kind, id), ct);
        foreach (var node in source["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node)) if (authority.Directory.Find(node.Id, node.Kind) != node) throw B.Changed(); return source;
    }
    private async Task<Context> ContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, SupplierOwner owner, string permission,
        CancellationToken ct, ProcurementLocation? readLocation = null) {
        SupplierRules.Owner(owner); var context = new Context(actor, authority, tx, owner, permission, new(), readLocation); _ = Clauses(context);
        foreach (var (name, kind, id) in new[] { ("organization", ScopeNodeKind.FulfillmentOrganization, owner.OrganizationId), ("shop", ScopeNodeKind.Shop, owner.ShopId), ("channel", ScopeNodeKind.Channel, owner.ChannelId) })
            context.Sources.Add(name, await SourceAsync(authority, tx, kind, id, ct));
        _ = Clauses(context); return context;
    }
    private async Task CaptureMappingsAsync(Context context, SupplierChangeInput input, CancellationToken ct) {
        var sources = new BsonArray(); var warehouses = new Dictionary<long, BsonDocument>();
        foreach (var mapping in input.Mappings) {
            if (!warehouses.TryGetValue(mapping.Location.WarehouseId, out var warehouse)) {
                warehouse = await SourceAsync(context.Authority, context.Tx, ScopeNodeKind.Warehouse, mapping.Location.WarehouseId, ct); warehouses.Add(mapping.Location.WarehouseId, warehouse);
            }
            if (warehouse["target"]["parentId"] != context.Owner.ShopId) throw B.Changed();
            var capture = await catalog.CaptureProcurementAsync(database, context.Actor, context.Authority, context.Tx, mapping.Location, context.Permission,
                [new(mapping.SkuId, mapping.ExpectedSkuRevision, mapping.DefinitionId, mapping.DefinitionVersion, 1, null)], ct);
            context.Catalog.Add((mapping.Location, capture));
            var labels = context.Sources.DeepClone().AsBsonDocument; labels.Add("warehouse", warehouse);
            sources.Add(new BsonDocument { { "location", ProcurementRequestBson.Location(mapping.Location) }, { "sources", labels }, { "catalog", capture.Sources[0] } });
        }
        context.MappingSources = sources;
    }
    private static void BindPeople(Context context, params BsonDocument?[] rows) {
        foreach (var row in rows.OfType<BsonDocument>()) context.OriginalPeople.AddRange(row["people"].AsBsonArray.Select(B.Person));
    }
    private async Task PeopleAsync(Context context, bool independent, long? observer, CancellationToken ct) {
        var maker = context.Proposal?["actorId"].AsInt64 ?? context.Actor.UserId;
        var users = context.OriginalPeople.Select(person => person.UserId).Concat([context.Actor.UserId, maker, observer ?? context.Actor.UserId]).Distinct().Order().ToArray();
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [.. users], ct); var byUser = proof.Evidence.ToDictionary(person => person.UserId);
        if (proof.Evidence.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw new AuthorizationDeniedException("person_evidence_invalid");
        foreach (var original in context.OriginalPeople) if (byUser[original.UserId].PersonId != original.PersonId) throw new AuthorizationDeniedException("supplier_person_changed");
        if (independent && (!PersonIndependencePolicy.AreIndependent(byUser[context.Actor.UserId], byUser[maker]) ||
            !PersonIndependencePolicy.AreIndependent(byUser[observer ?? context.Actor.UserId], byUser[maker]))) throw new AuthorizationDeniedException("independent_person_required");
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); context.People = proof;
    }
    private void Current(Context context) {
        _ = Clauses(context); context.People?.RequireCurrent(context.Tx, clock.UtcNow);
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client)) throw B.Broken();
        if (context.SessionExpiresAt is { } deadline && clock.UtcNow >= deadline) throw new AuthorizationDeniedException("stale_or_inactive_session");
        if (context.Activating) {
            SupplierRecords.PolicyAt(context.Policy ?? throw B.Broken(), Now);
            if (context.Observation is { } observation) SupplierRecords.ObservationAt(context.Proposal!, observation, Now);
            if (context.UnexpiredMappings is { } input && input.Mappings.Any(mapping => mapping.ValidUntil <= Now)) throw B.Changed();
        }
    }
    private async Task TouchAsync(Context context, string name, BsonDocument row, CancellationToken ct) {
        if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(name).UpdateOneAsync(context.Tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        row["fence"] = row["fence"].AsInt64 + 1;
    }
    private async Task FenceAsync(Context context, CancellationToken ct) {
        Current(context);
        foreach (var source in context.Sources.Values) await organizations.FenceAddressProfileAsync(context.Tx, source.AsBsonDocument, true, ct);
        foreach (var source in context.MappingSources) await organizations.FenceAddressProfileAsync(context.Tx, source["sources"]["warehouse"].AsBsonDocument, true, ct);
        foreach (var capture in context.Catalog) await catalog.FenceProcurementAsync(database, context.Actor, context.Authority, context.Tx, capture.Location, context.Permission, capture.Capture, ct);
        if (context.Head is { } head) await TouchAsync(context, B.Heads, head, ct);
        if (context.Activating && context.Policy is { } policy) await TouchAsync(context, B.Policies, policy, ct);
        var clauses = Clauses(context);
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        context.SessionExpiresAt = await LiveSessionAsync(context.Actor, context.Tx, ct);
        if (context.People is { } proof) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct);
        Current(context); ct.ThrowIfCancellationRequested();
    }
    private async Task<DateTimeOffset?> LiveSessionAsync(ActorContext actor, IClientSessionHandle tx, CancellationToken ct) {
        if (actor.Session is not { } session) return null;
        var row = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct);
        if (row is null || row["idleExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime || row["absoluteExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime) throw new AuthorizationDeniedException("stale_or_inactive_session");
        return new DateTimeOffset(row["idleExpiresAt"].ToUniversalTime() < row["absoluteExpiresAt"].ToUniversalTime() ? row["idleExpiresAt"].ToUniversalTime() : row["absoluteExpiresAt"].ToUniversalTime());
    }
    private async Task PolicyAsync(Context context, SupplierChangeInput input, CancellationToken ct, bool referenceOnly = false) {
        var policy = await records.RowAsync(context.Tx, B.Policies, input.PolicyId, ct);
        Equal(policy["scope"], B.Owner(context.Owner)); Equal(policy["revision"], input.PolicyRevision);
        if (context.Proposal is { } proposal) Equal(B.PolicySnapshot(policy), proposal["policy"]);
        SupplierRecords.MatchPolicy(policy, input.Details); SupplierRecords.PolicyAt(policy, Now);
        if (referenceOnly && !policy["allowReferenceOnly"].AsBoolean) throw new AuthorizationDeniedException("supplier_evidence_policy_unsupported");
        context.Policy = policy; context.Activating = true;
    }
}
