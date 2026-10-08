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
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Identity;
using B = UnitToko.Platform.Ordering.OrderCommercialPolicyBson;
using D = UnitToko.Tests.Ordering.OrderSpendingPolicyTestData;

namespace UnitToko.Tests.Ordering;

// Real signed application sessions, current organization approvals and commercial
// commands in isolated synthetic Mongo. No service mock or approved policy seed.
[Collection("Mongo")]
public sealed class OrderCommercialPolicyHttpTests(MongoFixture fixture)
{
    private const string Api = "/api/v1/ordering/commercial-policies", Origin = "https://portal.unit-toko.co.id", Purpose = "commercial-policy-review";
    [Fact]
    public async Task Signed_HTTP_choices_exact_decimal_bundle_independent_decision_history_and_original_key_recovery_are_public_and_private_cached()
    {
        await using var data = await HttpData.CreateAsync(fixture); using var maker = await data.OpenAsync(data.Maker); using var checker = await data.OpenAsync(data.Checker);
        var taskProjection = await maker.Client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        var actions = taskProjection.GetProperty("allowedActions").EnumerateArray().Select(action => action.GetString()).ToArray();
        foreach (var action in new[] { B.ReadPermission, B.ProposePermission, B.ApprovePermission }) Assert.Contains(action, actions);
        var sellers = await Ok(await maker.Post("/sellers/query", new { purpose = Purpose })); var seller = Assert.Single(sellers.GetProperty("items").EnumerateArray()).GetProperty("seller");
        var targets = await Ok(await maker.Post("/targets/query", new { purpose = Purpose, seller }));
        var choice = targets.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("target").GetProperty("id").GetString() == "11");
        var input = Input(choice, data.Existing.Clock.UtcNow); var preview = await Ok(await maker.Post("/preview/query", new { purpose = Purpose, input }));
        Assert.Equal("12.3456", preview.GetProperty("view").GetProperty("rule").GetProperty("taxRate").GetString());
        Assert.Equal("1.25", preview.GetProperty("view").GetProperty("rule").GetProperty("charges").GetString());
        var command = new { input, binding = preview.GetProperty("binding").GetString(), reason = "Synthetic Finance bundle review" };
        var proposed = await Ok(await maker.Post("/proposals", command, "commercial-http-original")); var id = proposed.GetProperty("proposalId").GetString()!;
        Assert.Equal(proposed.GetRawText(), (await Ok(await maker.Post("/proposals", command, "commercial-http-original"))).GetRawText());
        using (var denied = await maker.Post("/proposals/" + id + "/decision-preview/query", new { purpose = Purpose, decision = "approve" })) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var review = await Ok(await checker.Post("/proposals/" + id + "/decision-preview/query", new { purpose = Purpose, decision = "approve" }));
        var approved = await Ok(await checker.Post("/proposals/" + id + "/decisions", new { decision = "approve", binding = review.GetProperty("binding").GetString(), reason = "Synthetic independent Finance decision" }, "commercial-http-approved", "\"1\""));
        var current = await Ok(await maker.Post("/current/query", new { purpose = Purpose, target = choice.GetProperty("target") }));
        Assert.True(current.GetProperty("current").GetBoolean()); Assert.Equal(approved.GetProperty("versionId").GetString(), current.GetProperty("versionId").GetString());
        var history = await Ok(await maker.Post("/proposals/query", new { purpose = Purpose, target = choice.GetProperty("target") }));
        Assert.Equal(id, Assert.Single(history.GetProperty("items").EnumerateArray()).GetProperty("proposalId").GetString());
        var detail = await Ok(await checker.Post("/proposals/" + id + "/query", new { purpose = Purpose })); Assert.Equal("approved", detail.GetProperty("state").GetString());
        var receipt = await Ok(await maker.Post("/operations/query", new { purpose = "operation-recovery", operation = B.Prefix + "propose", key = "commercial-http-original" }));
        Assert.Equal("confirmed", receipt.GetProperty("status").GetString()); Assert.Equal(proposed.GetRawText(), receipt.GetProperty("result").GetRawText());
        var missing = await Ok(await checker.Post("/operations/query", new { purpose = "operation-recovery", operation = B.Prefix + "approve", key = "commercial-http-original" }));
        Assert.Equal("not_confirmed", missing.GetProperty("status").GetString());
        foreach (var value in new[] { sellers, targets, preview, proposed, review, approved, current, history, detail, receipt }) PublicOnly(value);
        var normalizedInput = input.DeepClone().AsObject(); normalizedInput["expectedPolicyVersion"] = "1"; normalizedInput["rule"]!["taxRate"] = "12.3400";
        var normalizedPreview = await Ok(await maker.Post("/preview/query", new { purpose = Purpose, input = normalizedInput }));
        Assert.Equal("12.34", normalizedPreview.GetProperty("view").GetProperty("rule").GetProperty("taxRate").GetString());
        var normalizedProposal = await Ok(await maker.Post("/proposals", new { input = normalizedInput, binding = normalizedPreview.GetProperty("binding").GetString(),
            reason = "Synthetic exact-scale normalization regression" }, "commercial-http-normalized"));
        var normalizedId = normalizedProposal.GetProperty("proposalId").GetString()!;
        var normalizedDetail = await Ok(await maker.Post("/proposals/" + normalizedId + "/query", new { purpose = Purpose }));
        Assert.Equal("12.34", normalizedDetail.GetProperty("rule").GetProperty("taxRate").GetString()); PublicOnly(normalizedDetail);
        var stored = await data.Rows(B.Proposals).Find(new BsonDocument("_id", long.Parse(normalizedId, CultureInfo.InvariantCulture))).SingleAsync();
        Assert.Equal(12.3400m, Decimal128.ToDecimal(stored["details"]["rule"]["taxRate"].AsDecimal128));
        Assert.Equal(stored["snapshotHash"], B.Hash(stored));
        Assert.Equal(2L, await data.Rows(B.Proposals).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1L, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument()));
        await data.NoBusinessEffectsAsync();
    }
    [Fact]
    public async Task Float_rates_money_numeric_ids_missing_applicability_and_cookie_CSRF_actor_failures_cannot_create_any_commercial_record()
    {
        await using var data = await HttpData.CreateAsync(fixture); using var maker = await data.OpenAsync(data.Maker); using var anonymous = await data.OpenAsync(null);
        var seller = Assert.Single((await Ok(await maker.Post("/sellers/query", new { purpose = Purpose }))).GetProperty("items").EnumerateArray()).GetProperty("seller");
        var choice = (await Ok(await maker.Post("/targets/query", new { purpose = Purpose, seller }))).GetProperty("items").EnumerateArray().Single(item => item.GetProperty("target").GetProperty("id").GetString() == "11");
        var input = Input(choice, data.Existing.Clock.UtcNow);
        foreach (var fault in new[] { "rate-float", "money-number", "numeric-id", "missing-applicability", "unknown-field", "rate-exponent", "rate-scale", "days-string" }) {
            var changed = input.DeepClone().AsObject(); var rule = changed["rule"]!.AsObject();
            if (fault == "rate-float") rule["taxRate"] = 12.3456;
            if (fault == "money-number") rule["charges"] = 1.25;
            if (fault == "numeric-id") changed["target"]!["id"] = 11;
            if (fault == "missing-applicability") rule["applicability"]!.AsObject().Remove("creditHold");
            if (fault == "unknown-field") rule["approvedBy"] = "902";
            if (fault == "rate-exponent") rule["taxRate"] = "1e1";
            if (fault == "rate-scale") rule["taxRate"] = "12.34567";
            if (fault == "days-string") rule["terms"]!["termDays"] = "14";
            using var response = await maker.Post("/preview/query", new { purpose = Purpose, input = changed }); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var response = await anonymous.Post("/preview/query", new { purpose = Purpose, input })) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        foreach (var fault in new[] { "csrf", "actor", "wrong-actor" }) {
            using var request = maker.Request("/preview/query", new { purpose = Purpose, input });
            if (fault == "csrf") request.Headers.Remove("X-CSRF-Token");
            else { request.Headers.Remove("X-Expected-Actor-Id"); if (fault == "wrong-actor") request.Headers.Add("X-Expected-Actor-Id", data.Checker.UserId.ToString(CultureInfo.InvariantCulture)); }
            using var response = await maker.Client.SendAsync(request);
            Assert.Equal(fault == "csrf" ? HttpStatusCode.Forbidden : fault == "actor" ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict, response.StatusCode);
        }
        var freshPreview = await Ok(await maker.Post("/preview/query", new { purpose = Purpose, input }));
        data.Existing.Clock.UtcNow = data.Existing.Clock.UtcNow.AddMinutes(6);
        var agedProjection = await maker.Client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        var agedActions = agedProjection.GetProperty("allowedActions").EnumerateArray().Select(action => action.GetString()).ToArray();
        Assert.Contains(B.ProposePermission, agedActions); Assert.Contains(B.ApprovePermission, agedActions);
        using var sessionResponse = await maker.Client.GetAsync("/api/v1/identity/session");
        var session = await sessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(sessionResponse.StatusCode == HttpStatusCode.OK,
            "Actual session status=" + sessionResponse.StatusCode + "; code=" + (session.TryGetProperty("code", out var code) ? code.GetString() : "none"));
        var verification = session.GetProperty("stepUpOperations").EnumerateArray().Select(action => action.GetString()).ToArray();
        Assert.Contains(B.ProposePermission, verification); Assert.Contains(B.ApprovePermission, verification);
        using (var denied = await maker.Post("/proposals", new { input, binding = freshPreview.GetProperty("binding").GetString(), reason = "Synthetic aged Finance factor refusal" }, "commercial-aged-factor")) {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal("step_up_required", (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        foreach (var name in OrderCommercialPolicySchemas.Collections) Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
        await data.NoBusinessEffectsAsync();
    }
    [Fact]
    public async Task Requester_discovery_hides_pending_only_relationship_until_genuine_independent_bundle_approval()
    {
        await using var data = await HttpData.CreateAsync(fixture); using var maker = await data.OpenAsync(data.Maker);
        using var checker = await data.OpenAsync(data.Checker); using var reader = await data.OpenAsync(data.Reader);
        var readerProjection = await reader.Client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        var readerActions = readerProjection.GetProperty("allowedActions").EnumerateArray().Select(action => action.GetString()).ToArray();
        Assert.Contains(B.ReadPermission, readerActions); Assert.DoesNotContain(B.ProposePermission, readerActions); Assert.DoesNotContain(B.ApprovePermission, readerActions);
        var seller = Assert.Single((await Ok(await maker.Post("/sellers/query", new { purpose = Purpose }))).GetProperty("items").EnumerateArray()).GetProperty("seller");
        var choice = (await Ok(await maker.Post("/targets/query", new { purpose = Purpose, seller }))).GetProperty("items").EnumerateArray().Single(item => item.GetProperty("target").GetProperty("id").GetString() == "11");
        var input = Input(choice, data.Existing.Clock.UtcNow); var preview = await Ok(await maker.Post("/preview/query", new { purpose = Purpose, input }));
        var proposal = await Ok(await maker.Post("/proposals", new { input, binding = preview.GetProperty("binding").GetString(), reason = "Synthetic pending-only buyer relationship" }, "commercial-pending-relationship"));
        Assert.Empty((await Ok(await reader.Post("/sellers/query", new { purpose = Purpose }))).GetProperty("items").EnumerateArray());
        Assert.Empty((await Ok(await reader.Post("/targets/query", new { purpose = Purpose, seller }))).GetProperty("items").EnumerateArray());
        var id = proposal.GetProperty("proposalId").GetString();
        var review = await Ok(await checker.Post("/proposals/" + id + "/decision-preview/query", new { purpose = Purpose, decision = "approve" }));
        await Ok(await checker.Post("/proposals/" + id + "/decisions", new { decision = "approve", binding = review.GetProperty("binding").GetString(), reason = "Synthetic independent relationship approval" }, "commercial-relationship-approved", "\"1\""));
        Assert.Single((await Ok(await reader.Post("/sellers/query", new { purpose = Purpose }))).GetProperty("items").EnumerateArray());
        var visible = Assert.Single((await Ok(await reader.Post("/targets/query", new { purpose = Purpose, seller }))).GetProperty("items").EnumerateArray());
        Assert.Equal("11", visible.GetProperty("target").GetProperty("id").GetString()); await data.NoBusinessEffectsAsync();
    }
    private static JsonObject Input(JsonElement choice, DateTimeOffset now) => new() {
        ["target"] = JsonNode.Parse(choice.GetProperty("target").GetRawText()), ["expectedScopeVersion"] = choice.GetProperty("expectedScopeVersion").GetString(),
        ["expectedProfileVersion"] = choice.GetProperty("expectedProfileVersion").GetString(), ["sellerVersions"] = JsonNode.Parse(choice.GetProperty("sellerVersions").GetRawText()),
        ["expectedPolicyVersion"] = choice.GetProperty("policyVersion").GetString(), ["validFrom"] = now.AddMinutes(-1).ToString("O"), ["validUntil"] = now.AddDays(1).ToString("O"),
        ["rule"] = new JsonObject { ["applicability"] = new JsonObject { ["budget"] = "required", ["costCenter"] = "not_applicable", ["spending"] = "required", ["creditHold"] = "required" },
            ["charges"] = "1.25", ["taxTreatment"] = "taxable_exclusive", ["taxRate"] = "12.3456", ["exemptionReference"] = null, ["taxBase"] = "subtotal_and_charges", ["taxRounding"] = "document", ["rounding"] = "half_even",
            ["terms"] = new JsonObject { ["grouping"] = "receipt", ["termOrigin"] = "issue_date", ["termDays"] = 14, ["dayRule"] = "calendar_days", ["workingDays"] = new JsonArray(), ["holidays"] = new JsonArray(),
                ["numberingProcedure"] = "Synthetic reviewed numbering procedure", ["reissueProcedure"] = "Synthetic linked reissue procedure", ["taxDocumentProcess"] = "Synthetic reviewed tax-document procedure" } } };
    private static async Task<JsonElement> Ok(HttpResponseMessage response) {
        using (response) { Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore); return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone(); }
    }
    private static void PublicOnly(JsonElement value) {
        if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) {
            Assert.DoesNotContain(property.Name, new[] { "source", "sellerSources", "people", "personId", "actorId", "actorGrantVersion", "issuer", "subject", "auditId", "bodyHash", "snapshotHash", "requestKey", "fence", "proposalHash" }); PublicOnly(property.Value);
        } else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) PublicOnly(item);
    }
    private sealed class HttpData(D existing, WebApplicationFactory<Program> factory, IdentityRuntimeTests.RuntimeKeys keys, MongoIdentitySessionStore sessions,
        ActorContext maker, ActorContext checker, ActorContext reader) : IAsyncDisposable
    {
        internal D Existing => existing; internal ActorContext Maker => maker; internal ActorContext Checker => checker; internal ActorContext Reader => reader;
        internal IMongoCollection<BsonDocument> Rows(string name) => existing.Rows(name);
        internal static async Task<HttpData> CreateAsync(MongoFixture fixture) {
            var data = await D.CreateAsync(fixture); var keys = new IdentityRuntimeTests.RuntimeKeys();
            try {
                var channel = await data.Organizations.Organizations.ProposeAsync(data.Organizations.ProfileMaker,
                    new(OrganizationChangeKind.UpdateProfile, ScopeNodeKind.Channel, 100, "RETAIL", "Synthetic retail channel", 103, 1, 0), D.Meta("commercial-http-channel"));
                await data.Organizations.Organizations.ApproveAsync(data.Organizations.ProfileChecker, channel.Id, 1, D.Meta("commercial-http-channel-approved"));
                var scope = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
                var maker = await data.Base.AddActorAsync(910, scope, B.ProposePermission, B.ApprovePermission, B.ReadPermission);
                var checker = await data.Base.AddActorAsync(911, scope, B.ApprovePermission, B.ReadPermission);
                var reader = await data.Base.AddActorAsync(912, new(GrantScopeKind.BuyingBranch, 10, 11), B.ReadPermission, Permissions.BranchPic);
                await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, 910, 911, 912);
                // Explicit synthetic contact bootstrap for the real own-profile
                // session projection; it supplies no commercial approval authority.
                foreach (var actor in new[] { maker, checker, reader }) await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument {
                    { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L }, { "displayName", "Synthetic commercial HTTP participant " + actor.UserId },
                    { "verifiedEmail", "commercial-http-" + actor.UserId + "@example.invalid" },
                    { "verifiedAt", data.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Clock.UtcNow.UtcDateTime }
                });
                await data.Rows("identities").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Set("securityVersion", 1L));
                var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture", "fixture"),
                    SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
                var settings = IdentityRuntimeTests.Settings(keys); settings["Mongo:Database"] = data.Database.DatabaseNamespace.DatabaseName;
                settings["Release:Profile"] = "full";
                var service = new OrderCommercialPolicyService(data.Database, data.Unit, new(data.Database), data.Clock, data.Organizations.Organizations);
                var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services => {
                    services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
                    services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
                    services.RemoveAll<OrderCommercialPolicyService>(); services.AddSingleton(service);
                }));
                return new(data, factory, keys, sessions, maker, checker, reader);
            } catch { keys.Dispose(); await data.DisposeAsync(); throw; }
        }
        internal async Task<Browser> OpenAsync(ActorContext? actor) {
            var client = factory.CreateClient(new() { BaseAddress = new(Origin), AllowAutoRedirect = false });
            if (actor is not null) {
                var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "commercial@example.invalid", true, "fixture", existing.Clock.UtcNow, existing.Clock.UtcNow, "otp", "fixture");
                var issued = await existing.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic", ct));
                client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + issued.OpaqueToken);
            }
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf"); return new(client, actor?.UserId ?? maker.UserId, csrf.GetProperty("requestToken").GetString()!);
        }
        internal async Task NoBusinessEffectsAsync() {
            foreach (var name in new[] { "orders", "invoices", "stockBalances", "reservations", "movements", "journalEntries", "creditVersions" }) Assert.False(await Rows(name).Find(new BsonDocument()).AnyAsync());
        }
        public async ValueTask DisposeAsync() { factory.Dispose(); keys.Dispose(); await existing.DisposeAsync(); }
    }
    private sealed class Browser(HttpClient client, long actor, string csrf) : IDisposable
    {
        internal HttpClient Client => client;
        internal HttpRequestMessage Request(string suffix, object body, string? key = null, string? revision = null) {
            var request = new HttpRequestMessage(HttpMethod.Post, Api + suffix) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", Origin); request.Headers.Add("X-CSRF-Token", csrf); request.Headers.Add("X-Expected-Actor-Id", actor.ToString(CultureInfo.InvariantCulture));
            if (key is not null) request.Headers.Add("Idempotency-Key", key); if (revision is not null) request.Headers.Add("If-Match", revision); return request;
        }
        internal async Task<HttpResponseMessage> Post(string suffix, object body, string? key = null, string? revision = null) { using var request = Request(suffix, body, key, revision); return await client.SendAsync(request); }
        public void Dispose() => client.Dispose();
    }
}
