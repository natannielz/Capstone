using System.Net;
using System.Text.RegularExpressions;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Tests.Authorization;

/// <summary>Pages decide what to offer from GET /api/v1/access/current. Every permission a page checks must be one the endpoint can
/// emit to someone who holds it, or the page silently hides a function its user is allowed to use (F-001).</summary>
public sealed partial class AccessHttpTests
{
    [Fact]
    public async Task Current_access_offers_invitations_and_recovery_to_the_administrator_who_holds_them()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var admin = await data.AddActorAsync(7091, new(GrantScopeKind.System), Permissions.InviteManage, Permissions.MembershipPropose,
            Permissions.RecoveryInitiate, Permissions.AccessAuditRead);
        var other = await data.AddActorAsync(7092, new(GrantScopeKind.System), Permissions.AccessAuditRead);
        async Task<string[]> Actions(ActorContext actor) {
            using var factory = Factory(data, actor); using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/access/current");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await Body(response))["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()).Order().ToArray();
        }
        Assert.Equal(new[] { Permissions.AccessAuditRead, Permissions.InviteManage, Permissions.MembershipPropose, Permissions.RecoveryInitiate }.Order(),
            await Actions(admin));
        // Recovery needs a fresh factor; an aged sign-in keeps the entry so the page can ask for verification instead of hiding it.
        Assert.Contains(Permissions.RecoveryInitiate, await Actions(admin with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-30) }));
        Assert.Equal(new[] { Permissions.AccessAuditRead }, await Actions(other));
    }

    /// <summary>G-032: pages read catalog.read as "may browse the organisation catalog". Staff hold shop-scoped catalog.read for their own
    /// work but have no catalog entitlement, so the buyer catalog refuses them; it is offered only to accounts the buyer catalog admits.</summary>
    [Fact]
    public async Task Catalog_reading_is_offered_only_to_accounts_the_buyer_catalog_admits()
    {
        await using var catalog = await UnitToko.Tests.Catalog.CatalogAuthoringTestData.CreateAsync(fixture);
        await catalog.AssortmentsAsync();
        async Task<string[]> Actions(ActorContext actor) {
            using var factory = Factory(catalog.Base, actor); using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/access/current");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await Body(response))["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        }
        Assert.Contains(Permissions.CatalogRead, await Actions(catalog.Buyer));
        var staff = await Actions(catalog.Maker);
        Assert.DoesNotContain(Permissions.CatalogRead, staff);
        Assert.Contains(Permissions.CatalogManage, staff);
    }

    /// <summary>G-039: person reviews need a configured trust adapter; none is configured in any release, so the review permissions are
    /// not offered and the page is not listed, instead of a listed page that always answers "not enabled".</summary>
    [Fact]
    public async Task Person_review_is_not_offered_while_the_review_service_is_not_enabled()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var reviewer = await data.AddActorAsync(7093, new(GrantScopeKind.System), Permissions.PersonReviewRead, Permissions.PersonReviewPropose,
            Permissions.AccessAuditRead);
        using var factory = Factory(data, reviewer); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/access/current");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actions = (await Body(response))["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { Permissions.AccessAuditRead }, actions);
    }

    [Fact]
    public void Every_permission_a_page_references_can_be_discovered()
    {
        var root = Root();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "frontend", "src"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".ts", StringComparison.Ordinal) || path.EndsWith(".tsx", StringComparison.Ordinal))
            .Where(path => !path.Contains(Path.Combine("api", "generated"), StringComparison.Ordinal) && Path.GetFileName(path) != "permission-labels.ts")
            .ToDictionary(path => path, File.ReadAllText);
        var literal = new Regex("['\"]([a-z]+(?:\\.[a-z]+)+)['\"]", RegexOptions.CultureInvariant);
        var referenced = sources.Values.SelectMany(text => literal.Matches(text).Select(match => match.Groups[1].Value))
            .Where(name => Permissions.Find(name) is not null).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(Permissions.InviteManage, referenced); Assert.Contains(Permissions.RecoveryInitiate, referenced);
        var discoverable = AccessEndpoints.DiscoverablePermissions.ToHashSet(StringComparer.Ordinal);
        // Scope capabilities (branch.pic, organization.wide) appear only where a grant is composed, never as a page task.
        Assert.Empty(referenced.Where(name => !discoverable.Contains(name) && !AccessEndpoints.ScopeCapabilities.Contains(name)).Order(StringComparer.Ordinal));
        // A page gate that names no permission can never open: every direct actions.includes('…') literal is a real permission.
        var gate = new Regex("ctions\\.includes\\(['\"]([a-z]+(?:\\.[a-z]+)+)['\"]\\)", RegexOptions.CultureInvariant);
        var gates = sources.Values.SelectMany(text => gate.Matches(text).Select(match => match.Groups[1].Value)).Distinct().ToArray();
        Assert.NotEmpty(gates);
        Assert.DoesNotContain(gates, name => Permissions.Find(name) is null);
        Assert.DoesNotContain(gates, name => AccessEndpoints.ScopeCapabilities.Contains(name));
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx")))
                return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
