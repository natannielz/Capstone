using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Organizations.OrganizationAddressBson;

namespace UnitToko.Platform.Organizations;

public sealed record OrganizationAddressPreview(string Binding, BsonDocument View);

/// <summary>Postal governance only. Address approval creates no ordering, tax, spending, stock or recipient authority.</summary>
public sealed partial class OrganizationAddressService
{
    private readonly IMongoDatabase database;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly OrganizationMasterDataService organizations;
    private readonly AuthorizationStore authorization;
    private readonly OrganizationAddressRecords records;
    public OrganizationAddressService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, OrganizationMasterDataService organizations) {
        if (!ReferenceEquals(database.Client, organizations.AddressSourceDatabase.Client) || database.DatabaseNamespace != organizations.AddressSourceDatabase.DatabaseNamespace)
            throw new ArgumentException("Postal source owners must use the same database and client", nameof(organizations));
        this.database = database; this.ids = ids; this.clock = clock; this.organizations = organizations;
        authorization = new(database, unit, clock); records = new(database);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private sealed class Context(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        string permission, ResourceScope resource, BsonDocument source) {
        internal ActorContext Actor { get; } = actor;
        internal AuthorizationSnapshot Authority { get; } = authority;
        internal IClientSessionHandle Tx { get; } = tx;
        internal string Permission { get; } = permission;
        internal ResourceScope Resource { get; } = resource;
        internal BsonDocument Source { get; } = source;
        internal BsonDocument? Head { get; set; }
        internal BsonDocument? Proposal { get; set; }
        internal BsonDocument? Saved { get; set; }
        internal PersonAuthorityProof? People { get; set; }
        internal long? Checker { get; set; }
        internal long? Maker { get; set; }
        internal PersonEvidence? OriginalMaker { get; set; }
        internal PersonEvidence? OriginalChecker { get; set; }
        internal OrganizationAddressPurpose AddressPurpose { get; set; }
    }
    private static void Metadata(AuthorizationCommandMetadata metadata) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (metadata.IdempotencyKey.Length > 128 || metadata.IdempotencyKey.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')) ||
            metadata.Reason != metadata.Reason.Trim() || metadata.Reason.Length > 1000 || metadata.Reason.Any(char.IsControl)) throw OrganizationAddressPolicy.Invalid();
    }
    private AuthorizationQuery Require(Context context) {
        var query = ScopePolicy.Compile(context.Actor, context.Authority, context.Permission, clock.UtcNow); AuthorizationStore.Require(query);
        if (!ScopePolicy.IsResourceActive(context.Resource, context.Authority.Directory) || !query.Matches(context.Resource)) throw new AuthorizationDeniedException("scope_denied");
        return query;
    }
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Changed(); }
    private async Task StorageAsync(CancellationToken ct) {
        foreach (var name in OrganizationAddressSchemas.Collections) {
            var keys = OrganizationAddressSchemas.Keys(name);
            await InventoryRepository.RequireStorageAsync(database, name, OrganizationAddressSchemas.Validator(name), keys, ct);
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
    private async Task<BsonDocument?> HeadAsync(IClientSessionHandle tx, OrganizationAddressTarget target, OrganizationAddressPurpose purpose, CancellationToken ct) {
        var row = await Rows(B.Heads).Find(tx, new BsonDocument { { "target", B.Target(target) }, { "purpose", B.Purpose(purpose) } }).SingleOrDefaultAsync(ct);
        return row is null ? null : await records.HeadAsync(tx, row["_id"].AsInt64, ct);
    }
    private static ImmutableArray<ScopeNode> DirectoryTarget(AuthorizationSnapshot authority, OrganizationAddressTarget target) {
        var nodes = ImmutableArray.CreateBuilder<ScopeNode>(); var id = target.Id; var kind = target.Kind;
        while (true) {
            var node = authority.Directory.Find(id, kind);
            if (node is not { Status: AuthorizationStatus.Active, Version: > 0 } || node.Version >= long.MaxValue)
                throw new AuthorizationDeniedException("resource_unavailable");
            nodes.Add(node); var parentKind = OrganizationChangePolicy.ExpectedParent(kind);
            if (parentKind is null) { if (node.ParentId is not null) throw new AuthorizationDeniedException("resource_unavailable"); break; }
            if (node.ParentId is not > 0 || nodes.Count >= 3) throw new AuthorizationDeniedException("resource_unavailable");
            id = node.ParentId.Value; kind = parentKind.Value;
        }
        return nodes.ToImmutable();
    }
    private ResourceScope ReadResource(ActorContext actor, AuthorizationSnapshot authority, OrganizationAddressTarget selected, ImmutableArray<ScopeNode> nodes) {
        foreach (var permission in new[] { Permissions.TechnicalConfigure, Permissions.OrganizationApprove })
            if (ScopePolicy.Compile(actor, authority, permission, clock.UtcNow).Matches(new(IsAdministrative: true))) return new(IsAdministrative: true);
        var query = ScopePolicy.Compile(actor, authority, Permissions.OrganizationAddressRead, clock.UtcNow); AuthorizationStore.Require(query);
        foreach (var clause in query.Clauses) {
            var scope = clause.Scope;
            var buyingTarget = selected.Kind is ScopeNodeKind.BuyingOrganization or ScopeNodeKind.Branch or ScopeNodeKind.Division;
            if (buyingTarget ? scope.Kind is not (GrantScopeKind.BuyerOwned or GrantScopeKind.BuyingDivision or GrantScopeKind.BuyingBranch or GrantScopeKind.BuyingOrganization)
                : scope.Kind is not (GrantScopeKind.FulfillmentShop or GrantScopeKind.FulfillmentWarehouse)) continue;
            var resource = new ResourceScope(BuyingOrganizationId: scope.Kind is GrantScopeKind.BuyerOwned or GrantScopeKind.BuyingDivision or GrantScopeKind.BuyingBranch or GrantScopeKind.BuyingOrganization ? scope.OrganizationId : null,
                BranchId: scope.BranchId, DivisionId: scope.DivisionId,
                FulfillmentOrganizationId: scope.Kind is GrantScopeKind.FulfillmentShop or GrantScopeKind.FulfillmentWarehouse ? scope.OrganizationId : null,
                ShopId: scope.ShopId, WarehouseId: scope.WarehouseId, ChannelId: scope.ChannelId, CreatedBy: actor.UserId);
            var target = nodes[0];
            resource = target.Kind switch {
                ScopeNodeKind.Branch => resource with { BranchId = target.Id, BuyingOrganizationId = target.ParentId },
                ScopeNodeKind.Division => resource with { DivisionId = target.Id, BranchId = target.ParentId, BuyingOrganizationId = nodes.Single(n => n.Kind == ScopeNodeKind.BuyingOrganization).Id },
                ScopeNodeKind.Warehouse => resource with { WarehouseId = target.Id, ShopId = target.ParentId, FulfillmentOrganizationId = nodes.Single(n => n.Kind == ScopeNodeKind.FulfillmentOrganization).Id },
                _ => resource };
            if (MatchesTarget(resource, target, nodes) && target.Id == selected.Id && ScopePolicy.IsResourceActive(resource, authority.Directory) && clause.Matches(resource)) return resource;
        }
        throw new AuthorizationDeniedException("scope_denied");
    }
    private string ReadPermission(ActorContext actor, AuthorizationSnapshot authority, ResourceScope resource) {
        if (resource.IsAdministrative) {
            foreach (var permission in new[] { Permissions.TechnicalConfigure, Permissions.OrganizationApprove })
                if (ScopePolicy.Compile(actor, authority, permission, clock.UtcNow).Matches(resource)) return permission;
            throw new AuthorizationDeniedException("scope_denied");
        }
        return Permissions.OrganizationAddressRead;
    }
    internal static bool MatchesSource(ResourceScope resource, BsonDocument source) {
        var target = OrganizationProfileRecords.Node(source["target"]); var nodes = source["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node).ToArray();
        return MatchesTarget(resource, target, nodes);
    }
    private static bool MatchesTarget(ResourceScope resource, ScopeNode target, IReadOnlyCollection<ScopeNode> nodes) {
        return target.Kind switch {
            ScopeNodeKind.BuyingOrganization => resource.BuyingOrganizationId == target.Id,
            ScopeNodeKind.FulfillmentOrganization => resource.FulfillmentOrganizationId == target.Id,
            ScopeNodeKind.Branch => resource.BranchId == target.Id && resource.BuyingOrganizationId == target.ParentId,
            ScopeNodeKind.Division => resource.DivisionId == target.Id && resource.BranchId == target.ParentId && resource.BuyingOrganizationId == nodes.Single(n => n.Kind == ScopeNodeKind.BuyingOrganization).Id,
            ScopeNodeKind.Warehouse => resource.WarehouseId == target.Id && resource.ShopId == target.ParentId && resource.FulfillmentOrganizationId == nodes.Single(n => n.Kind == ScopeNodeKind.FulfillmentOrganization).Id,
            _ => false };
    }
    private async Task<Context> ContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        OrganizationAddressTarget target, OrganizationAddressPurpose purpose, string? governance, CancellationToken ct) {
        OrganizationAddressPolicy.RequireTarget(target, purpose);
        if (governance is not null) {
            var query = ScopePolicy.Compile(actor, authority, governance, clock.UtcNow); AuthorizationStore.Require(query);
            if (!query.Matches(new(IsAdministrative: true))) throw new AuthorizationDeniedException("scope_denied");
        } else DemandRead(actor, authority);
        var directoryNodes = DirectoryTarget(authority, target);
        var resource = governance is null ? ReadResource(actor, authority, target, directoryNodes) : new ResourceScope(IsAdministrative: true);
        var permission = governance ?? ReadPermission(actor, authority, resource);
        var source = await organizations.ReadAddressProfileAsync(tx, target, ct);
        var context = new Context(actor, authority, tx, permission, resource, source) { AddressPurpose = purpose }; _ = Require(context);
        foreach (var node in source["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node))
            if (authority.Directory.Find(node.Id, node.Kind) != node) throw B.Changed();
        context.Head = await HeadAsync(tx, target, purpose, ct); return context;
    }
    private async Task PeopleAsync(Context context, long? checker, long? maker, CancellationToken ct) {
        var users = new[] { context.Actor.UserId, checker ?? context.Actor.UserId, maker ?? context.Actor.UserId,
            context.OriginalMaker?.UserId ?? context.Actor.UserId, context.OriginalChecker?.UserId ?? context.Actor.UserId }.Distinct().Order().ToArray();
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(context.Tx, [.. users], ct);
        if (proof.Evidence.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 })) throw new AuthorizationDeniedException("person_evidence_invalid");
        foreach (var original in new[] { context.OriginalMaker, context.OriginalChecker }.OfType<PersonEvidence>())
            if (proof.Evidence.Single(person => person.UserId == original.UserId).PersonId != original.PersonId)
                throw new AuthorizationDeniedException("organization_address_person_changed");
        if (checker is { } actualChecker && maker is { } actualMaker) {
            var byUser = proof.Evidence.ToDictionary(person => person.UserId);
            if (context.OriginalMaker is not { } original || original.UserId != actualMaker || byUser[actualMaker].PersonId != original.PersonId ||
                context.OriginalChecker is { } oldChecker && (oldChecker.UserId != actualChecker || byUser[actualChecker].PersonId != oldChecker.PersonId))
                throw new AuthorizationDeniedException("organization_address_person_changed");
            if (!PersonIndependencePolicy.AreIndependent(byUser[actualChecker], byUser[actualMaker], original)) throw new AuthorizationDeniedException("independent_person_required");
        }
        await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct);
        context.People = proof; context.Checker = checker; context.Maker = maker;
    }
    private async Task ApprovedPeopleAsync(Context context, CancellationToken ct) {
        if (context.Head is null || context.Head["versionId"].IsBsonNull) { await PeopleAsync(context, null, null, ct); return; }
        var version = await records.RowAsync(context.Tx, B.Versions, context.Head["versionId"].AsInt64, ct);
        var proposal = await records.ProposalAsync(context.Tx, version["proposalId"].AsInt64, ct);
        var decision = await records.DecisionAsync(context.Tx, version["decisionId"].AsInt64, proposal, ct);
        BindPeople(context, proposal, decision);
        await PeopleAsync(context, decision["actorId"].AsInt64, proposal["actorId"].AsInt64, ct);
    }
    private static void BindPeople(Context context, BsonDocument proposal, BsonDocument? decision = null) {
        context.OriginalMaker = proposal["people"].AsBsonArray.Select(B.Person).Single(person => person.UserId == proposal["actorId"].AsInt64);
        context.OriginalChecker = decision is null ? null : decision["people"].AsBsonArray.Select(B.Person).Single(person => person.UserId == decision["actorId"].AsInt64);
    }
    private void Current(Context context) {
        _ = Require(context); context.People?.RequireCurrent(context.Tx, clock.UtcNow);
        if (!context.Tx.IsInTransaction || !ReferenceEquals(context.Tx.Client, database.Client)) throw B.Broken();
    }
    private async Task FenceAsync(Context context, CancellationToken ct) {
        Current(context); await organizations.FenceAddressProfileAsync(context.Tx, context.Source, true, ct);
        if (context.Head is { } expected) {
            var actual = await records.HeadAsync(context.Tx, expected["_id"].AsInt64, ct);
            Equal(B.Hash(actual), B.Hash(expected));
            if (actual["fence"].AsInt64 >= long.MaxValue - 1 ||
                (await Rows(B.Heads).UpdateOneAsync(context.Tx, actual, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        } else {
            var target = OrganizationProfileRecords.Node(context.Source["target"]);
            // The target's real node fence also serializes a first address head created concurrently.
            if (await Rows(B.Heads).Find(context.Tx, new BsonDocument { { "target", B.Target(new OrganizationAddressTarget(target.Kind, target.Id)) }, { "purpose", B.Purpose(context.AddressPurpose) } }).AnyAsync(ct)) throw B.Changed();
        }
        var query = Require(context); var clauses = query.Clauses.Where(clause => clause.Matches(context.Resource)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            context.Source["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node).Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            context.Authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        await LiveSessionAsync(context.Actor, context.Tx, ct);
        if (context.People is { } proof) await new PersonAuthorizationFence(database, clock).TouchAsync(context.Tx, proof, ct);
        Current(context); ct.ThrowIfCancellationRequested();
    }
    private async Task LiveSessionAsync(ActorContext actor, IClientSessionHandle tx, CancellationToken ct) {
        if (actor.Session is not { } session) return;
        var row = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).SingleOrDefaultAsync(ct);
        if (row is null || row["idleExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime || row["absoluteExpiresAt"].ToUniversalTime() <= clock.UtcNow.UtcDateTime)
            throw new AuthorizationDeniedException("stale_or_inactive_session");
    }
    private static void Expected(Context context, OrganizationAddressInput input) {
        if (context.Source["target"]["version"].AsInt64 != input.ExpectedScopeVersion || context.Source["profile"]["version"].AsInt64 != input.ExpectedProfileVersion ||
            (context.Head?["version"].AsInt64 ?? 0) != input.ExpectedAddressVersion) throw B.Changed();
    }
    private static BsonDocument PreviewView(Context context, OrganizationAddressInput input) => new() {
        { "target", B.Target(input.Target) }, { "purpose", B.Purpose(input.Purpose) }, { "code", context.Source["profile"]["code"] }, { "name", context.Source["profile"]["name"] },
        { "scopeVersion", input.ExpectedScopeVersion }, { "profileVersion", input.ExpectedProfileVersion }, { "addressVersion", input.ExpectedAddressVersion },
        { "address", B.Details(input.Address) }, { "sourceHash", context.Source["snapshotHash"] } };
    public async Task<OrganizationAddressPreview> PreviewAsync(ActorContext actor, OrganizationAddressInput input, CancellationToken ct = default) {
        input = OrganizationAddressPolicy.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ContextAsync(actor, authority, tx, input.Target, input.Purpose, Permissions.TechnicalConfigure, token); context.AddressPurpose = input.Purpose;
            Expected(context, input); await PeopleAsync(context, null, null, token); var binding = B.Binding(input, context.Source, context.Head);
            await FenceAsync(context, token); return new OrganizationAddressPreview(binding, PreviewView(context, input));
        }, ct);
    }
    private async Task EffectsAsync(Context context, BsonDocument row, CancellationToken ct) {
        await Rows("auditEvents").InsertOneAsync(context.Tx, B.Audit(row), cancellationToken: ct);
        await Rows("outboxEvents").InsertOneAsync(context.Tx, new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 },
            { "sourceKey", B.SourceKey(row) }, { "kind", row["operation"] }, { "actorId", row["actorId"] }, { "requestKey", row["requestKey"] }, { "payload", B.Result(row) },
            { "createdAt", row["recordedAt"] }, { "dueAt", row["recordedAt"] }, { "status", "pending" }, { "attempts", 0 } }, cancellationToken: ct);
    }
    private Task<BsonDocument> CommandAsync(ActorContext actor, string operation, AuthorizationCommandMetadata metadata, object body,
        Func<AuthorizationSnapshot, IClientSessionHandle, BsonDocument?, CancellationToken, Task<Context>> capture,
        Func<Context, CancellationToken, Task<BsonDocument>> write, CancellationToken ct) {
        Context? selected = null;
        return authorization.ExecuteCommandAsync(actor, operation, metadata, body, async (authority, tx, token) => {
            var permission = B.Permission(operation); var allowed = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(allowed);
            if (!allowed.Matches(new(IsAdministrative: true))) throw new AuthorizationDeniedException("scope_denied");
            var saved = await records.ForKeyAsync(tx, actor.UserId, operation, metadata.IdempotencyKey, token);
            selected = await capture(authority, tx, saved, token); var query = Require(selected);
            return new(new(true, "allowed", query.Clauses.Where(clause => clause.Matches(selected.Resource)).Select(clause => clause.GrantId).Distinct().ToImmutableArray()),
                selected.Source["hierarchy"].AsBsonArray.Select(OrganizationProfileRecords.Node).ToImmutableArray());
        }, (_, token) => write(selected ?? throw B.Broken(), token), ct, async (_, tx, token) => {
            var context = selected ?? throw B.Broken(); var row = await records.ForKeyAsync(tx, actor.UserId, operation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, metadata.Reason)); context.Head = await records.HeadAsync(tx, row["addressId"].AsInt64, token);
            BindPeople(context, await records.ProposalAsync(tx, row["proposalId"].AsInt64, token), operation == B.Prefix + "propose" ? null : row);
            await PeopleAsync(context, operation == B.Prefix + "propose" ? null : actor.UserId,
                operation == B.Prefix + "propose" ? null : (await records.ProposalAsync(tx, row["proposalId"].AsInt64, token))["actorId"].AsInt64, token);
            await FenceAsync(context, token);
        }, validateCurrent: () => Current(selected ?? throw B.Broken()));
    }
    public async Task<BsonDocument> ProposeAsync(ActorContext actor, OrganizationAddressInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = OrganizationAddressPolicy.Copy(input); B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var proposalId = (await ids.NextAsync(B.Proposals, ct)).Value; var addressId = (await ids.NextAsync(B.Heads, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var body = B.ProposeBody(input, binding);
        return await CommandAsync(actor, B.Prefix + "propose", metadata, body, async (authority, tx, saved, token) => {
            var context = await ContextAsync(actor, authority, tx, input.Target, input.Purpose, Permissions.TechnicalConfigure, token); context.AddressPurpose = input.Purpose; context.Saved = saved;
            if (saved is not null) BindPeople(context, saved);
            if (saved is null) { Expected(context, input); Equal(binding, B.Binding(input, context.Source, context.Head)); }
            await PeopleAsync(context, null, null, token); return context;
        }, async (context, token) => {
            Expected(context, input); Equal(binding, B.Binding(input, context.Source, context.Head)); var now = Now;
            var row = new BsonDocument { { "_id", proposalId }, { "schemaVersion", 1 }, { "addressId", context.Head?["_id"] ?? addressId }, { "proposalId", proposalId },
                { "revision", 1L }, { "baseVersion", input.ExpectedAddressVersion }, { "target", B.Target(input.Target) }, { "purpose", B.Purpose(input.Purpose) },
                { "source", context.Source.DeepClone() }, { "details", B.Details(input.Address) }, { "input", B.Input(input) }, { "binding", binding },
                { "people", new BsonArray(context.People!.Evidence.OrderBy(p => p.UserId).Select(B.Person)) } };
            B.Metadata(row, B.Prefix + "propose", auditId, actor, metadata, body, now);
            if (context.Head is null) {
                var head = new BsonDocument { { "_id", addressId }, { "schemaVersion", 1 }, { "target", B.Target(input.Target) }, { "purpose", B.Purpose(input.Purpose) },
                    { "rootProposalId", proposalId }, { "version", 0L }, { "versionId", BsonNull.Value }, { "createdAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime }, { "fence", 0L } };
                await Rows(B.Heads).InsertOneAsync(context.Tx, head, cancellationToken: token); context.Head = head;
            }
            await Rows(B.Proposals).InsertOneAsync(context.Tx, row, cancellationToken: token); await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
    private async Task<Context> ProposalContextAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long proposalId, CancellationToken ct) {
        var query = ScopePolicy.Compile(actor, authority, Permissions.OrganizationApprove, clock.UtcNow); AuthorizationStore.Require(query);
        if (!query.Matches(new(IsAdministrative: true))) throw new AuthorizationDeniedException("scope_denied");
        var raw = await records.RowAsync(tx, B.Proposals, proposalId, ct);
        var context = await ContextAsync(actor, authority, tx, B.Target(raw["target"]), B.Purpose(raw["purpose"]), Permissions.OrganizationApprove, ct);
        context.AddressPurpose = B.Purpose(raw["purpose"]); context.Proposal = await records.ProposalAsync(tx, proposalId, ct);
        BindPeople(context, context.Proposal);
        Equal(context.Head!["_id"], raw["addressId"]); return context;
    }
    private static bool BindingCurrent(Context context) => context.Proposal is { } proposal && context.Head is { } head &&
        proposal["source"].Equals(context.Source) && proposal["baseVersion"] == head["version"];
    private async Task PendingAsync(Context context, OrganizationAddressDecision decision, CancellationToken ct) {
        if (context.Proposal is null || await records.DecisionForAsync(context.Tx, context.Proposal, ct) is not null) throw B.Changed();
        if (decision == OrganizationAddressDecision.Approve && !BindingCurrent(context)) throw B.Changed();
    }
    public async Task<OrganizationAddressPreview> PreviewDecisionAsync(ActorContext actor, long proposalId, long expectedRevision, OrganizationAddressDecision decision, CancellationToken ct = default) {
        if (proposalId <= 0 || expectedRevision != 1 || !Enum.IsDefined(decision)) throw OrganizationAddressPolicy.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, token); var proposal = context.Proposal!;
            await PendingAsync(context, decision, token); await PeopleAsync(context, actor.UserId, proposal["actorId"].AsInt64, token);
            var view = new BsonDocument { { "proposalId", proposalId }, { "revision", 1L }, { "addressId", proposal["addressId"] }, { "target", proposal["target"] }, { "purpose", proposal["purpose"] },
                { "decision", B.Decision(decision) }, { "address", proposal["details"] }, { "code", proposal["source"]["profile"]["code"] }, { "name", proposal["source"]["profile"]["name"] },
                { "baseVersion", proposal["baseVersion"] }, { "bindingCurrent", BindingCurrent(context) } };
            await FenceAsync(context, token); return new OrganizationAddressPreview(B.DecisionBinding(proposal, decision), view);
        }, ct);
    }
    public async Task<BsonDocument> DecideAsync(ActorContext actor, long proposalId, long expectedRevision, OrganizationAddressDecision decision, string binding,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (proposalId <= 0 || expectedRevision != 1 || !Enum.IsDefined(decision)) throw OrganizationAddressPolicy.Invalid();
        B.RequireBinding(binding); Metadata(metadata); await StorageAsync(ct);
        var id = (await ids.NextAsync(B.Decisions, ct)).Value; var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        long? versionId = decision == OrganizationAddressDecision.Approve ? (await ids.NextAsync(B.Versions, ct)).Value : null;
        var body = B.DecideBody(proposalId, expectedRevision, decision, binding); var operation = B.Prefix + B.Decision(decision);
        return await CommandAsync(actor, operation, metadata, body, async (authority, tx, saved, token) => {
            var context = await ProposalContextAsync(actor, authority, tx, proposalId, token); context.Saved = saved;
            Equal(binding, B.DecisionBinding(context.Proposal!, decision)); if (saved is null) await PendingAsync(context, decision, token);
            await PeopleAsync(context, actor.UserId, context.Proposal!["actorId"].AsInt64, token); return context;
        }, async (context, token) => {
            await PendingAsync(context, decision, token); var proposal = context.Proposal!; var head = context.Head!; var now = Now;
            if (now.UtcDateTime < proposal["recordedAt"].ToUniversalTime()) throw B.Changed();
            var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "revision", 1L }, { "decision", B.Decision(decision) },
                { "versionId", versionId is { } approvedId ? new BsonInt64(approvedId) : BsonNull.Value },
                { "addressVersion", versionId.HasValue ? new BsonInt64(checked(proposal["baseVersion"].AsInt64 + 1)) : BsonNull.Value },
                { "input", B.DecisionInput(proposalId, expectedRevision, decision) }, { "binding", binding },
                { "people", new BsonArray(context.People!.Evidence.OrderBy(p => p.UserId).Select(B.Person)) } };
            foreach (var field in new[] { "addressId", "proposalId", "target", "purpose", "source", "details", "baseVersion" }) row.Add(field, proposal[field].DeepClone());
            B.Metadata(row, operation, auditId, actor, metadata, body, now);
            await Rows(B.Decisions).InsertOneAsync(context.Tx, row, cancellationToken: token);
            if (versionId is { } approved) {
                var previous = head["versionId"].IsBsonNull ? null : await records.RowAsync(context.Tx, B.Versions, head["versionId"].AsInt64, token);
                var version = new BsonDocument { { "_id", approved }, { "schemaVersion", 1 }, { "version", row["addressVersion"] }, { "decisionId", id },
                    { "previousVersionId", previous is null ? BsonNull.Value : previous["_id"] }, { "previousHash", previous is null ? BsonNull.Value : previous["snapshotHash"] }, { "effectiveAt", now.UtcDateTime } };
                foreach (var field in new[] { "addressId", "proposalId", "target", "purpose", "source", "details" }) version.Add(field, proposal[field].DeepClone());
                version.Add("snapshotHash", B.Hash(version)); await Rows(B.Versions).InsertOneAsync(context.Tx, version, cancellationToken: token);
                if (head["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(B.Heads).UpdateOneAsync(context.Tx, head,
                    Builders<BsonDocument>.Update.Set("version", row["addressVersion"]).Set("versionId", approved).Set("updatedAt", now.UtcDateTime).Inc("fence", 1L), cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
                context.Head = await Rows(B.Heads).Find(context.Tx, new BsonDocument("_id", head["_id"])).SingleAsync(token);
            }
            await EffectsAsync(context, row, token); return B.Result(row);
        }, ct);
    }
}
