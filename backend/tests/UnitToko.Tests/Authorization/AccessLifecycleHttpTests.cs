using System.Net;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed partial class AccessHttpTests
{
    [Fact]
    public async Task A_stale_tab_cannot_read_or_close_a_receipt_or_write_as_a_different_signed_in_actor()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessPropose);
        var newlySignedIn = await data.AddActorAsync(1002, new(GrantScopeKind.System), Permissions.AccessPropose);
        using var factory = Factory(data, newlySignedIn); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Expected-Actor-Id", "1000");
        var lookup = new JsonObject { ["operation"] = "access.grant.propose", ["key"] = "old-account-request", ["purpose"] = "operation-recovery" };
        var resolve = new JsonObject { ["operation"] = "access.grant.propose", ["reason"] = "Resolve stale tab" };
        foreach (var pair in new[] { ("/api/v1/access/operations/lookup", lookup), ("/api/v1/access/operations/resolve", resolve),
            ("/api/v1/access/grants/proposals", new JsonObject { ["grant"] = Grant(data.Clock.UtcNow), ["reason"] = "Stale draft" }) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, pair.Item1) { Content = Json(pair.Item2) };
            request.Headers.Add("Idempotency-Key", "old-account-request");
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("actor_changed", (await Body(response))["code"]!.GetValue<string>());
        }
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/v1/access/catalog")).StatusCode);
        foreach (var collection in new[] { "idempotencyRecords", "authorizationReadEvents", "authorizationProposals" })
            Assert.Equal(0L, await data.Database.GetCollection<BsonDocument>(collection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Http_rejection_and_withdrawal_preserve_independence_scope_version_and_retry_identity()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var maker = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessPropose);
        await data.AddActorAsync(1001, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var checker = await data.AddActorAsync(1002, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.AccessApprove);
        var outside = await data.AddActorAsync(1003, new(GrantScopeKind.BuyingBranch, 10, 12), Permissions.AccessApprove);
        using var makerFactory = Factory(data, maker); using var client = makerFactory.CreateClient();
        using var checkerFactory = Factory(data, checker); using var check = checkerFactory.CreateClient();
        using var outsideFactory = Factory(data, outside); using var other = outsideFactory.CreateClient();
        var proposalBody = new JsonObject { ["grant"] = Grant(data.Clock.UtcNow), ["reason"] = "Reviewed branch duties" };
        var submitted = await Body(await Send(client, "/api/v1/access/grants/proposals", proposalBody, "reject-example"));
        var id = submitted["id"]!.GetValue<string>();
        var decision = new JsonObject { ["reason"] = "The requested authority is not approved" };
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, $"/api/v1/access/proposals/{id}/reject", decision, "self-reject", "1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(other, $"/api/v1/access/proposals/{id}/reject", decision, "outside-reject", "1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(check, $"/api/v1/access/proposals/{id}/reject", decision, "wrong-version", "2")).StatusCode);
        var rejected = await Send(check, $"/api/v1/access/proposals/{id}/reject", decision, "reject-once", "1");
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal("rejected", (await Body(rejected))["status"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await Send(check, $"/api/v1/access/proposals/{id}/reject", decision, "reject-once", "1")).StatusCode);
        var second = await Body(await Send(client, "/api/v1/access/grants/proposals", proposalBody, "withdraw-example"));
        var withdrawal = await Send(client, $"/api/v1/access/proposals/{second["id"]!.GetValue<string>()}/withdraw", decision, "withdraw-once", "1");
        Assert.Equal(HttpStatusCode.OK, withdrawal.StatusCode);
        Assert.Equal("withdrawn", (await Body(withdrawal))["status"]!.GetValue<string>());
        Assert.Equal(1L, await data.Database.GetCollection<BsonDocument>("authorizationGrants").CountDocumentsAsync(new BsonDocument("userId", 1001L)));
    }

    [Fact]
    public async Task Http_receipts_recover_commits_and_close_absent_commands_without_late_effects_or_other_actor_leaks()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessPropose);
        var another = await data.AddActorAsync(1002, new(GrantScopeKind.System), Permissions.AccessPropose);
        await data.AddActorAsync(1001, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        using var factory = Factory(data, actor); using var client = factory.CreateClient();
        using var secondFactory = Factory(data, another); using var second = secondFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Expected-Actor-Id", "1000");
        second.DefaultRequestHeaders.Add("X-Expected-Actor-Id", "1002");
        var body = new JsonObject { ["grant"] = Grant(data.Clock.UtcNow), ["reason"] = "Branch procurement duties" };
        var proposal = await Body(await Send(client, "/api/v1/access/grants/proposals", body, "interrupted"));
        var lookup = new JsonObject { ["operation"] = "access.grant.propose", ["key"] = "interrupted", ["purpose"] = "operation-recovery" };
        var receipt = await client.PostAsync("/api/v1/access/operations/lookup", Json(lookup));
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        Assert.True(receipt.Headers.CacheControl?.NoStore);
        var found = await Body(receipt);
        Assert.Equal("committed", found["state"]!.GetValue<string>());
        Assert.Equal(proposal["id"]!.GetValue<string>(), found["result"]!["id"]!.GetValue<string>());
        Assert.DoesNotContain("Branch procurement duties", found.ToJsonString());
        Assert.Equal("not_confirmed", (await Body(await second.PostAsync("/api/v1/access/operations/lookup", Json(lookup))))["state"]!.GetValue<string>());
        var resolve = new JsonObject { ["operation"] = "access.grant.propose", ["reason"] = "Resolve an interrupted request" };
        Assert.Equal("committed", (await Body(await Send(client, "/api/v1/access/operations/resolve", resolve, "interrupted")))["state"]!.GetValue<string>());
        Assert.Equal("closed", (await Body(await Send(client, "/api/v1/access/operations/resolve", resolve, "not-arrived")))["state"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(client, "/api/v1/access/grants/proposals", body, "not-arrived")).StatusCode);
        Assert.Equal(1L, await data.Database.GetCollection<BsonDocument>("authorizationProposals").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Http_history_requires_an_approved_purpose_and_returns_only_scoped_projected_records()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var maker = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessPropose);
        await data.AddActorAsync(1001, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var checker = await data.AddActorAsync(1002, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.AccessApprove);
        var outsider = await data.AddActorAsync(1003, new(GrantScopeKind.BuyingBranch, 10, 12), Permissions.AccessApprove);
        using var makerFactory = Factory(data, maker); using var client = makerFactory.CreateClient();
        await Send(client, "/api/v1/access/grants/proposals", new JsonObject { ["grant"] = Grant(data.Clock.UtcNow), ["reason"] = "Documented branch duties" }, "history-source");
        using var checkerFactory = Factory(data, checker); using var read = checkerFactory.CreateClient();
        using var outsiderFactory = Factory(data, outsider); using var outside = outsiderFactory.CreateClient();
        var query = new JsonObject { ["purpose"] = "access-review", ["offset"] = 0, ["limit"] = 25, ["subjectId"] = "1001" };
        var response = await read.PostAsync("/api/v1/access/history/query", Json(query));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await Body(response);
        Assert.Equal("1", page["total"]!.GetValue<string>());
        var item = Assert.Single(page["items"]!.AsArray())!;
        Assert.Equal("1001", item["subjectId"]!.GetValue<string>());
        foreach (var forbidden in new[] { "issuer", "providerSubject", "before", "after", "permissions", "delegablePermissions" })
            Assert.False(item.AsObject().ContainsKey(forbidden));
        Assert.Equal("0", (await Body(await outside.PostAsync("/api/v1/access/history/query", Json(query))))["total"]!.GetValue<string>());
        query["purpose"] = "arbitrary-purpose";
        Assert.Equal(HttpStatusCode.BadRequest, (await read.PostAsync("/api/v1/access/history/query", Json(query))).StatusCode);
        Assert.True(await data.Database.GetCollection<BsonDocument>("authorizationReadEvents").CountDocumentsAsync(new BsonDocument("actorId", 1002L)) > 0);
    }

    [Fact]
    public async Task Http_organization_creation_requires_separate_approver_and_keeps_exact_reference_contracts()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var maker = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.TechnicalConfigure);
        var checker = await data.AddActorAsync(1002, new(GrantScopeKind.System), Permissions.OrganizationApprove);
        using var makerFactory = Factory(data, maker); using var client = makerFactory.CreateClient();
        using var checkerFactory = Factory(data, checker); using var check = checkerFactory.CreateClient();
        var draft = new JsonObject { ["change"] = new JsonObject { ["changeKind"] = "create", ["kind"] = "branch", ["parentId"] = "10",
            ["code"] = "TEST-BRANCH", ["name"] = "Synthetic test branch" }, ["reason"] = "Isolated HTTP lifecycle check" };
        var response = await Send(client, "/api/v1/organizations/proposals", draft, "org-propose");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = (await Body(response))["id"]!.GetValue<string>();
        var approve = new JsonObject { ["reason"] = "Synthetic independent approval" };
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, $"/api/v1/organizations/proposals/{id}/approve", approve, "org-self", "1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(check, $"/api/v1/organizations/proposals/{id}/approve", approve, "org-unversioned")).StatusCode);
        var accepted = await Send(check, $"/api/v1/organizations/proposals/{id}/approve", approve, "org-approve", "1");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var result = await Body(accepted);
        var scopeId = result["scopeId"]!.GetValue<string>();
        var directory = await client.GetAsync($"/api/v1/organizations/{scopeId}");
        Assert.True(directory.Headers.CacheControl?.NoStore);
        var node = await Body(directory);
        Assert.Equal("TEST-BRANCH", node["code"]!.GetValue<string>());
        Assert.Equal("10", node["parentId"]!.GetValue<string>());
        Assert.Equal("1", node["scopeVersion"]!.GetValue<string>());
        using var anonymousFactory = Factory(data, authenticated: false); using var anonymous = anonymousFactory.CreateClient();
        var denied = await anonymous.GetAsync("/api/v1/organizations");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.True(denied.Headers.CacheControl?.NoStore);
    }
}
