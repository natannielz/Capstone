using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed class StockSourcePermissionTests
{
    [Theory]
    [InlineData("stock.source.read")]
    [InlineData("stock.source.propose")]
    [InlineData("stock.source.approve")]
    public void Setup_capabilities_are_explicit_fulfillment_grants(string permission)
    {
        var definition = Permissions.Find(permission);
        Assert.NotNull(definition);
        Assert.Equal(new[] { GrantScopeKind.FulfillmentShop, GrantScopeKind.FulfillmentWarehouse }, definition.AllowedScopes.ToArray());
        foreach (var role in RoleTemplates.Templates.Values) Assert.DoesNotContain(permission, role.Permissions);
        Assert.Contains(permission, RoleTemplates.Materialize("store-head", [], [permission]).Permissions);
    }

    [Fact]
    public void Approval_requires_fresh_MFA_and_stored_independent_proposer()
    {
        const string permission = "stock.source.approve";
        Assert.Equal(TimeSpan.FromMinutes(5), Permissions.Find(permission)?.Assurance.MaximumAge);
        Assert.True(ApprovalPolicy.RequiresEvidence(permission));
        Assert.True(PersonApprovalPolicy.RequiresEvidence(permission));
        Assert.True(ApprovalPolicy.Evaluate(20, permission, new(1, 1, ProposedBy: 10)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(10, permission, new(1, 1, ProposedBy: 10)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(20, permission, new(1, 2, ProposedBy: 10)).Allowed);
        Assert.False(ApprovalPolicy.Evaluate(20, permission, new(1, 1)).Allowed);
        Assert.Equal(new long[] { 10, 20 }, PersonApprovalPolicy.RequiredAccounts(20, permission, new(1, 1, ProposedBy: 10)).ToArray());
    }
}
