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
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Identity;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed partial class StockSourceSetupHttpTests(MongoFixture fixture)
{
    private const string Api = "/api/v1/inventory/sources", Origin = "https://portal.unit-toko.co.id";
    private const long Maker = 47001, Reviewer = 47002, Reader = 47003;
    private static readonly StockLocation Location = new(new(100), new(101), new(102), new(103));
    private static object LocationBody => new { kind = "fulfillmentWarehouse", organizationId = "100",
        shopId = "101", warehouseId = "102", channelId = "103", branchId = (string?)null, divisionId = (string?)null };
    private static object Command => new { location = LocationBody, physicalKey = "synthetic-pool", label = "Synthetic warehouse source",
        mode = "webLedger", control = "exclusiveWebAllocation", maximumAgeMilliseconds = "60000", safetyBufferBaseUnits = "0",
        externalNamespace = (string?)null, reservationProtocolReference = (string?)null,
        discrepancyOwnerReference = "synthetic-owner", handoffReference = "synthetic-handoff", reason = "Synthetic setup request" };
    private static object Review => new { reviewReference = "synthetic-review", reason = "Synthetic independent review" };
    private static object Query => new { purpose = "inventory-source-setup", location = LocationBody };

    [Fact]
    public async Task Authenticated_source_workflow_preserves_large_ids_and_recovers_without_read_permission()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.Rows("idCounters").UpdateOneAsync(new BsonDocument("_id", "stockSourceProposals"),
            Builders<BsonDocument>.Update.Set("value", 9007199254740992L), new UpdateOptions { IsUpsert = true });
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var reader = await data.OpenAsync(Reader);
        var proposed = await Ok(await maker.Post("", Command, "propose"));
        Assert.Equal("9007199254740993", proposed.GetProperty("proposalId").GetString());
        Assert.Equal("proposed", proposed.GetProperty("status").GetString());
        Assert.Equal(proposed.GetRawText(), (await Ok(await maker.Post("", Command, "propose"))).GetRawText());
        using var noRead = await maker.Post("/query", Query);
        Assert.Equal(HttpStatusCode.Forbidden, noRead.StatusCode);
        var recovered = await Ok(await maker.Post("/operations/query", new
            { purpose = "operation-recovery", location = LocationBody, operation = "stock.source.propose", key = "propose" }));
        Assert.Equal("committed", recovered.GetProperty("state").GetString());
        Assert.Equal(proposed.GetRawText(), recovered.GetProperty("result").GetRawText());
        var id = proposed.GetProperty("proposalId").GetString();
        var approved = await Ok(await reviewer.Post("/" + id + "/approve", Review, "approve", "\"1\""));
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        Assert.False((await data.Rows("inventoryPools").Find(new BsonDocument()).SingleAsync())["active"].AsBoolean);
        var detail = await Ok(await reader.Post("/" + id + "/query", Query));
        Assert.Equal("approved", detail.GetProperty("status").GetString());
        Assert.Equal("0", detail.GetProperty("safetyBufferBaseUnits").GetString());
        foreach (var hidden in new[] { "proposedBy", "decidedBy", "personApproval", "auditId", "bodyHash", "issuer", "subject", "record" })
            Assert.False(detail.TryGetProperty(hidden, out _));
        var list = await Ok(await reader.Post("/query", Query));
        Assert.Equal("1", list.GetProperty("total").GetString()); Assert.Single(list.GetProperty("items").EnumerateArray());
        var retry = await Ok(await reviewer.Post("/operations/query", new
            { purpose = "operation-recovery", location = LocationBody, operation = "stock.source.approve", key = "approve" }));
        Assert.Equal(approved.GetRawText(), retry.GetProperty("result").GetRawText());
        Assert.Equal(1, await data.Rows("stockSourceProposals").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("stockSourceDecisions").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Mutation_requires_same_origin_csrf_actor_binding_and_idempotency()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var maker = await data.OpenAsync(Maker); using var anonymous = await data.OpenAsync(null);
        using var noSession = await anonymous.Post("", Command, "anonymous");
        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
        foreach (var fault in new[] { "csrf", "origin", "actor", "key" })
        {
            using var request = maker.Request("", Command, "guard");
            switch (fault)
            {
                case "csrf": request.Headers.Remove("X-CSRF-Token"); break;
                case "origin": request.Headers.Remove("Origin"); request.Headers.Add("Origin", "https://foreign.example"); break;
                case "actor": request.Headers.Remove("X-Expected-Actor-Id"); request.Headers.Add("X-Expected-Actor-Id", Reviewer.ToString(CultureInfo.InvariantCulture)); break;
                case "key": request.Headers.Remove("Idempotency-Key"); break;
            }
            using var response = await maker.Client.SendAsync(request);
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Forbidden, HttpStatusCode.Conflict });
        }
        Assert.Equal(0, await data.Rows("stockSourceProposals").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Input_contract_rejects_ambiguous_numbers_unknown_fields_and_missing_acceptance_facts()
    {
        await using var data = await HttpData.CreateAsync(fixture); using var maker = await data.OpenAsync(Maker);
        foreach (var fault in new[] { "numeric-id", "numeric-buffer", "leading-zero", "overflow", "missing-label", "null-location", "activation", "mode", "external" })
        {
            var body = JsonSerializer.SerializeToNode(Command)!.AsObject();
            switch (fault)
            {
                case "numeric-id": body["location"]!["warehouseId"] = 102; break;
                case "numeric-buffer": body["safetyBufferBaseUnits"] = 0; break;
                case "leading-zero": body["safetyBufferBaseUnits"] = "00"; break;
                case "overflow": body["maximumAgeMilliseconds"] = "9223372036854775808"; break;
                case "missing-label": body.Remove("label"); break;
                case "null-location": body["location"] = null; break;
                case "activation": body["active"] = true; break;
                case "mode": body["mode"] = "unconfirmed"; break;
                case "external": body["mode"] = "externalLedger"; body["control"] = "externalHold"; break;
            }
            using var response = await maker.Post("", body, "bad-" + fault);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(0, await data.Rows("stockSourceProposals").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Decision_requires_version_and_current_approval_grant_and_read_scope_cannot_bypass_them()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var reader = await data.OpenAsync(Reader);
        var id = (await Ok(await maker.Post("", Command, "propose"))).GetProperty("proposalId").GetString();
        using var noVersion = await reviewer.Post("/" + id + "/approve", Review, "no-version");
        Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);
        using var wrong = await reviewer.Post("/" + id + "/approve", Review, "wrong", "\"2\"");
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        using var noGrant = await reader.Post("/" + id + "/approve", Review, "read-only", "\"1\"");
        Assert.Equal(HttpStatusCode.Forbidden, noGrant.StatusCode);
        using var self = await maker.Post("/" + id + "/approve", Review, "self", "\"1\"");
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        var missing = await Ok(await maker.Post("/operations/query", new
            { purpose = "operation-recovery", location = LocationBody, operation = "stock.source.propose", key = "never-committed" }));
        Assert.Equal("not_confirmed", missing.GetProperty("state").GetString());
        Assert.Equal(0, await data.Rows("stockSourceDecisions").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Decision_does_not_disclose_missing_or_foreign_proposals_or_load_them_without_permission()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var maker = await data.OpenAsync(Maker); using var reader = await data.OpenAsync(Reader);
        var id = (await Ok(await maker.Post("", Command, "propose"))).GetProperty("proposalId").GetString();
        await data.Rows("authorizationScopes").InsertOneAsync(AuthorizationBson.Node(
            new ScopeNode(104, ScopeNodeKind.Warehouse, 101, AuthorizationStatus.Active, 1)));
        await data.AddActorAsync(47004, new(new(100), new(101), new(104), new(103)),
            Permissions.StockSourceApprove, Permissions.StockSourcePropose);
        using var foreign = await data.OpenAsync(47004);
        foreach (var suffix in new[] { "approve", "reject", "withdraw" })
        {
            using var existing = await foreign.Post("/" + id + "/" + suffix, Review, "foreign-" + suffix, "\"1\"");
            using var missing = await foreign.Post("/9223372036854775807/" + suffix, Review, "missing-" + suffix, "\"1\"");
            Assert.Equal(HttpStatusCode.Forbidden, existing.StatusCode);
            Assert.Equal(existing.StatusCode, missing.StatusCode);
            Assert.Equal(await Code(existing), await Code(missing));
        }
        using var deniedExisting = await reader.Post("/" + id + "/approve", Review, "reader-existing", "\"1\"");
        using var deniedMissing = await reader.Post("/9223372036854775807/approve", Review, "reader-missing", "\"1\"");
        Assert.Equal(HttpStatusCode.Forbidden, deniedExisting.StatusCode);
        Assert.Equal(deniedExisting.StatusCode, deniedMissing.StatusCode);
        Assert.Equal(await Code(deniedExisting), await Code(deniedMissing));
        Assert.Equal(0, await data.Rows("stockSourceDecisions").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Rejection_and_maker_withdrawal_are_terminal_recoverable_and_create_no_pool()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer);
        using var reader = await data.OpenAsync(Reader);
        foreach (var action in new[] { "reject", "withdraw" })
        {
            var id = (await Ok(await maker.Post("", Command, "propose-" + action))).GetProperty("proposalId").GetString();
            var actor = action == "reject" ? reviewer : maker;
            var result = await Ok(await actor.Post("/" + id + "/" + action, Review, action, "\"1\""));
            Assert.Equal(action == "reject" ? "rejected" : "withdrawn", result.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("physicalPoolId").ValueKind);
            var recovered = await Ok(await actor.Post("/operations/query", new
                { purpose = "operation-recovery", location = LocationBody, operation = "stock.source." + action, key = action }));
            Assert.Equal(result.GetRawText(), recovered.GetProperty("result").GetRawText());
            var detail = await Ok(await reader.Post("/" + id + "/query", Query));
            Assert.Equal(result.GetProperty("status").GetString(), detail.GetProperty("status").GetString());
            using var second = await reviewer.Post("/" + id + "/approve", Review, "second-" + action, "\"1\"");
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        }
        Assert.Equal(2, await data.Rows("stockSourceDecisions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("inventoryPools").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Approval_and_receipt_recovery_require_fresh_mfa_and_current_session()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer);
        var id = (await Ok(await maker.Post("", Command, "propose"))).GetProperty("proposalId").GetString();
        await Ok(await reviewer.Post("/" + id + "/approve", Review, "approve", "\"1\""));
        data.Base.Clock.UtcNow += TimeSpan.FromMinutes(5) + TimeSpan.FromMilliseconds(1);
        using var staleReplay = await reviewer.Post("/" + id + "/approve", Review, "approve", "\"1\"");
        using var staleRecovery = await reviewer.Post("/operations/query", new
            { purpose = "operation-recovery", location = LocationBody, operation = "stock.source.approve", key = "approve" });
        Assert.Equal(HttpStatusCode.Forbidden, staleReplay.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, staleRecovery.StatusCode);
        await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", Maker),
            Builders<BsonDocument>.Update.Set("status", "suspended").Inc("grantVersion", 1L));
        using var suspended = await maker.Post("/operations/query", new
            { purpose = "operation-recovery", location = LocationBody, operation = "stock.source.propose", key = "propose" });
        Assert.Contains(suspended.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Equal(1, await data.Rows("stockSourceProposals").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("stockSourceDecisions").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task History_filters_location_before_paging_and_detail_does_not_reveal_foreign_ids()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var maker = await data.OpenAsync(Maker); using var reader = await data.OpenAsync(Reader);
        var id = (await Ok(await maker.Post("", Command, "propose"))).GetProperty("proposalId").GetString();
        await data.Rows("authorizationScopes").InsertOneAsync(AuthorizationBson.Node(
            new ScopeNode(104, ScopeNodeKind.Warehouse, 101, AuthorizationStatus.Active, 1)));
        await data.AddActorAsync(47004, new(new(100), new(101), new(104), new(103)), Permissions.StockSourceRead);
        using var foreign = await data.OpenAsync(47004);
        var foreignQuery = JsonSerializer.SerializeToNode(Query)!.AsObject();
        foreignQuery["location"]!["warehouseId"] = "104";
        var page = await Ok(await foreign.Post("/query", foreignQuery));
        Assert.Equal("0", page.GetProperty("total").GetString()); Assert.Empty(page.GetProperty("items").EnumerateArray());
        var unavailable = await Ok(await foreign.Post("/" + id + "/query", foreignQuery));
        var missing = await Ok(await foreign.Post("/9223372036854775807/query", foreignQuery));
        Assert.Equal(JsonValueKind.Null, unavailable.ValueKind); Assert.Equal(unavailable.ValueKind, missing.ValueKind);
        using var wrongScope = await foreign.Post("/" + id + "/query", Query);
        Assert.Equal(HttpStatusCode.Forbidden, wrongScope.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Ok(await reader.Post("/9223372036854775807/query", Query))).ValueKind);
        foreach (var fault in new[] { "negative-offset", "large-limit", "string-offset", "wrong-purpose", "unknown-field" })
        {
            var query = JsonSerializer.SerializeToNode(Query)!.AsObject();
            switch (fault)
            {
                case "negative-offset": query["offset"] = -1; break;
                case "large-limit": query["limit"] = 101; break;
                case "string-offset": query["offset"] = "0"; break;
                case "wrong-purpose": query["purpose"] = "arbitrary"; break;
                case "unknown-field": query["actorId"] = Maker; break;
            }
            using var invalid = await reader.Post("/query", query);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
    }
    [Fact]
    public async Task Warehouse_choices_use_exact_source_permission_and_current_reviewed_names()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.AddProfilesAsync();
        using var maker = await data.OpenAsync(Maker); using var reviewer = await data.OpenAsync(Reviewer); using var reader = await data.OpenAsync(Reader);
        foreach (var (actor, purpose) in new[] { (maker, "inventory-source-propose"), (reviewer, "inventory-source-review"), (reader, "inventory-source-history") })
        {
            var result = await Ok(await actor.Post("/warehouses/query", new { purpose }));
            var item = Assert.Single(result.GetProperty("items").EnumerateArray());
            Assert.Equal("102", item.GetProperty("location").GetProperty("warehouseId").GetString());
            Assert.Equal("Synthetic node 102", item.GetProperty("warehouseName").GetString());
            Assert.Equal(5, item.EnumerateObject().Count());
        }
        foreach (var (actor, purpose) in new[] { (maker, "inventory-source-review"), (reviewer, "inventory-source-history"), (reader, "inventory-source-propose") })
        {
            using var denied = await actor.Post("/warehouses/query", new { purpose });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        await data.AddActorAsync(47004, Location, Permissions.StockCount, Permissions.StockRead);
        using var counter = await data.OpenAsync(47004);
        using var wrongCapability = await counter.Post("/warehouses/query", new { purpose = "inventory-source-propose" });
        Assert.Equal(HttpStatusCode.Forbidden, wrongCapability.StatusCode);
        using var invented = await maker.Post("/warehouses/query", new { purpose = "stock.source.propose", location = LocationBody });
        Assert.Equal(HttpStatusCode.BadRequest, invented.StatusCode);
        Assert.Equal(0, await data.Rows("stockSourceProposals").CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Warehouse_choices_hide_inactive_scope_and_reject_missing_or_incompatible_profiles()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        using var maker = await data.OpenAsync(Maker);
        using var absent = await maker.Post("/warehouses/query", new { purpose = "inventory-source-propose" });
        Assert.Equal(HttpStatusCode.Conflict, absent.StatusCode);
        await data.AddProfilesAsync();
        await data.Rows("organizationProfiles").UpdateOneAsync(new BsonDocument("_id", 102L),
            Builders<BsonDocument>.Update.Set("name", " leading space"));
        using var malformed = await maker.Post("/warehouses/query", new { purpose = "inventory-source-propose" });
        Assert.Equal(HttpStatusCode.Conflict, malformed.StatusCode);
        await data.Rows("organizationProfiles").UpdateOneAsync(new BsonDocument("_id", 102L),
            Builders<BsonDocument>.Update.Set("name", "Synthetic node 102"));
        await data.Rows("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 102L),
            Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
        using var suspended = await maker.Post("/warehouses/query", new { purpose = "inventory-source-propose" });
        Assert.Equal(HttpStatusCode.Forbidden, suspended.StatusCode);
    }

    [Fact]
    public async Task Shop_choice_expansion_keeps_channel_and_excludes_suspended_warehouses()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        await data.Rows("authorizationScopes").InsertManyAsync([
            AuthorizationBson.Node(new ScopeNode(104, ScopeNodeKind.Warehouse, 101, AuthorizationStatus.Active, 1)),
            AuthorizationBson.Node(new ScopeNode(105, ScopeNodeKind.Warehouse, 101, AuthorizationStatus.Suspended, 1)),
            AuthorizationBson.Node(new ScopeNode(106, ScopeNodeKind.Channel, 100, AuthorizationStatus.Active, 1))]);
        await data.Base.AddGrantAsync(await data.ActorAsync(Maker), 49999,
            new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103), [Permissions.StockSourcePropose]);
        await data.AddProfilesAsync();
        using var maker = await data.OpenAsync(Maker);
        var result = await Ok(await maker.Post("/warehouses/query", new { purpose = "inventory-source-propose" }));
        var items = result.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "102", "104" }, items.Select(item => item.GetProperty("location").GetProperty("warehouseId").GetString()));
        Assert.All(items, item => Assert.Equal("103", item.GetProperty("location").GetProperty("channelId").GetString()));
    }
    private static async Task<string?> Code(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }
    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, response.StatusCode + ": " + text);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var document = JsonDocument.Parse(text); return document.RootElement.Clone();
        }
    }
    [Fact]
    public async Task Current_account_discovers_source_tasks_without_granting_administration_and_keeps_MFA_entry_visible()
    {
        await using var data = await HttpData.CreateAsync(fixture);
        foreach (var (id, permission) in new[] { (Maker, Permissions.StockSourcePropose), (Reviewer, Permissions.StockSourceApprove), (Reader, Permissions.StockSourceRead) })
        {
            using var browser = await data.OpenAsync(id);
            var current = await browser.Client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
            Assert.Equal(new[] { permission }, current.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()).ToArray());
            using var catalog = await browser.Client.GetAsync("/api/v1/access/catalog");
            Assert.Equal(HttpStatusCode.Forbidden, catalog.StatusCode);
        }
        using var reviewer = await data.OpenAsync(Reviewer);
        data.Base.Clock.UtcNow = data.Base.Clock.UtcNow.AddMinutes(5).AddSeconds(1);
        var stale = await reviewer.Client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        Assert.Contains(Permissions.StockSourceApprove, stale.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()));
        using var denied = await reviewer.Post("/warehouses/query", new { purpose = "inventory-source-review" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", Reviewer),
            Builders<BsonDocument>.Update.Set("status", "revoked").Inc("version", 1L));
        var revoked = await reviewer.Client.GetFromJsonAsync<JsonElement>("/api/v1/access/current");
        Assert.Empty(revoked.GetProperty("allowedActions").EnumerateArray());
    }
    private sealed class HttpData : IAsyncDisposable
    {
        internal StoreTestData Base { get; }
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
            factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.ConfigureTestServices(services =>
            {
                services.RemoveAll<IClock>(); services.AddSingleton<IClock>(data.Clock);
                services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(sessions);
            }));
        }
        internal static async Task<HttpData> CreateAsync(MongoFixture fixture)
        {
            var data = new HttpData(await StoreTestData.CreateAsync(fixture), fixture);
            try
            {
                data.Base.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                data.actors.Add(Maker, await data.Base.AddActorAsync(Maker, Location.ToScope(), Permissions.StockSourcePropose));
                data.actors.Add(Reviewer, await data.Base.AddActorAsync(Reviewer, Location.ToScope(), Permissions.StockSourceApprove));
                data.actors.Add(Reader, await data.Base.AddActorAsync(Reader, Location.ToScope(), Permissions.StockSourceRead));
                await PersonTestData.SeedDistinctPeopleAsync(data.Base.Database, data.Base.Clock, Maker, Reviewer);
                await data.Rows("identities").UpdateManyAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
                return data;
            }
            catch { await data.DisposeAsync(); throw; }
        }
        internal Task<ActorContext> ActorAsync(long id) => Task.FromResult(actors[id]);
        internal async Task AddProfilesAsync()
        {
            foreach (var node in await Rows("authorizationScopes").Find(new BsonDocument("_id", new BsonDocument("$gte", 100L))).ToListAsync())
            {
                var id = node["_id"].AsInt64;
                await Rows("organizationProfiles").InsertOneAsync(new BsonDocument
                {
                    { "_id", id }, { "schemaVersion", 1 }, { "kind", node["kind"] }, { "parentId", node.GetValue("parentId", BsonNull.Value) },
                    { "code", "NODE-" + id }, { "normalizedCode", "NODE-" + id }, { "name", "Synthetic node " + id }, { "version", 1L },
                    { "createdBy", 998L }, { "updatedBy", 999L }, { "createdAt", Base.Clock.UtcNow.UtcDateTime },
                    { "updatedAt", Base.Clock.UtcNow.UtcDateTime }, { "approvedProposalId", id + 10000 }
                });
            }
        }
        internal async Task AddActorAsync(long id, StockLocation location, params string[] permissions)
        {
            actors.Add(id, await Base.AddActorAsync(id, location.ToScope(), permissions));
            await Rows("identities").UpdateOneAsync(new BsonDocument("_id", id), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
        }
        internal IMongoCollection<BsonDocument> Rows(string name) => Base.Database.GetCollection<BsonDocument>(name);
        internal async Task<Browser> OpenAsync(long? id)
        {
            var client = factory.CreateClient(new() { BaseAddress = new(Origin), AllowAutoRedirect = false });
            if (id is { } value)
            {
                var actor = actors[value];
                var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "source@example.test", true, "fixture",
                    Base.Clock.UtcNow, Base.Clock.UtcNow, "otp", "fixture");
                var issued = await unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx,
                    new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "synthetic", ct));
                client.DefaultRequestHeaders.Add("Cookie", OpaqueSessionCookie.Name + "=" + issued.OpaqueToken);
            }
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/identity/csrf");
            return new(client, id ?? Maker, csrf.GetProperty("requestToken").GetString()!);
        }
        public async ValueTask DisposeAsync() { factory.Dispose(); keys.Dispose(); await Base.DisposeAsync(); }
    }
    private sealed class Browser(HttpClient client, long actorId, string csrf) : IDisposable
    {
        internal HttpClient Client => client;
        internal HttpRequestMessage Request(string suffix, object body, string? key = null, string? version = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Api + suffix) { Content = JsonContent.Create(body) };
            request.Headers.Add("Origin", Origin); request.Headers.Add("X-CSRF-Token", csrf);
            request.Headers.Add("X-Expected-Actor-Id", actorId.ToString(CultureInfo.InvariantCulture));
            if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
            return request;
        }
        internal async Task<HttpResponseMessage> Post(string suffix, object body, string? key = null, string? version = null)
        { using var request = Request(suffix, body, key, version); return await client.SendAsync(request); }
        public void Dispose() => client.Dispose();
    }
}
