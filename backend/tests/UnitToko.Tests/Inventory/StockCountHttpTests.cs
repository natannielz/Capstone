using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed class StockCountHttpTests(MongoFixture fixture)
{
    private const string Api = "/api/v1/inventory/stock-counts";
    private const string Origin = "https://portal.unit-toko.co.id";
    private const long Maker = 46001, Counter = 46002, Reader = 46003;
    private static readonly GrantScope Location = CatalogAuthoringTestData.Warehouse;
    private static readonly object LocationBody = new { kind = "fulfillmentWarehouse", organizationId = "100",
        shopId = "101", warehouseId = "102", channelId = "103", branchId = (string?)null, divisionId = (string?)null };

    [Fact]
    public async Task Stock_only_current_capability_does_not_open_administrative_permission_catalog()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var browser = await data.OpenAsync(Counter);
        using var current = await browser.Client.GetAsync("/api/v1/access/current");
        var actions = (await Ok(current)).GetProperty("allowedActions").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("stock.count", actions); Assert.DoesNotContain("stock.read", actions);
        using var catalog = await browser.Client.GetAsync("/api/v1/access/catalog");
        Assert.Equal(HttpStatusCode.Forbidden, catalog.StatusCode);
    }

    [Fact]
    public async Task Counter_records_zero_with_exact_large_id_and_safe_projection_then_recovers_without_history_permission()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", "stockCountObservations"),
            Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        using var browser = await data.OpenAsync(Counter);
        var command = data.Command(0);
        var saved = await Ok(await browser.Post("", command, "zero-once"));
        Assert.Equal("9007199254740993", saved.GetProperty("id").GetString());
        Assert.Equal("1", saved.GetProperty("version").GetString());
        Assert.Equal(0, saved.GetProperty("packages").GetInt32());
        Assert.Equal("0", saved.GetProperty("baseUnits").GetString());
        Assert.Equal(12, saved.GetProperty("baseUnitsPerPackage").GetInt32());
        Assert.Equal("unavailable", saved.GetProperty("baseline").GetString());
        Assert.Equal("single-observation", saved.GetProperty("coverage").GetString());
        Assert.Equal(new[] { "baseUnitCode", "baseUnits", "baseUnitsPerPackage", "baseline", "coverage", "definitionId",
            "definitionVersion", "id", "location", "observedLotLabel", "packageKind", "packageLabel", "packages",
            "reason", "recordedAt", "skuCode", "skuId", "version" },
            saved.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.Equal(saved.GetRawText(), (await Ok(await browser.Post("", command, "zero-once"))).GetRawText());
        using var changedReplay = await browser.Post("", data.Command(1), "zero-once");
        Assert.Equal(HttpStatusCode.Conflict, changedReplay.StatusCode);
        using var renewed = await data.OpenAsync(Counter);
        var receipt = await Ok(await renewed.Post("/operations/query", new { purpose = "operation-recovery", operation = "inventory.stock-count.record", key = "zero-once" }));
        Assert.Equal("committed", receipt.GetProperty("state").GetString());
        Assert.Equal(saved.GetProperty("id").GetString(), receipt.GetProperty("result").GetProperty("id").GetString());
        Assert.Equal("1", receipt.GetProperty("result").GetProperty("version").GetString());
        Assert.Equal(new[] { "id", "version" }, receipt.GetProperty("result").EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
        using var historyDenied = await renewed.Post("/query", new { purpose = "inventory-stock-history", location = LocationBody, offset = 0, limit = 25 });
        Assert.Equal(HttpStatusCode.Forbidden, historyDenied.StatusCode);
        Assert.Equal(1, await data.Count("stockCountObservations"));
        using var reader = await data.OpenAsync(Reader);
        var history = await Ok(await reader.Post("/query", new { purpose = "inventory-stock-history", location = LocationBody, offset = 0, limit = 25 }));
        Assert.Equal("1", history.GetProperty("total").GetString());
        Assert.Equal(saved.GetRawText(), Assert.Single(history.GetProperty("items").EnumerateArray()).GetRawText());
        var hidden = await Ok(await reader.Post("/9223372036854775807/query", new { purpose = "inventory-stock-history" }));
        Assert.Equal(JsonValueKind.Null, hidden.ValueKind);
    }

    [Fact]
    public async Task Stock_command_requires_explicit_bounded_packages_and_canonical_expected_references()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var browser = await data.OpenAsync(Counter);
        foreach (var bad in new[] { "missing", "null", "string", "fraction", "negative", "too-large", "numeric-id", "zero-revision", "unknown-field",
            "source-numeric-id", "source-zero-revision", "source-missing-hash", "source-unknown-field" })
        {
            var command = JsonSerializer.SerializeToNode(data.Command(0))!.AsObject();
            switch (bad)
            {
                case "missing": command.Remove("packages"); break;
                case "null": command["packages"] = null; break;
                case "string": command["packages"] = "0"; break;
                case "fraction": command["packages"] = 0.5; break;
                case "negative": command["packages"] = -1; break;
                case "too-large": command["packages"] = 1000001; break;
                case "numeric-id": command["skuId"] = data.Sku.Id.Value; break;
                case "zero-revision": command["expectedSkuRevision"] = "0"; break;
                case "unknown-field": command["balance"] = 0; break;
                case "source-numeric-id": command["poolSelection"] = new JsonObject { ["poolId"] = 1, ["expectedPoolRevision"] = "1", ["configurationHash"] = new string('A', 64) }; break;
                case "source-zero-revision": command["poolSelection"] = new JsonObject { ["poolId"] = "1", ["expectedPoolRevision"] = "0", ["configurationHash"] = new string('A', 64) }; break;
                case "source-missing-hash": command["poolSelection"] = new JsonObject { ["poolId"] = "1", ["expectedPoolRevision"] = "1" }; break;
                case "source-unknown-field": command["poolSelection"] = new JsonObject { ["poolId"] = "1", ["expectedPoolRevision"] = "1", ["configurationHash"] = new string('A', 64), ["balance"] = 0 }; break;
            }
            using var response = await browser.Post("", command, "bad-" + bad);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(0, await data.Count("stockCountObservations"));
        using var readOnly = await data.OpenAsync(Reader);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.Post("", data.Command(0), "reader-write")).StatusCode);
    }

    [Fact]
    public async Task Inventory_post_requires_real_session_origin_csrf_and_reviewed_actor_including_case_variants()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var browser = await data.OpenAsync(Counter);
        foreach (var fault in new[] { "origin", "csrf", "actor", "wrong-actor", "leading-zero", "case-variant" })
        {
            using var request = browser.Request("", data.Command(0), "not-written");
            var expected = HttpStatusCode.Forbidden;
            if (fault == "origin") request.Headers.Remove("Origin");
            if (fault == "csrf") request.Headers.Remove("X-CSRF-Token");
            if (fault is "actor" or "wrong-actor" or "leading-zero" or "case-variant")
            {
                request.Headers.Remove("X-Expected-Actor-Id"); expected = HttpStatusCode.BadRequest;
                if (fault == "wrong-actor") { request.Headers.Add("X-Expected-Actor-Id", Reader.ToString(CultureInfo.InvariantCulture)); expected = HttpStatusCode.Conflict; }
                if (fault == "leading-zero") request.Headers.Add("X-Expected-Actor-Id", "046002");
                if (fault == "case-variant") request.RequestUri = new Uri(Origin + Api.ToUpperInvariant());
            }
            Assert.Equal(expected, (await browser.Client.SendAsync(request)).StatusCode);
        }
        using var anonymous = await data.OpenAsync(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Post("", data.Command(0), "anonymous")).StatusCode);
        Assert.Equal(0, await data.Count("stockCountObservations"));
    }

    [Fact]
    public async Task Unknown_count_result_remains_not_confirmed_and_has_no_close_or_unrelated_operation_path()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var browser = await data.OpenAsync(Counter);
        var missing = await Ok(await browser.Post("/operations/query",
            new { purpose = "operation-recovery", operation = "inventory.stock-count.record", key = "never-sent" }));
        Assert.Equal("not_confirmed", missing.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("result").ValueKind);
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("recordedAt").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.Post("/operations/resolve",
            new { operation = "inventory.stock-count.record", reason = "No close contract" }, "never-sent")).StatusCode);
        foreach (var bad in new[] { "inventory.stock-count.close", "inventory.stock.adjust", "catalog.sku.create" })
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.Post("/operations/query",
                new { purpose = "operation-recovery", operation = bad, key = "never-sent" })).StatusCode);
        Assert.Equal(0, await data.Count("stockCountObservations"));
    }
    [Fact]
    public async Task Named_warehouse_and_package_routes_keep_count_and_history_permissions_separate()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.NamesAsync();
        using var counter = await data.OpenAsync(Counter);
        var places = await Ok(await counter.Post("/warehouses/query", new { purpose = "inventory-stock-count" }));
        var place = Assert.Single(places.GetProperty("items").EnumerateArray());
        Assert.Equal("Gudang Utama", place.GetProperty("warehouseName").GetString());
        Assert.Equal("Kanal Sekolah", place.GetProperty("channelName").GetString());
        Assert.Equal("102", place.GetProperty("location").GetProperty("warehouseId").GetString());
        var packages = await Ok(await counter.Post("/packages/query", new { purpose = "inventory-stock-count", location = LocationBody, offset = 0, limit = 25 }));
        var package = Assert.Single(packages.GetProperty("items").EnumerateArray());
        Assert.Equal(data.Sku.Id.ToString(), package.GetProperty("skuId").GetString());
        Assert.Equal("1", package.GetProperty("skuRevision").GetString());
        Assert.Equal(12, package.GetProperty("baseUnitsPerPackage").GetInt32());
        Assert.Equal("Pensil", package.GetProperty("familyName").GetString());
        Assert.Equal(10, package.EnumerateObject().Count());
        Assert.Equal(HttpStatusCode.Forbidden, (await counter.Post("/warehouses/query", new { purpose = "inventory-stock-history" })).StatusCode);
        using var reader = await data.OpenAsync(Reader);
        Assert.Single((await Ok(await reader.Post("/warehouses/query", new { purpose = "inventory-stock-history" }))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.Post("/packages/query", new { purpose = "inventory-stock-count", location = LocationBody })).StatusCode);
        foreach (var purpose in new[] { "catalog-authoring", "other", "inventory-stock-count " })
            Assert.Equal(HttpStatusCode.BadRequest, (await counter.Post("/warehouses/query", new { purpose })).StatusCode);
        Assert.Equal(0, await data.Count("stockCountObservations"));
    }

    [Fact]
    public async Task Scoped_HTTP_commands_and_history_cannot_cross_warehouses_or_borrow_another_actors_receipt()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.Rows("authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Warehouse, 101, AuthorizationStatus.Active, 1)));
        await data.AddActorAsync(46004, Location with { WarehouseId = 104 }, Permissions.StockCount, Permissions.StockRead);
        using var counter = await data.OpenAsync(Counter);
        var original = await Ok(await counter.Post("", data.Command(2), "private-count"));
        using var other = await data.OpenAsync(46004);
        Assert.Equal(JsonValueKind.Null, (await Ok(await other.Post("/" + original.GetProperty("id").GetString() + "/query", new { purpose = "inventory-stock-history" }))).ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.Post("/query", new { purpose = "inventory-stock-history", location = LocationBody })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.Post("", data.Command(2), "forbidden-count")).StatusCode);
        var receipt = await Ok(await other.Post("/operations/query", new { purpose = "operation-recovery", operation = "inventory.stock-count.record", key = "private-count" }));
        Assert.Equal("not_confirmed", receipt.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, receipt.GetProperty("result").ValueKind);
        var foreign = JsonSerializer.SerializeToNode(LocationBody)!.AsObject(); foreign["warehouseId"] = "104";
        Assert.Equal(HttpStatusCode.Forbidden, (await counter.Post("/packages/query", new { purpose = "inventory-stock-count", location = foreign })).StatusCode);
        var command = JsonSerializer.SerializeToNode(data.Command(1))!.AsObject(); command["location"] = foreign.DeepClone();
        Assert.Equal(HttpStatusCode.Forbidden, (await counter.Post("", command, "wrong-warehouse")).StatusCode);
        Assert.Equal(1, await data.Count("stockCountObservations"));
    }

    [Fact]
    public async Task Historical_HTTP_facts_and_same_key_replay_survive_definition_revision_and_retirement()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var counter = await data.OpenAsync(Counter);
        var original = await Ok(await counter.Post("", data.Command(2), "original-definition"));
        await data.ReviseAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await counter.Post("", data.Command(2), "stale-selection")).StatusCode);
        await data.RetireAsync();
        var choices = await Ok(await counter.Post("/packages/query", new { purpose = "inventory-stock-count", location = LocationBody }));
        Assert.Equal("0", choices.GetProperty("total").GetString()); Assert.Empty(choices.GetProperty("items").EnumerateArray());
        Assert.Equal(original.GetRawText(), (await Ok(await counter.Post("", data.Command(2), "original-definition"))).GetRawText());
        var receipt = await Ok(await counter.Post("/operations/query", new { purpose = "operation-recovery", operation = "inventory.stock-count.record", key = "original-definition" }));
        Assert.Equal(original.GetProperty("id").GetString(), receipt.GetProperty("result").GetProperty("id").GetString());
        using var reader = await data.OpenAsync(Reader);
        var historical = await Ok(await reader.Post("/" + original.GetProperty("id").GetString() + "/query", new { purpose = "inventory-stock-history" }));
        Assert.Equal(original.GetRawText(), historical.GetRawText());
        Assert.Equal("Dus", historical.GetProperty("packageLabel").GetString());
        Assert.Equal("24", historical.GetProperty("baseUnits").GetString());
        Assert.Equal(1, await data.Count("stockCountObservations"));
    }
    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, response.StatusCode + ": " + text);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var parsed = JsonDocument.Parse(text); return parsed.RootElement.Clone();
        }
    }

    private sealed class HttpData : IAsyncDisposable
    {
        internal StoreTestData Base { get; }
        internal CatalogMutationResult Sku { get; private set; } = null!;
        private readonly IdentityRuntimeTests.RuntimeKeys keys = new();
        private readonly WebApplicationFactory<Program> factory;
        private readonly MongoUnitOfWork unit;
        private readonly MongoIdentitySessionStore sessions;
        private readonly Dictionary<long, ActorContext> actors = [];
        private HttpData(StoreTestData data, MongoFixture fixture)
        {
            Base = data; unit = new(fixture.Client);
            sessions = new(data.Database, unit, data.Clock, new("https://approved.example/", "fixture", "fixture"),
                SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
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
            data.actors.Add(Maker, await data.Base.AddActorAsync(Maker, CatalogAuthoringTestData.Shop, Permissions.CatalogManage));
            data.actors.Add(Counter, await data.Base.AddActorAsync(Counter, Location, Permissions.StockCount));
            data.actors.Add(Reader, await data.Base.AddActorAsync(Reader, Location, Permissions.StockRead));
            await data.Rows("identities").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Set("securityVersion", 1L));
            var catalog = new CatalogAuthoringService(data.Base.Database, data.unit, new(data.Base.Database), data.Base.Clock);
            var category = await catalog.SaveCategoryAsync(data.actors[Maker], new(CatalogAuthoringTestData.Shop, null, 0, "COUNT", "Count fixtures"), CatalogAuthoringTestData.Metadata("count-category"));
            var family = await catalog.CreateFamilyAsync(data.actors[Maker], new(CatalogAuthoringTestData.Shop, category.Id, "Pensil", "Kemasan eksplisit", []), CatalogAuthoringTestData.Metadata("count-family"));
            data.Sku = await catalog.CreateSkuAsync(data.actors[Maker], new(family.Id, family.Version, "PENCIL-12", "private-physical-key", CatalogAuthoringTestData.Definition(12)), CatalogAuthoringTestData.Metadata("count-sku"));
            return data;
        }
        internal async Task AddActorAsync(long id, GrantScope location, params string[] permissions)
        {
            actors.Add(id, await Base.AddActorAsync(id, location, permissions));
            await Rows("identities").UpdateOneAsync(new BsonDocument("_id", id), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
        }
        internal async Task NamesAsync()
        {
            foreach (var node in (await Rows("authorizationScopes").Find(new BsonDocument()).ToListAsync()).Where(row => row["_id"].AsInt64 >= 100))
            {
                var id = node["_id"].AsInt64;
                var name = id switch { 100 => "Unit Selatan", 101 => "Toko Melati", 102 => "Gudang Utama", 103 => "Kanal Sekolah", _ => "Lokasi " + id };
                await Rows("organizationProfiles").InsertOneAsync(new BsonDocument
                {
                    { "_id", id }, { "schemaVersion", 1 }, { "kind", node["kind"] }, { "parentId", node.GetValue("parentId", BsonNull.Value) },
                    { "code", "NODE-" + id }, { "normalizedCode", "NODE-" + id }, { "name", name }, { "version", 1L },
                    { "createdBy", 998L }, { "updatedBy", 999L }, { "createdAt", Base.Clock.UtcNow.UtcDateTime },
                    { "updatedAt", Base.Clock.UtcNow.UtcDateTime }, { "approvedProposalId", id + 10000 }
                });
            }
        }
        internal Task<CatalogMutationResult> ReviseAsync() =>
            new CatalogAuthoringService(Base.Database, unit, new(Base.Database), Base.Clock).ReviseDefinitionAsync(actors[Maker],
                new(Sku.Id, 1, CatalogAuthoringTestData.Definition(12, "Dus diperbarui")), CatalogAuthoringTestData.Metadata("revise-count-package"));
        internal Task<CatalogMutationResult> RetireAsync() =>
            new CatalogAuthoringService(Base.Database, unit, new(Base.Database), Base.Clock).RetireSkuAsync(actors[Maker],
                new(Sku.Id, 2), CatalogAuthoringTestData.Metadata("retire-count-package"));
        internal object Command(int packages) => new { location = LocationBody, skuId = Sku.Id.ToString(),
            expectedSkuRevision = "1", expectedDefinitionId = Sku.CurrentDefinitionId!.Value.ToString(),
            packages, observedLotLabel = "Unverified lot", reason = "Pengamatan fisik" };
        internal IMongoCollection<BsonDocument> Rows(string name) => Base.Database.GetCollection<BsonDocument>(name);
        internal Task<long> Count(string name) => Rows(name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        internal async Task<Browser> OpenAsync(long? id)
        {
            var client = factory.CreateClient(new() { BaseAddress = new(Origin), AllowAutoRedirect = false });
            if (id is { } value)
            {
                var actor = actors[value];
                var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "count@example.test", true, "fixture", Base.Clock.UtcNow, Base.Clock.UtcNow, "otp", "fixture");
                var issued = await unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                    new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic", ct));
                client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + issued.OpaqueToken);
            }
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf");
            return new(client, id ?? Counter, csrf.GetProperty("requestToken").GetString()!);
        }
        public async ValueTask DisposeAsync() { factory.Dispose(); keys.Dispose(); await Base.DisposeAsync(); }
    }
    private sealed class Browser(HttpClient client, long actorId, string csrf) : IDisposable
    {
        internal HttpClient Client => client;
        internal HttpRequestMessage Request(string suffix, object body, string? key = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Api + suffix) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", Origin); request.Headers.Add("X-CSRF-Token", csrf);
            request.Headers.Add("X-Expected-Actor-Id", actorId.ToString(CultureInfo.InvariantCulture));
            if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            return request;
        }
        internal async Task<HttpResponseMessage> Post(string suffix, object body, string? key = null)
        { using var request = Request(suffix, body, key); return await client.SendAsync(request); }
        public void Dispose() => client.Dispose();
    }
}
