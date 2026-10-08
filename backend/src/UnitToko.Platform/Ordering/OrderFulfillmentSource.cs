using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;

namespace UnitToko.Platform.Ordering;

internal sealed record FulfillmentOrderSource(FulfillmentOrderView View, ResourceScope Resource);

/// <summary>Ordering's operational source boundary for Fulfillment. No consumer reads or writes
/// order collections directly. The caller's transaction and final source/authority fence are required.</summary>
public sealed partial class OrderSubmissionService
{
    internal IMongoDatabase SourceDatabase => database;
    internal Task RequireFulfillmentSourceStorageAsync(CancellationToken ct) => StorageAsync(ct);
    private AuthorizationQuery FulfillmentQuery(ActorContext actor, AuthorizationSnapshot snapshot, string permission) {
        if (permission is not (Permissions.ShipmentPrepare or Permissions.ShipmentDispatch)) throw Invalid();
        var query = ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow);
        AuthorizationStore.Require(query); return query;
    }
    internal async Task<FulfillmentOrderSource> ReadForShipmentPreparationAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        IClientSessionHandle tx, long suborderId, CancellationToken ct) =>
        await ReadForFulfillmentAsync(actor, snapshot, tx, suborderId, Permissions.ShipmentPrepare, ct);
    internal async Task<FulfillmentOrderSource> ReadForFulfillmentAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        IClientSessionHandle tx, long suborderId, string permission, CancellationToken ct) {
        var query = FulfillmentQuery(actor, snapshot, permission);
        var sub = await Rows(B.Suborders).Find(tx, ScopeQuery.Create(query) & F.Eq("_id", suborderId)).SingleOrDefaultAsync(ct) ?? throw Invalid();
        var (order, progress) = await GraphWithProgressAsync(tx, actor, sub["orderId"].AsInt64, ct, requireOwner: false);
        var resource = ReviewResource(order, sub);
        if (!query.Matches(resource) || !ScopePolicy.IsResourceActive(resource, snapshot.Directory)) throw Invalid();
        var reservations = await Rows(B.Reservations).Find(tx, F.Eq("orderId", order["_id"]) & F.Eq("suborderId", suborderId))
            .Sort(new BsonDocument("_id", 1)).Limit(101).ToListAsync(ct);
        var quote = B.Quote(order["quote"].AsBsonDocument);
        var lines = quote.Lines.Where(line => sub["skuIds"].AsBsonArray.Contains(new BsonInt64(line.Definition.SkuId.Value)))
            .Select(line => new FulfillmentOrderLine(line.Definition.SkuId, line.PublicId, line.SkuCode, line.FamilyName, line.Definition,
                line.Packages, checked((int)reservations.Where(row => row["allocation"]["skuId"] == line.Definition.SkuId.Value).Sum(row => row["remaining"].AsInt64))))
            .ToImmutableArray();
        if (lines.IsDefaultOrEmpty || reservations.Count is 0 or > 100 || lines.Any(line => line.RemainingPackages < 0 || line.RemainingPackages > line.OrderedPackages)) throw Invalid();
        var review = FulfillmentReview(order, suborderId, progress.Revision, reservations);
        var canPrepare = !progress.Cancelled && OrderSpendingPolicy.CanAcceptAtStore(progress.SpendingState) &&
            progress.AcceptedSuborders.Contains(new(suborderId)) && !progress.RejectedSuborders.Contains(new(suborderId)) && lines.Any(line => line.RemainingPackages > 0);
        return new(new(new(order["_id"].AsInt64), new(suborderId), progress.Revision, review, order["terms"]["buyer"]["name"].AsString,
            OrderDraftBson.Input(order["input"].AsBsonDocument).Delivery!, lines, canPrepare, OrderDraftBson.Time(order["orderedAt"])), resource);
    }
    private static string FulfillmentReview(BsonDocument order, long suborderId, long decisionRevision, IEnumerable<BsonDocument> reservations) =>
        CatalogRepository.Digest(new BsonDocument {
            { "orderSnapshot", order["snapshotHash"] }, { "suborderId", suborderId }, { "decisionRevision", decisionRevision },
            { "reservations", new BsonArray(reservations.OrderBy(row => row["_id"].AsInt64).Select(row => new BsonDocument {
                { "id", row["_id"] }, { "revision", row["revision"] }, { "remaining", row["remaining"] }, { "released", row["released"] }, { "dispatched", row["dispatched"] } })) }
        });
    internal async Task FenceShipmentPreparationAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx,
        IReadOnlyCollection<FulfillmentOrderSource> sources, CancellationToken ct) =>
        await FenceFulfillmentAsync(actor, snapshot, tx, sources, Permissions.ShipmentPrepare, ct);
    internal async Task FenceFulfillmentAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx,
        IReadOnlyCollection<FulfillmentOrderSource> sources, string permission, CancellationToken ct) {
        var query = FulfillmentQuery(actor, snapshot, permission);
        if (sources.Any(source => !query.Matches(source.Resource) || !ScopePolicy.IsResourceActive(source.Resource, snapshot.Directory))) throw Invalid();
        var clauses = query.Clauses.Where(clause => sources.Count == 0 || sources.Any(source => clause.Matches(source.Resource))).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(sources.SelectMany(source => ScopePolicy.RequiredNodes(source.Resource, snapshot.Directory))).Distinct(), [],
            snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        foreach (var source in sources.DistinctBy(source => source.View.OrderId)) {
            var order = await Rows(B.Orders).Find(tx, F.Eq("_id", source.View.OrderId.Value)).SingleOrDefaultAsync(ct) ?? throw Invalid();
            await TouchAsync(tx, order, ct);
        }
        if (actor.Session is { } reference) {
            var live = await Rows("identitySessions").Find(tx, SessionPersistence.LiveFilter(reference, actor.UserId, actor.ProviderIssuer,
                actor.ProviderSubject, actor.GrantVersion, clock.UtcNow)).Project(new BsonDocument { { "idleExpiresAt", 1 }, { "absoluteExpiresAt", 1 } }).SingleOrDefaultAsync(ct);
            var now = clock.UtcNow.ToUnixTimeMilliseconds();
            if (live is null || !live.TryGetValue("idleExpiresAt", out var idle) || !idle.IsBsonDateTime ||
                !live.TryGetValue("absoluteExpiresAt", out var absolute) || !absolute.IsBsonDateTime ||
                idle.AsBsonDateTime.MillisecondsSinceEpoch <= now || absolute.AsBsonDateTime.MillisecondsSinceEpoch <= now)
                throw new AuthorizationDeniedException("stale_or_inactive_session");
        }
        query = FulfillmentQuery(actor, snapshot, permission);
        if (sources.Any(source => !query.Matches(source.Resource))) throw Invalid();
        ct.ThrowIfCancellationRequested();
    }
    public async Task<FulfillmentOrderView> ReadFulfillmentOrderAsync(ActorContext actor, long suborderId, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var source = await ReadForShipmentPreparationAsync(actor, snapshot, tx, suborderId, token);
            await FenceShipmentPreparationAsync(actor, snapshot, tx, [source], token); return source.View;
        }, ct);
    }
    public async Task<(ImmutableArray<FulfillmentOrderView> Items, bool HasMore)> ListFulfillmentOrdersAsync(ActorContext actor, long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) => {
            var filter = ScopeQuery.Create(FulfillmentQuery(actor, snapshot, Permissions.ShipmentPrepare));
            if (beforeId.HasValue) filter &= F.Lt("_id", beforeId.Value);
            // Scope before joins and pagination. Only accepted, uncancelled store demand enters
            // the queue; the owner verifier checks all decision/reservation provenance afterward.
            var rows = await Rows(B.Suborders).Aggregate(tx).Match(filter)
                .AppendStage<BsonDocument>(new BsonDocument("$lookup", new BsonDocument {
                    { "from", Decisions }, { "localField", "_id" }, { "foreignField", "suborderId" }, { "as", "_store" } }))
                .Match(new BsonDocument("_store", new BsonDocument("$elemMatch", new BsonDocument { { "stage", "Store" }, { "action", "Approve" } })))
                .AppendStage<BsonDocument>(new BsonDocument("$lookup", new BsonDocument {
                    { "from", Decisions }, { "let", new BsonDocument("order", "$orderId") }, { "as", "_stop" },
                    { "pipeline", new BsonArray { new BsonDocument("$match", new BsonDocument {
                        { "$expr", new BsonDocument("$eq", new BsonArray { "$orderId", "$$order" }) },
                        { "$or", new BsonArray { new BsonDocument("stage", "Cancellation"), new BsonDocument { { "stage", "Spending" }, { "action", "Reject" } } } }
                    }), new BsonDocument("$limit", 1) } }
                }))
                .Match(new BsonDocument("_stop.0", new BsonDocument("$exists", false)))
                .AppendStage<BsonDocument>(new BsonDocument("$lookup", new BsonDocument {
                    { "from", B.Reservations }, { "localField", "_id" }, { "foreignField", "suborderId" }, { "as", "_reserved" } }))
                .Match(new BsonDocument("_reserved", new BsonDocument("$elemMatch", new BsonDocument("remaining", new BsonDocument("$gt", 0L)))))
                .Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var sources = new List<FulfillmentOrderSource>();
            foreach (var row in rows) sources.Add(await ReadForShipmentPreparationAsync(actor, snapshot, tx, row["_id"].AsInt64, token));
            await FenceShipmentPreparationAsync(actor, snapshot, tx, sources, token);
            return (sources.Take(25).Select(source => source.View).ToImmutableArray(), rows.Count > 25);
        }, ct);
    }
}
