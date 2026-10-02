using System.Net;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Catalog;

namespace UnitToko.Tests.Catalog;

public sealed partial class CatalogAuthoringHttpTests
{
    [Fact]
    public async Task Order_quote_HTTP_requires_current_session_branch_selection_exact_package_and_price_with_no_client_money()
    {
        await using var data = await HttpData.CreateAsync(fixture, pricing: true);
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", BuyerId), Builders<BsonDocument>.Update.AddToSet("permissions", Permissions.OrderSubmit));
        using var maker = await data.OpenAsync(ManagerId); using var checker = await data.OpenAsync(PriceReviewerId); using var buyer = await data.OpenAsync(BuyerId);
        var sku = await PriceSku(maker); var path = "/skus/" + sku;
        await data.Rows("catalogAssortments").InsertOneAsync(CatalogVisibilityBson.Assortment(new CatalogAssortment(501, Owner, AuthorizationStatus.Active, 1)));
        await Ok(await maker.Post(path + "/publications", new { assortmentIds = new[] { "501" }, expectedAssortments = new[] { new { id = "501", version = "1" } }, reason = "Synthetic quote catalog" }, "quote-publish", "1"));
        await data.Rows("catalogEntitlements").InsertOneAsync(CatalogVisibilityBson.Entitlement(new CatalogEntitlement(601, BuyerId, BuyerId, 501,
            new(GrantScopeKind.BuyerOwned, 10, 11), AuthorizationStatus.Active, data.Base.Clock.UtcNow, null, null, 1, 998, 999, 100601, 1, data.Base.Clock.UtcNow)));
        var preview = await Ok(await maker.Price(path + "/preview/query", PriceRead()));
        var proposal = await Ok(await maker.Price(path + "/proposals", PriceCommand(preview, "12000.25"), "quote-price", "2"));
        await Ok(await checker.Price("/proposals/" + proposal.GetProperty("proposalId").GetString() + "/approve", new { reviewReference = "Independent fixture", reason = "Approved test price" }, "quote-price-approve", "1"));
        var item = Assert.Single((await Ok(await buyer.Variant("/query", new { }))).GetProperty("items").EnumerateArray());
        var price = item.GetProperty("currentPrice"); var definition = item.GetProperty("definition");
        object Body(string branch = "11", string? priceVersion = null) => new {
            purpose = "order-preparation", context = new { organizationId = "10", branchId = branch, divisionId = (string?)null },
            selections = new[] { new { skuId = sku, definitionId = definition.GetProperty("id").GetString(), definitionVersion = definition.GetProperty("version").GetString(),
                priceId = price.GetProperty("id").GetString(), priceVersion = priceVersion ?? price.GetProperty("version").GetString(), packages = 2 } }
        };
        async Task<HttpResponseMessage> Request(string suffix, object body, string? fault = null, string? key = null, string? version = null) {
            using var request = buyer.Request("/owners/query", body, key, version); request.RequestUri = new Uri(Origin + "/api/v1/ordering/" + suffix);
            if (fault == "actor") request.Headers.Remove("X-Expected-Actor-Id");
            if (fault == "csrf") request.Headers.Remove("X-CSRF-Token");
            if (fault == "wrong-actor") { request.Headers.Remove("X-Expected-Actor-Id"); request.Headers.Add("X-Expected-Actor-Id", ManagerId.ToString()); }
            return await buyer.Client.SendAsync(request);
        }
        Assert.Equal("11", Assert.Single((await Ok(await Request("contexts/query", new { purpose = "order-preparation" }))).GetProperty("items").EnumerateArray()).GetProperty("context").GetProperty("branchId").GetString());
        object PostalBody(string purpose = "order-preparation", string branch = "11") => new {
            purpose, context = new { organizationId = "10", branchId = branch, divisionId = (string?)null }
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("addresses/query", PostalBody("access-review"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("addresses/query", PostalBody(), "actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request("addresses/query", PostalBody(), "csrf")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Request("addresses/query", PostalBody(), "wrong-actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request("addresses/query", PostalBody(branch: "12"))).StatusCode);
        var postalBody = JsonSerializer.SerializeToNode(PostalBody())!.AsObject(); postalBody["context"]!["branchId"] = 11;
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("addresses/query", postalBody)).StatusCode);
        postalBody = JsonSerializer.SerializeToNode(PostalBody())!.AsObject(); postalBody["approvedAddress"] = "Caller supplied approval";
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("addresses/query", postalBody)).StatusCode);
        var quote = await Ok(await Request("quote/query", Body()));
        Assert.Equal("24000.50", quote.GetProperty("catalogSubtotal").GetString()); Assert.Equal("IDR", quote.GetProperty("currency").GetString());
        var line = Assert.Single(quote.GetProperty("lines").EnumerateArray()); Assert.Equal("24", line.GetProperty("baseUnits").GetString());
        Assert.Equal(price.GetRawText(), line.GetProperty("price").GetRawText()); Assert.False(quote.TryGetProperty("orderId", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await Request("quote/query", Body("12"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Request("quote/query", Body(priceVersion: "2"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("quote/query", Body(), "actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request("quote/query", Body(), "csrf")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Request("quote/query", Body(), "wrong-actor")).StatusCode);
        var forged = JsonSerializer.SerializeToNode(Body())!.AsObject(); forged["unitPrice"] = "1.00";
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("quote/query", forged)).StatusCode);
        Assert.Equal(0, await data.Count("orders")); Assert.Equal(0, await data.Count("reservations"));

        var selection = JsonSerializer.SerializeToElement(Body());
        var draftInput = new { context = selection.GetProperty("context"), selections = selection.GetProperty("selections"), delivery = new {
            recipientName = "Penerima fixture", phone = "+62 812-3456-7890", address = "Jalan Contoh 1\nLantai 2", city = "Jakarta", postalCode = "12810", instructions = (string?)null
        }, purchaseReference = "PO fixture" };
        var save = new { draftId = (string?)null, input = draftInput, reason = "Buyer saves private draft" };
        var saved = await Ok(await Request("drafts", save, key: "http-draft-save", version: "0"));
        var draftId = saved.GetProperty("draftId").GetString()!;
        Assert.Equal("1", saved.GetProperty("revision").GetString());
        var readDraft = await Ok(await Request("drafts/" + draftId + "/query", new { purpose = "order-preparation" }));
        Assert.Equal("Jalan Contoh 1\nLantai 2", readDraft.GetProperty("input").GetProperty("delivery").GetProperty("address").GetString());
        var drafts = await Ok(await Request("drafts/query", new { purpose = "order-preparation", context = selection.GetProperty("context") }));
        Assert.Equal(draftId, Assert.Single(drafts.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(saved.GetRawText(), (await Ok(await Request("drafts", save, key: "http-draft-save", version: "0"))).GetRawText());
        var recovered = await Ok(await Request("drafts/operations/query", new { purpose = "operation-recovery", key = "http-draft-save" }));
        Assert.Equal("committed", recovered.GetProperty("status").GetString()); Assert.Equal(saved.GetRawText(), recovered.GetProperty("result").GetRawText());
        Assert.Equal(HttpStatusCode.Conflict, (await Request("drafts", new { draftId, input = draftInput, reason = "Stale revision" }, key: "http-stale-draft", version: "2")).StatusCode);
        Assert.Equal(1, await data.Count("orderDrafts")); Assert.Equal(1, await data.Count("orderDraftVersions"));
        Assert.Equal(0, await data.Count("orders")); Assert.Equal(0, await data.Count("reservations"));
    }
}
