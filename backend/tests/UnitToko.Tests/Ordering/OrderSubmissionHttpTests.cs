using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderSubmissionHttpTests(MongoFixture fixture)
{
    [Fact]
    public async Task Http_checkout_review_and_submission_require_exact_actor_csrf_and_review_and_hide_internal_authority()
    {
        await using var data = await OrderSubmissionTestData.CreateAsync(fixture); using var keys = new IdentityRuntimeTests.RuntimeKeys();
        var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Prices.Catalog.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(data.Prices.Catalog.Sessions);
            services.RemoveAll<OrderSubmissionService>(); services.AddSingleton(data.Service);
        }));
        const string origin = "https://portal.unit-toko.co.id";
        using var client = factory.CreateClient(new() { BaseAddress = new(origin), AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + data.Prices.Catalog.Issued[data.Buyer.UserId].OpaqueToken);
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf")).GetProperty("requestToken").GetString()!;
        var input = new { context = new { organizationId = "10", branchId = "11", divisionId = (string?)null },
            selections = data.Input.Selections.Select(line => new { skuId = line.SkuId.ToString(), definitionId = line.DefinitionId.ToString(), definitionVersion = line.DefinitionVersion.ToString(),
                priceId = line.PriceId.ToString(), priceVersion = line.PriceVersion.ToString(), packages = line.Packages }).ToArray(),
            delivery = data.Input.Delivery, purchaseReference = data.Input.PurchaseReference };
        async Task<HttpResponseMessage> Request(string path, object body, string? key = null, string? fault = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ordering/orders" + path) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", origin);
            if (fault != "actor") request.Headers.Add("X-Expected-Actor-Id", fault == "different-actor" ? data.Prices.Maker.UserId.ToString() : data.Buyer.UserId.ToString());
            if (fault != "csrf") request.Headers.Add("X-CSRF-Token", csrf);
            if (key is not null) { request.Headers.Add("Idempotency-Key", key); request.Headers.Add("If-Match", "\"0\""); }
            return await client.SendAsync(request);
        }
        static async Task<JsonElement> Ok(HttpResponseMessage response) {
            var raw = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, response.StatusCode + " " + raw);
            return JsonSerializer.Deserialize<JsonElement>(raw);
        }
        var preview = new { purpose = "order-preparation", input };
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("/preview/query", preview, fault: "actor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request("/preview/query", preview, fault: "csrf")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Request("/preview/query", preview, fault: "different-actor")).StatusCode);
        var review = await Ok(await Request("/preview/query", preview));
        Assert.Equal("25500.50", review.GetProperty("total").GetString()); Assert.Equal("Pending", review.GetProperty("spendingState").GetString());
        Assert.False(review.TryGetProperty("creditDecision", out _)); Assert.False(review.TryGetProperty("stock", out _));
        var command = new { input, review = review.GetProperty("review").GetString(), reason = "Synthetic buyer confirms order" };
        var forged = JsonSerializer.SerializeToNode(command)!.AsObject(); forged["total"] = "0.00";
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("", forged, "forged")).StatusCode);
        var saved = await Ok(await Request("", command, "http-submit")); var id = saved.GetProperty("orderId").GetString()!;
        Assert.Equal("1", saved.GetProperty("revision").GetString()); Assert.Single(saved.GetProperty("suborderIds").EnumerateArray());
        Assert.Equal(saved.GetRawText(), (await Ok(await Request("", command, "http-submit"))).GetRawText());
        var read = await Ok(await Request("/" + id + "/query", new { purpose = "order-preparation" }));
        Assert.Equal("25500.50", read.GetProperty("total").GetString()); Assert.False(read.TryGetProperty("auditId", out _));
        var recovered = await Ok(await Request("/operations/query", new { purpose = "operation-recovery", key = "http-submit" }));
        Assert.Equal("committed", recovered.GetProperty("status").GetString()); Assert.Equal(saved.GetRawText(), recovered.GetProperty("result").GetRawText());
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 6200L), Builders<BsonDocument>.Update.Set("status", "revoked"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Request("/" + id + "/query", new { purpose = "order-preparation" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Request("/operations/query", new { purpose = "operation-recovery", key = "http-submit" })).StatusCode);
        Assert.Equal(1, await data.Rows("orders").CountDocumentsAsync(new BsonDocument()));
    }
}
