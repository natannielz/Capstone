using UnitToko.Modules.Catalog;

namespace UnitToko.Tests.Catalog;

/// <summary>The assortment-level showcase (whole assortments public) was replaced by chosen sample products; the two scenarios
/// below are retired with it (docs/quality/retired-tests.md, "Assortment showcase"). CatalogPublicSampleTests covers the current path.</summary>
public sealed class CatalogPublicTests
{
    [Fact(Skip = Retired.AssortmentShowcase)]
    public void Nothing_is_public_until_an_independent_checker_approves_and_making_it_private_hides_it_again() { }

    [Fact(Skip = Retired.AssortmentShowcase)]
    public void The_proposer_cannot_approve_their_own_proposal_and_a_rejection_changes_nothing() { }

    [Fact]
    public void The_public_item_carries_no_price_stock_or_internal_identity()
    {
        var names = typeof(PublicCatalogItem).GetProperties().Concat(typeof(PublicCatalogPackage).GetProperties()).Select(property => property.Name).ToArray();
        Assert.DoesNotContain(names, name => name.Contains("Price", StringComparison.OrdinalIgnoreCase) || name.Contains("Stock", StringComparison.OrdinalIgnoreCase) ||
            name is "SkuId" or "Id" or "Owner" or "AssortmentId");
    }
}
