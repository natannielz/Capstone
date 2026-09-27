using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed partial class AccessHttpTests
{
    private static JsonObject HistoricalDraft(DateTimeOffset now) => new()
    {
        ["userId"] = "5101", ["membershipId"] = "5101",
        ["permissions"] = new JsonArray(Permissions.HistoryRead),
        ["scope"] = new JsonObject { ["kind"] = "buyingBranch", ["organizationId"] = "10", ["branchId"] = "11" },
        ["effectiveAt"] = now.ToString("O")
    };

    [Fact]
    public async Task Historical_http_preview_proposal_approval_and_revocation_keep_explicit_read_only_scope()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var maker = await data.AddActorAsync(5100, new(GrantScopeKind.System), Permissions.AccessPropose);
        await data.AddActorAsync(5101, new(GrantScopeKind.BuyingOrganization, 10), Permissions.HistoryRead);
        var checker = await data.AddActorAsync(5102, new(GrantScopeKind.BuyingOrganization, 10), Permissions.AccessApprove, Permissions.AccessRevoke);
        await data.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 5102L),
            Builders<BsonDocument>.Update.Set("delegablePermissions", new BsonArray { Permissions.HistoryRead }));
        await data.Database.GetCollection<BsonDocument>("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 11L),
            Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
        using var makerFactory = Factory(data, maker); using var client = makerFactory.CreateClient();
        using var checkerFactory = Factory(data, checker); using var check = checkerFactory.CreateClient();
        var draft = HistoricalDraft(data.Clock.UtcNow);
        var preview = await client.PostAsync("/api/v1/access/historical-grants/preview", Json(draft));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var reviewed = await Body(preview);
        Assert.Equal("historical-read", reviewed["templateId"]!.GetValue<string>());
        Assert.Equal(new[] { Permissions.HistoryRead }, reviewed["permissions"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.Empty(reviewed["delegablePermissions"]!.AsArray());
        Assert.True(reviewed["requiresIndependentApproval"]!.GetValue<bool>());
        var body = new JsonObject { ["grant"] = draft, ["reason"] = "Review suspended branch history" };
        var response = await Send(client, "/api/v1/access/historical-grants/proposals", body, "historical-proposal");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var proposal = await Body(response);
        var id = proposal["id"]!.GetValue<string>();
        Assert.Equal(id, (await Body(await Send(client, "/api/v1/access/historical-grants/proposals", body, "historical-proposal")))["id"]!.GetValue<string>());
        var reason = new JsonObject { ["reason"] = "Independent historical scope review" };
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, $"/api/v1/access/grants/proposals/{id}/approve", reason, "historical-self", "1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(check, $"/api/v1/access/grants/proposals/{id}/approve", reason, "historical-stale", "2")).StatusCode);
        var approved = await Send(check, $"/api/v1/access/grants/proposals/{id}/approve", reason, "historical-approved", "1");
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var grantId = (await Body(approved))["id"]!.GetValue<string>();
        var stored = await data.Database.GetCollection<BsonDocument>("authorizationGrants").Find(new BsonDocument("_id", long.Parse(grantId))).SingleAsync();
        Assert.Equal("historical-read", stored["templateId"].AsString);
        Assert.Equal(new BsonArray { Permissions.HistoryRead }, stored["permissions"]);
        var revoked = await Send(check, $"/api/v1/access/grants/{grantId}/revoke", reason, "historical-revoked", "1");
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
    }

    [Theory]
    [InlineData("financePacks")]
    [InlineData("delegablePermissions")]
    [InlineData("templateId")]
    public async Task Historical_http_rejects_fields_that_can_add_other_authority(string extra)
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var maker = await data.AddActorAsync(5100, new(GrantScopeKind.System), Permissions.AccessPropose);
        await data.AddActorAsync(5101, new(GrantScopeKind.BuyingOrganization, 10), Permissions.HistoryRead);
        using var factory = Factory(data, maker); using var client = factory.CreateClient();
        var draft = HistoricalDraft(data.Clock.UtcNow);
        draft[extra] = extra == "templateId" ? JsonValue.Create("system-admin") : new JsonArray("billing");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/access/historical-grants/preview", Json(draft))).StatusCode);
        Assert.Equal(0, await data.Database.GetCollection<BsonDocument>("authorizationProposals").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Historical_http_requires_reviewed_actor_and_never_accepts_numeric_ids_or_write_permissions()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var maker = await data.AddActorAsync(5100, new(GrantScopeKind.System), Permissions.AccessPropose);
        await data.AddActorAsync(5101, new(GrantScopeKind.BuyingOrganization, 10), Permissions.HistoryRead);
        using var factory = Factory(data, maker); using var client = factory.CreateClient();
        var draft = HistoricalDraft(data.Clock.UtcNow);
        var body = new JsonObject { ["grant"] = draft, ["reason"] = "Explicit history review" };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/access/historical-grants/proposals") { Content = Json(body) };
        request.Headers.Add("Idempotency-Key", "missing-reviewed-actor");
        var denied = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal("expected_actor_required", (await Body(denied))["code"]!.GetValue<string>());
        draft["userId"] = 5101;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/access/historical-grants/preview", Json(draft))).StatusCode);
        draft["userId"] = "5101";
        draft["permissions"] = new JsonArray(Permissions.OrderSubmit);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/access/historical-grants/preview", Json(draft))).StatusCode);
    }
    [Fact]
    public async Task Historical_target_query_is_scoped_to_current_recipient_parent_and_counts_before_paging()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var maker = await data.AddActorAsync(5200, new(GrantScopeKind.System), Permissions.AccessPropose);
        await data.AddActorAsync(5201, new(GrantScopeKind.BuyingOrganization, 10), Permissions.HistoryRead);
        await data.AddActorAsync(5202, new(GrantScopeKind.BuyingOrganization, 20), Permissions.HistoryRead);
        await data.Database.GetCollection<BsonDocument>("authorizationScopes").InsertOneAsync(
            UnitToko.Platform.Authorization.AuthorizationBson.Node(new ScopeNode(31, ScopeNodeKind.Division, 11, AuthorizationStatus.Suspended, 1)));
        await data.Database.GetCollection<BsonDocument>("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 11L),
            Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
        using var factory = Factory(data, maker); using var client = factory.CreateClient();
        var body = new JsonObject { ["userId"] = "5201", ["membershipId"] = "5201", ["offset"] = 0, ["limit"] = 2 };
        var response = await client.PostAsync("/api/v1/access/historical-targets/query", Json(body));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await Body(response);
        Assert.Equal("3", page["total"]!.GetValue<string>());
        Assert.Equal(2, page["items"]!.AsArray().Count);
        Assert.All(page["items"]!.AsArray(), item => Assert.Equal("10", item!["scope"]!["organizationId"]!.GetValue<string>()));
        Assert.Contains(page["items"]!.AsArray(), item => item!["id"]!.GetValue<string>() == "11" && item["status"]!.GetValue<string>() == "suspended");
        body["offset"] = 2;
        var child = Assert.Single((await Body(await client.PostAsync("/api/v1/access/historical-targets/query", Json(body))))["items"]!.AsArray())!;
        Assert.Equal("31", child["id"]!.GetValue<string>());
        Assert.Equal("11", child["scope"]!["branchId"]!.GetValue<string>());
        body["membershipId"] = "5202";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/access/historical-targets/query", Json(body))).StatusCode);
        body["membershipId"] = "5201";
        await data.Database.GetCollection<BsonDocument>("memberships").UpdateOneAsync(new BsonDocument("_id", 5201L),
            Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/access/historical-targets/query", Json(body))).StatusCode);
    }

}
