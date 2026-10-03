using System.Collections.Immutable;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Tests.Inventory;

public sealed class InventoryQuantityPolicyTests
{
    internal static readonly StockLocation Location = new(new(100), new(101), new(102), new(103));
    private static StockBalance Balance(long onHand = 20, long reserved = 4, long held = 3, long expired = 2) =>
        new(new(Location, new(200), new(201), new(202)), onHand, reserved, held, expired, 7);

    [Fact]
    public void Physical_packages_and_disjoint_unavailable_buckets_produce_exact_available_packages()
    {
        Assert.Equal(11, StockBalancePolicy.AvailablePackages(Balance()));
        Assert.Equal(0, StockBalancePolicy.AvailablePackages(Balance(9)));
        Assert.Equal(long.MaxValue, StockBalancePolicy.AvailablePackages(Balance(long.MaxValue, 0, 0, 0)));
        Assert.Equal(StockCountChoiceScope(), Location.ToScope());
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)] [InlineData(5, -1, 0, 0)] [InlineData(5, 0, -1, 0)]
    [InlineData(5, 0, 0, -1)] [InlineData(5, 1, 3, 2)] [InlineData(5, 0, 4, 2)]
    [InlineData(5, 0, 6, 0)] [InlineData(long.MaxValue, 0, long.MaxValue, 1)]
    public void Malformed_or_overlapping_buckets_cannot_be_treated_as_sellable(long onHand, long reserved, long held, long expired) =>
        Reject(() => StockBalancePolicy.AvailablePackages(Balance(onHand, reserved, held, expired)), "invalid_stock_balance");

    [Fact]
    public void Default_identifiers_missing_keys_and_nonpositive_versions_deny()
    {
        var value = Balance();
        foreach (var invalid in new[] { value with { Key = null! }, value with { Revision = 0 },
            value with { Key = value.Key with { Location = null! } },
            value with { Key = value.Key with { SkuId = default } },
            value with { Key = value.Key with { DefinitionId = default } },
            value with { Key = value.Key with { LotId = default } },
            value with { Key = value.Key with { Location = Location with { ChannelId = default } } } })
            Assert.Throws<DomainValidationException>(() => StockBalancePolicy.AvailablePackages(invalid));
        Assert.Throws<DomainValidationException>(() => StockBalancePolicy.AvailablePackages(null!));
    }

    [Fact]
    public void Reservation_and_release_change_only_reserved_packages_and_revision()
    {
        var original = Balance();
        var reserved = StockBalancePolicy.Reserve(original, 7, 11);
        Assert.Equal(original with { Reserved = 15, Revision = 8 }, reserved);
        Assert.Equal(0, StockBalancePolicy.AvailablePackages(reserved));
        var released = StockBalancePolicy.Release(reserved, 8, 11);
        Assert.Equal(original with { Revision = 9 }, released);
        Assert.Equal(4, original.Reserved);
    }

    [Fact]
    public void Dispatch_consumes_only_previously_reserved_physical_packages()
    {
        var value = StockBalancePolicy.Dispatch(Balance(), 7, 4);
        Assert.Equal(Balance() with { OnHand = 16, Reserved = 0, Revision = 8 }, value);
        Assert.Equal(11, StockBalancePolicy.AvailablePackages(value));
        Reject(() => StockBalancePolicy.Dispatch(Balance(), 7, 5), "insufficient_reserved_stock");
        Reject(() => StockBalancePolicy.Release(Balance(), 7, 5), "insufficient_reserved_stock");
        Reject(() => StockBalancePolicy.Reserve(Balance(), 7, 12), "insufficient_available_stock");
    }

    [Fact]
    public void Receive_adds_physical_packages_without_reclassifying_existing_stock()
    {
        Assert.Equal(Balance() with { OnHand = 25, Revision = 8 }, StockBalancePolicy.Receive(Balance(), 7, 5));
        Reject(() => StockBalancePolicy.Receive(Balance(long.MaxValue, 0, 0, 0), 7, 1), "stock_quantity_overflow");
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(long.MinValue)]
    public void Every_quantity_transition_requires_a_positive_exact_integer(long quantity)
    {
        foreach (var change in Changes())
            Reject(() => change(Balance(), 7, quantity), "invalid_quantity");
    }

    [Fact]
    public void Every_transition_fences_the_expected_revision_and_cannot_wrap_the_next_revision()
    {
        foreach (var change in Changes())
        {
            Reject(() => change(Balance(), 6, 1), "stock_revision_conflict");
            Reject(() => change(Balance(), 0, 1), "stock_revision_conflict");
            Reject(() => change(Balance() with { Revision = long.MaxValue }, long.MaxValue, 1), "stock_revision_exhausted");
        }
    }

    [Fact]
    public void Quarantine_and_expiry_partition_stock_without_double_subtracting_or_destroying_it()
    {
        var original = Balance();
        var held = StockBalancePolicy.Hold(original, 7, 5);
        Assert.Equal(original with { Held = 8, Revision = 8 }, held);
        var expiredHeld = StockBalancePolicy.ExpireHeld(held, 8, 5);
        Assert.Equal(original with { Expired = 7, Revision = 9 }, expiredHeld);
        Assert.Equal(6, StockBalancePolicy.AvailablePackages(expiredHeld));
        Assert.Equal(20, expiredHeld.OnHand);
        Assert.Equal(original with { Revision = 9 }, StockBalancePolicy.ReleaseHold(held, 8, 5));
        Assert.Equal(original with { Expired = 7, Revision = 8 }, StockBalancePolicy.Expire(original, 7, 5));
        Reject(() => StockBalancePolicy.Hold(original, 7, 12), "insufficient_available_stock");
        Reject(() => StockBalancePolicy.Expire(original, 7, 12), "insufficient_available_stock");
        Reject(() => StockBalancePolicy.ExpireHeld(original, 7, 4), "insufficient_held_stock");
        Reject(() => StockBalancePolicy.ReleaseHold(original, 7, 4), "insufficient_held_stock");
    }

    [Theory]
    [InlineData(12, 1, 1, 12, 1, 1L, 12L, 12L)]
    [InlineData(24, 1, 1, 24, 2, 2L, 48L, 48L)]
    [InlineData(12, 8, 2, 3, 5, 10L, 15L, 120L)]
    public void Explicit_conversion_conserves_product_specific_contents(int sourceContents, int destinationContents,
        int sourceStep, int destinationStep, int steps, long sourcePackages, long destinationPackages, long baseUnits)
    {
        Assert.Equal(new ConversionQuantities(sourcePackages, destinationPackages, baseUnits),
            Quantities(Conversion(sourceStep, destinationStep), Definition(201, 200, sourceContents),
                Definition(301, 300, destinationContents), steps));
    }

    [Fact]
    public void Equal_labels_cannot_supply_a_missing_ratio_or_silently_reverse_a_conversion()
    {
        var source = Definition(201, 200, 12);
        var destination = Definition(301, 300, 1);
        Reject(() => Quantities(Conversion(1, 10), source, destination, 1), "stock_conversion_not_conserved");
        Reject(() => Quantities(Conversion(), destination, source, 1), "stock_conversion_definition_mismatch");
        Assert.Equal(new ConversionQuantities(12, 1, 12),
            Quantities(new(new(501), 1, new(300), new(301), new(200), new(201), 12, 1), destination, source, 1));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(1000001)]
    public void Conversion_steps_are_explicit_bounded_whole_numbers(int steps) =>
        Reject(() => Quantities(Conversion(), Definition(201, 200, 12), Definition(301, 300, 1), steps), "invalid_quantity");

    [Fact]
    public void Conversion_validates_definition_identity_intrinsic_attributes_units_and_overflow()
    {
        var source = Definition(201, 200, 12);
        var destination = Definition(301, 300, 1);
        foreach (var rule in new[] { Conversion() with { Id = default }, Conversion() with { Version = 0 },
            Conversion() with { SourcePackagesPerStep = 0 }, Conversion() with { DestinationPackagesPerStep = -1 } })
            Reject(() => Quantities(rule, source, destination, 1), "invalid_stock_conversion");
        Reject(() => Quantities(Conversion() with { SourceDefinitionId = new(999) }, source, destination, 1), "stock_conversion_definition_mismatch");
        Reject(() => Quantities(Conversion(), source, destination with { BaseUnitCode = "bottle" }, 1), "stock_conversion_incompatible");
        Reject(() => Quantities(Conversion(), source, destination with { Attributes = destination.Attributes.Add("color", "red") }, 1), "stock_conversion_incompatible");
        Assert.Throws<DomainValidationException>(() => Quantities(Conversion(), source with { BaseUnitsPerPackage = 0 }, destination, 1));
        Reject(() => Quantities(Conversion(int.MaxValue, int.MaxValue),
            Definition(201, 200, int.MaxValue), Definition(301, 300, int.MaxValue), 1000000), "stock_quantity_overflow");
    }

    [Fact]
    public void Conversion_checks_physical_identity_and_organization_before_arithmetic()
    {
        var source = Package(Definition(201, 200, 12));
        var destination = Package(Definition(301, 300, 1));
        foreach (var invalid in new[] { destination with { Sku = destination.Sku with { PhysicalItemKey = "different-item" } },
            destination with { Sku = destination.Sku with { OrganizationId = new(999) } } })
            Reject(() => PackConversionPolicy.Quantities(Conversion(), source, invalid, 1), "stock_conversion_incompatible");
        Assert.Throws<DomainValidationException>(() => PackConversionPolicy.Quantities(Conversion(),
            source with { Sku = source.Sku with { Id = new(999) } }, destination, 1));
    }

    [Fact]
    public void Historical_descriptive_definitions_remain_convertible_without_reinterpreting_the_lot()
    {
        var original = Definition(201, 200, 12);
        var revised = original with { Id = new(211), Version = 2, PackageLabel = "Karton dua belas",
            MinimumOrderPackages = 5, OrderIncrementPackages = 5, EffectiveAt = original.EffectiveAt.AddDays(1) };
        var source = Package(original) with { CurrentDefinition = revised, Sku = Package(original).Sku with { CurrentDefinitionId = revised.Id, Revision = 2 } };
        Assert.Equal(new ConversionQuantities(1, 12, 12), PackConversionPolicy.Quantities(Conversion(), source, Package(Definition(301, 300, 1)), 1));
        Assert.Equal(new LongId(201), source.StoredDefinition.Id);
        foreach (var changed in new[] { revised with { BaseUnitsPerPackage = 10 }, revised with { BaseUnitCode = "bottle" },
            revised with { PackageKind = "other" }, revised with { Attributes = revised.Attributes.Add("color", "red") } })
            Reject(() => PackConversionPolicy.Quantities(Conversion(), source with { CurrentDefinition = changed },
                Package(Definition(301, 300, 1)), 1), "stock_definition_material_mismatch");
    }

    [Fact]
    public void Forged_historical_identity_version_or_reused_current_id_cannot_supply_package_facts()
    {
        var source = Package(Definition(201, 200, 12));
        foreach (var stored in new[] { source.StoredDefinition with { SkuId = new(999) },
            source.StoredDefinition with { Version = 2 }, source.StoredDefinition with { PackageLabel = "Changed under same ID" },
            source.StoredDefinition with { Id = new(211) }, source.StoredDefinition with { EffectiveAt = source.StoredDefinition.EffectiveAt.AddDays(1) } })
            Reject(() => PackConversionPolicy.Quantities(Conversion(), source with { StoredDefinition = stored },
                Package(Definition(301, 300, 1)), 1), "stock_definition_identity_mismatch");
    }

    private static ConversionQuantities Quantities(PackConversionDefinition rule, VariantDefinitionVersion source,
        VariantDefinitionVersion destination, int steps) => PackConversionPolicy.Quantities(rule, Package(source), Package(destination), steps);
    private static StockPackageDefinition Package(VariantDefinitionVersion definition) => new(
        new(definition.SkuId, new(100), new(150),
            Guid.Parse(definition.SkuId.Value == 200 ? "12345678-1234-4123-8123-123456789abc" : "12345678-1234-4123-8123-123456789abd"),
            "SKU-" + definition.SkuId, "shared-physical-item", true, definition.Id, 1), definition, definition);
    [Fact]
    public void A_bounded_conversion_request_can_produce_more_than_one_million_destination_packages()
    {
        var quantities = Quantities(Conversion(1, 24), Definition(201, 200, 24), Definition(301, 300, 1), 1000000);
        Assert.Equal(24000000L, quantities.DestinationPackages);
        // The ledger accepts derived counts; the user-command limit applies to steps, not derived loose pieces.
        var empty = Balance(0, 0, 0, 0);
        var received = StockBalancePolicy.Receive(empty, 7, quantities.DestinationPackages);
        Assert.Equal(24000000L, StockBalancePolicy.AvailablePackages(received));
    }
    [Fact]
    public void Derived_Int64_counts_remain_exact_through_reservation_and_dispatch()
    {
        var empty = Balance(0, 0, 0, 0);
        var received = StockBalancePolicy.Receive(empty, 7, long.MaxValue);
        var reserved = StockBalancePolicy.Reserve(received, 8, long.MaxValue);
        Assert.Equal(long.MaxValue, reserved.Reserved);
        var dispatched = StockBalancePolicy.Dispatch(reserved, 9, long.MaxValue);
        Assert.Equal(empty with { Revision = 10 }, dispatched);
    }
    private static IEnumerable<Func<StockBalance, long, long, StockBalance>> Changes() =>
        [StockBalancePolicy.Reserve, StockBalancePolicy.Release, StockBalancePolicy.Dispatch, StockBalancePolicy.Receive, StockBalancePolicy.ReceiveHeld,
            StockBalancePolicy.Hold, StockBalancePolicy.ReleaseHold, StockBalancePolicy.Expire, StockBalancePolicy.ExpireHeld];
    private static PackConversionDefinition Conversion(int sourceStep = 1, int destinationStep = 12) =>
        new(new(500), 1, new(200), new(201), new(300), new(301), sourceStep, destinationStep);
    private static VariantDefinitionVersion Definition(long id, long skuId, int contents) =>
        new(new(id), new(skuId), 1, "Dus", contents == 1 ? "loose" : "sealed", "piece", contents,
            ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal), [], 1, 1,
            new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero));
    private static UnitToko.Modules.Organizations.GrantScope StockCountChoiceScope() =>
        new(UnitToko.Modules.Organizations.GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
    private static void Reject(Action action, string code) => Assert.Equal(code, Assert.Throws<DomainValidationException>(action).Code);
}
