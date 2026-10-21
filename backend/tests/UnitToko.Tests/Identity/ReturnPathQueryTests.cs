using UnitToko.Api.Identity;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Identity;

/// <summary>Signing in again returns to the same page and the record it had open (Review Focus 2, G-011).</summary>
[Collection("Mongo")]
public sealed class ReturnPathQueryTests(MongoFixture fixture)
{
    [Theory]
    [InlineData("/ordering/checkout?draft=7")]
    [InlineData("/ordering/substitutions?order=41")]
    [InlineData("/ordering/requests?package=00000065-0000-4000-8000-000000000101")]
    [InlineData("/ordering/orders?order=9223372036854775807")]
    [InlineData("/cart?terlewat=2")]
    [InlineData("/ordering/checkout?draft=7&step=review&a=b&cd=e-f")]
    public void A_page_and_its_open_record_survive_sign_in(string destination) =>
        Assert.Equal(destination, ReturnPathPolicy.Require(destination, Auth0ClaimsTests.Settings()));

    [Theory]
    [InlineData("/ordering/checkout?")]
    [InlineData("/ordering/checkout?draft")]
    [InlineData("/ordering/checkout?draft=")]
    [InlineData("/ordering/checkout?draft=7&")]
    [InlineData("/ordering/checkout?Draft=7")]
    [InlineData("/ordering/checkout?draft=%37")]
    [InlineData("/ordering/checkout?draft=7?x=1")]
    [InlineData("/ordering/checkout?draft=7#x")]
    [InlineData("/ordering/checkout?draft=a+b")]
    [InlineData("/ordering/checkout?next=https://outside.co.id")]
    [InlineData("/ordering/checkout?next=//outside.co.id")]
    [InlineData("/ordering/checkout?a=1&b=2&c=3&d=4&e=5")]
    [InlineData("/ordering/checkout/?draft=7")]
    [InlineData("/not-reviewed?draft=7")]
    [InlineData("//outside.co.id?draft=7")]
    [InlineData("?draft=7")]
    public void Only_a_reviewed_page_with_plain_name_value_pairs_is_accepted(string destination) =>
        Assert.Throws<DomainValidationException>(() => ReturnPathPolicy.Require(destination, Auth0ClaimsTests.Settings()));

    [Fact]
    public void Query_values_are_bounded_and_the_whole_destination_stays_within_200_characters()
    {
        var settings = Auth0ClaimsTests.Settings();
        Assert.Equal("/ordering/checkout?draft=" + new string('7', 64), ReturnPathPolicy.Require("/ordering/checkout?draft=" + new string('7', 64), settings));
        Assert.Throws<DomainValidationException>(() => ReturnPathPolicy.Require("/ordering/checkout?draft=" + new string('7', 65), settings));
        var longest = "/ordering/checkout?" + string.Join('&', Enumerable.Range(0, 4).Select(index => "p" + index + "=" + new string('x', 64)));
        Assert.True(longest.Length > 200);
        Assert.Throws<DomainValidationException>(() => ReturnPathPolicy.Require(longest, settings));
    }

    [Fact]
    public void Reviewed_configuration_lists_pages_only_never_queries()
    {
        var settings = Auth0ClaimsTests.Settings();
        settings.AllowedReturnPaths = ["/ordering/checkout?draft=7"];
        Assert.True(new Auth0OptionsValidator().Validate(null, settings).Failed);
    }

    [Fact]
    public void Every_signed_in_page_that_navigation_lists_is_a_return_destination_including_replenishment_and_accounts()
    {
        foreach (var path in new[] { "/inventory/replenishment", "/administration/accounts", "/ordering/requests", "/ordering/requests/incoming" })
            Assert.Equal(path, ReturnPathPolicy.Require(path, new Auth0Options()));
        Assert.Throws<DomainValidationException>(() => ReturnPathPolicy.Require("/catalog/browse", new Auth0Options()));
    }

    [Fact]
    public async Task The_pending_sign_in_keeps_the_destination_with_its_query_and_refuses_an_unsafe_one()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var store = new OidcTransactionStore(data.Database, data.Clock);
        var settings = Auth0ClaimsTests.Settings();
        var request = new OidcTransactionRequest(OidcTransactionPurpose.Login, settings.Issuer, settings.ClientId, "/ordering/checkout?draft=7");
        var start = await store.BeginAsync(request);
        Assert.Equal("/ordering/checkout?draft=7", (await store.ReadPendingAsync(start.StateToken)).Request.ReturnPath);
        foreach (var unsafeDestination in new[] { "/ordering/checkout?next=https://outside.co.id", "/ordering/checkout?draft=7#x", "/ordering/checkout?", "/ordering/checkout?draft=%37" })
            await Assert.ThrowsAsync<DomainValidationException>(() => store.BeginAsync(request with { ReturnPath = unsafeDestination }));
    }
}
