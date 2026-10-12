using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;

namespace UnitToko.Platform.Ordering;

internal sealed record OrderDispatchReservation(BsonDocument Reservation, OrderStockAllocation Allocation, DateOnly? ExpiresOn) {
    internal ShipmentReservationFact Fact => new(new(Reservation["_id"].AsInt64), new(Allocation.SkuId), Allocation.Change.Key.DefinitionId,
        Allocation.Change.Key.LotId, checked((int)Reservation["remaining"].AsInt64), Allocation.LineRevision);
}
internal sealed record OrderDispatchSource(FulfillmentOrderSource Order, ImmutableArray<OrderDispatchReservation> Reservations);

public sealed partial class OrderSubmissionService
{
    /// <summary>Loads actual reserved lots and current stock revisions under the dispatch permission.
    /// Fulfillment must still obtain current source/cost/courier admission and explicitly confirm lots.</summary>
    internal async Task<OrderDispatchSource> ReadDispatchReservationsAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        IClientSessionHandle tx, long suborderId, CancellationToken ct) {
        var source = await ReadForFulfillmentAsync(actor, snapshot, tx, suborderId, Permissions.ShipmentDispatch, ct);
        var rows = await Rows(B.Reservations).Find(tx, F.Eq("orderId", source.View.OrderId.Value) & F.Eq("suborderId", suborderId) & F.Gt("remaining", 0L))
            .Sort(new BsonDocument("_id", 1)).Limit(101).ToListAsync(ct);
        if (rows.Count is < 1 or > 100) throw Invalid();
        var result = ImmutableArray.CreateBuilder<OrderDispatchReservation>();
        foreach (var row in rows) {
            if (row["remaining"].AsInt64 == 0) continue;
            var original = B.Allocation(row["allocation"].AsBsonDocument);
            var balance = await new InventoryRepository(database).ReadAsync(tx, original.Change.Id, ct);
            var pool = await Rows("inventoryPools").Find(tx, F.Eq("_id", original.Pool.Id)).SingleOrDefaultAsync(ct) ?? throw Invalid();
            InventoryGraph.RequirePool(pool);
            var lot = InventoryGraph.ReadLot(await Rows("lots").Find(tx, F.Eq("_id", original.Change.Key.LotId.Value)).SingleOrDefaultAsync(ct));
            if (balance.PhysicalPoolId != original.Pool.Id || balance.Balance.Key != original.Change.Key ||
                InventoryGraph.Location(pool["location"].AsBsonDocument) != original.Pool.Location || !pool["active"].AsBoolean ||
                balance.Balance.Reserved < row["remaining"].AsInt64) throw Invalid();
            result.Add(new(row.DeepClone().AsBsonDocument, original with { Pool = original.Pool with { Revision = pool["revision"].AsInt64 },
                Change = original.Change with { ExpectedRevision = balance.Balance.Revision } }, lot.ExpiresOn));
        }
        return new(source, result.ToImmutable());
    }
    internal static BsonArray DispatchAllocationFacts(OrderDispatchSource source, ImmutableArray<ShipmentPackageSelection> prepared,
        ImmutableArray<ShipmentLotSelection> selected) {
        selected = ShipmentDispatchPolicy.Require(source.Order.View, source.Order.View.Review, prepared,
            [.. source.Reservations.Select(row => row.Fact)], selected);
        return new(selected.Select(line => {
            var loaded = source.Reservations.Single(row => row.Fact.ReservationId == line.ReservationId); var reservation = loaded.Reservation;
            return new BsonDocument { { "reservationId", line.ReservationId.Value }, { "allocation", B.Allocation(loaded.Allocation with {
                Change = loaded.Allocation.Change with { Packages = line.Packages } }) },
                { "before", new BsonDocument(new[] { "revision", "remaining", "released", "dispatched" }.Select(field => new BsonElement(field, reservation[field]))) } };
        }));
    }
}

internal static class OrderDispatchFacts
{
    internal static void Require(BsonDocument reservation, BsonDocument dispatch, bool applied) {
        var original = B.Allocation(reservation["allocation"].AsBsonDocument); var allocation = B.Allocation(dispatch["allocation"].AsBsonDocument);
        var before = dispatch["before"].AsBsonDocument; var packages = allocation.Change.Packages;
        if (dispatch["reservationId"] != reservation["_id"] || allocation.SourceAuthority != original.SourceAuthority || allocation.Pool.Id != original.Pool.Id ||
            allocation.Pool.Location != original.Pool.Location || allocation.SkuId != original.SkuId || allocation.LineRevision != original.LineRevision || allocation.Change.Id != original.Change.Id ||
            allocation.Change.PhysicalPoolId != original.Change.PhysicalPoolId || allocation.Change.Key != original.Change.Key ||
            packages <= 0 || packages > before["remaining"].AsInt64 || before["remaining"].AsInt64 + before["released"].AsInt64 + before["dispatched"].AsInt64 != original.Change.Packages ||
            reservation["revision"].AsInt64 != before["revision"].AsInt64 + (applied ? 1 : 0) || reservation["released"] != before["released"] ||
            reservation["remaining"].AsInt64 != before["remaining"].AsInt64 - (applied ? packages : 0) ||
            reservation["dispatched"].AsInt64 != before["dispatched"].AsInt64 + (applied ? packages : 0))
            throw new AuthorizationDeniedException("order_dispatch_unavailable");
    }
}
