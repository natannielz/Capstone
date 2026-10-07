using System.Collections.Immutable;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Tests.Inventory;

public sealed class PackConversionStockPolicyTests
{
    private static readonly StockLocation Location = InventoryQuantityPolicyTests.Location;
    private static StockBalance Balance(long skuId = 200, long definitionId = 201, long lotId = 202,
        long onHand = 20, long reserved = 4, long held = 3, long expired = 2, long revision = 7) =>
        new(new(Location, new(skuId), new(definitionId), new(lotId)), onHand, reserved, held, expired, revision);

    [Fact]
    public void Conversion_consumes_only_available_source_and_preserves_both_balances_unavailable_buckets()
    {
        var source = Balance();
        var destination = Balance(300, 301, 302, 10, 2, 1, 3);
        var taken = StockBalancePolicy.ConvertOut(source, 7, 11);
        var received = StockBalancePolicy.ConvertIn(destination, 7, 12);

        Assert.Equal(source with { OnHand = 9, Revision = 8 }, taken);
        Assert.Equal(0, StockBalancePolicy.AvailablePackages(taken));
        Assert.Equal(destination with { OnHand = 22, Revision = 8 }, received);
        Assert.Equal(16, StockBalancePolicy.AvailablePackages(received));
        Assert.Equal("insufficient_available_stock", Assert.Throws<DomainValidationException>(() =>
            StockBalancePolicy.ConvertOut(source, 7, 12)).Code);
        Assert.Equal("insufficient_available_stock", Assert.Throws<DomainValidationException>(() =>
            StockBalancePolicy.ConvertOut(taken, 8, 1)).Code);
        Assert.Equal(Balance(), source);
        Assert.Equal(Balance(300, 301, 302, 10, 2, 1, 3), destination);
    }

    [Fact]
    public void Conversion_requires_exact_revision_positive_quantity_and_checked_destination_capacity()
    {
        foreach (var change in new Func<StockBalance, long, long, StockBalance>[] {
            StockBalancePolicy.ConvertOut, StockBalancePolicy.ConvertIn }) {
            foreach (var revision in new[] { 0L, 6L, 8L })
                Assert.Equal("stock_revision_conflict", Assert.Throws<DomainValidationException>(() =>
                    change(Balance(), revision, 1)).Code);
            foreach (var quantity in new[] { 0L, -1L, long.MinValue })
                Assert.Equal("invalid_quantity", Assert.Throws<DomainValidationException>(() =>
                    change(Balance(), 7, quantity)).Code);
            Assert.Equal("stock_revision_exhausted", Assert.Throws<DomainValidationException>(() =>
                change(Balance(revision: long.MaxValue), long.MaxValue, 1)).Code);
        }

        var largest = Balance(onHand: long.MaxValue, reserved: 1, held: 2, expired: 3, revision: long.MaxValue - 1);
        Assert.Equal(largest with { OnHand = 6, Revision = long.MaxValue },
            StockBalancePolicy.ConvertOut(largest, long.MaxValue - 1, long.MaxValue - 6));
        Assert.Equal("stock_quantity_overflow", Assert.Throws<DomainValidationException>(() =>
            StockBalancePolicy.ConvertIn(largest, long.MaxValue - 1, 1)).Code);
        var empty = Balance(onHand: 0, reserved: 0, held: 0, expired: 0);
        Assert.Equal(empty with { OnHand = long.MaxValue, Revision = 8 },
            StockBalancePolicy.ConvertIn(empty, 7, long.MaxValue));
    }

    [Fact]
    public void Exact_rule_counts_preserve_base_units_across_the_two_mechanical_legs()
    {
        var sourcePackage = Package(200, 201, 12);
        var destinationPackage = Package(300, 301, 1);
        var rule = new PackConversionDefinition(new(500), 1, new(200), new(201), new(300), new(301), 1, 12);
        var quantities = PackConversionPolicy.Quantities(rule, sourcePackage, destinationPackage, 3);
        Assert.Equal(new ConversionQuantities(3, 36, 36), quantities);

        var source = Balance(onHand: 100);
        var destination = Balance(300, 301, 302, 5, 1, 1, 1);
        var taken = StockBalancePolicy.ConvertOut(source, 7, quantities.SourcePackages);
        var received = StockBalancePolicy.ConvertIn(destination, 7, quantities.DestinationPackages);
        Assert.Equal(source with { OnHand = 97, Revision = 8 }, taken);
        Assert.Equal(destination with { OnHand = 41, Revision = 8 }, received);
        Assert.Equal(1205L, checked(source.OnHand * 12 + destination.OnHand));
        Assert.Equal(1205L, checked(taken.OnHand * 12 + received.OnHand));
    }

    private static StockPackageDefinition Package(long skuId, long definitionId, int contents)
    {
        var definition = new VariantDefinitionVersion(new(definitionId), new(skuId), 1, "Kemasan", contents == 1 ? "loose" : "sealed",
            "piece", contents, ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal), [], 1, 1,
            new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero));
        var sku = new Sku(new(skuId), Location.OrganizationId, new(150),
            Guid.Parse(skuId == 200 ? "12345678-1234-4123-8123-123456789abc" : "12345678-1234-4123-8123-123456789abd"),
            "SKU-" + skuId, "shared-physical-item", true, definition.Id, 1);
        return new(sku, definition, definition);
    }
}
