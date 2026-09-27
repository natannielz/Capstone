using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Organizations;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class StoreHistoryTests(MongoFixture fixture)
{
    [Fact]
    public async Task Global_auditor_keeps_malformed_legacy_scope_unknown_and_projects_only_safe_metadata()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var auditor = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessAuditRead);
        var history = data.Database.GetCollection<BsonDocument>("authorizationHistory");
        var badScopes = new BsonValue[]
        {
            new BsonArray { 10L, 11L }, new BsonString("synthetic-private-value"),
            new BsonDocument { { "kind", "buyingBranch" }, { "organizationId", new BsonArray { 10L, 20L } }, { "branchId", 11L } },
            new BsonDocument { { "kind", "buyingBranch" }, { "organizationId", 10 }, { "branchId", 11L } },
            new BsonDocument { { "kind", "buyingBranch" }, { "organizationId", 10L }, { "branchId", 21L } },
            new BsonDocument("kind", "unrecognized")
        };
        for (var index = 0; index < badScopes.Length; index++)
        {
            var after = new BsonDocument { { "_id", 3000L + index }, { "version", 0L }, { "status", new BsonArray { "active" } },
                { "scope", badScopes[index] }, { "secretToken", "synthetic-private-value" } };
            await history.InsertOneAsync(LegacyEvent(index + 1L, "access.grant.proposed", new(), after, data.Clock.UtcNow));
        }
        await history.InsertOneAsync(LegacyEvent(7L, "legacy.unknown", new(), new(), data.Clock.UtcNow));
        await history.InsertOneAsync(LegacyEvent(9L, "legacy.unknown", new(),
            new BsonDocument("scope", AuthorizationBson.Scope(new GrantScope(GrantScopeKind.System))), data.Clock.UtcNow));
        var typed = LegacyEvent(8L, "access.identity.suspended", new(), new(), data.Clock.UtcNow);
        typed.Add("scope", AuthorizationBson.Scope(new GrantScope(GrantScopeKind.System)));
        await history.InsertOneAsync(typed);
        var page = await data.Administration.ListHistoryAsync(auditor, new("security-investigation", "legacy-history"), 0, 100);
        Assert.Equal(9, page.Total);
        Assert.All(page.Items.Where(item => item.Id != 8), item => Assert.Null(item.Scope));
        Assert.All(page.Items.Where(item => item.Id <= 6), item => { Assert.Null(item.AfterVersion); Assert.Null(item.Status); });
        Assert.Equal(GrantScopeKind.System, page.Items.Single(item => item.Id == 8).Scope!.Kind);
        var json = JsonSerializer.Serialize(page);
        Assert.DoesNotContain("synthetic-private-value", json); Assert.DoesNotContain("secretToken", json);
        Assert.Equal(1, await data.Database.GetCollection<BsonDocument>("authorizationReadEvents").CountDocumentsAsync(new BsonDocument("operation", "access.history.read")));
    }

    [Fact]
    public async Task Organization_history_exposes_matching_scope_revision_and_status_without_profile_snapshots()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var service = new OrganizationMasterDataService(data.Database, data.Store, new(data.Database), data.Clock);
        var maker = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.TechnicalConfigure);
        var checker = await data.AddActorAsync(1001, new(GrantScopeKind.System), Permissions.OrganizationApprove);
        var auditor = await data.AddActorAsync(1002, new(GrantScopeKind.System), Permissions.AccessAuditRead);
        AuthorizationCommandMetadata Meta(string key) => new(key, "Reviewed organization display change", "history-organization");
        var proposal = await service.ProposeAsync(maker,
            new(OrganizationChangeKind.UpdateProfile, ScopeNodeKind.Branch, 10, "TEBET", "Synthetic private display name", 11, 1), Meta("propose"));
        await service.ApproveAsync(checker, proposal.Id, 1, Meta("approve"));
        var page = await data.Administration.ListHistoryAsync(auditor, new("access-review", "organization-history"), 0, 10);
        var proposed = Assert.Single(page.Items, item => item.Operation == "organization.proposed");
        Assert.Equal(proposal.Id, proposed.ResourceId); Assert.Null(proposed.BeforeVersion); Assert.Equal(1L, proposed.AfterVersion); Assert.Equal("pending", proposed.Status);
        var approved = Assert.Single(page.Items, item => item.Operation == "organization.approved");
        Assert.Equal(11L, approved.ResourceId); Assert.Equal(1L, approved.BeforeVersion); Assert.Equal(2L, approved.AfterVersion); Assert.Equal("active", approved.Status);
        Assert.Equal(GrantScopeKind.System, approved.Scope!.Kind);
        Assert.DoesNotContain("Synthetic private display name", JsonSerializer.Serialize(page));
        Assert.DoesNotContain("normalizedCode", JsonSerializer.Serialize(page));
        await data.Database.GetCollection<BsonDocument>("authorizationHistory").InsertOneAsync(
            LegacyEvent(100L, "organization.unrecognized", new(), new(), data.Clock.UtcNow));
        var organizationOnly = await data.Administration.ListHistoryAsync(maker, new("access-review", "organization-only-history"), 0, 10);
        Assert.Equal(2, organizationOnly.Total);
        Assert.DoesNotContain(organizationOnly.Items, item => item.Operation == "organization.unrecognized");
    }

    private static BsonDocument LegacyEvent(long id, string operation, BsonDocument before, BsonDocument after, DateTimeOffset at) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "actorId", 2000L }, { "subjectId", 3000L }, { "operation", operation },
        { "reason", "Review the historical access change" }, { "correlationId", "legacy-event" }, { "createdAt", at.UtcDateTime },
        { "before", before }, { "after", after }
    };

    [Fact]
    public async Task History_is_scoped_before_count_and_page_and_privileged_reads_log_without_exposing_raw_state()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var reviewer = await data.AddActorAsync(1000, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.AccessApprove);
        var history = data.Database.GetCollection<BsonDocument>("authorizationHistory");
        foreach (var (id, branch) in new[] { (1L, 11L), (2L, 12L), (3L, 11L), (4L, 12L) })
            await history.InsertOneAsync(new BsonDocument
            {
                { "_id", id }, { "schemaVersion", 1 }, { "actorId", 2000L }, { "subjectId", 3000L }, { "operation", "access.grant.proposed" },
                { "reason", "Review the scoped access assignment" }, { "correlationId", "old-event" }, { "createdAt", data.Clock.UtcNow.UtcDateTime },
                { "before", new BsonDocument("secretToken", "synthetic-private-value") },
                { "after", new BsonDocument { { "_id", id }, { "version", 1L }, { "status", "pending" },
                    { "scope", AuthorizationBson.Scope(new GrantScope(GrantScopeKind.BuyingBranch, 10, branch)) }, { "secretToken", "synthetic-private-value" } } }
            });
        var template = await history.Find(new BsonDocument("_id", 1L)).FirstAsync();
        var arrayScope = template.DeepClone().AsBsonDocument; arrayScope["_id"] = 5L;
        arrayScope["after"]["scope"]["organizationId"] = new BsonArray { 10L, 20L };
        await history.InsertOneAsync(arrayScope);
        var unrelatedOperation = template.DeepClone().AsBsonDocument; unrelatedOperation["_id"] = 6L; unrelatedOperation["operation"] = "unrelated.secret.event";
        await history.InsertOneAsync(unrelatedOperation);
        var mixedParents = template.DeepClone().AsBsonDocument; mixedParents["_id"] = 7L; mixedParents["after"]["scope"]["branchId"] = 21L;
        await history.InsertOneAsync(mixedParents);
        var page = await data.Administration.ListHistoryAsync(reviewer, new("access-review", "history-test"), 0, 1);
        Assert.Equal(2, page.Total); Assert.Equal(3L, Assert.Single(page.Items).Id);
        var json = JsonSerializer.Serialize(page);
        Assert.DoesNotContain("secretToken", json); Assert.DoesNotContain("synthetic-private-value", json);
        var next = await data.Administration.ListHistoryAsync(reviewer, new("access-review", "history-test-next"), 1, 1);
        Assert.Equal(2, next.Total); Assert.Equal(1L, Assert.Single(next.Items).Id);
        Assert.Equal(7, await history.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var events = await data.Database.GetCollection<BsonDocument>("authorizationReadEvents").Find(new BsonDocument("operation", "access.history.read")).ToListAsync();
        Assert.Equal(2, events.Count); Assert.All(events, entry => Assert.Equal("access-review", entry["purpose"].AsString));
        Assert.DoesNotContain("synthetic-private-value", events.ToJson());
        var owner = await data.AddActorAsync(1001, new(GrantScopeKind.BuyingOrganization, 10), Permissions.AccessApprove);
        var organizationHistory = await data.Administration.ListHistoryAsync(owner, new("access-review", "organization-history"), 0, 10);
        Assert.Equal(4, organizationHistory.Total);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.ListHistoryAsync(reviewer, new("security-investigation", "not-authorized"), 0, 1));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Administration.ListHistoryAsync(reviewer, new("anything", "bad-purpose"), 0, 1));
    }
}
