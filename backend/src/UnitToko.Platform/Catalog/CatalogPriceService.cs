using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Catalog.CatalogPriceBson;

namespace UnitToko.Platform.Catalog;

/// <summary>Scoped transactional price review. This service grants no buyer entitlement.
/// Requires the forward price migration; command recovery never resubmits a business action.</summary>
public sealed partial class CatalogPriceService
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private readonly IMongoDatabase database;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly CatalogRepository repository;
    private readonly AuthorizationStore authorization;
    public CatalogPriceService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock)
    {
        this.database = database; this.ids = ids; this.clock = clock;
        repository = new(database); authorization = new(database, unit, clock);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private sealed class Context(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx, string permission,
        BsonDocument sku, BsonDocument definition, BsonDocument family, BsonDocument category, GrantScope owner)
    {
        internal ActorContext Actor { get; } = actor;
        internal AuthorizationSnapshot Snapshot { get; } = snapshot;
        internal IClientSessionHandle Tx { get; } = tx;
        internal string Permission { get; } = permission;
        internal BsonDocument Sku { get; } = sku;
        internal BsonDocument Definition { get; } = definition;
        internal BsonDocument Family { get; } = family;
        internal BsonDocument Category { get; } = category;
        internal GrantScope Owner { get; } = owner;
        internal ResourceScope Resource => CatalogRepository.Resource(Owner);
        internal BsonDocument? Head { get; set; }
        internal PriceVersion? Current { get; set; }
        internal bool HeadChanged { get; set; }
    }
    private static string Permission(CatalogPriceAccess access) => access switch {
        CatalogPriceAccess.Prepare => Permissions.PricingManage, CatalogPriceAccess.Review => Permissions.PricingApprove,
        _ => throw new DomainValidationException("invalid_catalog_price_access", "Select a supported price workspace") };
    private static void Metadata(AuthorizationCommandMetadata metadata)
    {
        AuthorizationStore.ValidateMetadata(metadata);
        if (!CatalogReceiptRules.Key(metadata.IdempotencyKey) || !B.Text(metadata.Reason, 1000) ||
            !B.Text(metadata.CorrelationId, 128)) throw B.Invalid("invalid_catalog_price_request");
    }
    private DateTimeOffset Now()
    {
        var now = clock.UtcNow;
        if (now == default || now.Offset != TimeSpan.Zero) throw B.Invalid();
        return DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
    }
    private async Task StorageAsync(CancellationToken ct)
    {
        try {
            foreach (var name in CatalogPriceSchemas.Collections)
                await InventoryRepository.RequireStorageAsync(database, name, CatalogPriceSchemas.Validator(name), CatalogPriceSchemas.Key(name), ct);
        }
        catch (DomainValidationException error) when (error.Code == "inventory_storage_unavailable") { throw B.Invalid("catalog_price_storage_unavailable"); }
    }
    private AuthorizationQuery Require(Context context)
    {
        var query = ScopePolicy.Compile(context.Actor, context.Snapshot, context.Permission, clock.UtcNow);
        AuthorizationStore.Require(query);
        if (!ScopePolicy.IsResourceActive(context.Resource, context.Snapshot.Directory) || !query.Matches(context.Resource))
            throw B.Invalid("scope_denied");
        return query;
    }
    private async Task<Context> ContextAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx,
        long skuId, string permission, CancellationToken ct)
    {
        AuthorizationStore.Require(ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow));
        var sku = await repository.GetAsync(tx, "skus", skuId, ct);
        var owner = CatalogRepository.Owner(sku["authoringOwner"].AsBsonDocument);
        var resource = CatalogRepository.Resource(owner);
        if (!ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow).Matches(resource)) throw B.Invalid("scope_denied");
        var family = await repository.GetAsync(tx, "productFamilies", sku["familyId"].AsInt64, ct);
        var category = await repository.GetAsync(tx, "catalogCategories", family["categoryId"].AsInt64, ct);
        if (CatalogRepository.Owner(family["authoringOwner"].AsBsonDocument) != owner ||
            CatalogRepository.Owner(category["authoringOwner"].AsBsonDocument) != owner ||
            family["categoryCode"] != category["code"]) throw B.Invalid();
        var definition = await repository.GetAsync(tx, "variantDefinitions", sku["currentDefinitionId"].AsInt64, ct);
        CatalogAuthoringService.RequirePair(sku, definition);
        var context = new Context(actor, snapshot, tx, permission, sku, definition, family, category, owner);
        _ = Require(context);
        context.Head = await Rows(B.Heads).Find(tx, F.Eq("_id", skuId)).SingleOrDefaultAsync(ct);
        var latest = await Rows(B.Versions).Find(tx, F.Eq("skuId", skuId)).Sort(FSort("version", -1)).FirstOrDefaultAsync(ct);
        if (context.Head is null) {
            if (latest is not null) throw B.Invalid();
        } else {
            B.Head(context.Head);
            if (latest is null || context.Head["priceId"] != latest["_id"] || context.Head["version"] != latest["version"]) throw B.Invalid();
            context.Current = await PriceGraphAsync(context, latest, ct);
        }
        return context;
    }
    private static BsonDocument FSort(string name, int direction) => new(name, direction);
    private static CatalogPriceExpectation Expected(Context context) => new(context.Sku["revision"].AsInt64,
        new(context.Definition["_id"].AsInt64), context.Definition["version"].AsInt64, context.Current?.Version ?? 0);
    private static void Active(Context context)
    {
        if (!context.Sku["active"].AsBoolean || !context.Family["active"].AsBoolean || !context.Category["active"].AsBoolean)
            throw B.Invalid("catalog_price_inactive");
    }
    private async Task CompleteAsync(Context context, CancellationToken ct)
    {
        _ = Require(context);
        foreach (var item in new[] { ("catalogCategories", context.Category), ("productFamilies", context.Family), ("skus", context.Sku) })
            await repository.TouchAsync(context.Tx, item.Item1, item.Item2, ct);
        if (!context.HeadChanged && context.Head is { } head) {
            if (head["fence"].AsInt64 == long.MaxValue) throw B.Invalid();
            var filter = F.Eq("_id", head["_id"]) & F.Eq("priceId", head["priceId"]) & F.Eq("version", head["version"]) &
                F.Lt("fence", long.MaxValue) & new BsonDocumentFilterDefinition<BsonDocument>(CatalogPriceSchemas.Validator(B.Heads));
            if ((await Rows(B.Heads).UpdateOneAsync(context.Tx, filter, Builders<BsonDocument>.Update.Inc("fence", 1L),
                cancellationToken: ct)).ModifiedCount != 1) throw B.Invalid("catalog_price_changed");
        }
        var clauses = Require(context).Clauses.Where(clause => clause.Matches(context.Resource)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(ScopePolicy.RequiredNodes(context.Resource, context.Snapshot.Directory)).Distinct(),
            [], context.Snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        await RequireLiveSessionAsync(context.Actor, context.Tx, ct);
        _ = Require(context); ct.ThrowIfCancellationRequested();
    }
    private async Task RequireLiveSessionAsync(ActorContext actor, IClientSessionHandle tx, CancellationToken ct)
    {
        if (actor.Session is { } reference) {
            var live = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(reference, actor.UserId,
                actor.ProviderIssuer, actor.ProviderSubject, actor.GrantVersion, clock.UtcNow))
                .Project(new BsonDocument { { "idleExpiresAt", 1 }, { "absoluteExpiresAt", 1 } }).SingleOrDefaultAsync(ct);
            var now = clock.UtcNow.ToUnixTimeMilliseconds();
            if (live is null || !live.TryGetValue("idleExpiresAt", out var idle) || !idle.IsBsonDateTime ||
                !live.TryGetValue("absoluteExpiresAt", out var absolute) || !absolute.IsBsonDateTime ||
                idle.AsBsonDateTime.MillisecondsSinceEpoch <= now || absolute.AsBsonDateTime.MillisecondsSinceEpoch <= now)
                throw B.Invalid("stale_or_inactive_session");
        }
        ct.ThrowIfCancellationRequested();
    }
    private async Task<T> ReadAsync<T>(ActorContext actor, long skuId, CatalogPriceAccess access, string operation, BsonDocument selection,
        Func<Context, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        if (skuId <= 0) throw B.Invalid(); var permission = Permission(access); await StorageAsync(ct);
        var auditId = await ids.NextAsync("auditEvents", ct); var correlation = Guid.NewGuid().ToString("N");
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var context = await ContextAsync(actor, snapshot, tx, skuId, permission, token);
            var result = await read(context, token);
            await Rows("auditEvents").InsertOneAsync(tx, new BsonDocument {
                { "_id", auditId.Value }, { "schemaVersion", 1 }, { "kind", "catalog.price.read" }, { "operation", operation },
                { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "permission", permission },
                { "owner", CatalogRepository.Owner(context.Owner) }, { "selection", selection.DeepClone() },
                { "correlationId", correlation }, { "createdAt", Now().UtcDateTime } }, cancellationToken: token);
            await CompleteAsync(context, token);
            return result;
        }, ct);
    }
    public Task<CatalogPricePreview> PreviewAsync(ActorContext actor, long skuId, CatalogPriceAccess access, CancellationToken ct = default) =>
        ReadAsync(actor, skuId, access, B.Prefix + "preview", new("skuId", skuId), (context, _) =>
            Task.FromResult(new CatalogPricePreview(CatalogRepository.Sku(context.Sku), CatalogRepository.Definition(context.Definition),
                Expected(context), context.Current)), ct);
    public Task<PriceVersion?> ReadCurrentAsync(ActorContext actor, long skuId, CatalogPriceAccess access, CancellationToken ct = default) =>
        ReadAsync(actor, skuId, access, B.Prefix + "current", new("skuId", skuId), (context, _) => Task.FromResult(context.Current), ct);
    public Task<IReadOnlyList<BsonDocument>> ReadHistoryAsync(ActorContext actor, long skuId, CatalogPriceAccess access,
        long? beforeVersion = null, int limit = 50, CancellationToken ct = default)
    {
        if (beforeVersion is <= 0 || limit is < 1 or > 100) throw B.Invalid();
        return ReadAsync<IReadOnlyList<BsonDocument>>(actor, skuId, access, B.Prefix + "history", new() {
            { "skuId", skuId }, { "beforeVersion", beforeVersion.HasValue ? new BsonInt64(beforeVersion.Value) : BsonNull.Value }, { "limit", limit } },
            async (context, token) => {
                var filter = F.Eq("skuId", skuId);
                if (beforeVersion.HasValue) filter &= F.Lt("version", beforeVersion.Value);
                var rows = await Rows(B.Versions).Find(context.Tx, filter).Sort(FSort("version", -1)).Limit(limit).ToListAsync(token);
                foreach (var row in rows) _ = await PriceGraphAsync(context, row, token);
                return rows.Select(row => row.DeepClone().AsBsonDocument).ToArray();
            }, ct);
    }
    public Task<BsonDocument> ReadProposalAsync(ActorContext actor, long skuId, long proposalId, CatalogPriceAccess access, CancellationToken ct = default) =>
        ReadAsync<BsonDocument>(actor, skuId, access, B.Prefix + "proposal", new() { { "skuId", skuId }, { "proposalId", proposalId } },
            (context, token) => ProposalViewAsync(context, proposalId, token), ct);
    private async Task<BsonDocument> ProposalViewAsync(Context context, long proposalId, CancellationToken token)
    {
                var proposal = await ProposalAsync(context, proposalId, token);
                var decision = await Rows(B.Decisions).Find(context.Tx, F.Eq("proposalId", proposalId)).SingleOrDefaultAsync(token);
                BsonDocument? price = null;
                if (decision is not null) {
                    B.Decision(decision, proposal); await ProvenanceAsync(context.Tx, decision, proposal, token);
                    if (decision["decision"] == "approve") {
                        price = await GetAsync(context.Tx, B.Versions, decision["priceId"].AsInt64, token);
                        _ = await PriceGraphAsync(context, price, token);
                    }
                }
                return new() { { "proposal", proposal }, { "decision", (BsonValue?)decision ?? BsonNull.Value }, { "price", (BsonValue?)price ?? BsonNull.Value } };
    }
}
