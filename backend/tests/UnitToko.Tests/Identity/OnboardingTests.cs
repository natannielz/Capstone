using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Identity;

[Collection("Mongo")]
public sealed class OnboardingTests(MongoFixture fixture)
{
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Reviewed synthetic account access", "onboarding-test");
    private static readonly AuthorizationReadMetadata ReadAudit = new("access-review", "directory-test");

    [Fact]
    public async Task Closed_invitation_preserves_independent_approval_and_accepts_only_verified_exact_owner_once()
    {
        await using var data = await Data.CreateAsync(fixture);
        var request = data.Request();
        var pending = await data.Service.ProposeAsync(data.Maker, request, Meta("propose"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ApproveAsync(data.Maker, pending.Id, 1, Meta("self-approve")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Admit(data.Proof(), null));
        var approved = await data.Service.ApproveAsync(data.Checker, pending.Id, 1, Meta("approve"));
        Assert.Equal(approved, await data.Service.ApproveAsync(data.Checker, pending.Id, 1, Meta("approve")));
        var (binding, secret) = await data.Binding(approved.Id);
        var raw = await data.Rows("accountInvitations").Find(new BsonDocument("_id", approved.Id)).SingleAsync();
        Assert.DoesNotContain(secret, raw.ToJson());
        Assert.DoesNotContain(secret, (await data.Rows("authorizationHistory").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync()).ToJson());
        Assert.DoesNotContain(secret, (await data.Rows("idempotencyRecords").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync()).ToJson());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Admit(data.Proof() with { Email = "wrong@example.test" }, binding));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Admit(data.Proof() with { EmailVerified = false }, binding));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Admit(data.Proof() with { FactorType = "sms" }, binding));
        // Approval freshness was checked when approved. Redemption ten minutes later rechecks live
        // authority without manufacturing a fresh factor event for the approver.
        data.Store.Clock.UtcNow = data.Store.Clock.UtcNow.AddMinutes(10);
        var session = await data.Admit(data.Proof(), binding);
        Assert.Equal(approved.UserId, session.Session.UserId);
        var profile = await data.Rows("accountProfiles").Find(new BsonDocument("_id", approved.UserId)).SingleAsync();
        Assert.Equal("new.owner@example.test", profile["verifiedEmail"].AsString); Assert.Equal("Verified provider display", profile["displayName"].AsString);
        Assert.Equal("redeemed", (await data.Rows("accountInvitations").Find(new BsonDocument("_id", approved.Id)).SingleAsync())["status"].AsString);
        var grant = await data.Rows("authorizationGrants").Find(new BsonDocument("userId", approved.UserId)).SingleAsync();
        Assert.Contains(Permissions.OrderSubmit, grant["permissions"].AsBsonArray.Select(value => value.AsString));
        Assert.Equal(data.Maker.UserId, grant["requestedBy"].AsInt64); Assert.Equal(data.Checker.UserId, grant["approvedBy"].AsInt64);
        var history = (await data.Rows("authorizationHistory").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync()).ToJson();
        Assert.DoesNotContain("new.owner@example.test", history); Assert.DoesNotContain("synthetic-challenge", history);
        Assert.DoesNotContain("protectedPayload", history); Assert.DoesNotContain("secretHash", history);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Admit(data.Proof(), binding));
        // An equal email is not an identity binding and cannot silently merge a new provider subject.
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Admit(data.Proof() with { Subject = "different-provider-subject" }, null));
    }

    [Fact]
    public async Task Approved_invitation_cannot_move_to_a_different_configured_provider_connection()
    {
        await using var data = await Data.CreateAsync(fixture);
        var approved = await data.Approve("provider-bind"); var (binding, secret) = await data.Binding(approved.Id);
        var changed = new IdentityOnboardingService(data.Store.Database, data.Store.Store, new(data.Store.Database), data.Protector,
            new("https://approved.example/", "different-db", "v1"), data.Store.Clock);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => changed.BindInvitationAsync(approved.Id, secret));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync((tx, token) =>
            changed.AdmitAsync(tx, data.Proof() with { ConnectionId = "different-db" }, binding, token)));
        Assert.False(await data.Rows("identities").Find(new BsonDocument("_id", approved.UserId)).AnyAsync());
    }

    [Fact]
    public async Task Concurrent_redemption_has_one_winner_and_failed_session_creation_rolls_redemption_back()
    {
        await using var data = await Data.CreateAsync(fixture);
        var invite = await data.Approve("race"); var (binding, _) = await data.Binding(invite.Id);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Unit.ExecuteAsync(async (tx, token) =>
        {
            var admission = await data.Service.AdmitAsync(tx, data.Proof(), binding, token);
            return await data.Sessions.CreateAsync(tx, admission with { SecurityVersion = 99 }, data.Proof(), "{}", token);
        }));
        Assert.Equal("approved", (await data.Rows("accountInvitations").Find(new BsonDocument("_id", invite.Id)).SingleAsync())["status"].AsString);
        Assert.False(await data.Rows("identities").Find(new BsonDocument("_id", invite.UserId)).AnyAsync());
        async Task<bool> Redeem() { try { await data.Admit(data.Proof(), binding); return true; } catch (AuthorizationDeniedException) { return false; } catch (MongoWriteException) { return false; } }
        var results = await Task.WhenAll(Redeem(), Redeem());
        Assert.Equal(1, results.Count(value => value));
        Assert.Equal(1, await data.Rows("identitySessions").CountDocumentsAsync(new BsonDocument("userId", invite.UserId)));
        Assert.Equal(1, await data.Rows("memberships").CountDocumentsAsync(new BsonDocument("userId", invite.UserId)));
    }

    [Theory]
    [InlineData("approver-revoked")]
    [InlineData("requester-revoked")]
    [InlineData("branch-suspended")]
    [InlineData("expired")]
    public async Task Redemption_rechecks_current_authority_hierarchy_and_expiry(string change)
    {
        await using var data = await Data.CreateAsync(fixture);
        var invite = await data.Approve("recheck"); var (binding, _) = await data.Binding(invite.Id);
        if (change == "expired") data.Store.Clock.UtcNow = data.Store.Clock.UtcNow.AddDays(2);
        else if (change == "branch-suspended")
            await data.Rows("authorizationScopes").UpdateOneAsync(new BsonDocument("_id", 11L), Builders<BsonDocument>.Update.Set("status", "suspended").Inc("version", 1L).Inc("fence", 1L));
        else
            await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", change == "approver-revoked" ? data.Checker.UserId : data.Maker.UserId),
                Builders<BsonDocument>.Update.Inc("grantVersion", 1L).Inc("fence", 1L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Admit(data.Proof(), binding));
        Assert.False(await data.Rows("identities").Find(new BsonDocument("_id", invite.UserId)).AnyAsync());
        Assert.Equal("approved", (await data.Rows("accountInvitations").Find(new BsonDocument("_id", invite.Id)).SingleAsync())["status"].AsString);
    }

    [Fact]
    public async Task Scoped_directory_and_pending_invitations_filter_before_counts_pages_and_direct_user_lookup()
    {
        await using var data = await Data.CreateAsync(fixture);
        var memberA = await data.AddActor(2001, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var memberB = await data.AddActor(2002, new(GrantScopeKind.BuyerOwned, 10, 12), Permissions.OrderRead);
        await data.Admit(data.Proof() with { Subject = memberA.ProviderSubject, Email = "a@example.test", DisplayName = "Member A" }, null);
        await data.Admit(data.Proof() with { Subject = memberB.ProviderSubject, Email = "b@example.test", DisplayName = "Member B" }, null);
        var first = await data.Service.ListProfilesAsync(data.Checker, 0, 1, ReadAudit);
        Assert.Equal(1, first.Total); Assert.Equal(memberA.UserId, Assert.Single(first.Items).UserId);
        var outside = await data.Service.ListProfilesAsync(data.Checker, 0, 1, ReadAudit, memberB.UserId);
        Assert.Equal(0, outside.Total); Assert.Empty(outside.Items);
        await data.Service.ProposeAsync(data.Maker, data.Request(), Meta("in-scope"));
        await data.Service.ProposeAsync(data.Maker, data.Request() with { Scope = new(GrantScopeKind.BuyingBranch, 10, 12) }, Meta("other-branch"));
        var pending = await data.Service.ListInvitationsAsync(data.Checker, 0, 1, ReadAudit);
        Assert.Equal(1, pending.Total); Assert.Contains("approve", Assert.Single(pending.Items).AllowedActions);
        Assert.Equal(3, await data.Rows("identityReadEvents").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Invitation_rejection_withdrawal_and_revocation_are_versioned_and_never_dispatch_delivery()
    {
        await using var data = await Data.CreateAsync(fixture);
        var pending = await data.Service.ProposeAsync(data.Maker, data.Request(), Meta("reject-propose"));
        var rejected = await data.Service.RejectAsync(data.Checker, pending.Id, 1, Meta("reject"));
        Assert.Equal("rejected", rejected.Status); Assert.Equal(rejected, await data.Service.RejectAsync(data.Checker, pending.Id, 1, Meta("reject")));
        var withdrawn = await data.Service.ProposeAsync(data.Maker, data.Request(), Meta("withdraw-propose"));
        Assert.Equal("withdrawn", (await data.Service.WithdrawAsync(data.Maker, withdrawn.Id, 1, Meta("withdraw"))).Status);
        var approved = await data.Approve("revoke"); var (_, secret) = await data.Binding(approved.Id);
        Assert.Equal("revoked", (await data.Service.RevokeAsync(data.Maker, approved.Id, 2, Meta("revoke"))).Status);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.BindInvitationAsync(approved.Id, secret));
        Assert.All(await data.Rows("identityProviderIntents").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(), row => Assert.Equal("blocked", row["status"].AsString));
    }

    [Fact]
    public async Task Recovery_revokes_sessions_before_blocked_owner_intent_and_rolls_back_if_intent_cannot_persist()
    {
        await using var data = await Data.CreateAsync(fixture);
        var owner = await data.AddActor(2001, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var proof = data.Proof() with { Subject = owner.ProviderSubject, Email = "owner@example.test" };
        var oldSession = await data.Admit(proof, null);
        var recovery = new AccountRecoveryService(data.Store.Database, data.Store.Store, new(data.Store.Database), data.Sessions, data.Protector, data.Store.Clock);
        var result = await recovery.InitiateAsync(data.Maker, owner.UserId, 1, AccountRecoveryKind.Password, Meta("recover"));
        Assert.Equal(2, result.SecurityVersion); Assert.Equal("blocked", result.Status);
        Assert.Null(await data.Sessions.ReadAsync(oldSession.OpaqueToken));
        var intent = await data.Rows("identityProviderIntents").Find(new BsonDocument("_id", result.Id)).SingleAsync();
        Assert.Equal("passwordRecovery", intent["kind"].AsString); Assert.DoesNotContain(owner.ProviderSubject, intent.ToJson());
        Assert.Equal(result, await recovery.InitiateAsync(data.Maker, owner.UserId, 1, AccountRecoveryKind.Password, Meta("recover")));
        var freshSession = await data.Admit(proof, null);
        await data.Store.Database.RunCommandAsync<BsonDocument>(new BsonDocument
            { { "collMod", "identityProviderIntents" }, { "validator", new BsonDocument("$expr", false) }, { "validationLevel", "strict" } });
        await Assert.ThrowsAsync<MongoWriteException>(() => recovery.InitiateAsync(data.Maker, owner.UserId, 2, AccountRecoveryKind.Factor, Meta("must-roll-back")));
        Assert.NotNull(await data.Sessions.ReadAsync(freshSession.OpaqueToken));
        Assert.Equal(2L, (await data.Rows("identities").Find(new BsonDocument("_id", owner.UserId)).SingleAsync())["securityVersion"].AsInt64);
        Assert.Equal(1, await data.Rows("identityProviderIntents").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    internal sealed class Data : IAsyncDisposable
    {
        public required StoreTestData Store { get; init; }
        public required MongoUnitOfWork Unit { get; init; }
        public required IdentityOnboardingService Service { get; init; }
        public required MongoIdentitySessionStore Sessions { get; init; }
        public required SessionPayloadProtector Protector { get; init; }
        public required ActorContext Maker { get; init; }
        public required ActorContext Checker { get; init; }
        public IMongoCollection<BsonDocument> Rows(string name) => Store.Database.GetCollection<BsonDocument>(name);
        public ProviderIdentityProof Proof() => new("https://approved.example/", "new-provider-owner", "NEW.OWNER@example.test", true,
            "approved-db", Store.Clock.UtcNow, Store.Clock.UtcNow, "otp", "v1", "synthetic-challenge", DisplayName: "Verified provider display");
        public InvitationRequest Request() => new("new.owner@example.test", new(GrantScopeKind.BuyingBranch, 10, 11), Store.Clock.UtcNow.AddDays(1),
            new("buyer", [], [Permissions.BranchPic], []));
        public async Task<ActorContext> AddActor(long id, GrantScope scope, params string[] permissions)
        {
            var actor = await Store.AddActorAsync(id, scope, permissions);
            await Rows("identities").UpdateOneAsync(new BsonDocument("_id", id), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
            return actor;
        }
        public async Task<InvitationResult> Approve(string key)
        {
            var invitation = await Service.ProposeAsync(Maker, Request(), Meta(key + "-propose"));
            return await Service.ApproveAsync(Checker, invitation.Id, 1, Meta(key + "-approve"));
        }
        public async Task<(InvitationRedemptionBinding Binding, string Secret)> Binding(long id)
        {
            var row = await Rows("identityProviderIntents").Find(new BsonDocument("invitationId", id)).SingleAsync();
            var payload = BsonDocument.Parse(Protector.Unprotect("invitation-delivery", row["protectedPayload"].AsString));
            var secret = payload["secret"].AsString;
            return (await Service.BindInvitationAsync(id, secret), secret);
        }
        public Task<SessionIssue> Admit(ProviderIdentityProof proof, InvitationRedemptionBinding? binding) => Unit.ExecuteAsync(async (tx, token) =>
        {
            var admission = await Service.AdmitAsync(tx, proof, binding, token);
            return await Sessions.CreateAsync(tx, admission, proof, "{}", token);
        });
        public static async Task<Data> CreateAsync(MongoFixture fixture)
        {
            var store = await StoreTestData.CreateAsync(fixture);
            store.Clock.UtcNow = DateTimeOffset.UtcNow > store.Clock.UtcNow ? DateTimeOffset.UtcNow : store.Clock.UtcNow;
            var unit = new MongoUnitOfWork(fixture.Client);
            var protector = SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider());
            var policy = new SessionPolicy("https://approved.example/", "approved-db", "v1");
            var maker = await store.AddActorAsync(1000, new(GrantScopeKind.System), Permissions.InviteManage, Permissions.MembershipPropose, Permissions.AccessPropose, Permissions.RecoveryInitiate);
            var checker = await store.AddActorAsync(1001, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.MembershipApprove);
            await store.AddGrantAsync(checker, 1100, new(GrantScopeKind.BuyingBranch, 10, 11), [Permissions.AccessApprove],
                RoleTemplates.Materialize("buyer", [], [Permissions.BranchPic]).Permissions.ToArray());
            await store.Database.GetCollection<BsonDocument>("identities").UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Set("securityVersion", 1L));
            foreach (var sequence in new[] { "identities", "memberships", "authorizationGrants" })
                await store.Database.GetCollection<BsonDocument>("idCounters").UpdateOneAsync(new BsonDocument("_id", sequence), Builders<BsonDocument>.Update.Max("value", 10_000L), new() { IsUpsert = true });
            return new() { Store = store, Unit = unit, Protector = protector, Maker = maker, Checker = checker,
                Sessions = new(store.Database, unit, store.Clock, policy, protector), Service = new(store.Database, store.Store, new(store.Database), protector, policy, store.Clock) };
        }
        public ValueTask DisposeAsync() => Store.DisposeAsync();
    }
}
