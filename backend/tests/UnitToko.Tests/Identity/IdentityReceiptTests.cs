using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Identity;

[Collection("Mongo")]
public sealed class IdentityReceiptTests(MongoFixture fixture)
{
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Reconcile the synthetic identity request", "identity-receipt-test");
    private static readonly AuthorizationReadMetadata ReceiptAudit = new("operation-recovery", "identity-receipt-test");

    [Theory]
    [InlineData("propose", "pending")]
    [InlineData("approve", "approved")]
    [InlineData("rejected", "rejected")]
    [InlineData("withdrawn", "withdrawn")]
    [InlineData("revoked", "revoked")]
    public async Task Every_invitation_command_recovers_its_safe_committed_result(string command, string status)
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var invite = await data.Service.ProposeAsync(data.Maker, data.Request(), Meta(command == "propose" ? "lost" : "propose"));
        var actor = data.Maker;
        if (command == "approve") { actor = data.Checker; invite = await data.Service.ApproveAsync(actor, invite.Id, 1, Meta("lost")); }
        if (command == "rejected") { actor = data.Checker; invite = await data.Service.RejectAsync(actor, invite.Id, 1, Meta("lost")); }
        if (command == "withdrawn") invite = await data.Service.WithdrawAsync(actor, invite.Id, 1, Meta("lost"));
        if (command == "revoked") { await data.Service.ApproveAsync(data.Checker, invite.Id, 1, Meta("approve")); invite = await data.Service.RevokeAsync(actor, invite.Id, 2, Meta("lost")); }
        var operation = "identity.invitation." + command;
        var receipt = await data.Store.Administration.ReadOperationReceiptAsync(actor, operation, "lost", ReceiptAudit);
        Assert.Equal("committed", receipt.State); Assert.Equal(invite.Id, receipt.Result!.Id);
        Assert.Equal(invite.UserId, receipt.Result.SubjectId); Assert.Equal(invite.Version, receipt.Result.Version);
        Assert.Equal("invitation", receipt.Result.Kind); Assert.Equal(status, receipt.Result.Status);
        Assert.Equal(receipt, await data.Store.Administration.ResolveOperationReceiptAsync(actor, operation, "lost", Meta("lost")));
        var json = JsonSerializer.Serialize(receipt);
        Assert.DoesNotContain("new.owner@example.test", json); Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invitation_receipts_remain_actor_owned_and_current_complete_scope_authority_is_required()
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var other = await data.AddActor(2200, new(GrantScopeKind.System), Permissions.InviteManage, Permissions.MembershipPropose, Permissions.AccessPropose);
        var invite = await data.Service.ProposeAsync(data.Maker, data.Request(), Meta("same-key"));
        Assert.Equal("not_confirmed", (await data.Store.Administration.ReadOperationReceiptAsync(other, "identity.invitation.propose", "same-key", ReceiptAudit)).State);
        await data.Service.ApproveAsync(data.Checker, invite.Id, 1, Meta("approval"));
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked").Inc("version", 1L));
        await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId), Builders<BsonDocument>.Update.Inc("grantVersion", 1L).Inc("fence", 1L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.Administration.ReadOperationReceiptAsync(data.Checker, "identity.invitation.approve", "approval", ReceiptAudit));
        // AccessApprove alone does not replace the removed scoped MembershipApprove authority.
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.Administration.ReadOperationReceiptAsync(data.Checker with { GrantVersion = 2 }, "identity.invitation.approve", "approval", ReceiptAudit));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_committed_results_reconcile_without_rewriting_or_exposing_the_original_request(bool recoveryRequest)
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var operation = recoveryRequest ? "identity.recovery.request" : "identity.invitation.propose";
        if (recoveryRequest)
        {
            var owner = await data.AddActor(2201, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
            var recovery = new AccountRecoveryService(data.Store.Database, data.Store.Store, new(data.Store.Database), data.Sessions, data.Protector, data.Store.Clock);
            await recovery.InitiateAsync(data.Maker, owner.UserId, 1, AccountRecoveryKind.Password, Meta("legacy"));
        }
        else await data.Service.ProposeAsync(data.Maker, data.Request(), Meta("legacy"));
        var filter = new BsonDocument { { "actorId", data.Maker.UserId }, { "operation", operation }, { "key", "legacy" } };
        var update = Builders<BsonDocument>.Update.Unset("result.subjectId").Unset("result.kind");
        if (recoveryRequest) update = update.Unset("result.version");
        await data.Rows("idempotencyRecords").UpdateOneAsync(filter, update);
        var receipt = await data.Store.Administration.ReadOperationReceiptAsync(data.Maker, operation, "legacy", ReceiptAudit);
        Assert.Equal("committed", receipt.State); Assert.Equal(1, receipt.Result!.Version);
        Assert.Equal(recoveryRequest ? "blocked" : "pending", receipt.Result.Status);
        Assert.Equal(recoveryRequest ? "recovery" : "invitation", receipt.Result.Kind);
        // An inconsistent canonical subject must not become a falsely confirmed result.
        await data.Rows("idempotencyRecords").UpdateOneAsync(filter, Builders<BsonDocument>.Update.Set("result.subjectId", 999999L));
        var error = await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Store.Administration.ReadOperationReceiptAsync(data.Maker, operation, "legacy", ReceiptAudit));
        Assert.Equal("receipt_unavailable", error.Code);
    }

    [Fact]
    public async Task Approval_receipt_keeps_original_result_after_redemption_but_rechecks_complete_delegation()
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var invite = await data.Approve("completed"); var (binding, _) = await data.Binding(invite.Id);
        await data.Admit(data.Proof(), binding);
        var receipt = await data.Store.Administration.ReadOperationReceiptAsync(data.Checker, "identity.invitation.approve", "completed-approve", ReceiptAudit);
        Assert.Equal("approved", receipt.Result!.Status); Assert.Equal(2, receipt.Result.Version);
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 1100L),
            Builders<BsonDocument>.Update.Pull("delegablePermissions", Permissions.OrderSubmit).Inc("version", 1L));
        await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId), Builders<BsonDocument>.Update.Inc("grantVersion", 1L).Inc("fence", 1L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.Administration.ReadOperationReceiptAsync(
            data.Checker with { GrantVersion = 2 }, "identity.invitation.approve", "completed-approve", ReceiptAudit));
    }

    [Fact]
    public async Task Owner_recovery_receipt_survives_session_revocation_after_fresh_reauthentication_without_admin_permission()
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var owner = await data.AddActor(2201, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var proof = data.Proof() with { Subject = owner.ProviderSubject, Email = "owner@example.test" };
        var before = await data.Admit(proof, null);
        var actor = Actor(before);
        var recovery = new AccountRecoveryService(data.Store.Database, data.Store.Store, new(data.Store.Database), data.Sessions, data.Protector, data.Store.Clock);
        var result = await recovery.InitiateAsync(actor, owner.UserId, 1, AccountRecoveryKind.Password, Meta("lost-recovery"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.Administration.ReadOperationReceiptAsync(actor, "identity.recovery.request", "lost-recovery", ReceiptAudit));
        Assert.Null(await data.Sessions.ReadAsync(before.OpaqueToken));
        var reauthenticated = Actor(await data.Admit(proof, null));
        var receipt = await data.Store.Administration.ReadOperationReceiptAsync(reauthenticated, "identity.recovery.request", "lost-recovery", ReceiptAudit);
        Assert.Equal("committed", receipt.State); Assert.Equal(result.Id, receipt.Result!.Id); Assert.Equal(owner.UserId, receipt.Result.SubjectId);
        Assert.Equal("recovery", receipt.Result.Kind); Assert.Equal("blocked", receipt.Result.Status); Assert.Equal(result.SecurityVersion, receipt.Result.SecurityVersion);
        Assert.Equal(receipt, await data.Store.Administration.ResolveOperationReceiptAsync(reauthenticated, "identity.recovery.request", "lost-recovery", Meta("lost-recovery")));
        Assert.Equal(1, await data.Rows("identityProviderIntents").CountDocumentsAsync(new BsonDocument("userId", owner.UserId)));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.Administration.ReadOperationReceiptAsync(reauthenticated with { AuthenticatedAt = data.Store.Clock.UtcNow.AddMinutes(-6) }, "identity.recovery.request", "lost-recovery", ReceiptAudit));
    }

    [Fact]
    public async Task Admin_recovery_receipt_requires_current_permission_for_the_other_account()
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var owner = await data.AddActor(2201, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var recovery = new AccountRecoveryService(data.Store.Database, data.Store.Store, new(data.Store.Database), data.Sessions, data.Protector, data.Store.Clock);
        await recovery.InitiateAsync(data.Maker, owner.UserId, 1, AccountRecoveryKind.Factor, Meta("admin-recovery"));
        var receipt = await data.Store.Administration.ReadOperationReceiptAsync(data.Maker, "identity.recovery.request", "admin-recovery", ReceiptAudit);
        Assert.Equal("blocked", receipt.Result!.Status);
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Maker.UserId), Builders<BsonDocument>.Update.Pull("permissions", Permissions.RecoveryInitiate).Inc("version", 1L));
        await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", data.Maker.UserId), Builders<BsonDocument>.Update.Inc("grantVersion", 1L).Inc("fence", 1L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Store.Administration.ReadOperationReceiptAsync(data.Maker with { GrantVersion = 2 }, "identity.recovery.request", "admin-recovery", ReceiptAudit));
    }

    [Fact]
    public async Task Closing_absent_identity_operation_blocks_delayed_original_without_claiming_success()
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        const string operation = "identity.invitation.propose";
        Assert.Equal("not_confirmed", (await data.Store.Administration.ReadOperationReceiptAsync(data.Maker, operation, "close-before-send", ReceiptAudit)).State);
        var closed = await data.Store.Administration.ResolveOperationReceiptAsync(data.Maker, operation, "close-before-send", Meta("close-before-send"));
        Assert.Equal("closed", closed.State); Assert.Null(closed.Result);
        var conflict = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.ProposeAsync(data.Maker, data.Request(), Meta("close-before-send")));
        Assert.Equal("idempotency_key_reused", conflict.Code);
        Assert.Equal(0, await data.Rows("accountInvitations").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Identity_history_reports_minimal_redemption_and_recovery_metadata_without_email_or_delivery_material()
    {
        await using var data = await OnboardingTests.Data.CreateAsync(fixture);
        var invite = await data.Approve("history"); var (binding, _) = await data.Binding(invite.Id);
        await data.Admit(data.Proof(), binding);
        var page = await data.Store.Administration.ListHistoryAsync(data.Checker, new("access-review", "identity-history"), 0, 100);
        var redemption = Assert.Single(page.Items, item => item.Operation == "identity.invitation.redeemed");
        Assert.Equal(invite.Id, redemption.ResourceId); Assert.Equal("redeemed", redemption.Status); Assert.Equal(3, redemption.AfterVersion);
        Assert.DoesNotContain("new.owner@example.test", JsonSerializer.Serialize(page));
        Assert.DoesNotContain("secretHash", JsonSerializer.Serialize(page));
    }

    private static ActorContext Actor(SessionIssue issue) => new(issue.Session.UserId, issue.Session.Proof.Issuer, issue.Session.Proof.Subject,
        issue.Session.GrantVersion, issue.Session.Proof.FactorCompletedAt, FactorAssurance.MultiFactor, issue.Session.Reference);
}
