using System.Collections.Immutable;
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
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Inventory;

/// <summary>Actual API/session/catalog/Mongo rule preparation. People/provider proofs are
/// synthetic; no physical conversion, source activation, cost allocation or money is accepted.</summary>
[Collection("Mongo")]
public sealed class PackConversionRuleHttpTests(MongoFixture fixture)
{
    internal const string Api = "/api/v1/inventory/pack-conversions", Origin = "https://portal.unit-toko.co.id";
    internal const long Author = 970101, Maker = 970102, Reviewer = 970103, StockOnly = 970104, Foreign = 970105;
    internal static readonly GrantScope Location = CatalogAuthoringTestData.Warehouse;
    private static readonly object LocationBody = new { kind = "fulfillmentWarehouse", organizationId = "100", shopId = "101",
        warehouseId = "102", channelId = "103", branchId = (string?)null, divisionId = (string?)null };
    private static object History => new { purpose = "pack-conversion-rule-history", location = LocationBody };
    private static object Review => new { purpose = "pack-conversion-rule-review", action = "approve" };
    private static object Recovery(string operation, string key) => new { purpose = "operation-recovery", operation = "inventory.pack-conversion.rule." + operation, key };

    [Fact]
    public async Task Actual_rule_preview_pending_discovery_independent_approval_and_original_key_replay_persist_without_physical_or_financial_effects()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        var before = await data.PhysicalAsync(); using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer);
        foreach (var (role, purpose, kind, packageId) in new[] {
            ("source", "pack-conversion-rule-source", "sealed", data.Sealed.Id.ToString()),
            ("destination", "pack-conversion-rule-destination", "loose", data.Loose.Id.ToString()) }) {
            var choices = await Ok(await maker.Post("/packages/query", new { purpose, location = LocationBody, role, limit = 25 }));
            var selected = Assert.Single(choices.GetProperty("items").EnumerateArray());
            Assert.Equal(packageId, selected.GetProperty("skuId").GetString()); Assert.Equal(kind, selected.GetProperty("packageKind").GetString()); PrivateFieldsAbsent(choices);
        }
        var preview = await Ok(await maker.Post("/rules/preview/query", data.Preview()));
        Assert.Equal("1", preview.GetProperty("sourcePackages").GetString()); Assert.Equal("12", preview.GetProperty("destinationPackages").GetString());
        Assert.Equal("12", preview.GetProperty("baseUnits").GetString()); Assert.False(preview.GetProperty("physicalConversionAvailable").GetBoolean()); PrivateFieldsAbsent(preview);
        var command = new { input = data.Input(), binding = preview.GetProperty("binding").GetString(), reason = "Aturan pembukaan dus untuk pemeriksaan lokal" };
        var attempts = await Task.WhenAll(maker.Post("/proposals", command, "http-conversion-propose"), maker.Post("/proposals", command, "http-conversion-propose"));
        JsonElement? committed = null;
        foreach (var response in attempts) {
            using (response) {
                if (response.StatusCode == HttpStatusCode.OK) {
                    var value = await OkContent(response); var original = committed ?? value; committed = original; Assert.Equal(original.GetRawText(), value.GetRawText());
                } else {
                    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                    Assert.Equal("version_conflict", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
                }
            }
        }
        var proposed = committed ?? throw new InvalidOperationException("at least one original command must genuinely commit");
        var id = proposed.GetProperty("proposalId").GetString()!; Assert.Equal("pending", proposed.GetProperty("state").GetString());
        Assert.False(proposed.GetProperty("physicalConversionAvailable").GetBoolean()); PrivateFieldsAbsent(proposed);
        var pending = await Ok(await reviewer.Post("/proposals/query", History)); var pendingRow = Assert.Single(pending.GetProperty("items").EnumerateArray());
        Assert.Equal(id, pendingRow.GetProperty("id").GetString()); Assert.Equal("pending", pendingRow.GetProperty("status").GetString());
        Assert.True(pendingRow.GetProperty("canReview").GetBoolean()); PrivateFieldsAbsent(pending);
        var own = await Ok(await maker.Post("/proposals/" + id + "/query", new { purpose = "pack-conversion-rule-history" }));
        Assert.False(own.GetProperty("canReview").GetBoolean()); PrivateFieldsAbsent(own);
        using (var refused = await maker.Post("/proposals/" + id + "/review/query", Review)) {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode); Assert.Equal("independent_person_required", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        var review = await Ok(await reviewer.Post("/proposals/" + id + "/review/query", Review));
        Assert.True(review.GetProperty("proposal").GetProperty("canReview").GetBoolean()); PrivateFieldsAbsent(review);
        var decision = new { binding = review.GetProperty("binding").GetString(), reason = "Pemeriksaan independen rasio kemasan lokal" };
        foreach (var version in new string?[] { null, "\"01\"", "\"2\"" }) {
            using var refused = await reviewer.Post("/proposals/" + id + "/approve", decision, "never-approved", version);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
        Assert.Equal(0L, await data.Count("packConversionRuleDecisions"));
        var approved = await Ok(await reviewer.Post("/proposals/" + id + "/approve", decision, "http-conversion-approve", "\"1\""));
        Assert.Equal("approve", approved.GetProperty("state").GetString()); Assert.Equal(id, approved.GetProperty("ruleId").GetString());
        Assert.False(approved.GetProperty("physicalConversionAvailable").GetBoolean()); PrivateFieldsAbsent(approved);
        using var renewedMaker = await data.OpenAsync(Maker); using var renewedReviewer = await data.OpenAsync(Reviewer);
        Assert.Equal(proposed.GetRawText(), (await Ok(await renewedMaker.Post("/proposals", command, "http-conversion-propose"))).GetRawText());
        Assert.Equal(approved.GetRawText(), (await Ok(await renewedReviewer.Post("/proposals/" + id + "/approve", decision, "http-conversion-approve", "\"1\""))).GetRawText());
        foreach (var (actor, operation, key, expected) in new[] {
            (renewedMaker, "propose", "http-conversion-propose", proposed), (renewedReviewer, "decide", "http-conversion-approve", approved) }) {
            var recovered = await Ok(await actor.Post("/operations/query", Recovery(operation, key)));
            Assert.Equal("committed", recovered.GetProperty("state").GetString()); Assert.Equal(expected.GetRawText(), recovered.GetProperty("result").GetRawText()); PrivateFieldsAbsent(recovered);
        }
        var detail = await Ok(await renewedReviewer.Post("/proposals/" + id + "/query", new { purpose = "pack-conversion-rule-history" }));
        Assert.Equal("approved", detail.GetProperty("status").GetString()); Assert.False(detail.GetProperty("canReview").GetBoolean()); PrivateFieldsAbsent(detail);
        var rules = await Ok(await renewedReviewer.Post("/rules/query", History)); var rule = Assert.Single(rules.GetProperty("items").EnumerateArray());
        Assert.Equal(id, rule.GetProperty("id").GetString()); Assert.Equal("1", rule.GetProperty("version").GetString()); Assert.False(rule.GetProperty("physicalConversionAvailable").GetBoolean()); PrivateFieldsAbsent(rules);
        var changed = JsonSerializer.SerializeToNode(command)!.AsObject(); changed["reason"] = "Alasan berbeda dengan kunci asli";
        using (var refused = await renewedMaker.Post("/proposals", changed, "http-conversion-propose")) Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        foreach (var name in new[] { "packConversionRuleProposals", "packConversionRuleDecisions", "packConversionRules" }) Assert.Equal(1L, await data.Count(name));
        foreach (var operation in new[] { "inventory.pack-conversion.rule.propose", "inventory.pack-conversion.rule.decide" }) {
            Assert.Equal(1L, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", operation)));
            Assert.Equal(1L, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", operation)));
            Assert.Equal(1L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", operation)));
        }
        Same(before, await data.PhysicalAsync(), "rule-only physical/financial state");
    }

    [Fact]
    public async Task Current_session_MFA_scoped_StockConvert_and_person_authority_are_required_without_cross_warehouse_disclosure()
    {
        await using var data = await HttpData.CreateAsync(fixture); var before = await data.PhysicalAsync();
        using var maker = await data.OpenAsync(Maker); var preview = await Ok(await maker.Post("/rules/preview/query", data.Preview()));
        var proposed = await Ok(await maker.Post("/proposals", new { input = data.Input(), binding = preview.GetProperty("binding").GetString(), reason = "Aturan lokal untuk pengujian cakupan" }, "scope-rule"));
        var id = proposed.GetProperty("proposalId").GetString()!;
        using var anonymous = await data.OpenAsync(null); using var wrong = await data.OpenAsync(StockOnly); using var foreign = await data.OpenAsync(Foreign);
        var makerTasks = await Ok(await maker.Client.GetAsync("/api/v1/access/current"));
        Assert.Contains(Permissions.StockConvert, makerTasks.GetProperty("allowedActions").EnumerateArray().Select(value => value.GetString()));
        var stockTasks = await Ok(await wrong.Client.GetAsync("/api/v1/access/current"));
        Assert.DoesNotContain(Permissions.StockConvert, stockTasks.GetProperty("allowedActions").EnumerateArray().Select(value => value.GetString()));
        using (var response = await anonymous.Post("/rules/preview/query", data.Preview())) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        foreach (var actor in new[] { wrong, foreign }) {
            using (var response = await actor.Post("/rules/preview/query", data.Preview())) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using (var response = await actor.Post("/proposals/" + id + "/query", new { purpose = "pack-conversion-rule-history" })) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using var expired = await data.OpenAsync(Reviewer);
        var beforeExpiry = await Ok(await expired.Post("/proposals/" + id + "/review/query", Review));
        Assert.True(beforeExpiry.GetProperty("proposal").GetProperty("canReview").GetBoolean());
        var expiredReference = expired.Reference ?? throw new InvalidOperationException("genuine session reference is required");
        Assert.Equal(1L, (await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", expiredReference.Id),
            Builders<BsonDocument>.Update.Set("idleExpiresAt", data.Base.Clock.UtcNow.UtcDateTime))).ModifiedCount);
        using (var response = await expired.Post("/proposals/" + id + "/review/query", Review)) {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("antiforgery_failed", problem.RootElement.GetProperty("code").GetString());
        }
        using var reviewer = await data.OpenAsync(Reviewer);
        var missing = await Ok(await foreign.Post("/operations/query", Recovery("propose", "scope-rule")));
        Assert.Equal("not_confirmed", missing.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, missing.GetProperty("result").ValueKind);
        await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", Reviewer), Builders<BsonDocument>.Update.Set("permissions", new BsonArray { Permissions.StockRead }));
        var revokedTasks = await Ok(await reviewer.Client.GetAsync("/api/v1/access/current"));
        Assert.DoesNotContain(Permissions.StockConvert, revokedTasks.GetProperty("allowedActions").EnumerateArray().Select(value => value.GetString()));
        using (var response = await reviewer.Post("/proposals/" + id + "/review/query", Review)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1L, await data.Count("packConversionRuleProposals")); Assert.Equal(0L, await data.Count("packConversionRuleDecisions")); Assert.Equal(0L, await data.Count("packConversionRules"));
        Same(before, await data.PhysicalAsync(), "denied physical/financial state");
    }

    [Fact]
    public async Task Strict_JSON_canonical_identifiers_ratios_purposes_and_security_headers_refuse_before_publishing_any_rule()
    {
        await using var data = await HttpData.CreateAsync(fixture); using var actor = await data.OpenAsync(Maker); var before = await data.PhysicalAsync();
        foreach (var fault in new[] { "numeric-id", "leading-zero", "unknown-input", "unknown-body", "zero-ratio", "fraction-ratio", "string-ratio", "overflow-ratio", "unconserved-ratio", "same-sku", "missing-purpose", "wrong-purpose" }) {
            var body = JsonSerializer.SerializeToNode(data.Preview())!.AsObject(); var input = body["input"]!.AsObject();
            switch (fault) {
                case "numeric-id": input["sourceSkuId"] = data.Sealed.Id.Value; break;
                case "leading-zero": input["sourceSkuId"] = "0" + data.Sealed.Id; break;
                case "unknown-input": input["stockApproval"] = true; break;
                case "unknown-body": body["physicalConversionAvailable"] = true; break;
                case "zero-ratio": input["sourcePackagesPerStep"] = 0; break;
                case "fraction-ratio": input["sourcePackagesPerStep"] = 1.5; break;
                case "string-ratio": input["sourcePackagesPerStep"] = "1"; break;
                case "overflow-ratio": input["destinationPackagesPerStep"] = 1000001; break;
                case "unconserved-ratio": input["destinationPackagesPerStep"] = 11; break;
                case "same-sku": input["destinationSkuId"] = data.Sealed.Id.ToString(); input["destinationDefinitionId"] = data.Sealed.CurrentDefinitionId!.Value.ToString(); break;
                case "missing-purpose": body.Remove("purpose"); break;
                case "wrong-purpose": body["purpose"] = "inventory-stock-count"; break;
            }
            using var response = await actor.Post("/rules/preview/query", body); Assert.True(response.StatusCode == HttpStatusCode.BadRequest, "strict preview refusal: " + fault + " status " + (int)response.StatusCode);
        }
        foreach (var malformed in new[] { "0", "01", "-1", "9223372036854775808", "not-an-id" }) {
            using var response = await actor.Post("/proposals/" + malformed + "/query", new { purpose = "pack-conversion-rule-history" });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        var preview = await Ok(await actor.Post("/rules/preview/query", data.Preview()));
        var command = new { input = data.Input(), binding = preview.GetProperty("binding").GetString(), reason = "Pengujian batas keamanan lokal" };
        foreach (var fault in new[] { "origin", "csrf", "actor", "wrong-actor", "missing-key" }) {
            using var request = actor.Request("/proposals", command, fault == "missing-key" ? null : "never-written");
            var expected = HttpStatusCode.Forbidden;
            if (fault == "origin") { request.Headers.Remove("Origin"); request.Headers.Add("Origin", "https://foreign.example.invalid"); }
            if (fault == "csrf") request.Headers.Remove("X-CSRF-Token");
            if (fault == "actor") { request.Headers.Remove("X-Expected-Actor-Id"); expected = HttpStatusCode.BadRequest; }
            if (fault == "wrong-actor") { request.Headers.Remove("X-Expected-Actor-Id"); request.Headers.Add("X-Expected-Actor-Id", Reviewer.ToString(CultureInfo.InvariantCulture)); expected = HttpStatusCode.Conflict; }
            if (fault == "missing-key") expected = HttpStatusCode.BadRequest;
            using var response = await actor.Client.SendAsync(request); Assert.Equal(expected, response.StatusCode);
        }
        foreach (var name in new[] { "packConversionRuleProposals", "packConversionRuleDecisions", "packConversionRules" }) Assert.Equal(0L, await data.Count(name));
        foreach (var name in new[] { "auditEvents", "outboxEvents" }) Assert.Equal(0L, await data.Rows(name).CountDocumentsAsync(new BsonDocument("kind", new BsonDocument("$regex", "^inventory[.]pack-conversion[.]rule[.]"))));
        Same(before, await data.PhysicalAsync(), "invalid-input physical/financial state");
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage response) { using (response) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await OkContent(response); } }
    private static async Task<JsonElement> OkContent(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    private static void PrivateFieldsAbsent(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) {
            Assert.DoesNotContain(property.Name, new[] { "people", "personId", "actorId", "actorGrantVersion", "issuer", "subject", "auditId", "bodyHash", "snapshotHash", "requestKey", "authoringOwner", "physicalItemKey", "catalog" });
            PrivateFieldsAbsent(property.Value);
        } else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) PrivateFieldsAbsent(item);
    }
    private static void Same(BsonDocument expected, BsonDocument actual, string label) => Assert.True(expected.ToBson().SequenceEqual(actual.ToBson()), label + " changed");
    internal sealed class HttpData : IAsyncDisposable
    {
        internal StoreTestData Base { get; }
        internal CatalogMutationResult Sealed { get; private set; } = null!;
        internal CatalogMutationResult Loose { get; private set; } = null!;
        private readonly IdentityRuntimeTests.RuntimeKeys keys = new();
        private readonly WebApplicationFactory<Program> factory;
        private readonly MongoUnitOfWork unit;
        private readonly MongoIdentitySessionStore sessions;
        private readonly Dictionary<long, ActorContext> actors = [];
        internal IReadOnlyDictionary<long, ActorContext> Actors => actors;
        internal MongoUnitOfWork Unit => unit;
        internal MongoIdentitySessionStore Sessions => sessions;
        private HttpData(StoreTestData data, MongoFixture fixture)
        {
            Base = data; unit = new(fixture.Client);
            sessions = new(data.Database, unit, data.Clock, new("https://approved.example/", "fixture", "fixture"), SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
            var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
            factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
                services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock); services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            }));
        }
        internal static async Task<HttpData> CreateAsync(MongoFixture fixture)
        {
            Assert.True(CollectionMigrations.CurrentVersion >= 97);
            var value = new HttpData(await StoreTestData.CreateAsync(fixture), fixture);
            try {
                value.Base.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                await value.Rows("authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Warehouse, 101, AuthorizationStatus.Active, 1)));
                value.actors[Author] = await value.Base.AddActorAsync(Author, CatalogAuthoringTestData.Shop, Permissions.CatalogManage);
                value.actors[Maker] = await value.Base.AddActorAsync(Maker, Location, Permissions.StockConvert);
                value.actors[Reviewer] = await value.Base.AddActorAsync(Reviewer, Location, Permissions.StockConvert);
                value.actors[StockOnly] = await value.Base.AddActorAsync(StockOnly, Location, Permissions.StockRead, Permissions.StockCount);
                value.actors[Foreign] = await value.Base.AddActorAsync(Foreign, CatalogAuthoringTestData.SecondWarehouse, Permissions.StockConvert);
                await value.Rows("identities").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Set("securityVersion", 1L));
                await PersonTestData.SeedDistinctPeopleAsync(value.Base.Database, value.Base.Clock, Maker, Reviewer, StockOnly, Foreign);
                var catalog = new CatalogAuthoringService(value.Base.Database, value.unit, new(value.Base.Database), value.Base.Clock);
                var category = await catalog.SaveCategoryAsync(value.actors[Author], new(CatalogAuthoringTestData.Shop, null, 0, "CONVERT", "Kemasan lokal"), Meta("category"));
                var family = await catalog.CreateFamilyAsync(value.actors[Author], new(CatalogAuthoringTestData.Shop, category.Id, "Pensil untuk aturan kemasan", "Pemeriksaan lokal", []), Meta("family"));
                value.Sealed = await catalog.CreateSkuAsync(value.actors[Author], new(family.Id, family.Version, "CONVERT-PENCIL-12", "private-conversion-physical-item",
                    new("Dus segel isi 12", "sealed", "piece", 12, ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal), [], 1, 1)), Meta("sealed"));
                var currentFamily = await value.Rows("productFamilies").Find(new BsonDocument("_id", family.Id.Value)).SingleAsync();
                value.Loose = await catalog.CreateSkuAsync(value.actors[Author], new(family.Id, currentFamily["revision"].AsInt64, "CONVERT-PENCIL-1", "private-conversion-physical-item",
                    new("Satuan lepas", "loose", "piece", 1, ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal), [], 1, 1)), Meta("loose"));
                return value;
            } catch { await value.DisposeAsync(); throw; }
        }
        internal object Input() => new { location = LocationBody, sourceSkuId = Sealed.Id.ToString(), sourceSkuRevision = Sealed.Version.ToString(CultureInfo.InvariantCulture),
            sourceDefinitionId = Sealed.CurrentDefinitionId!.Value.ToString(), destinationSkuId = Loose.Id.ToString(), destinationSkuRevision = Loose.Version.ToString(CultureInfo.InvariantCulture),
            destinationDefinitionId = Loose.CurrentDefinitionId!.Value.ToString(), sourcePackagesPerStep = 1, destinationPackagesPerStep = 12 };
        internal object Preview() => new { purpose = "pack-conversion-rule-preview", input = Input() };
        internal IMongoCollection<BsonDocument> Rows(string name) => Base.Database.GetCollection<BsonDocument>(name);
        internal Task<long> Count(string name) => Rows(name).CountDocumentsAsync(new BsonDocument());
        internal async Task<BsonDocument> PhysicalAsync() {
            var result = new BsonDocument();
            foreach (var name in new[] { "inventoryPools", "stockBalances", "lots", "movements", "reservations", "orders", "shipments", "procurementStockReceipts",
                "journalEntries", "cashMovements", "paymentSubmissions", "paymentAllocations" })
                result[name] = new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
            return result;
        }
        internal async Task<Browser> OpenAsync(long? id)
        {
            var client = factory.CreateClient(new() { BaseAddress = new(Origin), AllowAutoRedirect = false });
            SessionReference? reference = null;
            if (id is { } value) {
                var actor = actors[value]; var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "conversion@example.invalid", true, "fixture",
                    Base.Clock.UtcNow, Base.Clock.UtcNow, "otp", "fixture");
                var issued = await unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic", ct));
                client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + issued.OpaqueToken);
                reference = issued.Session.Reference;
            }
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf"); return new(client, id ?? Maker, csrf.GetProperty("requestToken").GetString()!, reference);
        }
        private static AuthorizationCommandMetadata Meta(string key) => new("http-conversion-" + key, "Synthetic local catalogue definition", "conversion-http-fixture");
        public async ValueTask DisposeAsync() { factory.Dispose(); keys.Dispose(); await Base.DisposeAsync(); }
    }
    internal sealed class Browser(HttpClient client, long actorId, string csrf, SessionReference? reference) : IDisposable
    {
        internal HttpClient Client => client;
        internal SessionReference? Reference => reference;
        internal HttpRequestMessage Request(string suffix, object body, string? key = null, string? version = null) {
            var request = new HttpRequestMessage(HttpMethod.Post, Api + suffix) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", Origin); request.Headers.Add("X-CSRF-Token", csrf); request.Headers.Add("X-Expected-Actor-Id", actorId.ToString(CultureInfo.InvariantCulture));
            if (key is not null) request.Headers.Add("Idempotency-Key", key); if (version is not null) request.Headers.Add("If-Match", version); return request;
        }
        internal async Task<HttpResponseMessage> Post(string suffix, object body, string? key = null, string? version = null) {
            using var request = Request(suffix, body, key, version); return await client.SendAsync(request);
        }
        public void Dispose() => client.Dispose();
    }
}
