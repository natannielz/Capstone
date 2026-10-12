using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    /// <summary>Current source control for one independently reread buyer catalog line.
    /// This supplies no quantity, lot eligibility, reservation or finite-value authority.</summary>
    internal Task<OrderSourceCapture> CaptureOrderSubmitSourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, OrderBuyingContext buying, OrderQuotedLine quotedLine, StockLocation requested,
        long poolId, long expectedRevision, CancellationToken ct) =>
        OrderSourceCapture.CreateAsync(this, actor, authority, tx, buying, quotedLine, requested, poolId, expectedRevision, ct);

    internal sealed class OrderSourceCapture
    {
        private readonly StockSourceSetupService owner;
        private readonly ActorContext actor;
        private readonly AuthorizationSnapshot authority;
        private readonly IClientSessionHandle tx;
        private readonly long number;
        private readonly OrderBuyingContext buying;
        private readonly OrderQuotedLine line;
        private readonly StockLocation requested;
        private readonly BsonDocument source, poolIdentity;
        private long minimumFence;
        private DateTimeOffset validUntil;
        private OrderSourceCapture(StockSourceSetupService owner, ActorContext actor, AuthorizationSnapshot authority,
            IClientSessionHandle tx, OrderBuyingContext buying, OrderQuotedLine line, StockLocation requested,
            BsonDocument source, BsonDocument pool, DateTimeOffset capturedAt, DateTimeOffset validUntil) {
            this.owner = owner; this.actor = actor; this.authority = authority; this.tx = tx; this.buying = buying; this.line = line;
            this.requested = requested; this.source = source.DeepClone().AsBsonDocument; poolIdentity = PoolIdentity(pool);
            number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; minimumFence = pool["fence"].AsInt64;
            CapturedAt = capturedAt; this.validUntil = validUntil;
            Pool = new(pool["_id"].AsInt64, InventoryGraph.Location(pool["location"].AsBsonDocument), pool["revision"].AsInt64);
            Authority = new(source["policyView"]["policy"]["_id"].AsInt64, source["policyView"]["policy"]["policyRevision"].AsInt64);
        }
        internal BsonDocument Snapshot => source.DeepClone().AsBsonDocument;
        internal InventoryPoolExpectation Pool { get; }
        internal OrderPolicyReference Authority { get; }
        internal DateTimeOffset CapturedAt { get; }
        internal DateTimeOffset ValidUntil => validUntil;
        internal void RequireContext(StockSourceSetupService expectedOwner, ActorContext expectedActor, AuthorizationSnapshot expectedAuthority,
            IClientSessionHandle session, OrderBuyingContext expectedBuying, OrderQuotedLine expectedLine) {
            if (!ReferenceEquals(owner, expectedOwner) || actor != expectedActor || !ReferenceEquals(authority, expectedAuthority) ||
                !ReferenceEquals(tx, session) || buying != expectedBuying || Quote(expectedLine) != source["catalog"]) throw OrderSourceChanged();
            RequireCurrent();
        }
        internal void RequireCurrent() {
            new InventoryRepository(owner.HistoryOwnerDatabase).RequireSession(tx);
            if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || owner.Now() < CapturedAt || owner.Now() >= validUntil ||
                Quote(line) != source["catalog"])
                throw OrderSourceChanged();
            owner.Require(actor, authority, Permissions.OrderSubmit, buying.Resource(actor.UserId));
        }
        internal async Task FenceAsync(CancellationToken ct) {
            RequireCurrent();
            var fresh = await owner.ReadOrderSourceAsync(actor, authority, tx, buying, line, requested, Pool.Id, Pool.Revision, CapturedAt, ct);
            if (SourceIdentity(fresh.Source) != SourceIdentity(source) || PoolIdentity(fresh.Pool) != poolIdentity ||
                fresh.Pool["fence"].AsInt64 < minimumFence) throw OrderSourceChanged();
            // Stock writes by the surrounding owning command can advance this same
            // pool fence. They confer no source authority; all non-fence fields and
            // the complete configuration/policy/activation histories still match.
            await owner.FenceRevisionPoolAsync(tx, fresh.Pool, false, ct);
            minimumFence = checked(fresh.Pool["fence"].AsInt64 + 1);
            foreach (var (table, row) in new[] {
                (StockSourcePolicyBson.Proposals, source["policyView"]["proposal"].AsBsonDocument),
                (StockSourcePolicyBson.Decisions, source["policyView"]["decision"].AsBsonDocument),
                (StockSourcePolicyBson.Policies, source["policyView"]["policy"].AsBsonDocument),
                (StockSourceActivationBson.Activations, source["activation"].AsBsonDocument) }) {
                if ((await owner.Rows(table).DeleteOneAsync(tx, row, cancellationToken: ct)).DeletedCount != 1) throw OrderSourceChanged();
                await owner.Rows(table).InsertOneAsync(tx, row.DeepClone().AsBsonDocument, cancellationToken: ct);
            }
            var checkedSource = await owner.ReadOrderSourceAsync(actor, authority, tx, buying, line, requested, Pool.Id, Pool.Revision, CapturedAt, ct);
            if (SourceIdentity(checkedSource.Source) != SourceIdentity(source) || PoolIdentity(checkedSource.Pool) != poolIdentity ||
                checkedSource.Pool["fence"] != minimumFence) throw OrderSourceChanged();
            validUntil = new[] { validUntil, fresh.ValidUntil, checkedSource.ValidUntil }.Min();
            RequireCurrent(); ct.ThrowIfCancellationRequested();
        }
        internal static async Task<OrderSourceCapture> CreateAsync(StockSourceSetupService owner, ActorContext actor, AuthorizationSnapshot authority,
            IClientSessionHandle tx, OrderBuyingContext buying, OrderQuotedLine line, StockLocation requested, long poolId, long revision, CancellationToken ct) {
            var at = owner.Now();
            var actual = await owner.ReadOrderSourceAsync(actor, authority, tx, buying, line, requested, poolId, revision, at, ct);
            var capture = new OrderSourceCapture(owner, actor, authority, tx, buying, line, requested, actual.Source, actual.Pool, at, actual.ValidUntil);
            await capture.FenceAsync(ct); return capture;
        }
        private static BsonDocument PoolIdentity(BsonDocument pool) { var value = pool.DeepClone().AsBsonDocument; value.Remove("fence"); return value; }
        private static BsonDocument SourceIdentity(BsonDocument source) { var value = source.DeepClone().AsBsonDocument; value.Remove("poolFence"); return value; }
    }

    private static PersistenceConflictException OrderSourceChanged() => new("order_stock_source_changed", "The current buyer catalog or approved source control changed");
    private static BsonDocument Quote(OrderQuotedLine line) {
        if (line is null || line.Definition is null || line.Price is null || line.FulfillmentOwner is null) throw OrderSourceChanged();
        return OrderSubmissionBson.Quote(new OrderCatalogQuote([line], line.Price.Currency, line.CatalogSubtotal));
    }
    private async Task<(BsonDocument Source, BsonDocument Pool, DateTimeOffset ValidUntil)> ReadOrderSourceAsync(
        ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, OrderBuyingContext buying, OrderQuotedLine quoted,
        StockLocation requested, long poolId, long revision, DateTimeOffset at, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx); buying.Validate(); requested.Validate();
        if (actor.Session is null) throw new AuthorizationDeniedException("stale_or_inactive_session");
        if (poolId <= 0 || revision <= 0 || Now() < at) throw OrderSourceChanged();
        var reviewed = Quote(quoted); var buyer = buying.Resource(actor.UserId);
        Require(actor, authority, Permissions.OrderSubmit, buyer);
        var catalog = new CatalogDefinitionQueryService(database, authorization, clock);
        var facts = await catalog.ReadSelectedAsync(actor, authority, tx, buying.Scope, [quoted.Definition.SkuId.Value],
            token => CompleteAuthorityAsync(actor, authority, Permissions.OrderSubmit, [buyer], tx, token), ct);
        var current = OrderQuotePolicy.Create([new(quoted.Definition.SkuId, quoted.Definition.Id, quoted.Definition.Version,
            quoted.Price.Id, quoted.Price.Version, quoted.Packages)], [.. facts.Select(value => new OrderCatalogFact(value.Family, value.Sku, value.Definition, value.CurrentPrice))]);
        // Catalog facts do not carry an order decision revision. Retain that exact
        // caller-bound identity while comparing all freshly read commercial facts.
        var line = current.Lines.Single() with { LineRevision = quoted.LineRevision };
        if (reviewed != Quote(line)) throw OrderSourceChanged();
        var seller = line.FulfillmentOwner;
        if (seller.OrganizationId != requested.OrganizationId.Value || seller.ShopId != requested.ShopId.Value ||
            seller.ChannelId != requested.ChannelId.Value || seller.WarehouseId is { } warehouse && warehouse != requested.WarehouseId.Value)
            throw new AuthorizationDeniedException("scope_denied");
        var combined = buyer with { FulfillmentOrganizationId = requested.OrganizationId.Value, ShopId = requested.ShopId.Value,
            WarehouseId = requested.WarehouseId.Value, ChannelId = requested.ChannelId.Value };
        Require(actor, authority, Permissions.OrderSubmit, combined);
        await OperatingPolicyStorageAsync(ct);
        await new StockSourceTopologyFence(database).FenceAsync(tx, requested.OrganizationId.Value, ct);
        var context = await HeldConfigurationAsync(tx, poolId, ct); var pool = context.Pool.DeepClone().AsBsonDocument;
        var configured = StockSourceSetupBson.ReadInput(context.Current["input"].AsBsonDocument);
        // Ordering's existing allocation contract binds the quote to this canonical
        // location. Cross-channel alias consumption needs its own explicit contract.
        if (configured.Location != requested || InventoryGraph.Location(pool["location"].AsBsonDocument) != requested)
            throw new DomainValidationException("order_stock_source_alias_unavailable", "Ordering requires the exact canonical seller location");
        if (pool["revision"] != revision || !pool["active"].AsBoolean || configured.Mode != StockAuthorityMode.WebLedger ||
            configured.Control is not (StockPoolControl.ExclusiveWebAllocation or StockPoolControl.SharedWebLedger)) throw OrderSourceChanged();
        var policies = await OperatingPolicyHistoryAsync(tx, context, ct); var policy = policies.Latest ?? throw OrderSourceChanged();
        await RequireActivatedPoolAsync(tx, pool, ct); var activation = await ActivationHistoryAsync(tx, context.Current, ct) ?? throw OrderSourceChanged();
        var source = new BsonDocument { { "poolId", poolId }, { "poolRevision", revision }, { "requestedLocation", StockSourceSetupBson.Location(requested) },
            { "configuration", context.Configuration.DeepClone() }, { "policyView", policy.DeepClone() },
            { "suspensionRevision", context.Latest?["suspensionRevision"] ?? 0L }, { "activation", activation.DeepClone() },
            { "poolFence", pool["fence"] }, { "capturedAt", at.UtcDateTime }, { "catalog", reviewed }, { "buying", OrderDraftBson.Context(buying) } };
        HeldPolicy(source);
        var expected = StockSourceActivationBson.ReadExpected(activation["expected"].AsBsonDocument);
        if (expected.PolicyId != policy["policy"]["_id"].AsInt64 || expected.PolicyRevision != policy["policy"]["policyRevision"].AsInt64 ||
            expected.PoolRevision != revision || expected.SuspensionRevision != source["suspensionRevision"].AsInt64 ||
            context.ApprovedAt > Now()) throw OrderSourceChanged();
        await CompleteAuthorityAsync(actor, authority, Permissions.OrderSubmit, [combined], tx, ct);
        var until = await OrderSourceDeadlineAsync(actor, authority, tx, buying, line, ct);
        if (Now() < at || Now() >= until) throw OrderSourceChanged();
        return (source, pool, until);
    }

    // The catalog owner above establishes and fences entitlement. These same domain
    // clauses provide a conservative lifetime for the final synchronous guard; they
    // never replace the catalog owner's fresh read or authorize another assortment.
    private async Task<DateTimeOffset> OrderSourceDeadlineAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, OrderBuyingContext buying, OrderQuotedLine line, CancellationToken ct) {
        var now = Now();
        var eligibleEntitlements = F.Eq("userId", actor.UserId) & F.Type("userId", BsonType.Int64) & F.Eq("status", "active") &
            F.Type("effectiveAt", BsonType.DateTime) & F.Lte("effectiveAt", now.UtcDateTime) & F.Eq("revokedAt", BsonNull.Value) &
            (F.Eq("expiresAt", BsonNull.Value) | F.Type("expiresAt", BsonType.DateTime) & F.Gt("expiresAt", now.UtcDateTime));
        var rows = await Rows("catalogEntitlements").Find(tx, eligibleEntitlements).Limit(257).ToListAsync(ct);
        if (rows.Count > 256) throw new AuthorizationDeniedException("catalog_authority_limit");
        var entitlements = rows.Select(CatalogVisibilityBson.Entitlement).Where(value => ScopePolicy.ContainsScope(value.AudienceScope, buying.Scope)).ToImmutableArray();
        var assortments = await Rows("catalogAssortments").Find(tx, F.In("_id", entitlements.Select(value => value.AssortmentId))).Limit(257).ToListAsync(ct);
        if (assortments.Count > 256) throw new AuthorizationDeniedException("catalog_authority_limit");
        var query = CatalogVisibilityPolicy.Compile(actor, authority, entitlements, [.. assortments.Select(CatalogVisibilityBson.Assortment)], now);
        var sku = await Rows("skus").Find(tx, F.Eq("_id", line.Definition.SkuId.Value) & CatalogSchemas.Validator("skus")).SingleOrDefaultAsync(ct) ?? throw OrderSourceChanged();
        var listings = sku["catalogListings"].AsBsonArray;
        var eligible = query.Clauses.Where(clause => listings.Any(value => value["assortmentId"] == clause.Assortment.Id &&
            CatalogRepository.Owner(value["ownerScope"].AsBsonDocument) == clause.Assortment.OwnerScope)).ToArray();
        if (!query.IsAllowed || eligible.Length == 0) throw new AuthorizationDeniedException("catalog_visibility_denied");
        static DateTimeOffset End(params DateTimeOffset?[] values) => values.OfType<DateTimeOffset>().DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
        var until = eligible.Max(clause => End(clause.Entitlement.ExpiresAt, clause.Grant.ExpiresAt, clause.Membership.ExpiresAt));
        var submit = Require(actor, authority, Permissions.OrderSubmit, buying.Resource(actor.UserId));
        until = new[] { until, submit.Clauses.Max(clause => End(authority.Grants.Single(grant => grant.Id == clause.GrantId).ExpiresAt,
            authority.Memberships.Single(member => member.Id == clause.MembershipId).ExpiresAt)) }.Min();
        if (actor.Session is { } session) {
            var live = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer,
                actor.ProviderSubject, actor.GrantVersion, now)).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("stale_or_inactive_session");
            until = new[] { until, new DateTimeOffset(live["idleExpiresAt"].ToUniversalTime()), new DateTimeOffset(live["absoluteExpiresAt"].ToUniversalTime()) }.Min();
        }
        return until;
    }
}
