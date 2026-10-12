using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Ordering;

namespace UnitToko.Tests.Ordering;

public sealed class OrderReservationHistoryLimitTests
{
    private static BsonDocument Row(long id, long remaining) => new() { { "_id", id }, { "remaining", remaining } };
    private static BsonArray Release(long id, long packages) => [new BsonDocument { { "reservationId", id }, { "allocation", new BsonDocument("packages", packages) } }];

    [Fact]
    public void Replacing_a_full_active_order_retains_retired_rows_without_creating_an_extra_active_reservation() {
        var current = Enumerable.Range(1, 100).Select(id => Row(id, 1)).ToList();
        OrderReservationHistoryLimits.RequireReplacement(current, Release(1, 1), 1);
        current[0]["remaining"] = 0L; current.Add(Row(101, 1));
        OrderReservationHistoryLimits.Require(current);
        Assert.Equal(101, current.Count); Assert.Equal(100, current.Count(row => row["remaining"] == 1L));
        // An A→B→A replacement retires its own immutable reservation, leaving both earlier rows.
        OrderReservationHistoryLimits.RequireReplacement(current, Release(101, 1), 1);
        current[^1]["remaining"] = 0L; current.Add(Row(102, 1)); OrderReservationHistoryLimits.Require(current);
        Assert.Equal(102, current.Count); Assert.Equal(100, current.Count(row => row["remaining"] == 1L));
        current[0]["remaining"] = 1L;
        Assert.Throws<DomainValidationException>(() => OrderReservationHistoryLimits.Require(current));
    }

    [Fact]
    public void Partial_release_does_not_free_an_active_slot_and_retained_overflow_stays_bounded() {
        var current = Enumerable.Range(1, 100).Select(id => Row(id, 2)).ToArray();
        Assert.Throws<DomainValidationException>(() => OrderReservationHistoryLimits.RequireReplacement(current, Release(1, 1), 1));
        Assert.Throws<DomainValidationException>(() => OrderReservationHistoryLimits.RequireReplacement(current, Release(1, 2), 2));
        var full = Enumerable.Range(1, OrderReservationHistoryLimits.MaximumRetained).Select(id => Row(id, id == 1 ? 1 : 0)).ToList();
        OrderReservationHistoryLimits.Require(full);
        Assert.Throws<DomainValidationException>(() => OrderReservationHistoryLimits.RequireReplacement(full, Release(1, 1), 1));
        full.Add(Row(OrderReservationHistoryLimits.MaximumRetained + 1, 0));
        Assert.Throws<DomainValidationException>(() => OrderReservationHistoryLimits.Require(full));
    }
}
