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
        Assert.Equal(15, roles.Count);
        Assert.Equal(new[] { Permissions.BranchOrderRequestRead, Permissions.BranchOrderRequestSubmit, Permissions.SubstitutionAccept, Permissions.OrganizationAddressRead },
            Values(roles["buyer"], "suggestedPermissions"));
        Assert.Contains("order.accept", Values(roles["store-head"], "suggestedPermissions"));
        Assert.Contains("substitution.propose", Values(roles["store-staff"], "suggestedPermissions"));
        foreach (var role in roles.Values)
        {
            Assert.Equal(1, role["version"]!.GetValue<int>());
            // Spend approval is never granted by a template; only the branch approver job suggests it (F-006).
            if (role["id"]!.GetValue<string>() != "branch-approver") Assert.DoesNotContain("order.spend.approve", Values(role, "suggestedPermissions"));
            Assert.DoesNotContain("order.spend.approve", Values(role, "permissions"));
            Assert.Empty(Values(role, "suggestedPermissions").Intersect(Values(role, "permissions")));
        }
        // The finance template keeps growing with new finance work (funding, supplier banks, procurement evidence); the banking set must stay suggested.
        foreach (var permission in new[] { Permissions.BankReceiptRead, Permissions.BankReceiptConfirm, Permissions.BankStatementRead, Permissions.BankStatementStage,
            Permissions.PaymentReviewRead, Permissions.PaymentReviewCorrect, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead,
            Permissions.ReceivingAccountPropose })
            Assert.Contains(permission, Values(roles["finance"], "suggestedPermissions"));
        // The deciding side of receiving accounts is offered by the finance checker job, never beside its proposing side (F-006).
        foreach (var permission in new[] { Permissions.ReceivingAccountVerify, Permissions.ReceivingAccountApprove }) {
            Assert.DoesNotContain(permission, Values(roles["finance"], "suggestedPermissions"));
            Assert.Contains(permission, Values(roles["finance-checker"], "suggestedPermissions"));
        }
        Assert.Empty(Values(roles["finance"], "permissions"));
        Assert.Empty(RoleTemplates.Materialize("finance", [], []).Permissions);
        Assert.DoesNotContain(Permissions.PaymentVerify, Values(roles["finance"], "suggestedPermissions"));
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
        var currentBody = await Body(current);
        Assert.Equal(new[] { Permissions.AccessPropose }, Values(currentBody["grants"]![0]!, "permissions"));
        Assert.Equal(new[] { Permissions.AccessPropose }, Values(currentBody, "allowedActions"));
    }
}
