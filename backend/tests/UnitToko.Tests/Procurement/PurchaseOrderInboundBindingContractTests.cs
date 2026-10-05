using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Procurement;

namespace UnitToko.Tests.Procurement;

public sealed class PurchaseOrderInboundBindingContractTests
{
    private static PurchaseOrderInput Input => new(new(100, 101, 102, 103), 1, 1, 2, 3, 4, 1, new(2026, 10, 6), "IDR",
        new("quote", "SYNTHETIC-QUOTE", new(2026, 10, 6), new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero)), null,
        new("credit", "Synthetic terms", new Money(0), "due_date", new(2026, 11, 5)),
        [new(55, 4, "supplier", "SYNTHETIC-SUPPLIER", "SYNTHETIC-ITEM", 56, 1, new Money(100), new Money(0), new Money(0))]);
    [Fact]
    public void Split_planned_allocations_are_canonical_bounded_and_preserve_exact_old_body_when_absent() {
        var selection = new PurchaseOrderInboundPoolSelection([new(55, 3, 20, 1), new(55, 1, 10, 1)]);
        var copy = PurchaseOrderInboundRules.Copy(Input, selection);
        Assert.Equal(new long[] { 10, 20 }, copy.Allocations.Select(row => row.PoolId));
        Assert.NotSame(selection, copy); Assert.NotSame(selection.Allocations[0], copy.Allocations[1]);
        Assert.Equal(AuthorizationStore.CommandHash(PurchaseOrderBson.ProposeBody(Input, new string('a', 64)), "Synthetic reason"),
            AuthorizationStore.CommandHash(PurchaseOrderInboundBindingBson.Body(Input, new string('a', 64), null), "Synthetic reason"));
        Assert.NotEqual(AuthorizationStore.CommandHash(PurchaseOrderBson.ProposeBody(Input, new string('a', 64)), "Synthetic reason"),
            AuthorizationStore.CommandHash(PurchaseOrderInboundBindingBson.Body(Input, new string('a', 64), copy), "Synthetic reason"));
        Assert.Throws<DomainValidationException>(() => PurchaseOrderInboundRules.Copy(Input, new([new(55, 5, 10, 1)])));
        Assert.Throws<DomainValidationException>(() => PurchaseOrderInboundRules.Copy(Input, new([new(55, 3, 10, 1)])));
        Assert.Throws<DomainValidationException>(() => PurchaseOrderInboundRules.Copy(Input, new([new(55, 2, 10, 1), new(55, 2, 10, 1)])));
        Assert.Throws<DomainValidationException>(() => PurchaseOrderInboundRules.Copy(Input, new(ImmutableArray<PurchaseOrderInboundAllocation>.Empty)));
        Assert.Throws<DomainValidationException>(() => PurchaseOrderInboundRules.Copy(Input, new([new(55, 4, long.MaxValue, 1)])));
    }
}
