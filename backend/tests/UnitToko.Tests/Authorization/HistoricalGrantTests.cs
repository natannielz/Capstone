using System.Collections.Immutable;
using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class HistoricalGrantTests(MongoFixture fixture)
{
    private static readonly GrantScope Organization = new(GrantScopeKind.BuyingOrganization, 10);
    private static readonly GrantScope Branch = new(GrantScopeKind.BuyingBranch, 10, 11);
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Reviewed historical branch records", "historical-grant-test");

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Explicit_reviewed_selection_is_approvable_before_and_after_target_suspension(bool suspendedBeforeProposal, bool division)
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actors = await ActorsAsync(data);
        if (division) await Rows(data, "authorizationScopes").InsertOneAsync(AuthorizationBson.Node(new ScopeNode(13, ScopeNodeKind.Division, 11, AuthorizationStatus.Active, 1)));
        var target = division ? new GrantScope(GrantScopeKind.BuyingDivision, 10, 11, 13) : Branch;
        var request = Request(data, actors.Subject, target) with { Permissions = [Permissions.ReportRead] };
        if (suspendedBeforeProposal) await SuspendTargetAsync(data, target);
        var preview = await data.Administration.PreviewHistoricalGrantAsync(actors.Maker, request);
        Assert.Equal(HistoricalReadPolicy.TemplateId, preview.TemplateId);
        Assert.Equal(new[] { Permissions.ReportRead }, preview.Permissions.ToArray());
        Assert.Empty(preview.DelegablePermissions);
        Assert.True(preview.RequiresIndependentApproval);
        var proposal = await data.Administration.ProposeHistoricalGrantAsync(actors.Maker, request, Meta("propose"));
        if (!suspendedBeforeProposal) await SuspendTargetAsync(data, target);
        var listed = Assert.Single(await data.Administration.ListProposalsAsync(actors.Approver, 0, 10));
        Assert.Contains("approve", listed.AllowedActions);
        Assert.Equal(request.Permissions.ToArray(), listed.Permissions.ToArray());
        Assert.Equal(actors.Subject.UserId, listed.MembershipId);
        var result = await data.Administration.ApproveGrantAsync(actors.Approver, proposal.Id, 1, Meta("approve"));
        Assert.Equal(2, result.GrantVersion);
        var reader = actors.Subject with { GrantVersion = result.GrantVersion };
        var current = await data.Store.ReadCurrentAsync(reader);
        var grant = Assert.Single(current.Grants.Where(grant => grant.Id == result.Id));
        Assert.True(HistoricalReadPolicy.IsGrantActive(grant, reader.UserId, data.Clock.UtcNow));
        Assert.False(ScopePolicy.IsGrantActive(grant, reader.UserId, data.Clock.UtcNow));
        Assert.False(ScopePolicy.Compile(reader, current, Permissions.ReportRead, data.Clock.UtcNow).IsAllowed);
        Assert.False(ScopePolicy.Compile(reader, current, Permissions.HistoryRead, data.Clock.UtcNow).IsAllowed);
        Assert.Equal(actors.Maker.UserId, grant.RequestedBy);
        Assert.Equal(actors.Approver.UserId, grant.ApprovedBy);
        Assert.Equal(target, grant.Scope);
        Assert.Equal(new[] { Permissions.ReportRead }, (await data.Administration.PreviewHistoricalGrantAsync(actors.Maker, request)).ExistingPermissions.ToArray());
        Assert.DoesNotContain(HistoricalReadPolicy.TemplateId, RoleTemplates.Templates.Keys);
        Assert.Equal(2, await Rows(data, "authorizationHistory").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Preview_and_materialization_reject_widening_and_require_current_approved_parent_membership()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actors = await ActorsAsync(data);
        var valid = Request(data, actors.Subject, Branch);
        foreach (var permissions in new ImmutableArray<string>[] { default, [], [Permissions.HistoryRead, Permissions.HistoryRead],
                     [Permissions.OrderRead], [Permissions.BranchPic], [Permissions.OrganizationWide], ["unknown"],
                     [Permissions.HistoryRead, Permissions.ReportRead, Permissions.AccessApprove] })
            await Assert.ThrowsAsync<DomainValidationException>(() => data.Administration.PreviewHistoricalGrantAsync(actors.Maker, valid with { Permissions = permissions }));
        foreach (var scope in new[] { Organization, new GrantScope(GrantScopeKind.BuyerOwned, 10, 11),
                     Branch with { ShopId = 101 }, Branch with { DivisionId = 13 }, new GrantScope(GrantScopeKind.System) })
            await Assert.ThrowsAsync<DomainValidationException>(() => data.Administration.PreviewHistoricalGrantAsync(actors.Maker, valid with { Scope = scope }));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Administration.ProposeHistoricalGrantAsync(actors.Maker, valid,
            Meta("long-reason") with { Reason = new string('x', 501) }));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Administration.PreviewGrantAsync(actors.Maker,
            new(actors.Subject.UserId, actors.Subject.UserId, HistoricalReadPolicy.TemplateId, [], [Permissions.HistoryRead], Branch, data.Clock.UtcNow)));
        await data.AddGrantAsync(actors.Subject, 2000, Branch, [Permissions.OrderRead, Permissions.BranchPic]);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.PreviewHistoricalGrantAsync(actors.Maker, valid with { MembershipId = 2000 }));
        await Rows(data, "memberships").UpdateOneAsync(new BsonDocument("_id", actors.Subject.UserId),
            Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.ProposeHistoricalGrantAsync(actors.Maker, valid, Meta("suspended-parent-member")));
        Assert.Equal(0, await Rows(data, "authorizationProposals").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Approval_requires_independent_current_complete_delegation_and_fresh_factor()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actors = await ActorsAsync(data);
        var proposal = await data.Administration.ProposeHistoricalGrantAsync(actors.Maker, Request(data, actors.Subject, Branch), Meta("propose"));
        await data.AddGrantAsync(actors.Maker, 2000, Organization, [Permissions.AccessApprove], [Permissions.HistoryRead, Permissions.ReportRead]);
        await data.AddGrantAsync(actors.Subject, 2001, Organization, [Permissions.AccessApprove], [Permissions.HistoryRead, Permissions.ReportRead]);
        foreach (var actor in new[] { actors.Maker, actors.Subject })
            Assert.Equal("independent_actor_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
                data.Administration.ApproveGrantAsync(actor, proposal.Id, 1, Meta("self-" + actor.UserId)))).Code);
        await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", actors.Approver.UserId),
            Builders<BsonDocument>.Update.Set("delegablePermissions", new BsonArray { Permissions.HistoryRead }));
        await data.AddGrantAsync(actors.Approver, 2002, Organization, [Permissions.AccessApprove], [Permissions.ReportRead]);
        Assert.Equal("delegation_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Administration.ApproveGrantAsync(actors.Approver, proposal.Id, 1, Meta("split")))).Code);
        Assert.DoesNotContain("approve", Assert.Single(await data.Administration.ListProposalsAsync(actors.Approver, 0, 10)).AllowedActions);
        await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", actors.Approver.UserId),
            Builders<BsonDocument>.Update.Set("delegablePermissions", new BsonArray { Permissions.HistoryRead, Permissions.ReportRead }));
        var stale = actors.Approver with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) };
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Administration.ApproveGrantAsync(stale, proposal.Id, 1, Meta("stale-factor")))).Code);
        var outside = await data.AddActorAsync(3000, new(GrantScopeKind.BuyingOrganization, 20), Permissions.AccessApprove);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.ApproveGrantAsync(outside, proposal.Id, 1, Meta("outside")));
        Assert.Equal("active", (await data.Administration.ApproveGrantAsync(actors.Approver, proposal.Id, 1, Meta("valid"))).Status);
    }

    [Fact]
    public async Task Proposed_lifetime_and_parent_membership_are_revalidated_when_reviewing()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actors = await ActorsAsync(data);
        var request = Request(data, actors.Subject, Branch);
        var expires = data.Clock.UtcNow.AddHours(1);
        await Rows(data, "memberships").UpdateOneAsync(new BsonDocument("_id", actors.Subject.UserId),
            Builders<BsonDocument>.Update.Set("expiresAt", expires.UtcDateTime));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.PreviewHistoricalGrantAsync(actors.Maker, request));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.PreviewHistoricalGrantAsync(actors.Maker,
            request with { ExpiresAt = expires.AddSeconds(1) }));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.PreviewHistoricalGrantAsync(actors.Maker,
            request with { EffectiveAt = data.Clock.UtcNow.AddMinutes(-1), ExpiresAt = expires }));
        var proposal = await data.Administration.ProposeHistoricalGrantAsync(actors.Maker, request with { ExpiresAt = expires }, Meta("propose"));
        await Rows(data, "memberships").UpdateOneAsync(new BsonDocument("_id", actors.Subject.UserId),
            Builders<BsonDocument>.Update.Set("expiresAt", expires.AddMinutes(-1).UtcDateTime).Inc("version", 1L));
        Assert.DoesNotContain("approve", Assert.Single(await data.Administration.ListProposalsAsync(actors.Approver, 0, 10)).AllowedActions);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.ApproveGrantAsync(actors.Approver, proposal.Id, 1, Meta("approve")));
        Assert.False(await Rows(data, "authorizationGrants").Find(new BsonDocument("templateId", HistoricalReadPolicy.TemplateId)).AnyAsync());
    }

    [Theory]
    [InlineData("delegation-type")]
    [InlineData("delegation-missing")]
    [InlineData("delegation-nonempty")]
    [InlineData("scope-extra")]
    [InlineData("permissions-type")]
    [InlineData("permissions-missing")]
    [InlineData("reviewed-scope")]
    [InlineData("reviewed-subject")]
    [InlineData("reviewed-requester")]
    [InlineData("reviewed-reason")]
    public async Task Malformed_stored_historical_proposals_never_gain_approval(string defect)
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actors = await ActorsAsync(data);
        var proposal = await data.Administration.ProposeHistoricalGrantAsync(actors.Maker, Request(data, actors.Subject, Branch), Meta("propose"));
        var update = defect switch
        {
            "delegation-type" => Builders<BsonDocument>.Update.Set("request.delegablePermissions", "malformed"),
            "delegation-missing" => Builders<BsonDocument>.Update.Unset("request.delegablePermissions"),
            "delegation-nonempty" => Builders<BsonDocument>.Update.Set("request.delegablePermissions", new BsonArray { Permissions.HistoryRead }),
            "scope-extra" => Builders<BsonDocument>.Update.Set("request.scope.unreviewedDimension", 21L),
            "permissions-type" => Builders<BsonDocument>.Update.Set("request.permissions", "history.read"),
            "reviewed-scope" => Builders<BsonDocument>.Update.Set("scope.branchId", 12L),
            "reviewed-subject" => Builders<BsonDocument>.Update.Set("subjectId", actors.Approver.UserId),
            "reviewed-requester" => Builders<BsonDocument>.Update.Set("requestedBy", actors.Approver.UserId),
            "reviewed-reason" => Builders<BsonDocument>.Update.Set("reason", "Different reviewed reason"),
            _ => Builders<BsonDocument>.Update.Unset("request.permissions")
        };
        await Rows(data, "authorizationProposals").UpdateOneAsync(new BsonDocument("_id", proposal.Id), update);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.ApproveGrantAsync(actors.Approver, proposal.Id, 1, Meta("approve")));
        Assert.False(await Rows(data, "authorizationGrants").Find(new BsonDocument("templateId", HistoricalReadPolicy.TemplateId)).AnyAsync());
        Assert.False(await Rows(data, "idempotencyRecords").Find(new BsonDocument("key", "approve")).AnyAsync());
    }

    [Fact]
    public async Task Historical_idempotency_is_normalized_distinct_from_ordinary_and_receipts_resolve_once()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actors = await ActorsAsync(data);
        var request = Request(data, actors.Subject, Branch);
        var proposal = await data.Administration.ProposeHistoricalGrantAsync(actors.Maker, request, Meta("propose"));
        Assert.Equal(proposal, await data.Administration.ProposeHistoricalGrantAsync(actors.Maker,
            request with { Permissions = [Permissions.ReportRead, Permissions.HistoryRead] }, Meta("propose")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Administration.ProposeHistoricalGrantAsync(actors.Maker,
            request with { Permissions = [Permissions.HistoryRead] }, Meta("propose")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Administration.ProposeGrantAsync(actors.Maker,
            new(actors.Subject.UserId, actors.Subject.UserId, "buyer", [], [Permissions.OrganizationWide], Organization, data.Clock.UtcNow), Meta("propose")));
        var result = await data.Administration.ApproveGrantAsync(actors.Approver, proposal.Id, 1, Meta("approve"));
        Assert.Equal(result, await data.Administration.ApproveGrantAsync(actors.Approver, proposal.Id, 1, Meta("approve")));
        var receipt = await data.Administration.ReadOperationReceiptAsync(actors.Approver, "access.grant.approve", "approve", new("operation-recovery", "historical-receipt"));
        Assert.Equal("committed", receipt.State);
        Assert.Equal(result.Id, receipt.Result!.Id);
        Assert.Equal(1, await Rows(data, "authorizationGrants").CountDocumentsAsync(new BsonDocument("templateId", HistoricalReadPolicy.TemplateId)));
        Assert.Equal(2, await Rows(data, "authorizationHistory").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", actors.Approver.UserId),
            Builders<BsonDocument>.Update.Set("delegablePermissions", new BsonArray()).Inc("version", 1L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Administration.ApproveGrantAsync(actors.Approver, proposal.Id, 1, Meta("approve")));
    }

    [Fact]
    public async Task Rejection_withdrawal_and_revocation_remain_available_for_suspended_historical_targets()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actors = await ActorsAsync(data);
        await SuspendTargetAsync(data, Branch);
        var request = Request(data, actors.Subject, Branch);
        var rejected = await data.Administration.ProposeHistoricalGrantAsync(actors.Maker, request, Meta("reject-proposal"));
        Assert.Equal("rejected", (await data.Administration.RejectProposalAsync(actors.Approver, rejected.Id, 1, Meta("reject"))).Status);
        var withdrawn = await data.Administration.ProposeHistoricalGrantAsync(actors.Maker, request, Meta("withdraw-proposal"));
        Assert.Equal("withdrawn", (await data.Administration.WithdrawProposalAsync(actors.Maker, withdrawn.Id, 1, Meta("withdraw"))).Status);
        var proposal = await data.Administration.ProposeHistoricalGrantAsync(actors.Maker, request, Meta("active-proposal"));
        var approved = await data.Administration.ApproveGrantAsync(actors.Approver, proposal.Id, 1, Meta("approve"));
        var subject = await data.Administration.ReadSubjectAsync(actors.Approver, actors.Subject.UserId);
        Assert.Contains("revoke", Assert.Single(subject.Grants.Where(grant => grant.Id == approved.Id)).AllowedActions);
        var revoked = await data.Administration.RevokeGrantAsync(actors.Approver, approved.Id, 1, Meta("revoke"));
        Assert.Equal("revoked", revoked.Status);
        Assert.Equal(3, revoked.GrantVersion);
        Assert.Equal(revoked, await data.Administration.RevokeGrantAsync(actors.Approver, approved.Id, 1, Meta("revoke")));
        var grant = AuthorizationBson.Grant(await Rows(data, "authorizationGrants").Find(new BsonDocument("_id", approved.Id)).SingleAsync());
        Assert.False(HistoricalReadPolicy.IsGrantActive(grant, actors.Subject.UserId, data.Clock.UtcNow));
    }

    [Theory]
    [InlineData("beneficiary-identity")]
    [InlineData("beneficiary-membership")]
    [InlineData("actor-identity")]
    [InlineData("actor-grant")]
    [InlineData("actor-membership")]
    [InlineData("actor-session")]
    [InlineData("organization")]
    [InlineData("target-parent")]
    [InlineData("target-revoked")]
    [InlineData("factor-expiry")]
    [InlineData("grant-expiry")]
    [InlineData("membership-expiry")]
    public async Task Approval_cannot_commit_after_current_evidence_changes(string change)
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actors = await ActorsAsync(data);
        var request = Request(data, actors.Subject, Branch);
        if (change == "grant-expiry") await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", actors.Approver.UserId),
            Builders<BsonDocument>.Update.Set("expiresAt", data.Clock.UtcNow.AddMinutes(1).UtcDateTime));
        if (change == "membership-expiry")
        {
            request = request with { ExpiresAt = data.Clock.UtcNow.AddMinutes(1) };
            await Rows(data, "memberships").UpdateOneAsync(new BsonDocument("_id", actors.Subject.UserId),
                Builders<BsonDocument>.Update.Set("expiresAt", request.ExpiresAt.Value.UtcDateTime));
        }
        if (change == "actor-session") actors = actors with { Approver = await SessionActorAsync(data, actors.Approver) };
        var proposal = await data.Administration.ProposeHistoricalGrantAsync(actors.Maker, request, Meta("propose"));
        await RaceApprovalAsync(data, actors.Approver, proposal.Id, async () =>
        {
            if (change.EndsWith("expiry", StringComparison.Ordinal)) { data.Clock.UtcNow += TimeSpan.FromMinutes(change == "factor-expiry" ? 6 : 1); return; }
            var (collection, id, update) = change switch
            {
                "beneficiary-identity" => ("identities", (BsonValue)actors.Subject.UserId, Builders<BsonDocument>.Update.Set("status", "suspended").Inc("grantVersion", 1L)),
                "beneficiary-membership" => ("memberships", (BsonValue)actors.Subject.UserId, Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L)),
                "actor-identity" => ("identities", (BsonValue)actors.Approver.UserId, Builders<BsonDocument>.Update.Inc("grantVersion", 1L)),
                "actor-grant" => ("authorizationGrants", (BsonValue)actors.Approver.UserId, Builders<BsonDocument>.Update.Set("delegablePermissions", new BsonArray()).Inc("version", 1L)),
                "actor-membership" => ("memberships", (BsonValue)actors.Approver.UserId, Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L)),
                "actor-session" => ("identitySessions", (BsonValue)actors.Approver.Session!.Id, Builders<BsonDocument>.Update.Set("status", "revoked")
                    .Set("revokedAt", data.Clock.UtcNow.UtcDateTime).Set("revocationReason", "owner_logout").Inc("version", 1L)),
                "organization" => ("authorizationScopes", (BsonValue)10L, Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L)),
                "target-parent" => ("authorizationScopes", (BsonValue)11L, Builders<BsonDocument>.Update.Set("parentId", 20L).Inc("version", 1L)),
                _ => ("authorizationScopes", (BsonValue)11L, Builders<BsonDocument>.Update.Set("status", "revoked").Inc("version", 1L))
            };
            await Rows(data, collection).UpdateOneAsync(new BsonDocument("_id", id), update);
        }, succeeds: false);
        Assert.Equal("pending", (await Rows(data, "authorizationProposals").Find(new BsonDocument("_id", proposal.Id)).SingleAsync())["status"].AsString);
        Assert.False(await Rows(data, "authorizationGrants").Find(new BsonDocument("templateId", HistoricalReadPolicy.TemplateId)).AnyAsync());
        Assert.False(await Rows(data, "idempotencyRecords").Find(new BsonDocument("key", "racing-approve")).AnyAsync());
        Assert.False(await Rows(data, "authorizationHistory").Find(new BsonDocument("operation", "access.grant.approved")).AnyAsync());
    }

    [Fact]
    public async Task Concurrent_target_suspension_reloads_and_preserves_explicit_historical_approval()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actors = await ActorsAsync(data);
        var proposal = await data.Administration.ProposeHistoricalGrantAsync(actors.Maker, Request(data, actors.Subject, Branch), Meta("propose"));
        await RaceApprovalAsync(data, actors.Approver, proposal.Id, () => SuspendTargetAsync(data, Branch), succeeds: true);
        Assert.Equal("suspended", (await Rows(data, "authorizationScopes").Find(new BsonDocument("_id", 11L)).SingleAsync())["status"].AsString);
        Assert.Equal(1, await Rows(data, "authorizationGrants").CountDocumentsAsync(new BsonDocument("templateId", HistoricalReadPolicy.TemplateId)));
    }

    private async Task RaceApprovalAsync(StoreTestData data, ActorContext approver, long proposalId, Func<Task> change, bool succeeds)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume = new ManualResetEventSlim();
        var intercepted = 0;
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command =>
        {
            if (command.CommandName == "update" && command.Command.GetValue("update", "") == "identities" &&
                Interlocked.CompareExchange(ref intercepted, 1, 0) == 0)
            {
                reached.TrySetResult();
                if (!resume.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Historical approval race did not release the transaction barrier");
            }
        });
        var client = new MongoClient(settings);
        var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var service = new AccessAdministrationService(database, new(database, new(client), data.Clock), new(database), data.Clock);
        var pending = Task.Run(() => service.ApproveGrantAsync(approver, proposalId, 1, Meta("racing-approve")));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await change(); }
        finally { resume.Set(); }
        if (succeeds) Assert.Equal("active", (await pending).Status);
        else
        {
            var error = await Record.ExceptionAsync(async () => await pending);
            Assert.True(error is AuthorizationDeniedException or DomainValidationException,
                "Expected current authorization to deny, received: " + (error?.ToString() ?? "successful approval"));
        }
    }

    private async Task<ActorContext> SessionActorAsync(StoreTestData data, ActorContext actor)
    {
        await Rows(data, "identities").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
        var unit = new MongoUnitOfWork(fixture.Client);
        var sessions = new MongoIdentitySessionStore(data.Database, unit, data.Clock, new(actor.ProviderIssuer, "fixture", "fixture"),
            SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var proof = new ProviderIdentityProof(actor.ProviderIssuer, actor.ProviderSubject, "fixture@example.test", true,
            "fixture", data.Clock.UtcNow, data.Clock.UtcNow, "otp", "fixture");
        var issued = await unit.ExecuteAsync((tx, token) => sessions.CreateAsync(tx,
            new(actor.UserId, 1, actor.GrantVersion, actor.ProviderIssuer, actor.ProviderSubject), proof, "fixture", token));
        return actor with { Session = issued.Session.Reference };
    }

    private static async Task<Actors> ActorsAsync(StoreTestData data)
    {
        var maker = await data.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.AccessPropose);
        var subject = await data.AddActorAsync(1001, Organization, Permissions.OrganizationWide);
        var approver = await data.AddActorAsync(1002, Organization, Permissions.AccessApprove, Permissions.AccessRevoke);
        await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", approver.UserId),
            Builders<BsonDocument>.Update.Set("delegablePermissions", new BsonArray { Permissions.HistoryRead, Permissions.ReportRead }));
        return new(maker, subject, approver);
    }
    private static HistoricalGrantChangeRequest Request(StoreTestData data, ActorContext subject, GrantScope scope) =>
        new(subject.UserId, subject.UserId, [Permissions.HistoryRead, Permissions.ReportRead], scope, data.Clock.UtcNow);
    private static IMongoCollection<BsonDocument> Rows(StoreTestData data, string name) => data.Database.GetCollection<BsonDocument>(name);
    private static Task SuspendTargetAsync(StoreTestData data, GrantScope target) => Rows(data, "authorizationScopes").UpdateOneAsync(
        new BsonDocument("_id", target.DivisionId ?? target.BranchId!.Value), Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L));
    private sealed record Actors(ActorContext Maker, ActorContext Subject, ActorContext Approver);
}
