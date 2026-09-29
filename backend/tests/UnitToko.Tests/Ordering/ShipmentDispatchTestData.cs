using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

// Isolated synthetic operational approval, never registered in production.
internal sealed class ShipmentDispatchTestData(OrderSubmissionTestData order) : IAsyncDisposable
{
    internal OrderSubmissionTestData Order { get; } = order;
    internal ActorContext Picker { get; private set; } = null!;
    internal ActorContext Dispatcher { get; private set; } = null!;
    internal ActorContext Courier { get; private set; } = null!;
    internal FulfillmentOrderView Source { get; private set; } = null!;
    internal FixtureAdmission Admission { get; private set; } = null!;
    internal ShipmentPreparationService Preparations => ShipmentPreparationTests.Service(Order);
    internal ShipmentDispatchService Service => new(Order.Database, new(Order.Prices.Catalog.Client), new(Order.Database),
        Order.Prices.Catalog.Clock, Order.Service, Preparations, Admission);
    internal ImmutableArray<ShipmentLotSelection> Lots { get; private set; }
    internal ShipmentDispatchPreparation Prepared { get; private set; } = null!;
    internal static async Task<ShipmentDispatchTestData> CreateAsync(MongoFixture fixture, int packages = 1) {
        var result = new ShipmentDispatchTestData(await OrderSubmissionTestData.CreateAsync(fixture));
        try {
            var data = result.Order; var catalog = data.Prices.Catalog; var ready = await ShipmentPreparationTests.Ready(data);
            result.Picker = ready.Picker; result.Source = ready.Source;
            result.Dispatcher = await catalog.ActorAsync(8900, CatalogAuthoringTestData.Shop, Permissions.ShipmentDispatch, Permissions.ShipmentAssign);
            result.Courier = await catalog.ActorAsync(9100, new(GrantScopeKind.CourierAssignment, 100, ShopId: 101, ChannelId: 103), Permissions.ShipmentRead, Permissions.DeliveryAttempt);
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", result.Courier.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Kurir fixture" }, { "verifiedEmail", "courier@example.invalid" }, { "verifiedAt", catalog.Clock.UtcNow.UtcDateTime }, { "updatedAt", catalog.Clock.UtcNow.UtcDateTime } });
            await data.Rows("fixtureDispatchApprovals").InsertOneAsync(new BsonDocument { { "_id", 9400L }, { "revision", 1L }, { "fence", 0L }, { "approved", true },
                { "expiresAt", catalog.Clock.UtcNow.AddMinutes(30).UtcDateTime }, { "packageCost", new BsonDecimal128(7000.25m) } });
            result.Admission = new(result); await result.PrepareAsync(packages, 0, "dispatch-prepare");
            await data.Prices.Catalog.Base.AddGrantAsync(data.Buyer, 6500, CatalogAuthoringTestData.Audience, [Permissions.OrderCancel]);
            return result;
        } catch { await result.DisposeAsync(); throw; }
    }
    internal async Task PrepareAsync(int packages, long revision, string key) {
        Source = await Order.Service.ReadFulfillmentOrderAsync(Picker, Source.SuborderId.Value);
        await Preparations.SaveAsync(Picker, Source.SuborderId.Value, revision, Source.Review, [new(Source.Lines[0].SkuId, packages)], CatalogPriceTestData.Meta(key));
        Prepared = await Service.ReadPreparationAsync(Dispatcher, Source.SuborderId.Value);
        Lots = [new(Assert.Single(Prepared.Lots).Reservation.ReservationId, packages)];
    }
    internal Task<ShipmentDispatchReview> PreviewAsync() => Service.PreviewAsync(Dispatcher, Source.SuborderId.Value, Prepared.PreparationId.Value, Prepared.PreparationRevision, Courier.UserId, Lots);
    internal Task<BsonDocument> DispatchAsync(ShipmentDispatchReview review, string key) => Service.DispatchAsync(Dispatcher, Source.SuborderId.Value,
        review.Preparation.PreparationId.Value, review.Preparation.PreparationRevision, review.CourierId.Value, review.Selected, review.Review, CatalogPriceTestData.Meta(key));
    public ValueTask DisposeAsync() => Order.DisposeAsync();

    internal sealed class FixtureAdmission(ShipmentDispatchTestData data) : IShipmentDispatchAdmissionReader
    {
        public IMongoDatabase Database => data.Order.Database;
        internal Func<IClientSessionHandle, CancellationToken, Task>? BeforeComplete { get; set; }
        private async Task<BsonDocument> RowAsync(IClientSessionHandle tx, CancellationToken ct) {
            var row = await data.Order.Rows("fixtureDispatchApprovals").Find(tx, new BsonDocument("_id", 9400L)).SingleOrDefaultAsync(ct);
            if (row is null || !row["approved"].AsBoolean || row["expiresAt"].AsBsonDateTime.ToUniversalTime() <= data.Order.Prices.Catalog.Clock.UtcNow.UtcDateTime)
                throw ShipmentDispatchAdmissionPolicy.Invalid();
            return row;
        }
        private async Task FenceAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
            if ((await data.Order.Rows("fixtureDispatchApprovals").UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1)
                throw ShipmentDispatchAdmissionPolicy.Invalid();
        }
        public async Task<ShipmentDispatchAdmission> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, OrderDispatchSource source,
            ImmutableArray<ShipmentLotSelection> selected, CancellationToken ct) {
            var row = await RowAsync(tx, ct); await FenceAsync(tx, row, ct);
            return new(new(new(9401, 1), "Penjual fixture", "Alamat penjual sintetis"), new(9400, row["revision"].AsInt64),
                selected.Select(selection => new ShipmentLotAuthority(selection.ReservationId.Value,
                    source.Reservations.Single(item => item.Fact.ReservationId == selection.ReservationId).Allocation.SourceAuthority,
                    new(9402, row["revision"].AsInt64), new Money(Decimal128.ToDecimal(row["packageCost"].AsDecimal128)))).ToImmutableArray(),
                new DateTimeOffset(row["expiresAt"].AsBsonDateTime.ToUniversalTime()));
        }
        public async Task CompleteAsync(IClientSessionHandle tx, ActorContext actor, ShipmentDispatchAdmission admission, CancellationToken ct) {
            if (BeforeComplete is not null) await BeforeComplete(tx, ct);
            var row = await RowAsync(tx, ct);
            if (row["revision"].AsInt64 != admission.OperatingPolicy.Revision || admission.ValidUntil.UtcDateTime != row["expiresAt"].AsBsonDateTime.ToUniversalTime() ||
                admission.Lots.Any(lot => lot.PackageCost.Value != Decimal128.ToDecimal(row["packageCost"].AsDecimal128))) throw ShipmentDispatchAdmissionPolicy.Invalid();
            await FenceAsync(tx, row, ct);
        }
    }
}
