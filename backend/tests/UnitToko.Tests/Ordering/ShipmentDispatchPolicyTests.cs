using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Ordering;

namespace UnitToko.Tests.Ordering;

public sealed class ShipmentDispatchPolicyTests
{
    private static FulfillmentOrderView Source() => new(new(1), new(2), 3, new('a', 64), "Cabang fixture",
        new("Penerima", "081234567890", "Alamat fixture", "Jakarta", "12810", null),
        [new(new(10), Guid.Parse("12345678-1234-4123-8123-123456789abc"), "PEN-12", "Pena", new(new(11), new(10), 1,
            "Dus", "sealed", "piece", 12, ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal), [], 2, 2, DateTimeOffset.UnixEpoch), 2, 2)],
        true, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Actual_lots_must_exactly_match_each_prepared_package_quantity_without_guessing() {
        var source = Source();
        var reservations = ImmutableArray.Create(new ShipmentReservationFact(new(21), new(10), new(11), new(31), 1),
            new ShipmentReservationFact(new(22), new(10), new(11), new(32), 1));
        var selected = ImmutableArray.Create(new ShipmentLotSelection(new(22), 1));
        var result = ShipmentDispatchPolicy.Require(source, source.Review, [new(new(10), 1)], reservations, selected);
        Assert.Equal(new LongId(22), Assert.Single(result).ReservationId);
        Assert.Throws<DomainValidationException>(() => ShipmentDispatchPolicy.Require(source, source.Review, [new(new(10), 2)], reservations, selected));
        Assert.Throws<DomainValidationException>(() => ShipmentDispatchPolicy.Require(source, source.Review, [new(new(10), 1)], reservations, [new(new(22), 2)]));
        Assert.Throws<DomainValidationException>(() => ShipmentDispatchPolicy.Require(source, source.Review, [new(new(10), 1)], reservations, [new(new(99), 1)]));
        Assert.Throws<DomainValidationException>(() => ShipmentDispatchPolicy.Require(source, source.Review, [new(new(10), 2)], reservations, [new(new(22), 1), new(new(22), 1)]));
        Assert.Throws<DomainValidationException>(() => ShipmentDispatchPolicy.Require(source with { CanPrepare = false }, source.Review, [new(new(10), 1)], reservations, selected));
    }

    [Fact]
    public void Inconsistent_reservation_provenance_or_remaining_demand_never_becomes_a_pickable_lot() {
        var source = Source(); var picked = ImmutableArray.Create(new ShipmentLotSelection(new(21), 1));
        foreach (var invalid in new[] {
            ImmutableArray.Create(new ShipmentReservationFact(new(21), new(10), new(12), new(31), 2)),
            ImmutableArray.Create(new ShipmentReservationFact(new(21), new(19), new(11), new(31), 2)),
            ImmutableArray.Create(new ShipmentReservationFact(new(21), new(10), new(11), new(31), 3)),
            ImmutableArray.Create(new ShipmentReservationFact(new(21), new(10), new(11), new(31), 1)),
            ImmutableArray.Create(new ShipmentReservationFact(new(21), new(10), new(11), new(31), 1), new ShipmentReservationFact(new(21), new(10), new(11), new(32), 1)) })
            Assert.Throws<DomainValidationException>(() => ShipmentDispatchPolicy.Require(source, source.Review, [new(new(10), 1)], invalid, picked));
        Assert.Throws<DomainValidationException>(() => ShipmentDispatchPolicy.Copy([new(new(21), 0)]));
        Assert.Throws<DomainValidationException>(() => ShipmentDispatchPolicy.Copy([]));
    }

    [Fact]
    public void Dispatch_admission_preserves_the_exact_reserved_source_authority_revision() {
        var reference = new OrderPolicyReference(300, 7);
        var location = new StockLocation(new(100), new(101), new(102), new(103)).Validate();
        var allocation = new OrderStockAllocation(10, reference, new(200, location, 3),
            new(201, 200, new(location, new(10), new(11), new(31)), 4, InventoryBalanceAction.Reserve, 2));
        OrderAdmissionPolicy.Reference(reference);
        var reservation = new BsonDocument { { "_id", 21L }, { "revision", 1L }, { "remaining", 2L },
            { "released", 0L }, { "dispatched", 0L }, { "allocation", OrderSubmissionBson.Allocation(allocation) } };
        var source = new OrderDispatchSource(new(Source(), new ResourceScope(FulfillmentOrganizationId: 100,
            ShopId: 101, WarehouseId: 102, ChannelId: 103, AssignedUserIds: [])),
            [new(reservation, allocation, new DateOnly(2030, 1, 1))]);
        var selected = ShipmentDispatchPolicy.Copy([new(new(21), 1)]);
        var authority = new ShipmentLotAuthority(21, new(reference.Id, reference.Revision), new(301, 2), new Money(12500));
        var admission = new ShipmentDispatchAdmission(new(new(302, 1), "Toko fixture", "Alamat toko fixture"),
            new(303, 1), [authority], DateTimeOffset.UnixEpoch.AddDays(1));

        ShipmentDispatchAdmissionPolicy.Require(source, selected, admission);
        foreach (var revision in new[] { reference.Revision - 1, reference.Revision + 1 }) {
            var rebased = admission with { Lots = [authority with { SourceAuthority = new(reference.Id, revision) }] };
            var error = Assert.Throws<DomainValidationException>(() => ShipmentDispatchAdmissionPolicy.Require(source, selected, rebased));
            Assert.Equal("shipment_dispatch_admission_unavailable", error.Code);
        }
        Assert.Equal(reference, Assert.Single(source.Reservations).Allocation.SourceAuthority);
    }
}
