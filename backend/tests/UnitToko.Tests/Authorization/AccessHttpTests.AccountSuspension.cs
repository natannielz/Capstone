using System.Net;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed partial class AccessHttpTests
{
    [Fact]
    public async Task Subject_HTTP_actions_are_nonnull_server_owned_and_do_not_turn_scoped_revocation_into_account_suspension()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var system = await data.AddActorAsync(2400, new(GrantScopeKind.System), Permissions.AccessRevoke, Permissions.AccessAuditRead);
        var branch = await data.AddActorAsync(2401, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.AccessRevoke);
        var target = await data.AddActorAsync(2402, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        foreach (var actor in new[] { system, branch }) {
            using var factory = Factory(data, actor); using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Expected-Actor-Id", actor.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var response = await client.GetAsync("/api/v1/access/subjects/2402");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
            var body = await Body(response);
            var actions = body["allowedActions"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
            Assert.Equal(actor.UserId == system.UserId ? new[] { "suspend" } : [], actions);
            Assert.Equal("1", body["grantVersion"]!.GetValue<string>());
            using var self = await client.GetAsync("/api/v1/access/subjects/" + actor.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(HttpStatusCode.OK, self.StatusCode); Assert.Empty((await Body(self))["allowedActions"]!.AsArray());
        }
        await data.Administration.SuspendIdentityAsync(system, target.UserId, 1, new("http-disabled-target", "Reviewed account suspension", "account-suspension-http"));
        using var disabledFactory = Factory(data, system); using var disabledClient = disabledFactory.CreateClient();
        disabledClient.DefaultRequestHeaders.Add("X-Expected-Actor-Id", "2400");
        var disabled = await Body(await disabledClient.GetAsync("/api/v1/access/subjects/2402"));
        Assert.Equal("suspended", disabled["status"]!.GetValue<string>()); Assert.Empty(disabled["allowedActions"]!.AsArray());
        Assert.Equal("2", disabled["grantVersion"]!.GetValue<string>());
        var openapi = await Body(await disabledClient.GetAsync("/openapi/v1.json"));
        var subject = openapi["components"]!["schemas"]!["AccessSubjectDto"]!;
        Assert.Equal("array", subject["properties"]!["allowedActions"]!["type"]!.GetValue<string>());
        Assert.Contains("allowedActions", subject["required"]!.AsArray().Select(value => value!.GetValue<string>()));
    }
}
