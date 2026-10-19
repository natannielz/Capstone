using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Identity;

/// <summary>Pins guarantees behind the owner-approved D01/D02 rules (docs/quality/report-decisions.md, 16 Oct 2026).</summary>
public sealed class ApprovedRuleConformanceTests
{
    // D02: the Super Admin administers (users, roles, configuration, audit) but performs no business work.
    // Fails if any business permission (e.g. order.submit, payment.allocate, journal.post) is added to the system-admin template.
    [Fact]
    public void System_admin_template_holds_only_system_scoped_administrative_permissions() {
        var template = RoleTemplates.Templates["system-admin"];
        Assert.NotEmpty(template.Permissions);
        foreach (var name in template.Permissions) {
            var definition = Permissions.Find(name);
            Assert.NotNull(definition);
            Assert.Equal([GrantScopeKind.System], definition.AllowedScopes.ToArray());
        }
    }

    // D02: approval of access/membership is a separate (delegated) permission the admin template never holds,
    // so an admin's proposal always needs a second person to approve it.
    [Fact]
    public void System_admin_cannot_approve_access_membership_or_any_business_decision() {
        var permissions = RoleTemplates.Templates["system-admin"].Permissions;
        foreach (var forbidden in new[] { Permissions.MembershipApprove, Permissions.AccessApprove, Permissions.AccessRevoke })
            Assert.DoesNotContain(forbidden, permissions);
        Assert.DoesNotContain(permissions, name => name.EndsWith(".approve", StringComparison.Ordinal) || name.EndsWith(".allocate", StringComparison.Ordinal) ||
            name.EndsWith(".post", StringComparison.Ordinal) || name.StartsWith("order.", StringComparison.Ordinal) || name.StartsWith("payment.", StringComparison.Ordinal));
    }
}
