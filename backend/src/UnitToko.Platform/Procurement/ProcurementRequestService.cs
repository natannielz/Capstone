using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Procurement;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

/// <summary>Reviewed internal needs and purchase requests. Approval has no supplier, stock or financial effect.</summary>
public sealed partial class ProcurementRequestService
{
    private readonly IMongoDatabase database;
    private readonly CatalogAuthoringService catalog;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly AuthorizationStore authorization;
    private readonly OrganizationMasterDataService organizations;
    private readonly ProcurementRequestRecords records;
    public ProcurementRequestService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, CatalogAuthoringService catalog) {
        this.database = database; this.catalog = catalog; this.ids = ids; this.clock = clock; authorization = new(database, unit, clock);
        organizations = new(database, authorization, ids, clock); records = new(database, catalog);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string permission, ProcurementLocation key, BsonDocument sources) {
        internal ActorContext Actor { get; } = actor;
        internal AuthorizationSnapshot Authority { get; } = authority;
        internal IClientSessionHandle Tx { get; } = tx;
        internal string Permission { get; } = permission;
        internal ProcurementLocation Key { get; } = key;
        internal ResourceScope Resource => ResourceFor(Key);
        internal BsonDocument Sources { get; } = sources;
        internal BsonDocument? Proposal { get; set; }
        internal PersonEvidence? OriginalMaker { get; set; }
        internal PersonEvidence? OriginalChecker { get; set; }
        internal PersonAuthorityProof? People { get; set; }
        internal ProcurementCatalogCapture? Catalog { get; set; }
        internal DateTimeOffset? SessionExpiresAt { get; set; }
    }
    private static ResourceScope ResourceFor(ProcurementLocation key) => new(FulfillmentOrganizationId: key.OrganizationId, ShopId: key.ShopId, WarehouseId: key.WarehouseId, ChannelId: key.ChannelId);
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Changed(); }
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (metadata.IdempotencyKey.Length > 128 || metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')) ||
            metadata.Reason != metadata.Reason.Trim() || metadata.Reason.Length > 1000 || metadata.Reason.Any(char.IsControl)) throw ProcurementRequestRules.Invalid();
    }
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot authority, string permission) {
        var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query); return query;
    }
    private AuthorizationQuery Require(Context context) {
        var query = Query(context.Actor, context.Authority, context.Permission);
        if (!ScopePolicy.IsResourceActive(context.Resource, context.Authority.Directory) || !query.Matches(context.Resource)) throw new AuthorizationDeniedException("scope_denied");
        return query;
    }
    private async Task StorageAsync(CancellationToken ct) {
        foreach (var name in ProcurementRequestSchemas.Collections) {
            var keys = ProcurementRequestSchemas.Keys(name);
            await InventoryRepository.RequireStorageAsync(database, name, ProcurementRequestSchemas.Validator(name), keys, ct);
            var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression"))) throw B.Broken();
        }
        foreach (var name in ProcurementRequestSchemas.Collections.Concat(["auditEvents", "idempotencyRecords", "outboxEvents"])) {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw B.Broken();
            if (name is "auditEvents" or "idempotencyRecords" or "outboxEvents") PaymentEvidenceMigration.RequireIndexes(name, await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct));
        }
    }
    private async Task<Context> ContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementLocation key, string permission, CancellationToken ct) {
        ProcurementRequestRules.Location(key); var resource = ResourceFor(key); var query = Query(actor, authority, permission);
        if (!ScopePolicy.IsResourceActive(resource, authority.Directory) || !query.Matches(resource)) throw new AuthorizationDeniedException("scope_denied");
        var sources = new BsonDocument();
        foreach (var (name, kind, id) in new[] { ("organization", ScopeNodeKind.FulfillmentOrganization, key.OrganizationId), ("shop", ScopeNodeKind.Shop, key.ShopId), ("warehouse", ScopeNodeKind.Warehouse, key.WarehouseId), ("channel", ScopeNodeKind.Channel, key.ChannelId) }) {
            var source = await organizations.ReadAddressProfileAsync(tx, new(kind, id), ct);
            foreach (var node in source["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node))
                if (authority.Directory.Find(node.Id, node.Kind) != node) throw B.Changed();
            sources.Add(name, source);
        }
        var context = new Context(actor, authority, tx, permission, key, sources); _ = Require(context); return context;
    }
    private static void BindPeople(Context context, BsonDocument proposal, BsonDocument? decision = null) {
        context.OriginalMaker = proposal["people"].AsBsonArray.Select(B.Person).Single(person => person.UserId == proposal["actorId"].AsInt64);
        context.OriginalChecker = decision is null ? null : decision["people"].AsBsonArray.Select(B.Person).Single(person => person.UserId == decision["actorId"].AsInt64);
    }
    private async Task PeopleAsync(Context context, long? checker, long? maker, CancellationToken ct) {
        var users = new[] { context.Actor.UserId, checker ?? context.Actor.UserId, maker ?? context.Actor.UserId,
            context.OriginalMaker?.UserId ?? context.Actor.UserId, context.OriginalChecker?.UserId ?? context.Actor.UserId }.Distinct().Order().ToArray();
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [.. users], ct);
        if (proof.Evidence.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw new AuthorizationDeniedException("person_evidence_invalid");
        foreach (var original in new[] { context.OriginalMaker, context.OriginalChecker }.OfType<PersonEvidence>())
            if (proof.Evidence.Single(person => person.UserId == original.UserId).PersonId != original.PersonId) throw new AuthorizationDeniedException("procurement_person_changed");
        if (checker is { } actualChecker && maker is { } actualMaker) {
            var byUser = proof.Evidence.ToDictionary(person => person.UserId);
            if (context.OriginalMaker is not { } original || original.UserId != actualMaker || byUser[actualMaker].PersonId != original.PersonId ||
                context.OriginalChecker is { } oldChecker && (oldChecker.UserId != actualChecker || byUser[actualChecker].PersonId != oldChecker.PersonId)) throw new AuthorizationDeniedException("procurement_person_changed");
            if (!PersonIndependencePolicy.AreIndependent(byUser[actualChecker], byUser[actualMaker], original)) throw new AuthorizationDeniedException("independent_person_required");
        }
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); context.People = proof;
    }
    private void Current(Context context) {
        _ = Require(context); context.People?.RequireCurrent(context.Tx, clock.UtcNow);
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client)) throw B.Broken();
        if (context.SessionExpiresAt is { } deadline && clock.UtcNow >= deadline) throw new AuthorizationDeniedException("stale_or_inactive_session");
    }
    private async Task FenceAsync(Context context, CancellationToken ct) {
        Current(context);
        foreach (var name in B.SourceNames) await organizations.FenceAddressProfileAsync(context.Tx, context.Sources[name].AsBsonDocument, true, ct);
        if (context.Catalog is { } capture) await catalog.FenceProcurementAsync(database, context.Actor, context.Authority, context.Tx, context.Key, context.Permission, capture, ct);
        var query = Require(context); var clauses = query.Clauses.Where(clause => clause.Matches(context.Resource)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
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
    private async Task<BsonDocument?> ForKeyAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string operation, string key, CancellationToken ct) {
        var name = operation == B.Prefix + "submit" ? B.Requests : B.Decisions;
        var route = await Rows(name).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "operation", operation }, { "requestKey", key } }).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(ct);
        if (route is not null && !Query(actor, authority, B.Permission(operation)).Matches(ResourceFor(B.Location(route["location"])))) throw new AuthorizationDeniedException("scope_denied");
        return await records.ForKeyAsync(tx, actor.UserId, operation, key, ct);
    }
}
