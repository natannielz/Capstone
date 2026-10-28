using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

public sealed class PersonWithdrawalPermissionTests
{
    [Fact]
    public void Review_capabilities_are_registered_but_never_enter_existing_role_templates()
    {
        foreach (var permission in new[] { Permissions.PersonReviewPropose, Permissions.PersonReviewApprove, Permissions.PersonReviewRead })
        {
            var definition = Assert.IsType<PermissionDefinition>(Permissions.Find(permission));
            Assert.Equal(new[] { GrantScopeKind.System }, definition.AllowedScopes.ToArray());
            Assert.All(RoleTemplates.Templates.Values, template => Assert.DoesNotContain(permission, template.Permissions));
            Assert.All(RoleTemplates.Suggestions.Values, suggestions => Assert.DoesNotContain(permission, suggestions));
            Assert.All(RoleTemplates.FinancePacks.Values, pack => Assert.DoesNotContain(permission, pack));
        }
        Assert.Equal(15, RoleTemplates.Templates.Count);
        Assert.NotNull(Permissions.Find(Permissions.PersonReviewPropose)!.Assurance.MaximumAge);
        Assert.NotNull(Permissions.Find(Permissions.PersonReviewApprove)!.Assurance.MaximumAge);
    }
}
