using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed class StockReleaseApprovalPolicyTests
{
    [Fact]
    public void Release_requires_explicit_current_warehouse_permission_fresh_MFA_and_independent_verified_person()
    {
        var permission = Assert.IsType<PermissionDefinition>(Permissions.Find(Permissions.StockReleaseApprove));
        Assert.Contains(GrantScopeKind.FulfillmentWarehouse, permission.AllowedScopes);
        Assert.DoesNotContain(GrantScopeKind.BuyingBranch, permission.AllowedScopes);
        Assert.Equal(TimeSpan.FromMinutes(5), permission.Assurance.MaximumAge);
        var proposed = new ApprovalRequest(1, 1, ProposedBy: 12);
        Assert.False(ApprovalPolicy.Evaluate(12, Permissions.StockReleaseApprove, proposed).Allowed);
        Assert.True(ApprovalPolicy.Evaluate(13, Permissions.StockReleaseApprove, proposed).Allowed);
        Assert.Equal(new long[] { 12, 13 }, PersonApprovalPolicy.RequiredAccounts(13, Permissions.StockReleaseApprove, proposed));
        Assert.True(PersonApprovalPolicy.RequiredAccounts(13, Permissions.StockReleaseApprove, null).IsDefault);
        Assert.False(PersonApprovalPolicy.Evaluate(13, Permissions.StockReleaseApprove, proposed,
            [new(12, 42, PersonVerification.Verified, 1), new(13, 42, PersonVerification.Verified, 1)]).Allowed);
        Assert.True(PersonApprovalPolicy.Evaluate(13, Permissions.StockReleaseApprove, proposed,
            [new(12, 42, PersonVerification.Verified, 1), new(13, 43, PersonVerification.Verified, 1)]).Allowed);
        Assert.DoesNotContain(Permissions.StockReleaseApprove, RoleTemplates.Templates["system-admin"].Permissions);
        Assert.DoesNotContain(Permissions.StockReleaseApprove, RoleTemplates.Templates["finance"].Permissions);
    }
}
