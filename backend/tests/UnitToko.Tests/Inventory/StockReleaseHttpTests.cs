using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
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
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Identity;
using ReleaseData = UnitToko.Tests.Inventory.StockReleaseServiceTests.Data;

namespace UnitToko.Tests.Inventory;

/// <summary>Real API/session/activated-source/inspection/quality/posted-value owners in
/// isolated synthetic Mongo. No mocked release service or raw active flag is used.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class StockReleaseHttpTests(MongoFixture fixture)
{
    private const string Api = "/api/v1/inventory/stock-releases", Origin = "https://portal.unit-toko.co.id";
    [WindowsStorageFact]
    public async Task Exact_public_preview_independent_approval_fresh_session_original_key_replay_and_recovery_transfer_quantity_and_value_once()
    {
        await using var http = await HttpData.CreateAsync(fixture); var data = http.Data;
        var accounting = await data.Valuation.StateAsync(); var documents = await data.Valuation.Documents.StateAsync();
        using var maker = await http.OpenAsync(data.Maker); using var checker = await http.OpenAsync(data.Checker); using var foreign = await http.OpenAsync(data.Outsider);
        var location = JsonSerializer.SerializeToNode(http.Input())!["location"];
        foreach (var browser in new[] { maker, checker }) {
            var warehouses = await Ok(await browser.Post("/warehouses/query", new { purpose = "stock-release-warehouses" }));
            Assert.Contains(warehouses.GetProperty("items").EnumerateArray(), item => item.GetProperty("location").GetProperty("warehouseId").GetString() == "102");
            PrivateFieldsAbsent(warehouses);
        }
        var choices = await Ok(await maker.Post("/sources/query", new { purpose = "stock-release-sources", location }));
        var choice = Assert.Single(choices.GetProperty("items").EnumerateArray());
        Assert.Equal(data.Valuation.ReceiptId.ToString(CultureInfo.InvariantCulture), choice.GetProperty("receiptId").GetString());
        Assert.Equal("2", choice.GetProperty("releaseablePackages").GetString()); PrivateFieldsAbsent(choices);
        var preview = await Ok(await maker.Post("/preview/query", new { purpose = "stock-release-preview", input = http.Input() }));
        Assert.Equal("0", preview.GetProperty("expected").GetProperty("heldValueRevision").GetString());
        Assert.Equal("0", preview.GetProperty("expected").GetProperty("lotValueRevision").GetString());
        Assert.Equal("3", preview.GetProperty("stockBefore").GetProperty("heldPackages").GetString());
        Assert.Equal("2", preview.GetProperty("stockAfter").GetProperty("heldPackages").GetString());
        Assert.Equal("1", preview.GetProperty("stockAfter").GetProperty("availablePackages").GetString());
        Assert.Equal(data.QualityAllocationId.ToString(CultureInfo.InvariantCulture), preview.GetProperty("selection").GetProperty("qualityAllocationId").GetString());
        PrivateFieldsAbsent(preview);
        var command = new { input = http.Input(), expected = preview.GetProperty("expected"), binding = preview.GetProperty("binding").GetString(), reason = "Pelepasan lokal satu kemasan dengan nilai asal terpilih" };
        var proposalVersion = "\"" + preview.GetProperty("expected").GetProperty("balanceRevision").GetString() + "\"";
        foreach (var version in new string?[] { null, "\"01\"", "\"0\"", "\"9223372036854775807\"" }) {
            using var response = await maker.Post("/proposals", command, "never-proposed", version); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(0L, await data.Rows(StockReleaseBson.Proposals).CountDocumentsAsync(new BsonDocument()));
        var proposed = await Ok(await maker.Post("/proposals", command, "http-release-original", proposalVersion));
        var id = proposed.GetProperty("proposalId").GetString()!; Assert.Equal("proposed", proposed.GetProperty("status").GetString()); PrivateFieldsAbsent(proposed);
        var pending = await Ok(await checker.Post("/proposals/" + id + "/query", new { purpose = "stock-release-history" }));
        Assert.Equal("proposed", pending.GetProperty("status").GetString()); PrivateFieldsAbsent(pending);
        var history = await Ok(await checker.Post("/proposals/query", new { purpose = "stock-release-history", location }));
        Assert.Equal(id, Assert.Single(history.GetProperty("items").EnumerateArray()).GetProperty("proposalId").GetString()); PrivateFieldsAbsent(history);
        var reviewBody = new { purpose = "stock-release-review", action = "approve", policy = LotValuePolicy.ProportionalFloorPolicy };
        using (var response = await maker.Post("/proposals/" + id + "/review/query", reviewBody)) {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Equal("independent_person_required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        using (var response = await foreign.Post("/proposals/" + id + "/review/query", reviewBody)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var review = await Ok(await checker.Post("/proposals/" + id + "/review/query", reviewBody)); PrivateFieldsAbsent(review);
        Assert.Equal(review.GetProperty("expected").GetProperty("balanceRevision").GetString(), review.GetProperty("stockBefore").GetProperty("revision").GetString());
        Assert.Equal("3", review.GetProperty("stockBefore").GetProperty("heldPackages").GetString());
        Assert.Equal("2", review.GetProperty("stockAfter").GetProperty("heldPackages").GetString());
        var decision = new { policy = LotValuePolicy.ProportionalFloorPolicy, expected = review.GetProperty("expected"), binding = review.GetProperty("binding").GetString(),
            reason = "Peninjau independen memeriksa jumlah dan nilai lokal" };
        foreach (var version in new string?[] { null, "\"01\"", "\"0\"", "\"2\"" }) {
            using var response = await checker.Post("/proposals/" + id + "/approve", decision, "never-approved", version); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(0L, await data.Rows(StockReleaseBson.Decisions).CountDocumentsAsync(new BsonDocument()));
        var approved = await Ok(await checker.Post("/proposals/" + id + "/approve", decision, "http-release-approved", "\"1\""));
        Assert.Equal("approved", approved.GetProperty("status").GetString()); Assert.Equal("11250.00", approved.GetProperty("transferredValue").GetString()); PrivateFieldsAbsent(approved);
        Assert.Equal(2L, (await data.BalanceAsync())["held"].AsInt64);
        using var renewedMaker = await http.OpenAsync(data.Maker); using var renewedChecker = await http.OpenAsync(data.Checker);
        Assert.Equal(proposed.GetRawText(), (await Ok(await renewedMaker.Post("/proposals", command, "http-release-original", proposalVersion))).GetRawText());
        Assert.Equal(approved.GetRawText(), (await Ok(await renewedChecker.Post("/proposals/" + id + "/approve", decision, "http-release-approved", "\"1\""))).GetRawText());
        foreach (var (browser, operation, key, expected) in new[] { (renewedMaker, StockReleaseBson.Propose, "http-release-original", proposed),
            (renewedChecker, StockReleaseBson.Decide, "http-release-approved", approved) }) {
            var recovered = await Ok(await browser.Post("/operations/query", new { purpose = "operation-recovery", operation, key }));
            Assert.Equal("committed", recovered.GetProperty("state").GetString()); Assert.Equal(expected.GetRawText(), recovered.GetProperty("result").GetRawText()); PrivateFieldsAbsent(recovered);
        }
        var deniedReceipt = await Ok(await foreign.Post("/operations/query", new { purpose = "operation-recovery", operation = StockReleaseBson.Propose, key = "http-release-original" }));
        Assert.Equal("not_confirmed", deniedReceipt.GetProperty("state").GetString()); Assert.Equal(JsonValueKind.Null, deniedReceipt.GetProperty("result").ValueKind);
        var detail = await Ok(await renewedMaker.Post("/proposals/" + id + "/query", new { purpose = "stock-release-history" }));
        Assert.Equal("approved", detail.GetProperty("status").GetString()); Assert.Equal("approve", detail.GetProperty("decision").GetProperty("action").GetString()); PrivateFieldsAbsent(detail);
        var changed = JsonSerializer.SerializeToNode(command)!.AsObject(); changed["reason"] = "Alasan berubah dengan kunci yang sama";
        using (var response = await renewedMaker.Post("/proposals", changed, "http-release-original", proposalVersion)) Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        foreach (var name in new[] { StockReleaseBson.Proposals, StockReleaseBson.Decisions }) Assert.Equal(1L, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows("movements").CountDocumentsAsync(new BsonDocument("source.namespace", StockReleaseBson.Namespace)));
        Assert.Equal(1L, await data.Rows(LotValueStore.Transitions).CountDocumentsAsync(new BsonDocument()));
        foreach (var operation in new[] { StockReleaseBson.Propose, StockReleaseBson.Decide }) {
            Assert.Equal(1L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", operation)));
            foreach (var table in new[] { "auditEvents", "outboxEvents" }) Assert.Equal(1L, await data.Rows(table).CountDocumentsAsync(new BsonDocument("kind", operation)));
        }
        Assert.Equal(accounting, await data.Valuation.StateAsync()); Assert.Equal(documents, await data.Valuation.Documents.StateAsync());
    }
    [WindowsStorageFact]
    public async Task Strict_identifiers_zero_capable_versions_purposes_current_sessions_and_security_headers_refuse_without_release_or_value_changes()
    {
        await using var http = await HttpData.CreateAsync(fixture); var data = http.Data;
        var before = await data.BalanceAsync(); var accounting = await data.Valuation.StateAsync();
        using var maker = await http.OpenAsync(data.Maker); using var anonymous = await http.OpenAsync(null); using var foreign = await http.OpenAsync(data.Outsider);
        using var readOnly = await http.OpenAsync(data.Valuation.Reader);
        var valid = new { purpose = "stock-release-preview", input = http.Input() };
        using (var response = await anonymous.Post("/preview/query", valid)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        foreach (var browser in new[] { foreign, readOnly }) {
            using var response = await browser.Post("/preview/query", valid); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        foreach (var fault in new[] { "numeric-id", "leading-zero", "unknown-input", "unknown-body", "zero-packages", "fraction-packages", "string-packages", "unknown-policy", "missing-purpose", "wrong-purpose" }) {
            var body = JsonSerializer.SerializeToNode(valid)!.AsObject(); var input = body["input"]!.AsObject();
            switch (fault) {
                case "numeric-id": input["receiptId"] = data.Valuation.ReceiptId; break;
                case "leading-zero": input["qualityAllocationId"] = "0" + data.QualityAllocationId; break;
                case "unknown-input": input["approved"] = true; break;
                case "unknown-body": body["people"] = new JsonArray(); break;
                case "zero-packages": input["packages"] = 0; break;
                case "fraction-packages": input["packages"] = 1.5; break;
                case "string-packages": input["packages"] = "1"; break;
                case "unknown-policy": input["policy"] = "guessed-value-policy"; break;
                case "missing-purpose": body.Remove("purpose"); break;
                case "wrong-purpose": body["purpose"] = "stock-count-history"; break;
            }
            using var response = await maker.Post("/preview/query", body); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var malformed in new[] { "0", "01", "-1", "9223372036854775808", "not-an-id" }) {
            using var response = await maker.Post("/proposals/" + malformed + "/query", new { purpose = "stock-release-history" }); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        var preview = await Ok(await maker.Post("/preview/query", valid));
        var proposalVersion = "\"" + preview.GetProperty("expected").GetProperty("balanceRevision").GetString() + "\"";
        var command = new { input = http.Input(), expected = preview.GetProperty("expected"), binding = preview.GetProperty("binding").GetString(), reason = "Uji batas pelepasan lokal" };
        foreach (var field in new[] { "poolFence", "heldValueRevision", "lotValueRevision" })
            foreach (var invalid in new JsonNode?[] { JsonValue.Create("00"), JsonValue.Create("-1"), JsonValue.Create(0), null }) {
                var body = JsonSerializer.SerializeToNode(command)!.AsObject(); body["expected"]![field] = invalid;
                using var response = await maker.Post("/proposals", body, "never-versioned", proposalVersion); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
        foreach (var fault in new[] { "origin", "csrf", "actor", "wrong-actor", "missing-key" }) {
            using var request = maker.Request("/proposals", command, fault == "missing-key" ? null : "never-written", proposalVersion); var expected = HttpStatusCode.Forbidden;
            if (fault == "origin") { request.Headers.Remove("Origin"); request.Headers.Add("Origin", "https://foreign.example.invalid"); }
            if (fault == "csrf") request.Headers.Remove("X-CSRF-Token");
            if (fault == "actor") { request.Headers.Remove("X-Expected-Actor-Id"); expected = HttpStatusCode.BadRequest; }
            if (fault == "wrong-actor") { request.Headers.Remove("X-Expected-Actor-Id"); request.Headers.Add("X-Expected-Actor-Id", data.Checker.UserId.ToString(CultureInfo.InvariantCulture)); expected = HttpStatusCode.Conflict; }
            if (fault == "missing-key") expected = HttpStatusCode.BadRequest;
            using var response = await maker.Client.SendAsync(request); Assert.Equal(expected, response.StatusCode);
        }
        using var expired = await http.OpenAsync(data.Maker);
        Assert.Equal(1L, (await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", expired.Reference!.Id),
            Builders<BsonDocument>.Update.Set("idleExpiresAt", data.Clock.UtcNow.UtcDateTime))).ModifiedCount);
        // This token was issued to the actual OTP principal. After session expiry,
        // antiforgery rejects that stale principal binding before endpoint authorization.
        using (var response = await expired.Post("/preview/query", valid)) {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("antiforgery_failed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.False((await expired.Client.GetFromJsonAsync<JsonElement>("/api/v1/identity/session")).GetProperty("authenticated").GetBoolean());
        await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", data.Maker.UserId), Builders<BsonDocument>.Update.Set("permissions", new BsonArray { Permissions.StockRead }));
        using (var response = await maker.Post("/preview/query", valid)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        foreach (var name in new[] { StockReleaseBson.Proposals, StockReleaseBson.Decisions, LotValueStore.Transitions }) Assert.Equal(0L, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await data.Rows("movements").CountDocumentsAsync(new BsonDocument("source.namespace", StockReleaseBson.Namespace)));
        Assert.Equal(before, await data.BalanceAsync()); Assert.Equal(accounting, await data.Valuation.StateAsync());
    }
    private static async Task<JsonElement> Ok(HttpResponseMessage response) {
        using (response) {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
            if (response.RequestMessage?.RequestUri?.AbsolutePath is { } path && !path.EndsWith("/operations/query", StringComparison.Ordinal) &&
                !path.EndsWith("/sources/query", StringComparison.Ordinal) && !path.EndsWith("/proposals/query", StringComparison.Ordinal) &&
                !path.EndsWith("/warehouses/query", StringComparison.Ordinal)) Assert.NotNull(response.Headers.ETag);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
        }
    }
    private static void PrivateFieldsAbsent(JsonElement value) {
        if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) {
            Assert.DoesNotContain(property.Name, new[] { "source", "people", "personId", "actorId", "actorGrantVersion", "issuer", "subject", "auditId", "journalId", "bodyHash", "snapshotHash",
                "requestKey", "causeHash", "proposalHash", "phase", "qualityJoin", "invoice", "match", "receiptHash", "valuationAllocationHash", "qualityAllocationHash" });
            PrivateFieldsAbsent(property.Value);
        } else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) PrivateFieldsAbsent(item);
    }
    private sealed class HttpData(ReleaseData data, WebApplicationFactory<Program> factory, IdentityRuntimeTests.RuntimeKeys keys,
        MongoIdentitySessionStore sessions) : IAsyncDisposable
    {
        internal ReleaseData Data => data;
        internal static async Task<HttpData> CreateAsync(MongoFixture fixture) {
            var data = await ReleaseData.CreateAsync(fixture); var keys = new IdentityRuntimeTests.RuntimeKeys();
            try {
                await data.Rows("identities").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Set("securityVersion", 1L));
                var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture", "fixture"),
                    SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
                var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
                var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
                    services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
                    services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
                    services.RemoveAll<StockReleaseService>(); services.AddSingleton(data.NewService());
                }));
                return new(data, factory, keys, sessions);
            } catch { keys.Dispose(); await data.DisposeAsync(); throw; }
        }
        internal object Input() {
            var input = data.Input(); return new { location = new { kind = "fulfillmentWarehouse", organizationId = input.Location.OrganizationId.ToString(),
                shopId = input.Location.ShopId.ToString(), warehouseId = input.Location.WarehouseId.ToString(), channelId = input.Location.ChannelId.ToString(), branchId = (string?)null, divisionId = (string?)null },
                receiptId = input.ReceiptId.ToString(CultureInfo.InvariantCulture), qualityAllocationId = input.QualityAllocationId.ToString(CultureInfo.InvariantCulture),
                valuationAllocationId = input.ValuationAllocationId.ToString(CultureInfo.InvariantCulture), packages = checked((int)input.Packages), policy = input.Policy };
        }
        internal async Task<Browser> OpenAsync(ActorContext? actor) {
            var client = factory.CreateClient(new() { BaseAddress = new(Origin), AllowAutoRedirect = false }); SessionReference? reference = null;
            if (actor is not null) {
                var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "release@example.invalid", true, "fixture", data.Clock.UtcNow, data.Clock.UtcNow, "otp", "fixture");
                var issued = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic", ct));
                client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + issued.OpaqueToken); reference = issued.Session.Reference;
            }
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf"); return new(client, actor?.UserId ?? data.Maker.UserId, csrf.GetProperty("requestToken").GetString()!, reference);
        }
        public async ValueTask DisposeAsync() { factory.Dispose(); keys.Dispose(); await data.DisposeAsync(); }
    }
    private sealed class Browser(HttpClient client, long actor, string csrf, SessionReference? reference) : IDisposable {
        internal HttpClient Client => client; internal SessionReference? Reference => reference;
        internal HttpRequestMessage Request(string suffix, object body, string? key = null, string? version = null) {
            var request = new HttpRequestMessage(HttpMethod.Post, Api + suffix) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", Origin); request.Headers.Add("X-CSRF-Token", csrf); request.Headers.Add("X-Expected-Actor-Id", actor.ToString(CultureInfo.InvariantCulture));
            if (key is not null) request.Headers.Add("Idempotency-Key", key); if (version is not null) request.Headers.Add("If-Match", version); return request;
        }
        internal async Task<HttpResponseMessage> Post(string suffix, object body, string? key = null, string? version = null) { using var request = Request(suffix, body, key, version); return await client.SendAsync(request); }
        public void Dispose() => client.Dispose();
    }
}
