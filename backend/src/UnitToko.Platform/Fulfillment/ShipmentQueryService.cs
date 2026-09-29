using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Fulfillment;

public sealed record ShipmentReadLine(LongId SkuId, string SkuCode, string ProductName, VariantDefinitionVersion Definition, int Packages, long BaseUnits);
public sealed record ShipmentReadView(LongId Id, long Revision, LongId OrderId, LongId SuborderId, LongId NumberEntity, LongId NumberChannel,
    int NumberYear, long Number, DateTimeOffset DispatchedAt, string BuyerName, OrderDeliveryRequest Delivery, LongId CourierId,
    string CourierName, ImmutableArray<ShipmentReadLine> Lines);

/// <summary>Current scoped access to accepted dispatch facts, without costs, approval bodies or stock authority.
/// Courier assignment is compiled into the query before pagination; BuyerOwned remains creator-bound.</summary>
public sealed partial class ShipmentQueryService
{
    private readonly IMongoDatabase database;
    private readonly IClock clock;
    private readonly AuthorizationStore authorization;
    private readonly OrderSubmissionService orders;
    private readonly ShipmentPreparationService preparations;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    public ShipmentQueryService(IMongoDatabase database, MongoUnitOfWork unit, IClock clock, OrderSubmissionService orders, ShipmentPreparationService preparations) {
        if (!ReferenceEquals(database.Client, orders.SourceDatabase.Client) || !database.DatabaseNamespace.Equals(orders.SourceDatabase.DatabaseNamespace) ||
            !ReferenceEquals(database.Client, preparations.SourceDatabase.Client) || !database.DatabaseNamespace.Equals(preparations.SourceDatabase.DatabaseNamespace))
            throw new ArgumentException("Shipment reads and source verification must share one database transaction");
        this.database = database; this.clock = clock; this.orders = orders; this.preparations = preparations; authorization = new(database, unit, clock);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal IMongoDatabase SourceDatabase => database;
    internal async Task StorageAsync(CancellationToken ct) { await preparations.StorageAsync(ct); await ShipmentDispatchStorage.RequireAsync(database, ct); }
    private AuthorizationQuery Query(ActorContext actor, AuthorizationSnapshot snapshot, string permission = Permissions.ShipmentRead) {
        var query = ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow); AuthorizationStore.Require(query); return query;
    }
    private static ResourceScope Resource(BsonDocument row) => ShipmentPreparationBson.Scope(row["scope"].AsBsonDocument) with {
        AssignedUserIds = [row["courierId"].AsInt64] };
    private async Task<ShipmentReadView> VerifiedAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await new ShipmentDispatchRecords(database).ReadAsync(tx, id, ct);
        if (!Query(actor, snapshot).Matches(Resource(row))) throw ShipmentDispatchRecords.Invalid();
        await orders.RequireShipmentHistoryAsync(tx, actor, row, ct);
        var source = ShipmentPreparationBson.Source(row["source"].AsBsonDocument);
        var lines = ShipmentPreparationBson.Lines(row["lines"].AsBsonArray).Select(selected => {
            var line = source.Lines.Single(line => line.SkuId == selected.SkuId);
            return new ShipmentReadLine(line.SkuId, line.SkuCode, line.ProductName, line.Package, selected.Packages,
                checked((long)selected.Packages * line.Package.BaseUnitsPerPackage));
        }).ToImmutableArray();
        return new(new(id), row["revision"].AsInt64, source.OrderId, source.SuborderId, new(row["numberEntity"].AsInt64), new(row["numberChannel"].AsInt64),
            checked((int)row["numberYear"].AsInt64), row["number"].AsInt64, OrderDraftBson.Time(row["recordedAt"]), source.BuyerName,
            source.Delivery, new(row["courierId"].AsInt64), row["courierName"].AsString, lines);
    }
    public async Task<ShipmentReadView> ReadAsync(ActorContext actor, long id, CancellationToken ct = default) {
        if (id <= 0) throw ShipmentDispatchRecords.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var row = await Rows("shipments").Find(tx, F.Eq("_id", id) & ScopeQuery.Create(Query(actor, snapshot))).SingleOrDefaultAsync(token) ?? throw ShipmentDispatchRecords.Invalid();
            var result = await VerifiedAsync(actor, snapshot, tx, id, token);
            await FenceAsync(actor, snapshot, tx, [Resource(row)], token); return result;
        }, ct);
    }
    public async Task<(ImmutableArray<ShipmentReadView> Items, bool HasMore)> ListAsync(ActorContext actor, long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw ShipmentDispatchRecords.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var filter = ScopeQuery.Create(Query(actor, snapshot)); if (beforeId.HasValue) filter &= F.Lt("_id", beforeId.Value);
            var rows = await Rows("shipments").Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = ImmutableArray.CreateBuilder<ShipmentReadView>();
            // Verify the extra row as well: its existence influences the disclosed paging state.
            foreach (var row in rows) items.Add(await VerifiedAsync(actor, snapshot, tx, row["_id"].AsInt64, token));
            await FenceAsync(actor, snapshot, tx, rows.Select(Resource).ToArray(), token);
            return (items.Take(25).ToImmutableArray(), rows.Count > 25);
        }, ct);
    }
    internal async Task FenceAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx, ResourceScope[] resources, CancellationToken ct, string permission = Permissions.ShipmentRead) {
        if (permission is not (Permissions.ShipmentRead or Permissions.ShipmentPrepare)) throw ShipmentDispatchRecords.Invalid();
        var query = Query(actor, snapshot, permission);
        if (resources.Any(resource => !query.Matches(resource) || !ScopePolicy.IsResourceActive(resource, snapshot.Directory))) throw ShipmentDispatchRecords.Invalid();
        var clauses = query.Clauses.Where(clause => resources.Length == 0 || resources.Any(clause.Matches)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(resources.SelectMany(resource => ScopePolicy.RequiredNodes(resource, snapshot.Directory))).Distinct(), [],
            snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        if (actor.Session is { } reference) {
            var live = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(reference, actor.UserId, actor.ProviderIssuer,
                actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).Project(new BsonDocument { { "idleExpiresAt", 1 }, { "absoluteExpiresAt", 1 } }).SingleOrDefaultAsync(ct);
            var now = clock.UtcNow.ToUnixTimeMilliseconds();
            if (live is null || !live.TryGetValue("idleExpiresAt", out var idle) || !idle.IsBsonDateTime ||
                !live.TryGetValue("absoluteExpiresAt", out var absolute) || !absolute.IsBsonDateTime ||
                idle.AsBsonDateTime.MillisecondsSinceEpoch <= now || absolute.AsBsonDateTime.MillisecondsSinceEpoch <= now) throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        query = Query(actor, snapshot, permission); if (resources.Any(resource => !query.Matches(resource))) throw ShipmentDispatchRecords.Invalid();
        ct.ThrowIfCancellationRequested();
    }
}
