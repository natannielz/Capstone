using System.Net;
using System.Text.Json.Nodes;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed partial class AccessHttpTests
{
    [Fact]
    public async Task Business_catalog_keeps_suggestions_separate_from_materialized_authority()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(4201, new(GrantScopeKind.System), Permissions.AccessPropose);
        using var factory = Factory(data, actor);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/access/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Body(response);
        var roles = body["roles"]!.AsArray().ToDictionary(role => role!["id"]!.GetValue<string>(), role => role!);
        string[] Values(JsonNode role, string key) => role[key]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        Assert.Equal(6, roles.Count);
        Assert.Equal(new[] { "substitution.accept" }, Values(roles["buyer"], "suggestedPermissions"));
        Assert.Contains("order.accept", Values(roles["store-head"], "suggestedPermissions"));
        Assert.Contains("substitution.propose", Values(roles["store-staff"], "suggestedPermissions"));
        foreach (var role in roles.Values)
        {
            Assert.Equal(1, role["version"]!.GetValue<int>());
            Assert.DoesNotContain("order.spend.approve", Values(role, "suggestedPermissions"));
            Assert.Empty(Values(role, "suggestedPermissions").Intersect(Values(role, "permissions")));
        }
        Assert.Empty(Values(roles["finance"], "suggestedPermissions"));
        Assert.Empty(Values(roles["courier"], "suggestedPermissions"));
        Assert.Empty(Values(roles["system-admin"], "suggestedPermissions"));
        Assert.Contains("order.substitute", Values(roles["buyer"], "permissions"));
        Assert.DoesNotContain("substitution.accept", Values(roles["buyer"], "permissions"));
        Assert.DoesNotContain("order.accept", Values(roles["store-head"], "permissions"));

        var definitions = body["permissions"]!.AsArray().ToDictionary(permission => permission!["id"]!.GetValue<string>(), permission => permission!);
        Assert.Equal(new[] { "buyingBranch", "buyingDivision", "buyingOrganization" },
            Values(definitions["order.spend.approve"], "allowedScopes").Order().ToArray());
        Assert.Equal(new[] { "fulfillmentShop", "fulfillmentWarehouse" }, Values(definitions["order.accept"], "allowedScopes"));
        Assert.True(definitions["order.spend.approve"]["requiresFreshMfa"]!.GetValue<bool>());
        Assert.True(definitions["supplier.change.approve"]["requiresFreshMfa"]!.GetValue<bool>());
        using var current = await client.GetAsync("/api/v1/access/current");
        Assert.Equal(new[] { Permissions.AccessPropose }, Values((await Body(current))["grants"]![0]!, "permissions"));
    }
}
