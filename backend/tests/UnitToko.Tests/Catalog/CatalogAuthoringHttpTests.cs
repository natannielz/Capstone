using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Api.Identity;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Catalog;

[Collection("Mongo")]
public sealed class CatalogAuthoringHttpTests(MongoFixture fixture)
{
    private const string Api = "/api/v1/catalog/administration";
    private const string Origin = "https://portal.unit-toko.co.id";
    private const long ManagerId = 45001;
    private const long BuyerId = 45002;
    private static readonly GrantScope Owner = new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
    private static readonly object OwnerBody = new { kind = "fulfillmentShop", organizationId = "100", shopId = "101", warehouseId = (string?)null, channelId = "103", branchId = (string?)null, divisionId = (string?)null };
    private static object Read => new { purpose = "catalog-authoring" };
    private static object Definition(int count, string label = "Kemasan", object? attributes = null) => new {
        packageLabel = label, packageKind = "sealed", baseUnitCode = "EA", baseUnitsPerPackage = count,
        attributes = attributes ?? new[] { new { key = "tipe", value = "ATK" } }, imageAssetIds = Array.Empty<string>(),
        minimumOrderPackages = 1, orderIncrementPackages = 1
    };

    [Fact]
    public async Task Http_catalog_owners_use_real_verified_session_and_expose_management_navigation_without_buyer_entitlement()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var browser = await data.OpenAsync(ManagerId);
        var body = await Ok(await browser.Post("/owners/query", Read));
        Assert.Contains(body.GetProperty("items").EnumerateArray(), item => item.GetProperty("owner").GetProperty("shopId").GetString() == "101");
        using var current = await browser.Client.GetAsync("/api/v1/access/current");
        Assert.Contains((await Ok(current)).GetProperty("allowedActions").EnumerateArray(), value => value.GetString() == "catalog.manage");
        Assert.Equal(0, await data.Count("catalogEntitlements"));
        using var buyer = await data.OpenAsync(BuyerId);
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.Post("/owners/query", Read)).StatusCode);
    }

    [Fact]
    public async Task Http_catalog_persists_explicit_twelve_and_twenty_four_variants_with_exact_large_identifiers()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        foreach (var name in new[] { "catalogCategories", "productFamilies", "skus", "variantDefinitions" })
            await data.Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", name), Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        using var browser = await data.OpenAsync(ManagerId);
        var (category, family) = await CreateFamily(browser);
        Assert.Equal("9007199254740993", family.GetProperty("id").GetString());
        var familyId = family.GetProperty("id").GetString()!;
        foreach (var count in new[] { 12, 24 })
        {
            var created = await Ok(await browser.Post("/families/" + familyId + "/skus", new { code = "ATK-" + count, physicalItemKey = "PENCIL-BLUE", definition = Definition(count, "Paket " + count), reason = "Kemasan dinyatakan petugas" }, "sku-" + count, "1"));
            Assert.Equal(familyId, created.GetProperty("familyId").GetString());
            Assert.Equal("1", created.GetProperty("definitionVersion").GetString());
        }
        using var reloaded = await data.OpenAsync(ManagerId);
        var list = await Ok(await reloaded.Post("/families/" + familyId + "/skus/query", new { purpose = "catalog-authoring", offset = 0, limit = 25 }));
        Assert.Equal("2", list.GetProperty("total").GetString());
        Assert.Equal(new[] { 12, 24 }, list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("definition").GetProperty("baseUnitsPerPackage").GetInt32()).Order());
        foreach (var item in list.GetProperty("items").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, item.GetProperty("id").ValueKind);
            Assert.Equal(JsonValueKind.String, item.GetProperty("version").ValueKind);
            Assert.Empty(item.GetProperty("publications").EnumerateArray());
            Assert.False(item.TryGetProperty("price", out _)); Assert.False(item.TryGetProperty("availability", out _));
        }
        var read = await Ok(await reloaded.Post("/families/" + familyId + "/query", Read));
        Assert.Equal("Alat tulis 📦", read.GetProperty("name").GetString());
        Assert.Equal(category.GetProperty("id").GetString(), read.GetProperty("categoryId").GetString());
        Assert.Equal(2, await data.Count("catalogIdentifiers")); Assert.Equal(2, await data.Count("catalogIdentifierHistory"));
    }

    [Fact]
    public async Task Http_catalog_current_owner_checks_origin_csrf_and_expected_actor_before_private_queries()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var browser = await data.OpenAsync(ManagerId);
        foreach (var fault in new[] { "origin", "csrf", "actor", "actor-wrong", "actor-leading-zero" })
        {
            using var request = browser.Request("/owners/query", Read);
            var expected = HttpStatusCode.Forbidden;
            if (fault == "origin") request.Headers.Remove("Origin");
            if (fault == "csrf") request.Headers.Remove("X-CSRF-Token");
            if (fault.StartsWith("actor", StringComparison.Ordinal))
            {
                request.Headers.Remove("X-Expected-Actor-Id"); expected = HttpStatusCode.BadRequest;
                if (fault == "actor-wrong") { request.Headers.Add("X-Expected-Actor-Id", BuyerId.ToString(CultureInfo.InvariantCulture)); expected = HttpStatusCode.Conflict; }
                if (fault == "actor-leading-zero") request.Headers.Add("X-Expected-Actor-Id", "045001");
            }
            Assert.Equal(expected, (await browser.Client.SendAsync(request)).StatusCode);
        }
        using var mixedCase = browser.Request("/owners/query", Read);
        mixedCase.RequestUri = new Uri(Origin + Api.ToUpperInvariant() + "/owners/query"); mixedCase.Headers.Remove("X-Expected-Actor-Id");
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.Client.SendAsync(mixedCase)).StatusCode);
        using var anonymous = await data.OpenAsync(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Post("/owners/query", Read)).StatusCode);
        Assert.Equal(0, await data.Count("auditEvents")); Assert.Equal(0, await data.Count("productFamilies"));
    }

    [Fact]
    public async Task Http_catalog_strict_payloads_do_not_coerce_ids_counts_versions_or_duplicate_attributes()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var browser = await data.OpenAsync(ManagerId);
        var (_, family) = await CreateFamily(browser); var id = family.GetProperty("id").GetString()!;
        foreach (var invalid in new[] { "{}", "{\"purpose\":\"catalog-authoring\",\"owner\":null}", "{\"purpose\":\"catalog-authoring\",\"owner\":{\"kind\":\"fulfillmentShop\",\"organizationId\":100,\"shopId\":\"101\",\"channelId\":\"103\"}}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.Raw("/families/query", invalid)).StatusCode);
        foreach (var value in new object[] { "12", 12.5, 0, 2147483648L })
        {
            var definition = new Dictionary<string, object> { ["packageLabel"] = "Paket", ["packageKind"] = "sealed", ["baseUnitCode"] = "EA", ["baseUnitsPerPackage"] = value, ["attributes"] = Array.Empty<object>(), ["imageAssetIds"] = Array.Empty<string>(), ["minimumOrderPackages"] = 1, ["orderIncrementPackages"] = 1 };
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.Post("/families/" + id + "/skus", new { code = "BAD", physicalItemKey = "PENCIL", definition, reason = "Invalid numeric contract" }, "bad-numeric", "1")).StatusCode);
        }
        foreach (var attributes in new[] { new[] { new { key = "same", value = "a" }, new { key = "same", value = "b" } }, new[] { new { key = "same", value = "a" }, new { key = " same ", value = "b" } } })
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.Post("/families/" + id + "/skus", new { code = "BAD", physicalItemKey = "PENCIL", definition = Definition(12, attributes: attributes), reason = "Invalid duplicate attribute" }, "bad-attributes", "1")).StatusCode);
        foreach (var version in new string?[] { null, "1", "W/\"1\"", "\"01\"", "\"0\"" })
        {
            using var request = browser.Request("/families/" + id + "/skus", new { code = "BAD", physicalItemKey = "PENCIL", definition = Definition(12), reason = "Invalid version" }, "bad-version");
            if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.Client.SendAsync(request)).StatusCode);
        }
        Assert.Equal(0, await data.Count("skus")); Assert.Equal(0, await data.Count("variantDefinitions"));
    }

    [Fact]
    public async Task Http_catalog_receipts_recover_same_actor_commits_and_cannot_close_an_absent_command()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var browser = await data.OpenAsync(ManagerId);
        var body = new { owner = OwnerBody, categoryId = (string?)null, code = "ATK", name = "Alat tulis", reason = "Kategori awal" };
        var saved = await Ok(await browser.Post("/categories", body, "category-once", "0"));
        var retry = await Ok(await browser.Post("/categories", body, "category-once", "0"));
        Assert.Equal(saved.GetRawText(), retry.GetRawText());
        using var renewed = await data.OpenAsync(ManagerId);
        var receipt = await Ok(await renewed.Post("/operations/query", new { purpose = "operation-recovery", operation = "catalog.category.save", key = "category-once" }));
        Assert.Equal("committed", receipt.GetProperty("state").GetString()); Assert.Equal(saved.GetRawText(), receipt.GetProperty("result").GetRawText());
        var absent = await Ok(await renewed.Post("/operations/query", new { purpose = "operation-recovery", operation = "catalog.sku.create", key = "never-submitted" }));
        Assert.Equal("not_confirmed", absent.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, absent.GetProperty("result").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await renewed.Post("/operations/resolve", new { operation = "catalog.sku.create", reason = "No closure contract" }, "never-submitted")).StatusCode);
        Assert.Equal(1, await data.Count("catalogCategories"));
        Assert.Equal(HttpStatusCode.Conflict, (await renewed.Post("/categories", new { owner = OwnerBody, categoryId = (string?)null, code = "DIFFERENT", name = "Different", reason = "Changed body" }, "category-once", "0")).StatusCode);
    }

    [Fact]
    public async Task Http_catalog_definition_revision_replacement_retirement_and_publication_keep_original_package_history()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var browser = await data.OpenAsync(ManagerId);
        var (_, family) = await CreateFamily(browser); var familyId = family.GetProperty("id").GetString()!;
        var created = await Ok(await browser.Post("/families/" + familyId + "/skus", new { code = "ATK-12", physicalItemKey = "PENCIL", definition = Definition(12), reason = "Twelve units" }, "initial-sku", "1"));
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.Post("/skus/" + id + "/definition", new { definition = Definition(10), reason = "Must replace material identity" }, "material-change", "1")).StatusCode);
        var revised = await Ok(await browser.Post("/skus/" + id + "/definition", new { definition = Definition(12, "Dus dua belas"), reason = "Correct descriptive label" }, "label-correction", "1"));
        Assert.Equal("2", revised.GetProperty("definitionVersion").GetString());
        await data.Rows("catalogAssortments").InsertOneAsync(CatalogVisibilityBson.Assortment(new CatalogAssortment(501, Owner, AuthorizationStatus.Active, 1)));
        var published = await Ok(await browser.Post("/skus/" + id + "/publications", new { assortmentIds = new[] { "501" }, expectedAssortments = new[] { new { id = "501", version = "1" } }, reason = "Explicit publication" }, "publish-sku", "2"));
        Assert.Equal("3", published.GetProperty("version").GetString());
        var replacement = await Ok(await browser.Post("/skus/" + id + "/replacement", new { code = "ATK-10", physicalItemKey = "PENCIL", definition = Definition(10), reason = "Distinct new packaging" }, "replace-sku", "3"));
        Assert.NotEqual(id, replacement.GetProperty("id").GetString()); Assert.Equal(id, replacement.GetProperty("predecessorSkuId").GetString());
        await Ok(await browser.Post("/skus/" + id + "/retire", new { reason = "Stop publishing old pack" }, "retire-sku", "3"));
        var old = await Ok(await browser.Post("/skus/" + id + "/query", Read));
        Assert.False(old.GetProperty("active").GetBoolean()); Assert.Empty(old.GetProperty("publications").EnumerateArray());
        Assert.Equal(12, old.GetProperty("definition").GetProperty("baseUnitsPerPackage").GetInt32());
        Assert.Equal(3, await data.Count("variantDefinitions")); Assert.Equal(2, await data.Count("catalogIdentifiers"));
        Assert.Equal(3L, (await data.Rows("catalogAssortments").Find(new BsonDocument("_id", 501L)).SingleAsync())["version"].AsInt64);
    }


    [Fact]
    public async Task Http_buyer_projection_requires_entitlement_and_never_exposes_staff_fields_or_invented_commercial_values()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var manager = await data.OpenAsync(ManagerId);
        using var buyer = await data.OpenAsync(BuyerId);
        Assert.Equal(HttpStatusCode.Forbidden, (await buyer.Variant("/query", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Variant("/query", new { })).StatusCode);
        var (_, family) = await CreateFamily(manager);
        var familyId = family.GetProperty("id").GetString()!;
        var visible = await Ok(await manager.Post("/families/" + familyId + "/skus",
            new { code = "VISIBLE-12", physicalItemKey = "STAFF-PRIVATE", definition = Definition(12), reason = "Explicit catalog item" }, "visible-sku", "1"));
        var hidden = await Ok(await manager.Post("/families/" + familyId + "/skus",
            new { code = "HIDDEN-24", physicalItemKey = "STAFF-PRIVATE", definition = Definition(24), reason = "Unpublished item" }, "hidden-sku", "1"));
        await data.Rows("catalogAssortments").InsertOneAsync(CatalogVisibilityBson.Assortment(new CatalogAssortment(501, Owner, AuthorizationStatus.Active, 1)));
        await Ok(await manager.Post("/skus/" + visible.GetProperty("id").GetString() + "/publications",
            new { assortmentIds = new[] { "501" }, expectedAssortments = new[] { new { id = "501", version = "1" } }, reason = "Approved synthetic publication" }, "publish", "1"));
        await data.Rows("catalogEntitlements").InsertOneAsync(CatalogVisibilityBson.Entitlement(new CatalogEntitlement(
            601, BuyerId, BuyerId, 501, new(GrantScopeKind.BuyerOwned, 10, 11), AuthorizationStatus.Active,
            data.Base.Clock.UtcNow, null, null, 1, 998, 999, 100601, 1, data.Base.Clock.UtcNow)));
        var page = await Ok(await buyer.Variant("/query", new { offset = 0, limit = 25 }));
        Assert.Equal("1", page.GetProperty("total").GetString());
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "categoryCode", "code", "definition", "description", "familyId", "familyName", "familyPublicId", "publicId", "skuId" },
            item.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(12, item.GetProperty("definition").GetProperty("baseUnitsPerPackage").GetInt32());
        Assert.Equal(visible.GetProperty("id").GetString(), item.GetProperty("skuId").GetString());
        Assert.DoesNotContain("STAFF-PRIVATE", item.GetRawText(), StringComparison.Ordinal);
        foreach (var name in new[] { "owner", "physicalItemKey", "images", "imageAssetIds", "price", "availability", "downloadUrl" })
        {
            Assert.False(item.TryGetProperty(name, out _));
            Assert.False(item.GetProperty("definition").TryGetProperty(name, out _));
        }
        var read = await Ok(await buyer.Variant("/" + item.GetProperty("publicId").GetString() + "/query", new { }));
        Assert.Equal(item.GetRawText(), read.GetRawText());
        var hiddenStaff = await Ok(await manager.Post("/skus/" + hidden.GetProperty("id").GetString() + "/query", Read));
        Assert.Equal(JsonValueKind.Null, (await Ok(await buyer.Variant("/" + hiddenStaff.GetProperty("publicId").GetString() + "/query", new { }))).ValueKind);
        using var stale = buyer.Request("/owners/query", new { });
        stale.RequestUri = new Uri(Origin + "/api/v1/catalog/variants/query");
        stale.Headers.Remove("X-Expected-Actor-Id");
        Assert.Equal(HttpStatusCode.BadRequest, (await buyer.Client.SendAsync(stale)).StatusCode);
    }

    private static async Task<(JsonElement Category, JsonElement Family)> CreateFamily(Browser browser)
    {
        var category = await Ok(await browser.Post("/categories", new { owner = OwnerBody, categoryId = (string?)null, code = "ATK", name = "Alat tulis", reason = "Kategori toko" }, "category", "0"));
        var family = await Ok(await browser.Post("/families", new { owner = OwnerBody, categoryId = category.GetProperty("id").GetString(), name = "Alat tulis 📦", description = "Kemasan dipilih secara eksplisit", imageAssetIds = Array.Empty<string>(), reason = "Produk toko" }, "family"));
        return (category, family);
    }
    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, response.StatusCode + ": " + text);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var parsed = JsonDocument.Parse(text); return parsed.RootElement.Clone();
    }

    private sealed class HttpData : IAsyncDisposable
    {
        internal StoreTestData Base { get; }
        private readonly IdentityRuntimeTests.RuntimeKeys keys = new();
        private readonly WebApplicationFactory<Program> factory;
        private readonly MongoUnitOfWork unit;
        private readonly MongoIdentitySessionStore sessions;
        private readonly Dictionary<long, ActorContext> actors = new();
        private HttpData(StoreTestData data, MongoFixture fixture)
        {
            Base = data; unit = new(fixture.Client);
            sessions = new(data.Database, unit, data.Clock, new("https://approved.example/", "fixture", "fixture"), SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
            var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
            factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
                services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
                services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            }));
        }
        internal static async Task<HttpData> CreateAsync(MongoFixture fixture)
        {
            var data = new HttpData(await StoreTestData.CreateAsync(fixture), fixture);
            data.Base.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            data.actors.Add(ManagerId, await data.Base.AddActorAsync(ManagerId, Owner, "catalog.manage"));
            data.actors.Add(BuyerId, await data.Base.AddActorAsync(BuyerId, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.CatalogRead));
            await data.Rows("identities").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Set("securityVersion", 1L));
            return data;
        }
        internal IMongoCollection<BsonDocument> Rows(string name) => Base.Database.GetCollection<BsonDocument>(name);
        internal Task<long> Count(string name) => Rows(name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        internal async Task<Browser> OpenAsync(long? id)
        {
            var client = factory.CreateClient(new() { BaseAddress = new(Origin), AllowAutoRedirect = false });
            if (id is { } value)
            {
                var actor = actors[value];
                var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "catalog@example.test", true, "fixture", Base.Clock.UtcNow, Base.Clock.UtcNow, "otp", "fixture");
                var issued = await unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic", ct));
                client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + issued.OpaqueToken);
            }
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf");
            return new(client, id ?? ManagerId, csrf.GetProperty("requestToken").GetString()!);
        }
        public async ValueTask DisposeAsync() { factory.Dispose(); keys.Dispose(); await Base.DisposeAsync(); }
    }
    private sealed class Browser(HttpClient client, long actorId, string csrf) : IDisposable
    {
        internal HttpClient Client => client;
        internal HttpRequestMessage Request(string suffix, object body, string? key = null, string? version = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Api + suffix) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", Origin); request.Headers.Add("X-CSRF-Token", csrf); request.Headers.Add("X-Expected-Actor-Id", actorId.ToString(CultureInfo.InvariantCulture));
            if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", "\"" + version + "\"");
            return request;
        }
        internal async Task<HttpResponseMessage> Post(string suffix, object body, string? key = null, string? version = null)
        { using var request = Request(suffix, body, key, version); return await client.SendAsync(request); }
        internal async Task<HttpResponseMessage> Raw(string suffix, string body)
        { using var request = Request(suffix, new { }); request.Content = new StringContent(body, Encoding.UTF8, "application/json"); return await client.SendAsync(request); }
        internal async Task<HttpResponseMessage> Variant(string suffix, object body)
        {
            using var request = Request("/owners/query", body);
            request.RequestUri = new Uri(Origin + "/api/v1/catalog/variants" + suffix);
            return await client.SendAsync(request);
        }
        public void Dispose() => client.Dispose();
    }
}
