using System.Collections.Immutable;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Organizations;

public sealed class OrganizationPolicyTests
{
    [Fact]
    public void Codes_are_canonical_and_names_bounded_without_hidden_controls()
    {
        var normalized = OrganizationChangePolicy.Normalize(new(OrganizationChangeKind.Create, ScopeNodeKind.Branch, 10, "  tebet-01  ", "  Kantor Tebet  "));
        Assert.Equal("TEBET-01", normalized.Code); Assert.Equal("Kantor Tebet", normalized.Name);
        foreach (var name in new[] { "", "\u202eMisleading", "line\nbreak", new string('x', 161) })
            Assert.Throws<DomainValidationException>(() => OrganizationChangePolicy.Normalize(normalized with { Name = name }));
        Assert.Throws<DomainValidationException>(() => OrganizationChangePolicy.Normalize(normalized with { Code = "A B" }));
    }

    [Fact]
    public void Existing_kind_parent_and_scope_are_immutable_and_all_ancestors_must_be_active()
    {
        var nodes = new ScopeDirectory([new(10, ScopeNodeKind.BuyingOrganization, null, AuthorizationStatus.Active, 1),
            new(11, ScopeNodeKind.Branch, 10, AuthorizationStatus.Active, 1), new(12, ScopeNodeKind.Division, 11, AuthorizationStatus.Active, 1)]);
        var request = new OrganizationChangeRequest(OrganizationChangeKind.UpdateProfile, ScopeNodeKind.Division, 11, "A", "A", 12, 1);
        Assert.Equal(3, OrganizationChangePolicy.ValidateHierarchy(request, nodes, nodes.Find(12, ScopeNodeKind.Division)).Length);
        Assert.Throws<DomainValidationException>(() => OrganizationChangePolicy.ValidateHierarchy(request with { ParentId = 10 }, nodes, nodes.Find(12, ScopeNodeKind.Division)));
        Assert.Throws<DomainValidationException>(() => OrganizationChangePolicy.ValidateHierarchy(request with { Kind = ScopeNodeKind.Branch }, nodes, nodes.Find(12, ScopeNodeKind.Division)));
        var suspended = new ScopeDirectory(nodes.Nodes.Select(node => node.Id == 10 ? node with { Status = AuthorizationStatus.Suspended } : node).ToImmutableArray());
        Assert.Throws<DomainValidationException>(() => OrganizationChangePolicy.ValidateHierarchy(request, suspended, suspended.Find(12, ScopeNodeKind.Division)));
    }

    [Fact]
    public void Organization_approval_is_explicit_System_and_fresh_MFA_without_default_role_inclusion()
    {
        var permission = Assert.IsType<PermissionDefinition>(Permissions.Find(Permissions.OrganizationApprove));
        Assert.Equal(GrantScopeKind.System, Assert.Single(permission.AllowedScopes));
        Assert.True(permission.Assurance.RequiresMfa);
        Assert.Equal(TimeSpan.FromMinutes(5), permission.Assurance.MaximumAge);
        Assert.DoesNotContain(RoleTemplates.Templates.Values.SelectMany(template => template.Permissions), permission => permission == Permissions.OrganizationApprove);
    }
}
