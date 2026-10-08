using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Procurement;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    internal Task<DispatchSourceCapture> CaptureShipmentDispatchSourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, ResourceScope resource, StockLocation requested, long poolId, long poolRevision,
        OrderPolicyReference originalAuthority, CancellationToken ct) =>
        DispatchSourceCapture.CreateAsync(this, actor, authority, tx, resource, requested, poolId, poolRevision, originalAuthority, ct);

    internal sealed class DispatchSourceCapture
    {
        private readonly StockSourceSetupService owner;
        private readonly ActorContext actor;
        private readonly AuthorizationSnapshot authority;
        private readonly IClientSessionHandle tx;
        private readonly long number;
        private readonly ResourceScope resource;
        private readonly ResourceScope[] resources;
        private readonly StockLocation requested;
        private readonly BsonDocument source, poolIdentity;
        private readonly PurchaseOrderSourceReadCapture membership;
        private readonly DateTimeOffset capturedAt;
        private long minimumFence;
        private DateTimeOffset validUntil;
        private DispatchSourceCapture(StockSourceSetupService owner, ActorContext actor, AuthorizationSnapshot authority,
            IClientSessionHandle tx, ResourceScope resource, StockLocation requested, BsonDocument source,
            BsonDocument pool, PurchaseOrderSourceReadCapture membership, ResourceScope[] resources, DateTimeOffset at, DateTimeOffset until) {
            this.owner = owner; this.actor = actor; this.authority = authority; this.tx = tx; this.resource = resource; this.requested = requested;
            this.source = source.DeepClone().AsBsonDocument; poolIdentity = PoolIdentity(pool); this.membership = membership;
            this.resources = [.. resources];
            capturedAt = at; validUntil = until; number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; minimumFence = pool["fence"].AsInt64;
            Pool = new(pool["_id"].AsInt64, InventoryGraph.Location(pool["location"].AsBsonDocument), pool["revision"].AsInt64);
            Authority = new(source["policyView"]["policy"]["_id"].AsInt64, source["policyView"]["policy"]["policyRevision"].AsInt64);
        }
        internal InventoryPoolExpectation Pool { get; }
        internal OrderPolicyReference Authority { get; }
        internal DateTimeOffset ValidUntil => validUntil;
        internal BsonDocument Snapshot => source.DeepClone().AsBsonDocument;
        internal void RequireContext(StockSourceSetupService expectedOwner, ActorContext expectedActor, AuthorizationSnapshot expectedAuthority,
            IClientSessionHandle current, ResourceScope expectedResource, StockLocation expectedLocation, OrderPolicyReference originalAuthority) {
            if (!ReferenceEquals(owner, expectedOwner) || actor != expectedActor || !ReferenceEquals(authority, expectedAuthority) ||
                !ReferenceEquals(tx, current) || resource != expectedResource || requested != expectedLocation || Authority != originalAuthority) throw DispatchSourceChanged();
            RequireCurrent();
        }
        internal void RequireCurrent() {
            new InventoryRepository(owner.HistoryOwnerDatabase).RequireSession(tx);
            if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || owner.Now() < capturedAt || owner.Now() >= validUntil) throw DispatchSourceChanged();
            owner.Require(actor, authority, Permissions.ShipmentDispatch, resource);
        }
        internal async Task FenceAsync(CancellationToken ct) {
            var fresh = await RevalidateAsync(ct);
            await owner.FenceRevisionPoolAsync(tx, fresh, false, ct); minimumFence = checked(fresh["fence"].AsInt64 + 1);
            foreach (var (table, row) in new[] {
                (StockSourcePolicyBson.Proposals, source["policyView"]["proposal"].AsBsonDocument),
                (StockSourcePolicyBson.Decisions, source["policyView"]["decision"].AsBsonDocument),
                (StockSourcePolicyBson.Policies, source["policyView"]["policy"].AsBsonDocument),
                (StockSourceActivationBson.Activations, source["activation"].AsBsonDocument) }) {
                if ((await owner.Rows(table).DeleteOneAsync(tx, row, cancellationToken: ct)).DeletedCount != 1) throw DispatchSourceChanged();
                await owner.Rows(table).InsertOneAsync(tx, row.DeepClone().AsBsonDocument, cancellationToken: ct);
            }
            var final = await RevalidateAsync(ct);
            if (final["fence"] != minimumFence) throw DispatchSourceChanged();
            RequireCurrent(); ct.ThrowIfCancellationRequested();
        }
        private async Task<BsonDocument> RevalidateAsync(CancellationToken ct) {
            RequireCurrent();
            await new StockSourceTopologyFence(owner.HistoryOwnerDatabase).FenceAsync(tx, requested.OrganizationId.Value, ct);
            await owner.CompleteAuthorityAsync(actor, authority, Permissions.ShipmentDispatch, resources, tx, ct);
            validUntil = new[] { validUntil, await owner.DispatchSourceDeadlineAsync(actor, authority, tx, resources, ct) }.Min();
            // The initial owner read authenticated the whole graph. Re-read its exact
            // rows, query membership, counts and schemas after the current authority fences.
            await membership.RequireAsync(owner.HistoryOwnerDatabase, tx, ct);
            var pool = await owner.Rows("inventoryPools").Find(tx, F.Eq("_id", Pool.Id)).SingleOrDefaultAsync(ct) ?? throw DispatchSourceChanged();
            InventoryGraph.RequirePool(pool);
            if (PoolIdentity(pool) != poolIdentity || pool["fence"].AsInt64 < minimumFence) throw DispatchSourceChanged();
            RequireCurrent(); ct.ThrowIfCancellationRequested(); return pool;
        }
        internal static async Task<DispatchSourceCapture> CreateAsync(StockSourceSetupService owner, ActorContext actor, AuthorizationSnapshot authority,
            IClientSessionHandle tx, ResourceScope resource, StockLocation requested, long poolId, long revision, OrderPolicyReference originalAuthority, CancellationToken ct) {
            var at = owner.Now(); var membership = new PurchaseOrderSourceReadCapture(); var reader = owner.CaptureHistory(membership);
            var read = await reader.ReadDispatchControlAsync(actor, authority, tx, resource, requested, poolId, revision, originalAuthority, at, ct);
            membership.Seal();
            var result = new DispatchSourceCapture(owner, actor, authority, tx, resource, requested, read.Source, read.Pool, membership, read.Resources, at, read.Until);
            await result.FenceAsync(ct); return result;
        }
        private static BsonDocument PoolIdentity(BsonDocument row) { var value = row.DeepClone().AsBsonDocument; value.Remove("fence"); return value; }
    }

    private async Task<(BsonDocument Source, BsonDocument Pool, ResourceScope[] Resources, DateTimeOffset Until)> ReadDispatchControlAsync(ActorContext actor,
        AuthorizationSnapshot authority, IClientSessionHandle tx, ResourceScope resource, StockLocation requested, long poolId, long revision,
        OrderPolicyReference originalAuthority, DateTimeOffset at, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx); requested.Validate(); OrderAdmissionPolicy.Reference(originalAuthority);
        if (actor.Session is null || poolId <= 0 || revision <= 0 || Now() < at || resource.IsAdministrative ||
            resource.FulfillmentOrganizationId != requested.OrganizationId.Value || resource.ShopId != requested.ShopId.Value ||
            resource.ChannelId != requested.ChannelId.Value || resource.WarehouseId is { } warehouse && warehouse != requested.WarehouseId.Value)
            throw DispatchSourceChanged();
        Require(actor, authority, Permissions.ShipmentDispatch, resource); await OperatingPolicyStorageAsync(ct); await ActivationOwnerStorageAsync(ct);
        await new StockSourceTopologyFence(database).FenceAsync(tx, requested.OrganizationId.Value, ct);
        var scoped = await SuspensionContextAsync(actor, authority, tx, poolId, Permissions.ShipmentDispatch, ct);
        // This second existing owner read retains full current/reverse alias membership,
        // including disabled mappings, in the immutable operation-local capture.
        var context = await HeldConfigurationAsync(tx, poolId, ct);
        if (context.Configuration != scoped.Configuration || context.Pool != scoped.Pool) throw DispatchSourceChanged();
        var pool = context.Pool.DeepClone().AsBsonDocument; var setup = StockSourceSetupBson.ReadInput(context.Current["input"].AsBsonDocument);
        if (setup.Location != requested || InventoryGraph.Location(pool["location"].AsBsonDocument) != requested || pool["revision"] != revision ||
            !pool["active"].AsBoolean || setup.Mode != StockAuthorityMode.WebLedger || setup.Control is not (StockPoolControl.ExclusiveWebAllocation or StockPoolControl.SharedWebLedger))
            throw DispatchSourceChanged();
        var history = await OperatingPolicyHistoryAsync(tx, context, ct); var policy = history.Latest ?? throw DispatchSourceChanged();
        var policyReference = new OrderPolicyReference(policy["policy"]["_id"].AsInt64, policy["policy"]["policyRevision"].AsInt64);
        if (policyReference != originalAuthority || policy["proposal"]["configuration"] != ReviewedHeldConfiguration(context.Configuration) ||
            policy["proposal"]["suspensionRevision"] != (context.Latest?["suspensionRevision"] ?? 0L) || policy["proposal"]["input"]["writer"] != "unitToko")
            throw DispatchSourceChanged();
        await RequireActivatedPoolAsync(tx, pool, ct); var activation = await ActivationHistoryAsync(tx, context.Current, ct) ?? throw DispatchSourceChanged();
        var expected = StockSourceActivationBson.ReadExpected(activation["expected"].AsBsonDocument);
        if (expected.PoolRevision != revision || expected.PolicyId != policyReference.Id || expected.PolicyRevision != policyReference.Revision ||
            expected.SuspensionRevision != (context.Latest?["suspensionRevision"].AsInt64 ?? 0) ||
            expected.ConfigurationHash != StockSourceActivationBson.Hash(ReviewedHeldConfiguration(context.Configuration)) || context.ApprovedAt > Now()) throw DispatchSourceChanged();
        var resources = scoped.Resources.Append(resource).Distinct().ToArray();
        await CompleteAuthorityAsync(actor, authority, Permissions.ShipmentDispatch, resources, tx, ct);
        var until = await DispatchSourceDeadlineAsync(actor, authority, tx, resources, ct);
        if (Now() < at || Now() >= until) throw DispatchSourceChanged();
        return (new BsonDocument { { "poolId", poolId }, { "poolRevision", revision }, { "requestedLocation", StockSourceSetupBson.Location(requested) },
            { "configuration", context.Configuration.DeepClone() }, { "policyView", policy.DeepClone() }, { "activation", activation.DeepClone() },
            { "suspensionRevision", context.Latest?["suspensionRevision"] ?? 0L }, { "poolFence", pool["fence"] }, { "capturedAt", at.UtcDateTime } }, pool, resources, until);
    }
    private async Task<DateTimeOffset> DispatchSourceDeadlineAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        ResourceScope[] resources, CancellationToken ct) {
        var query = ScopePolicy.Compile(actor, authority, Permissions.ShipmentDispatch, Now()); AuthorizationStore.Require(query);
        static DateTimeOffset End(params DateTimeOffset?[] values) => values.OfType<DateTimeOffset>().DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
        var until = resources.Min(resource => query.Clauses.Where(clause => clause.Matches(resource)).Select(clause => End(
            authority.Grants.Single(grant => grant.Id == clause.GrantId).ExpiresAt,
            authority.Memberships.Single(member => member.Id == clause.MembershipId).ExpiresAt)).DefaultIfEmpty(Now()).Max());
        until = new[] { until, actor.FactorCompletedAt.Add(Permissions.Catalog[Permissions.ShipmentDispatch].Assurance.MaximumAge ?? SessionPolicy.FreshFactorLifetime) }.Min();
        var session = actor.Session ?? throw DispatchSourceChanged();
        var live = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(session, actor.UserId, actor.ProviderIssuer,
            actor.ProviderSubject, actor.GrantVersion, Now())).SingleOrDefaultAsync(ct) ?? throw DispatchSourceChanged();
        return new[] { until, new DateTimeOffset(live["idleExpiresAt"].ToUniversalTime(), TimeSpan.Zero),
            new DateTimeOffset(live["absoluteExpiresAt"].ToUniversalTime(), TimeSpan.Zero) }.Min();
    }
    private static DomainValidationException DispatchSourceChanged() => new("shipment_dispatch_source_changed", "The original reserved source and current approved dispatcher control must remain exact");
}
