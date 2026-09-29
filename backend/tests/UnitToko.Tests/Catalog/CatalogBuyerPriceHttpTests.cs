using System.Net;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Catalog;

namespace UnitToko.Tests.Catalog;

public sealed partial class CatalogAuthoringHttpTests
{
    [Fact]
    public async Task Buyer_price_HTTP_exposes_only_current_approved_package_price_under_current_entitlement()
    {
        await using var data = await HttpData.CreateAsync(fixture, pricing: true);
        using var maker = await data.OpenAsync(ManagerId); using var checker = await data.OpenAsync(PriceReviewerId);
        using var buyer = await data.OpenAsync(BuyerId);
        Assert.Contains((await Ok(await buyer.Client.GetAsync("/api/v1/access/current"))).GetProperty("allowedActions").EnumerateArray(), value => value.GetString() == "catalog.read");
        var sku = await PriceSku(maker); var path = "/skus/" + sku;
        await data.Rows("catalogAssortments").InsertOneAsync(CatalogVisibilityBson.Assortment(new CatalogAssortment(501, Owner, AuthorizationStatus.Active, 1)));
        await Ok(await maker.Post(path + "/publications", new {
            assortmentIds = new[] { "501" }, expectedAssortments = new[] { new { id = "501", version = "1" } }, reason = "Synthetic entitled catalog"
        }, "buyer-price-publish", "1"));
        await data.Rows("catalogEntitlements").InsertOneAsync(CatalogVisibilityBson.Entitlement(new CatalogEntitlement(
            601, BuyerId, BuyerId, 501, new(GrantScopeKind.BuyerOwned, 10, 11), AuthorizationStatus.Active,
            data.Base.Clock.UtcNow, null, null, 1, 998, 999, 100601, 1, data.Base.Clock.UtcNow)));
        async Task<JsonElement> Item() => Assert.Single((await Ok(await buyer.Variant("/query", new { offset = 0, limit = 25 }))).GetProperty("items").EnumerateArray());
        var missing = await Item();
        Assert.True(missing.TryGetProperty("currentPrice", out var empty), "The buyer must distinguish missing price from an approved zero.");
        Assert.Equal(JsonValueKind.Null, empty.ValueKind);
        await data.Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", "priceVersions"),
            Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        foreach (var amount in new[] { "12000.25", "0.00" })
        {
            var preview = await Ok(await maker.Price(path + "/preview/query", PriceRead()));
            var proposal = await Ok(await maker.Price(path + "/proposals", PriceCommand(preview, amount), "buyer-price-propose-" + amount, "2"));
            var before = await Item();
            Assert.Equal(amount == "12000.25" ? JsonValueKind.Null : JsonValueKind.Object, before.GetProperty("currentPrice").ValueKind);
            await Ok(await checker.Price("/proposals/" + proposal.GetProperty("proposalId").GetString() + "/approve",
                new { reviewReference = "Independent fixture review", reason = "Private review rationale" }, "buyer-price-approve-" + amount, "1"));
            var item = await Item(); var price = item.GetProperty("currentPrice");
            Assert.Equal(amount, price.GetProperty("unitPrice").GetString()); Assert.Equal("IDR", price.GetProperty("currency").GetString());
            Assert.True(long.Parse(price.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture) > 9007199254740992L);
            Assert.Equal(new[] { "currency", "effectiveAt", "id", "skuId", "unitPrice", "version" }, price.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
            Assert.Equal(12, item.GetProperty("definition").GetProperty("baseUnitsPerPackage").GetInt32());
            Assert.DoesNotContain("Private review rationale", item.GetRawText());
            var detail = await Ok(await buyer.Variant("/" + item.GetProperty("publicId").GetString() + "/query", new { }));
            Assert.Equal(price.GetRawText(), detail.GetProperty("currentPrice").GetRawText());
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.Price(path + "/history/query", PriceRead())).StatusCode);
        await data.Rows("catalogEntitlements").UpdateOneAsync(new BsonDocument("_id", 601L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.Variant("/query", new { })).StatusCode);
    }
}
