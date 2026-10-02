using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Organizations;
using B = UnitToko.Platform.Ordering.OrderSpendingPolicyBson;

namespace UnitToko.Platform.Ordering;

public sealed record OrderSpendingPolicyPreview(string Binding, BsonDocument View);

/// <summary>Explicit approved spending rules. No budget, credit, tax, stock or order admission is supplied.</summary>
public sealed partial class OrderSpendingPolicyService
{
    private readonly IMongoDatabase database;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly OrganizationMasterDataService organizations;
    private readonly AuthorizationStore authorization;
    private readonly OrderSpendingPolicyRecords records;
    public OrderSpendingPolicyService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, OrganizationMasterDataService organizations) {
        if (!ReferenceEquals(database.Client, organizations.AddressSourceDatabase.Client) || database.DatabaseNamespace != organizations.AddressSourceDatabase.DatabaseNamespace)
            throw new ArgumentException("Policy source owners must use the same database and client", nameof(organizations));
        this.database = database; this.ids = ids; this.clock = clock; this.organizations = organizations;
        authorization = new(database, unit, clock); records = new(database);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string permission, ResourceScope resource, BsonDocument source) {
        internal ActorContext Actor { get; } = actor;
        internal AuthorizationSnapshot Authority { get; } = authority;
        internal IClientSessionHandle Tx { get; } = tx;
        internal string Permission { get; } = permission;
        internal ResourceScope Resource { get; } = resource;
        internal BsonDocument Source { get; } = source;
        internal BsonDocument? Head { get; set; }
        internal BsonDocument? Proposal { get; set; }
        internal PersonAuthorityProof? People { get; set; }
        internal PersonEvidence? OriginalMaker { get; set; }
        internal PersonEvidence? OriginalChecker { get; set; }
        internal DateTimeOffset? NotBefore { get; set; }
        internal DateTimeOffset? ExpiresAt { get; set; }
        internal DateTimeOffset? SessionExpiresAt { get; set; }
    }
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (metadata.IdempotencyKey.Length > 128 || metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')) ||
            metadata.Reason != metadata.Reason.Trim() || metadata.Reason.Length > 1000 || metadata.Reason.Any(char.IsControl)) throw OrderSpendingPolicyRules.Invalid();
    }
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot authority, string permission) {
        if (permission is not (Permissions.OrderSpendingPolicyRead or Permissions.OrderSpendingPolicyPropose or Permissions.OrderSpendingPolicyApprove or Permissions.OrderSubmit)) throw B.Broken();
        var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query); return query;
    }
    private AuthorizationQuery Require(Context context) {
        var query = Query(context.Actor, context.Authority, context.Permission);
        if (!ScopePolicy.IsResourceActive(context.Resource, context.Authority.Directory) || !query.Matches(context.Resource)) throw new AuthorizationDeniedException("scope_denied");
        return query;
    }
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Changed(); }
    private async Task StorageAsync(CancellationToken ct) {
        foreach (var name in OrderSpendingPolicySchemas.Collections) {
            var keys = OrderSpendingPolicySchemas.Keys(name);
            await InventoryRepository.RequireStorageAsync(database, name, OrderSpendingPolicySchemas.Validator(name), keys, ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleAsync(ct);
            if (definition["options"].AsBsonDocument.Contains("encryptedFields")) throw B.Broken();
            var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            bool Matches(BsonDocument index, BsonDocument key) => index["key"].Equals(key) && index.GetValue("unique", false).AsBoolean && !index.Contains("partialFilterExpression") && !InvoiceStorage.Unsafe(index);
            if (keys.Any(key => !indexes.Any(index => Matches(index, key))) || indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !keys.Any(key => Matches(index, key)))) throw B.Broken();
        }
        foreach (var name in new[] { "auditEvents", "idempotencyRecords", "outboxEvents" }) {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw B.Broken();
            PaymentEvidenceMigration.RequireIndexes(name, await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct));
        }
    }
    private static ImmutableArray<ScopeNode> DirectoryTarget(AuthorizationSnapshot authority, OrderSpendingPolicyTarget target) {
        OrderSpendingPolicyRules.RequireTarget(target);
        var nodes = ImmutableArray.CreateBuilder<ScopeNode>(); var id = target.Id; var kind = target.Kind;
        while (true) {
            var node = authority.Directory.Find(id, kind);
            if (node is not { Status: AuthorizationStatus.Active, Version: > 0 } || node.Version >= long.MaxValue) throw new AuthorizationDeniedException("resource_unavailable");
            nodes.Add(node); var parentKind = OrganizationChangePolicy.ExpectedParent(kind);
            if (parentKind is null) { if (node.ParentId is not null) throw new AuthorizationDeniedException("resource_unavailable"); break; }
            if (node.ParentId is not > 0 || nodes.Count >= 3) throw new AuthorizationDeniedException("resource_unavailable");
            id = node.ParentId.Value; kind = parentKind.Value;
        }
        return nodes.ToImmutable();
    }
    private static ResourceScope Resource(ActorContext actor, ImmutableArray<ScopeNode> nodes) => new(
        BuyingOrganizationId: nodes.Single(n => n.Kind == ScopeNodeKind.BuyingOrganization).Id,
        BranchId: nodes.Single(n => n.Kind == ScopeNodeKind.Branch).Id, DivisionId: nodes.SingleOrDefault(n => n.Kind == ScopeNodeKind.Division)?.Id, CreatedBy: actor.UserId);
    private async Task<Context> ContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        OrderSpendingPolicyTarget target, string permission, CancellationToken ct) {
        var nodes = DirectoryTarget(authority, target); var resource = Resource(actor, nodes);
        if (!Query(actor, authority, permission).Matches(resource)) throw new AuthorizationDeniedException("scope_denied");
        var source = await organizations.ReadAddressProfileAsync(tx, new(target.Kind, target.Id), ct);
        if (!nodes.OrderBy(node => node.Id).SequenceEqual(source["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node))) throw B.Changed();
        var context = new Context(actor, authority, tx, permission, resource, source); _ = Require(context);
        var row = await Rows(B.Heads).Find(tx, new BsonDocument("target", B.Target(target))).SingleOrDefaultAsync(ct);
        context.Head = row is null ? null : await records.HeadAsync(tx, row["_id"].AsInt64, ct); return context;
    }
    private async Task PeopleAsync(Context context, long? checker, long? maker, CancellationToken ct) {
        var users = new[] { context.Actor.UserId, checker ?? context.Actor.UserId, maker ?? context.Actor.UserId,
            context.OriginalMaker?.UserId ?? context.Actor.UserId, context.OriginalChecker?.UserId ?? context.Actor.UserId }.Distinct().Order().ToArray();
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [.. users], ct);
        if (proof.Evidence.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw new AuthorizationDeniedException("person_evidence_invalid");
        foreach (var original in new[] { context.OriginalMaker, context.OriginalChecker }.OfType<PersonEvidence>())
            if (proof.Evidence.Single(person => person.UserId == original.UserId).PersonId != original.PersonId) throw new AuthorizationDeniedException("order_spending_policy_person_changed");
        if (checker is { } actualChecker && maker is { } actualMaker) {
            var byUser = proof.Evidence.ToDictionary(person => person.UserId);
            if (context.OriginalMaker is not { } original || original.UserId != actualMaker || byUser[actualMaker].PersonId != original.PersonId ||
                context.OriginalChecker is { } oldChecker && (oldChecker.UserId != actualChecker || byUser[actualChecker].PersonId != oldChecker.PersonId)) throw new AuthorizationDeniedException("order_spending_policy_person_changed");
            if (!PersonIndependencePolicy.AreIndependent(byUser[actualChecker], byUser[actualMaker], original)) throw new AuthorizationDeniedException("independent_person_required");
        }
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct); context.People = proof;
    }
    private static void BindPeople(Context context, BsonDocument proposal, BsonDocument? decision = null) {
        context.OriginalMaker = proposal["people"].AsBsonArray.Select(B.Person).Single(person => person.UserId == proposal["actorId"].AsInt64);
        context.OriginalChecker = decision is null ? null : decision["people"].AsBsonArray.Select(B.Person).Single(person => person.UserId == decision["actorId"].AsInt64);
    }
    private async Task ApprovedPeopleAsync(Context context, CancellationToken ct) {
        if (context.Head is null || context.Head["versionId"].IsBsonNull) { await PeopleAsync(context, null, null, ct); return; }
        var version = await records.RowAsync(context.Tx, B.Versions, context.Head["versionId"].AsInt64, ct);
        var proposal = await records.ProposalAsync(context.Tx, version["proposalId"].AsInt64, ct);
        var decision = await records.DecisionAsync(context.Tx, version["decisionId"].AsInt64, proposal, ct);
        BindPeople(context, proposal, decision); await PeopleAsync(context, decision["actorId"].AsInt64, proposal["actorId"].AsInt64, ct);
    }
    private void Current(Context context) {
        _ = Require(context); context.People?.RequireCurrent(context.Tx, clock.UtcNow);
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client)) throw B.Broken();
        if (context.NotBefore is { } start && Now < start || context.ExpiresAt is { } end && Now >= end) throw B.Changed();
        if (context.SessionExpiresAt is { } sessionEnd && Now >= sessionEnd) throw new AuthorizationDeniedException("stale_or_inactive_session");
    }
    private async Task FenceAsync(Context context, CancellationToken ct) {
        Current(context); await organizations.FenceAddressProfileAsync(context.Tx, context.Source, true, ct);
        if (context.Head is { } expected) {
            var actual = await records.HeadAsync(context.Tx, expected["_id"].AsInt64, ct); Equal(B.Hash(actual), B.Hash(expected));
            if (actual["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(B.Heads).UpdateOneAsync(context.Tx, actual, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        } else if (await Rows(B.Heads).Find(context.Tx, new BsonDocument("target", B.Target(B.Target(context.Source["target"])))).AnyAsync(ct)) throw B.Changed();
        var query = Require(context); var clauses = query.Clauses.Where(clause => clause.Matches(context.Resource)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            context.Source["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
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
        return B.Date(row["idleExpiresAt"]) < B.Date(row["absoluteExpiresAt"]) ? B.Date(row["idleExpiresAt"]) : B.Date(row["absoluteExpiresAt"]);
    }
    private async Task<BsonDocument?> ForKeyAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string operation, string key, CancellationToken ct) {
        var name = operation == B.Prefix + "propose" ? B.Proposals : B.Decisions;
        var route = await Rows(name).Find(tx, new BsonDocument { { "actorId", actor.UserId }, { "operation", operation }, { "requestKey", key } }).Project(new BsonDocument("target", 1)).SingleOrDefaultAsync(ct);
        if (route is not null && !Query(actor, authority, B.Permission(operation)).Matches(Resource(actor, DirectoryTarget(authority, B.Target(route["target"]))))) throw new AuthorizationDeniedException("scope_denied");
        return await records.ForKeyAsync(tx, actor.UserId, operation, key, ct);
    }
}
