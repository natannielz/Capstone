using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Organizations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Organizations;

[Collection("Mongo")]
public sealed class OrganizationServiceTests(MongoFixture fixture)
{
    private static readonly GrantScope SystemScope = new(GrantScopeKind.System);
    private static AuthorizationCommandMetadata Metadata(string key) => new(key, "Reviewed synthetic organization change", "organization-test");
    private static readonly AuthorizationReadMetadata ReadAudit = new("access-review", "organization-test");
    private static OrganizationMasterDataService Service(StoreTestData data) => new(data.Database, data.Store, new(data.Database), data.Clock);
    private static OrganizationChangeRequest Create(string code = "NEW", long parent = 10) => new(OrganizationChangeKind.Create, ScopeNodeKind.Branch, parent, code, "New branch");
    private static async Task<StoreTestData> DataAsync(MongoFixture fixture)
    {
        var data = await StoreTestData.CreateAsync(fixture);
        // Synthetic scopes are inserted after migrations by the shared fixture. Production migration
        // synchronizes this high-water mark from existing IDs before any organization commands run.
        await data.Database.GetCollection<BsonDocument>("idCounters").UpdateOneAsync(new BsonDocument("_id", "authorizationScopes"),
            Builders<BsonDocument>.Update.Max("value", 103L), new() { IsUpsert = true });
        return data;
    }
    [Fact]
    public async Task Independent_fresh_checker_creates_once_and_records_complete_audit_and_read_events()
    {
        await using var data = await DataAsync(fixture); var service = Service(data);
        var maker = await data.AddActorAsync(1, SystemScope, Permissions.TechnicalConfigure, Permissions.OrganizationApprove);
        var checker = await data.AddActorAsync(2, SystemScope, Permissions.OrganizationApprove);
        var proposal = await service.ProposeAsync(maker, Create(" new "), Metadata("propose"));
        Assert.Equal(9, (await service.ListAsync(checker, 0, 100, ReadAudit)).Total);
        Assert.All((await service.ListAsync(checker, 0, 100, ReadAudit)).Items, value => Assert.Null(value.Name));
        Assert.Equal("independent_actor_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ApproveAsync(maker, proposal.Id, 1, Metadata("self")))).Code);
        var oldFactor = checker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) };
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ApproveAsync(oldFactor, proposal.Id, 1, Metadata("old-factor")));
        var view = await service.ReadProposalAsync(checker, proposal.Id, ReadAudit);
        Assert.Contains("approve", view.AllowedActions); Assert.Contains("reject", view.AllowedActions); Assert.DoesNotContain("withdraw", view.AllowedActions);
        var approved = await service.ApproveAsync(checker, proposal.Id, 1, Metadata("approve"));
        var replay = await service.ApproveAsync(checker, proposal.Id, 1, Metadata("approve"));
        Assert.Equal(approved, replay); Assert.True(approved.ScopeId > 103);
        var organization = await service.ReadAsync(maker, approved.ScopeId!.Value, ReadAudit);
        Assert.Equal("NEW", organization.Code); Assert.Equal(1, organization.ProfileVersion);
        Assert.Equal(10, (await service.ListAsync(maker, 0, 100, ReadAudit)).Total);
        Assert.Equal(2, await data.Database.GetCollection<BsonDocument>("authorizationHistory").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var audit = await data.Database.GetCollection<BsonDocument>("authorizationHistory").Find(new BsonDocument("operation", "organization.approved")).SingleAsync();
        Assert.Equal(2L, audit["actorId"].AsInt64); Assert.True(audit["before"]["scope"].IsBsonNull);
        Assert.Equal("NEW", audit["after"]["profile"]["code"].AsString); Assert.Equal("system", audit["scope"]["kind"].AsString);
        Assert.True(await data.Database.GetCollection<BsonDocument>("authorizationReadEvents").Find(new BsonDocument("operation", "organization.workbench.read")).AnyAsync());
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.ApproveAsync(checker, proposal.Id, 2, Metadata("approve")));
    }
    [Fact]
    public async Task Technical_configuration_alone_cannot_approve_and_stale_parent_keeps_reject_available()
    {
        await using var data = await DataAsync(fixture); var service = Service(data);
        var maker = await data.AddActorAsync(1, SystemScope, Permissions.TechnicalConfigure);
        var otherMaker = await data.AddActorAsync(2, SystemScope, Permissions.TechnicalConfigure);
        var checker = await data.AddActorAsync(3, SystemScope, Permissions.OrganizationApprove);
        var proposal = await service.ProposeAsync(maker, Create(), Metadata("propose"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ApproveAsync(otherMaker, proposal.Id, 1, Metadata("wrong")));
        await data.Database.GetCollection<BsonDocument>("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 10L), Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
        var summary = await service.ReadProposalAsync(checker, proposal.Id, ReadAudit);
        Assert.DoesNotContain("approve", summary.AllowedActions); Assert.Contains("reject", summary.AllowedActions);
        await Assert.ThrowsAsync<DomainValidationException>(() => service.ApproveAsync(checker, proposal.Id, 1, Metadata("stale-parent")));
        var rejected = await service.RejectAsync(checker, proposal.Id, 1, Metadata("reject"));
        Assert.Equal("rejected", rejected.Status); Assert.Equal(rejected, await service.RejectAsync(checker, proposal.Id, 1, Metadata("reject")));
        Assert.Equal(9, (await service.ListAsync(maker, 0, 100, ReadAudit)).Total);
        Assert.False(await data.Database.GetCollection<BsonDocument>("idempotencyRecords").Find(new BsonDocument("key", "stale-parent")).AnyAsync());
    }
    [Fact]
    public async Task Canonical_code_uniqueness_and_profile_revision_prevent_lost_updates_without_inventing_legacy_profiles()
    {
        await using var data = await DataAsync(fixture); var service = Service(data);
        var maker = await data.AddActorAsync(1, SystemScope, Permissions.TechnicalConfigure);
        var checker = await data.AddActorAsync(2, SystemScope, Permissions.OrganizationApprove);
        var first = await service.ProposeAsync(maker, Create("DUP"), Metadata("one"));
        var duplicate = await service.ProposeAsync(maker, Create("dup"), Metadata("two"));
        await service.ApproveAsync(checker, first.Id, 1, Metadata("approve-one"));
        Assert.Equal("organization_code_exists", (await Assert.ThrowsAsync<PersistenceConflictException>(() => service.ApproveAsync(checker, duplicate.Id, 1, Metadata("approve-two")))).Code);
        var otherParent = await service.ProposeAsync(maker, Create("DUP", 20), Metadata("different-parent"));
        await service.ApproveAsync(checker, otherParent.Id, 1, Metadata("approve-other"));
        var legacy = new OrganizationChangeRequest(OrganizationChangeKind.UpdateProfile, ScopeNodeKind.Branch, 10, "LEGACY", "Reviewed branch", 11, 1, 0);
        var a = await service.ProposeAsync(maker, legacy, Metadata("legacy-a"));
        var b = await service.ProposeAsync(maker, legacy with { Name = "Conflicting name" }, Metadata("legacy-b"));
        var approved = await service.ApproveAsync(checker, a.Id, 1, Metadata("legacy-approve"));
        Assert.Equal(2, approved.ScopeVersion); Assert.Equal(1, approved.ProfileVersion);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.ApproveAsync(checker, b.Id, 1, Metadata("legacy-stale")));
        Assert.Equal("Reviewed branch", (await service.ReadAsync(maker, 11, ReadAudit)).Name);
        Assert.Null((await service.ReadAsync(maker, 12, ReadAudit)).Name);
    }
    [Fact]
    public async Task Withdrawal_requires_original_current_maker_and_terminal_actions_are_immutable()
    {
        await using var data = await DataAsync(fixture); var service = Service(data);
        var maker = await data.AddActorAsync(1, SystemScope, Permissions.TechnicalConfigure);
        var other = await data.AddActorAsync(2, SystemScope, Permissions.TechnicalConfigure, Permissions.OrganizationApprove);
        var proposal = await service.ProposeAsync(maker, Create(), Metadata("pending"));
        Assert.Equal("original_requester_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.WithdrawAsync(other, proposal.Id, 1, Metadata("wrong")))).Code);
        var result = await service.WithdrawAsync(maker, proposal.Id, 1, Metadata("withdraw"));
        Assert.Equal("withdrawn", result.Status); Assert.Equal(result, await service.WithdrawAsync(maker, proposal.Id, 1, Metadata("withdraw")));
        Assert.Empty((await service.ReadProposalAsync(maker, proposal.Id, ReadAudit)).AllowedActions);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.RejectAsync(other, proposal.Id, 1, Metadata("late")));
        await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Inc("grantVersion", 1L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.WithdrawAsync(maker, proposal.Id, 1, Metadata("withdraw")));
    }
    [Fact]
    public async Task Reactivation_reviews_potential_impact_invalidates_old_sessions_and_preserves_suspended_descendants()
    {
        await using var data = await DataAsync(fixture); var service = Service(data);
        var maker = await data.AddActorAsync(1, SystemScope, Permissions.TechnicalConfigure);
        var checker = await data.AddActorAsync(2, SystemScope, Permissions.OrganizationApprove);
        var buyer = await data.AddActorAsync(3, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.OrderRead, Permissions.BranchPic);
        await data.Database.GetCollection<BsonDocument>("authorizationScopes").UpdateManyAsync(new BsonDocument("_id", new BsonDocument("$in", new BsonArray { 10L, 12L })), Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
        var request = new OrganizationChangeRequest(OrganizationChangeKind.Reactivate, ScopeNodeKind.BuyingOrganization, null, "BNI", "Reviewed organization", 10, 2, 0);
        var proposal = await service.ProposeAsync(maker, request, Metadata("reactivate"));
        var summary = await service.ReadProposalAsync(checker, proposal.Id, ReadAudit);
        Assert.Equal(3, summary.Impact.AffectedScopes); Assert.Equal(1, summary.Impact.ActiveMemberships);
        Assert.Equal(1, summary.Impact.ActiveGrants); Assert.Equal(1, summary.Impact.AffectedAccounts);
        var result = await service.ApproveAsync(checker, proposal.Id, 1, Metadata("approve"));
        Assert.Equal(1, result.AffectedAccounts); Assert.Equal("active", (await service.ReadAsync(maker, 10, ReadAudit)).Status);
        Assert.Equal("suspended", (await service.ReadAsync(maker, 12, ReadAudit)).Status);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.ReadCurrentAsync(buyer));
        Assert.Equal(2, (await data.Store.ReadCurrentAsync(buyer with { GrantVersion = 2 })).GrantVersion);
        Assert.Equal(result, await service.ApproveAsync(checker, proposal.Id, 1, Metadata("approve")));
    }
    [Fact]
    public async Task Reactivation_impact_drift_requires_new_review_and_no_partial_activation()
    {
        await using var data = await DataAsync(fixture); var service = Service(data);
        var maker = await data.AddActorAsync(1, SystemScope, Permissions.TechnicalConfigure);
        var checker = await data.AddActorAsync(2, SystemScope, Permissions.OrganizationApprove);
        await data.AddActorAsync(3, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.OrderRead, Permissions.BranchPic);
        await data.Database.GetCollection<BsonDocument>("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 10L), Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
        var proposal = await service.ProposeAsync(maker, new(OrganizationChangeKind.Reactivate, ScopeNodeKind.BuyingOrganization, null, "BNI", "Reviewed", 10, 2), Metadata("reactivate"));
        await data.Database.GetCollection<BsonDocument>("identities").UpdateOneAsync(new BsonDocument("_id", 3L), Builders<BsonDocument>.Update.Inc("grantVersion", 1L));
        Assert.Equal("organization_impact_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() => service.ApproveAsync(checker, proposal.Id, 1, Metadata("approve")))).Code);
        Assert.Equal("suspended", (await service.ReadAsync(maker, 10, ReadAudit)).Status);
        Assert.Null((await service.ReadAsync(maker, 10, ReadAudit)).ProfileVersion);
        Assert.Equal("pending", (await service.ReadProposalAsync(checker, proposal.Id, ReadAudit)).Status);
        Assert.DoesNotContain("approve", (await service.ReadProposalAsync(checker, proposal.Id, ReadAudit)).AllowedActions);
        Assert.False(await data.Database.GetCollection<BsonDocument>("idempotencyRecords").Find(new BsonDocument("key", "approve")).AnyAsync());
    }

    [Fact]
    public async Task Reactivation_approver_cannot_restore_their_own_business_access()
    {
        await using var data = await DataAsync(fixture); var service = Service(data);
        var maker = await data.AddActorAsync(1, SystemScope, Permissions.TechnicalConfigure);
        var checker = await data.AddActorAsync(2, SystemScope, Permissions.OrganizationApprove);
        await data.AddGrantAsync(checker, 20, new(GrantScopeKind.BuyingBranch, 10, 11), [Permissions.OrderRead, Permissions.BranchPic]);
        await data.Database.GetCollection<BsonDocument>("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 10L), Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
        var proposal = await service.ProposeAsync(maker, new(OrganizationChangeKind.Reactivate, ScopeNodeKind.BuyingOrganization, null, "BNI", "Reviewed", 10, 2), Metadata("propose"));
        Assert.DoesNotContain("approve", (await service.ReadProposalAsync(checker, proposal.Id, ReadAudit)).AllowedActions);
        Assert.Equal("beneficiary_cannot_approve", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ApproveAsync(checker, proposal.Id, 1, Metadata("approve")))).Code);
        Assert.Equal("suspended", (await service.ReadAsync(maker, 10, ReadAudit)).Status);
        Assert.Equal(1L, (await data.Store.ReadCurrentAsync(checker)).GrantVersion);
    }

    [Fact]
    public async Task Concurrent_parent_suspension_aborts_reactivation_and_reloads_authoritative_ancestry()
    {
        await using var data = await DataAsync(fixture); var service = Service(data);
        var maker = await data.AddActorAsync(1, SystemScope, Permissions.TechnicalConfigure);
        var checker = await data.AddActorAsync(2, SystemScope, Permissions.OrganizationApprove);
        var revoker = await data.AddActorAsync(3, SystemScope, Permissions.AccessRevoke);
        await data.Database.GetCollection<BsonDocument>("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 11L), Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
        var proposal = await service.ProposeAsync(maker, new(OrganizationChangeKind.Reactivate, ScopeNodeKind.Branch, 10, "TEBET", "Reviewed branch", 11, 2), Metadata("propose"));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var intercepted = 0;
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command =>
        {
            if (command.CommandName == "update" && command.Command.GetValue("update", "") == "authorizationScopes" &&
                Interlocked.CompareExchange(ref intercepted, 1, 0) == 0)
            {
                reached.TrySetResult();
                if (!resume.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("The independent suspension did not release the transaction barrier");
            }
        });
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var guardedService = new OrganizationMasterDataService(database, new(database, new(client), data.Clock), new(database), data.Clock);
        var pending = Task.Run(() => guardedService.ApproveAsync(checker, proposal.Id, 1, Metadata("approve")));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await data.Administration.SuspendScopeAsync(revoker, 10, 1, Metadata("suspend-parent")); }
        finally { resume.Set(); }
        await Assert.ThrowsAsync<DomainValidationException>(() => pending);
        Assert.Equal("suspended", (await service.ReadAsync(maker, 11, ReadAudit)).Status);
        Assert.Null((await service.ReadAsync(maker, 11, ReadAudit)).Name);
        Assert.Equal("pending", (await service.ReadProposalAsync(checker, proposal.Id, ReadAudit)).Status);
        Assert.False(await data.Database.GetCollection<BsonDocument>("idempotencyRecords").Find(new BsonDocument("key", "approve")).AnyAsync());
    }

    [Fact]
    public async Task Reactivating_branch_includes_organization_wide_beneficiaries_and_invalidates_their_sessions()
    {
        await using var data = await DataAsync(fixture); var service = Service(data);
        var maker = await data.AddActorAsync(1, SystemScope, Permissions.TechnicalConfigure);
        var beneficiaryChecker = await data.AddActorAsync(2, SystemScope, Permissions.OrganizationApprove);
        await data.AddGrantAsync(beneficiaryChecker, 20, new(GrantScopeKind.BuyingOrganization, 10), [Permissions.OrderRead, Permissions.OrganizationWide]);
        var checker = await data.AddActorAsync(3, SystemScope, Permissions.OrganizationApprove);
        var broadBuyer = await data.AddActorAsync(4, new(GrantScopeKind.BuyingOrganization, 10), Permissions.OrderRead, Permissions.OrganizationWide);
        var unrelatedBuyer = await data.AddActorAsync(5, new(GrantScopeKind.BuyingBranch, 10, 12), Permissions.OrderRead, Permissions.BranchPic);
        await data.Database.GetCollection<BsonDocument>("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 11L), Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
        var proposal = await service.ProposeAsync(maker, new(OrganizationChangeKind.Reactivate, ScopeNodeKind.Branch, 10, "TEBET", "Reviewed branch", 11, 2), Metadata("propose"));
        var view = await service.ReadProposalAsync(beneficiaryChecker, proposal.Id, ReadAudit);
        Assert.Equal(2, view.Impact.AffectedAccounts); Assert.Equal(2, view.Impact.ActiveGrants); Assert.Equal(2, view.Impact.ActiveMemberships);
        Assert.DoesNotContain("approve", view.AllowedActions);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ApproveAsync(beneficiaryChecker, proposal.Id, 1, Metadata("self-benefit")));
        await service.ApproveAsync(checker, proposal.Id, 1, Metadata("independent"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.ReadCurrentAsync(broadBuyer));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.ReadCurrentAsync(beneficiaryChecker));
        Assert.Equal(1, (await data.Store.ReadCurrentAsync(unrelatedBuyer)).GrantVersion);
    }
}
