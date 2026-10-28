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

    /// <summary>Discovery fails closed: a malformed entitlement row only withholds catalog.read; it never fails the whole access read.</summary>
    [Fact]
    public async Task A_malformed_catalog_entitlement_withholds_catalog_reading_without_failing_the_access_read()
    {
        await using var catalog = await UnitToko.Tests.Catalog.CatalogAuthoringTestData.CreateAsync(fixture);
        await catalog.AssortmentsAsync();
        // Matches the eligibility filter (user, active, dates) but lacks the membership and approval fields the reader requires.
        await catalog.Rows("catalogEntitlements").InsertOneAsync(new MongoDB.Bson.BsonDocument {
            { "_id", 10002L }, { "userId", catalog.Maker.UserId }, { "status", "active" }, { "effectiveAt", catalog.Clock.UtcNow.UtcDateTime.AddDays(-1) },
            { "expiresAt", MongoDB.Bson.BsonNull.Value }, { "revokedAt", MongoDB.Bson.BsonNull.Value } },
            new MongoDB.Driver.InsertOneOptions { BypassDocumentValidation = true });
        using var factory = Factory(catalog.Base, catalog.Maker); using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/access/current");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actions = (await Body(response))["allowedActions"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        Assert.DoesNotContain(Permissions.CatalogRead, actions);
        Assert.Contains(Permissions.CatalogManage, actions);
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

    /// <summary>The drift test for F-001. Every permission a page references (outside the label table and the generated client) is
    /// granted alone, in one scope it allows, to an in-memory account with a fresh factor; CurrentActions must emit it. A hand-kept list
    /// that drops a permission fails here. Scope capabilities (branch.pic, organization.wide) only compose grants and are never emitted;
    /// the HTTP layer additionally withholds catalog.read without a catalog entitlement and person reviews while that service is off.</summary>
    [Fact]
    public void Every_permission_a_page_references_is_emitted_to_someone_who_holds_it()
    {
        var root = Root();
        var sources = Directory.EnumerateFiles(Path.Combine(root, "frontend", "src"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".ts", StringComparison.Ordinal) || path.EndsWith(".tsx", StringComparison.Ordinal))
            .Where(path => !path.Contains(Path.Combine("api", "generated"), StringComparison.Ordinal) && Path.GetFileName(path) != "permission-labels.ts")
            .ToDictionary(path => path, File.ReadAllText);
        var literal = new Regex("['\"`]([a-z]+(?:\\.[a-z]+)+)['\"`]", RegexOptions.CultureInvariant);
        var referenced = sources.Values.SelectMany(text => literal.Matches(text).Select(match => match.Groups[1].Value))
            .Where(name => Permissions.Find(name) is not null && !AccessEndpoints.ScopeCapabilities.Contains(name))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.Contains(Permissions.InviteManage, referenced); Assert.Contains(Permissions.RecoveryInitiate, referenced);
        var now = new DateTimeOffset(2026, 10, 23, 6, 0, 0, TimeSpan.Zero);
        var actor = new ActorContext(7101, "https://identity.example", "subject-7101", 1, now.AddMinutes(-1), FactorAssurance.MultiFactor);
        var directory = new ScopeDirectory([
            new(1, ScopeNodeKind.BuyingOrganization, null, AuthorizationStatus.Active, 1), new(11, ScopeNodeKind.Branch, 1, AuthorizationStatus.Active, 1),
            new(111, ScopeNodeKind.Division, 11, AuthorizationStatus.Active, 1), new(2, ScopeNodeKind.FulfillmentOrganization, null, AuthorizationStatus.Active, 1),
            new(21, ScopeNodeKind.Shop, 2, AuthorizationStatus.Active, 1), new(211, ScopeNodeKind.Warehouse, 21, AuthorizationStatus.Active, 1),
            new(201, ScopeNodeKind.Channel, 2, AuthorizationStatus.Active, 1)]);
        (GrantScope Scope, string[] Capabilities) For(GrantScopeKind kind) => kind switch {
            GrantScopeKind.FulfillmentShop => (new(kind, 2, ShopId: 21, ChannelId: 201), []),
            GrantScopeKind.FulfillmentWarehouse => (new(kind, 2, ShopId: 21, WarehouseId: 211, ChannelId: 201), []),
            GrantScopeKind.CourierAssignment => (new(kind, 2, ShopId: 21, ChannelId: 201), []),
            GrantScopeKind.System => (new(kind), []),
            GrantScopeKind.BuyerOwned => (new(kind, 1, 11, 111), []),
            GrantScopeKind.BuyingDivision => (new(kind, 1, 11, 111), [Permissions.BranchPic]),
            GrantScopeKind.BuyingBranch => (new(kind, 1, 11), [Permissions.BranchPic]),
            GrantScopeKind.BuyingOrganization => (new(kind, 1), [Permissions.OrganizationWide]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
        var preferred = new[] { GrantScopeKind.FulfillmentShop, GrantScopeKind.BuyerOwned, GrantScopeKind.System, GrantScopeKind.CourierAssignment,
            GrantScopeKind.FulfillmentWarehouse, GrantScopeKind.BuyingBranch, GrantScopeKind.BuyingDivision, GrantScopeKind.BuyingOrganization };
        var missing = new List<string>();
        foreach (var permission in referenced)
        {
            var kind = preferred.First(candidate => Permissions.Find(permission)!.AllowedScopes.Contains(candidate));
            var (scope, capabilities) = For(kind);
            var snapshot = new AuthorizationSnapshot(actor.UserId, actor.GrantVersion, AuthorizationStatus.Active,
                [new(1001, actor.UserId, 501, "store-staff", RoleTemplates.CurrentVersion, [permission, .. capabilities], scope,
                    AuthorizationStatus.Active, 801, 802, "Synthetic discovery check", now.AddDays(-1))],
                [new(501, actor.UserId, scope, AuthorizationStatus.Active, 802, now.AddDays(-1))], directory);
            Assert.True(ScopePolicy.Compile(actor, snapshot, permission, now).IsAllowed, permission + " cannot be granted in " + kind);
            if (!AccessEndpoints.CurrentActions(actor, snapshot, now).Contains(permission)) missing.Add(permission);
        }
        Assert.Empty(missing);
        // A page gate that names no permission can never open: every direct actions.includes('…') literal is a real, emitted permission.
        var gate = new Regex("ctions\\.includes\\(['\"`]([a-z]+(?:\\.[a-z]+)+)['\"`]\\)", RegexOptions.CultureInvariant);
        var gates = sources.Values.SelectMany(text => gate.Matches(text).Select(match => match.Groups[1].Value)).Distinct().ToArray();
        Assert.NotEmpty(gates);
        Assert.DoesNotContain(gates, name => Permissions.Find(name) is null);
        Assert.DoesNotContain(gates, name => AccessEndpoints.ScopeCapabilities.Contains(name));
        Assert.All(gates, name => Assert.Contains(name, referenced));
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "package.json")) && File.Exists(Path.Combine(directory.FullName, "backend", "UnitToko.slnx")))
                return directory.FullName;
        throw new InvalidOperationException("Run from the authoritative checkout");
    }
}
