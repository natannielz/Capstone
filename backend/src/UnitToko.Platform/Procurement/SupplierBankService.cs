using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
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
using B = UnitToko.Platform.Procurement.SupplierBankBson;
using S = UnitToko.Platform.Procurement.SupplierBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierBankService
{
    private readonly IMongoDatabase database;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly CatalogAuthoringService catalog;
    private readonly AuthorizationStore authorization;
    private readonly OrganizationMasterDataService organizations;
    private readonly SupplierBankRecords records;
    public SupplierBankService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, CatalogAuthoringService catalog) {
        this.database = database; this.ids = ids; this.clock = clock; this.catalog = catalog; authorization = new(database, unit, clock);
        organizations = new(database, authorization, ids, clock); records = new(database, catalog);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, SupplierOwner owner, string permission, bool full) {
        internal ActorContext Actor { get; } = actor;
        internal AuthorizationSnapshot Authority { get; } = authority;
        internal IClientSessionHandle Tx { get; } = tx;
        internal SupplierOwner Owner { get; } = owner;
        internal string Permission { get; } = permission;
        internal string[] Permissions => Permission == UnitToko.Modules.Identity.Permissions.SupplierBankDetailsRead
            ? [UnitToko.Modules.Identity.Permissions.SupplierBankRead, Permission] : full ? [Permission, UnitToko.Modules.Identity.Permissions.SupplierBankRead, UnitToko.Modules.Identity.Permissions.SupplierBankDetailsRead] : [Permission];
        internal ResourceScope Resource => new(FulfillmentOrganizationId: Owner.OrganizationId, ShopId: Owner.ShopId, ChannelId: Owner.ChannelId);
        internal BsonDocument Sources { get; } = new();
        internal BsonDocument? Supplier { get; set; }
        internal BsonDocument? SupplierHead { get; set; }
        internal BsonDocument? SupplierPolicy { get; set; }
        internal PersonAuthorityProof? SupplierPeople { get; set; }
        internal BsonDocument? Head { get; set; }
        internal BsonDocument? Proposal { get; set; }
        internal BsonDocument? Observation { get; set; }
        internal BsonDocument? Policy { get; set; }
        internal bool Activating { get; set; }
        internal bool ReferenceOnly { get; set; }
        internal List<PersonEvidence> OriginalPeople { get; } = [];
        internal PersonAuthorityProof? People { get; set; }
        internal DateTimeOffset? SessionExpiresAt { get; set; }
    }
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Changed(); }
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (!ReceivingAccountPolicy.Text(metadata.Reason, 1000) || !ReceivingAccountPolicy.Text(metadata.CorrelationId, 128) || metadata.IdempotencyKey.Length > 128 ||
            metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw SupplierBankRules.Invalid();
    }
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot authority, string permission) {
        var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query); return query;
    }
    private ScopeClause[] Clauses(Context context) {
        var result = new List<ScopeClause>();
        foreach (var permission in context.Permissions.Distinct()) {
            var clauses = Query(context.Actor, context.Authority, permission).Clauses.Where(clause => clause.Matches(context.Resource) && clause.Scope.Kind == GrantScopeKind.FulfillmentShop && clause.Scope == SupplierRules.Scope(context.Owner)).ToArray();
            if (!ScopePolicy.IsResourceActive(context.Resource, context.Authority.Directory) || clauses.Length == 0) throw new AuthorizationDeniedException("scope_denied"); result.AddRange(clauses);
        }
        return [.. result.Distinct()];
    }
    private async Task StorageAsync(CancellationToken ct) {
        foreach (var name in SupplierBankSchemas.Collections.Concat(SupplierSchemas.Collections)) {
            var bank = SupplierBankSchemas.Collections.Contains(name);
            await InventoryRepository.RequireStorageAsync(database, name, bank ? SupplierBankSchemas.Validator(name) : SupplierSchemas.Validator(name), bank ? SupplierBankSchemas.Keys(name) : SupplierSchemas.Keys(name), ct);
            var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression"))) throw B.Broken();
        }
        foreach (var name in SupplierBankSchemas.Collections.Concat(SupplierSchemas.Collections).Concat(["auditEvents", "idempotencyRecords", "outboxEvents"])) {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw B.Broken();
            if (name is "auditEvents" or "idempotencyRecords" or "outboxEvents") PaymentEvidenceMigration.RequireIndexes(name, await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct));
        }
    }
    private async Task<Context> ContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, SupplierOwner owner, string permission, bool full, CancellationToken ct) {
        SupplierRules.Owner(owner); var context = new Context(actor, authority, tx, owner, permission, full); _ = Clauses(context);
        foreach (var (name, kind, id) in new[] { ("organization", ScopeNodeKind.FulfillmentOrganization, owner.OrganizationId), ("shop", ScopeNodeKind.Shop, owner.ShopId), ("channel", ScopeNodeKind.Channel, owner.ChannelId) }) {
            var source = await organizations.ReadAddressProfileAsync(tx, new(kind, id), ct);
            foreach (var node in source["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node)) if (authority.Directory.Find(node.Id, node.Kind) != node) throw B.Changed(); context.Sources.Add(name, source);
        }
        _ = Clauses(context); return context;
    }
    private async Task<Context> SupplierContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long supplierId, string permission, bool full, CancellationToken ct) {
        _ = Query(actor, authority, permission);
        var route = await Rows(S.Heads).Find(tx, new BsonDocument("_id", supplierId)).Project(new BsonDocument("owner", 1)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        return await ContextAsync(actor, authority, tx, S.Owner(route["owner"]), permission, full, ct);
    }
    private async Task SupplierAsync(Context context, long supplierId, long? versionId, long? version, bool current, CancellationToken ct) {
        if (!await Rows(S.Heads).Find(context.Tx, new BsonDocument { { "_id", supplierId }, { "owner", S.Owner(context.Owner) } }).AnyAsync(ct)) throw new AuthorizationDeniedException("resource_unavailable");
        var supplier = new SupplierRecords(database, catalog); var head = await supplier.HeadAsync(context.Tx, supplierId, ct); Equal(head["owner"], S.Owner(context.Owner));
        if (head["versionId"].IsBsonNull) throw B.Changed();
        var chosen = versionId ?? head["versionId"].AsInt64; var number = version ?? head["version"].AsInt64;
        context.Supplier = await records.SupplierAsync(context.Tx, supplierId, chosen, number, ct);
        if (!current) return;
        Equal(head["versionId"], chosen); Equal(head["version"], number); context.SupplierHead = head;
        if (context.Supplier["version"]["effectiveAt"].ToUniversalTime() > Now.UtcDateTime) throw B.Changed();
        var proposal = await supplier.ProposalAsync(context.Tx, context.Supplier["proposalId"].AsInt64, ct);
        var decision = await supplier.DecisionAsync(context.Tx, context.Supplier["decisionId"].AsInt64, proposal, ct);
        var observation = await supplier.ObservationAsync(context.Tx, context.Supplier["observationId"].AsInt64, proposal, ct);
        Equal(context.Sources, proposal["sources"]);
        var policy = await supplier.RowAsync(context.Tx, S.Policies, proposal["policy"]["_id"].AsInt64, ct); Equal(S.PolicySnapshot(policy), proposal["policy"]); SupplierRecords.PolicyAt(policy, Now); context.SupplierPolicy = policy;
        var originals = new[] { proposal, decision, observation }.SelectMany(row => row["people"].AsBsonArray.Select(S.Person)).ToArray();
        var people = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [.. originals.Select(person => person.UserId).Distinct().Order()], ct);
        if (people.Evidence.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 }) ||
            originals.Any(original => people.Evidence.Single(person => person.UserId == original.UserId).PersonId != original.PersonId)) throw new AuthorizationDeniedException("supplier_person_changed");
        context.SupplierPeople = people; Current(context);
    }
    private async Task HeadAsync(Context context, long supplierId, CancellationToken ct) {
        var row = await Rows(B.Heads).Find(context.Tx, new BsonDocument { { "owner", S.Owner(context.Owner) }, { "supplierId", supplierId } }).SingleOrDefaultAsync(ct);
        context.Head = row is null ? null : await records.HeadAsync(context.Tx, row["_id"].AsInt64, ct);
        if (row is null) {
            foreach (var name in new[] { B.Proposals, B.Observations, B.Decisions, B.Versions })
                if (await Rows(name).Find(context.Tx, new BsonDocument { { "owner", S.Owner(context.Owner) }, { "supplierId", supplierId } }).AnyAsync(ct)) throw B.Broken();
            foreach (var name in new[] { "auditEvents", "outboxEvents" }) if (await Rows(name).Find(context.Tx, new BsonDocument { { "owner", S.Owner(context.Owner) }, { "supplierId", supplierId },
                { "kind", new BsonDocument("$in", new BsonArray { B.Prefix + "propose", B.Prefix + "observe", B.Prefix + "approve", B.Prefix + "reject" }) } }).AnyAsync(ct)) throw B.Broken();
        }
    }
    private static void BindPeople(Context context, params BsonDocument?[] rows) { foreach (var row in rows.OfType<BsonDocument>()) context.OriginalPeople.AddRange(row["people"].AsBsonArray.Select(S.Person)); }
    private async Task PeopleAsync(Context context, bool independent, long? observer, CancellationToken ct) {
        var maker = context.Proposal?["actorId"].AsInt64 ?? context.Actor.UserId;
        var users = context.OriginalPeople.Select(person => person.UserId).Concat([context.Actor.UserId, maker, observer ?? context.Actor.UserId]).Distinct().Order().ToArray();
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [.. users], ct); var people = proof.Evidence.ToDictionary(person => person.UserId);
        if (proof.Evidence.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw new AuthorizationDeniedException("person_evidence_invalid");
        foreach (var original in context.OriginalPeople) if (people[original.UserId].PersonId != original.PersonId) throw new AuthorizationDeniedException("supplier_bank_person_changed");
        if (independent) ReceivingAccountPolicy.RequireIndependent(people[context.Actor.UserId], people[observer ?? context.Actor.UserId], people[maker]);
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); context.People = proof;
    }
    private void Current(Context context) {
        _ = Clauses(context); context.People?.RequireCurrent(context.Tx, clock.UtcNow); context.SupplierPeople?.RequireCurrent(context.Tx, clock.UtcNow);
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client)) throw B.Broken();
        if (context.SessionExpiresAt is { } expiry && clock.UtcNow >= expiry) throw new AuthorizationDeniedException("stale_or_inactive_session");
        if (context.SupplierPolicy is { } supplierPolicy) SupplierRecords.PolicyAt(supplierPolicy, Now);
        if (context.Activating) {
            SupplierBankRecords.PolicyAt(context.Policy ?? throw B.Broken(), Now, context.ReferenceOnly);
            if (context.Observation is { } observed) SupplierBankRecords.ObservationAt(context.Proposal!, observed, Now);
        }
    }
    private async Task TouchAsync(Context context, string name, BsonDocument row, CancellationToken ct) {
        if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(name).UpdateOneAsync(context.Tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed(); row["fence"] = row["fence"].AsInt64 + 1;
    }
    private async Task FenceAsync(Context context, CancellationToken ct) {
        Current(context); foreach (var source in context.Sources.Values) await organizations.FenceAddressProfileAsync(context.Tx, source.AsBsonDocument, true, ct);
        if (context.Head is { } head) await TouchAsync(context, B.Heads, head, ct);
        if (context.SupplierHead is { } supplier) await TouchAsync(context, S.Heads, supplier, ct);
        if (context.SupplierPolicy is { } supplierPolicy) await TouchAsync(context, S.Policies, supplierPolicy, ct);
        if (context.Policy is { } policy) await TouchAsync(context, B.Policies, policy, ct);
        var clauses = Clauses(context); await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            ScopePolicy.RequiredNodes(context.Resource, context.Authority.Directory).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        context.SessionExpiresAt = await LiveSessionAsync(context.Actor, context.Tx, ct);
        foreach (var proof in new[] { context.People, context.SupplierPeople }.OfType<PersonAuthorityProof>()) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct);
        Current(context); ct.ThrowIfCancellationRequested();
    }
    private async Task<DateTimeOffset?> LiveSessionAsync(ActorContext actor, IClientSessionHandle tx, CancellationToken ct) {
        if (actor.Session is not { } session) return null;
        var row = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct);
        if (row is null || row["idleExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime || row["absoluteExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime) throw new AuthorizationDeniedException("stale_or_inactive_session");
        return new DateTimeOffset(row["idleExpiresAt"].ToUniversalTime() < row["absoluteExpiresAt"].ToUniversalTime() ? row["idleExpiresAt"].ToUniversalTime() : row["absoluteExpiresAt"].ToUniversalTime());
    }
    private async Task PolicyAsync(Context context, SupplierBankInput input, bool referenceOnly, CancellationToken ct) {
        var policy = await records.RowAsync(context.Tx, B.Policies, input.PolicyId, ct); SupplierBankRecords.PolicySource(policy, input, context.Supplier!);
        if (context.Proposal is { } proposal) Equal(B.Snapshot(policy), proposal["policy"]);
        SupplierBankRecords.PolicyAt(policy, Now, referenceOnly); context.Policy = policy; context.Activating = true; context.ReferenceOnly = referenceOnly;
    }
}
