using UnitToko.Api.Identity;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Identity;

public sealed class ReturnPathProductTests
{
    [Fact]
    public void Signing_in_from_a_public_product_returns_to_its_priced_page_but_only_by_canonical_public_id()
    {
        var options = new Auth0Options { AllowedReturnPaths = ["/shop"] };
        Assert.Equal("/shop/00000065-0000-4000-8000-000000000101", ReturnPathPolicy.Require("/shop/00000065-0000-4000-8000-000000000101", options));
        foreach (var forged in new[] { "/shop/00000065-0000-4000-8000-000000000101/x", "/shop/ABCDEF65-0000-4000-8000-000000000101",
            "/shop/00000065-0000-3000-8000-000000000101", "/shop/1", "/shop/../identity/account", "/billing/00000065-0000-4000-8000-000000000101" })
            Assert.Throws<DomainValidationException>(() => ReturnPathPolicy.Require(forged, options));
    }
}
