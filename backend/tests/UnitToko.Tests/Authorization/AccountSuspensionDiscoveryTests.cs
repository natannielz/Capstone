using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class AccountSuspensionDiscoveryTests(MongoFixture fixture)
{
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Penangguhan akun ditinjau oleh petugas sistem", "account-suspension-test");

    [Fact]
    public async Task Only_current_system_authority_advertises_other_active_account_suspension_and_command_keeps_scoped_access_unchanged()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var system = await data.AddActorAsync(2100, new(GrantScopeKind.System), Permissions.AccessRevoke, Permissions.AccessAuditRead);
        var branch = await data.AddActorAsync(2101, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.AccessRevoke);
        var target = await data.AddActorAsync(2102, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var beforeScoped = await ScopedAsync(data, target.UserId);

        Assert.Equal(new[] { "suspend" }, (await data.Administration.ReadSubjectAsync(system, target.UserId)).AllowedActions.ToArray());
        var branchView = await data.Administration.ReadSubjectAsync(branch, target.UserId);
        Assert.Empty(branchView.AllowedActions);
        Assert.Contains("suspend", Assert.Single(branchView.Memberships).AllowedActions);
        Assert.Contains("revoke", Assert.Single(branchView.Grants).AllowedActions);
        Assert.Equal("scope_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Administration.SuspendIdentityAsync(branch, target.UserId, 1, Meta("branch-cannot-disable-account")))).Code);

        var command = Meta("system-disable-once");
        var saved = await data.Administration.SuspendIdentityAsync(system, target.UserId, 1, command);
        Assert.Equal("suspended", saved.Status); Assert.Equal(2L, saved.GrantVersion);
        Assert.Equal(saved, await data.Administration.SuspendIdentityAsync(system, target.UserId, 1, command));
        var disabled = await data.Administration.ReadSubjectAsync(system, target.UserId);
        Assert.Equal("suspended", disabled.Status); Assert.Equal(2L, disabled.GrantVersion); Assert.Empty(disabled.AllowedActions);
        Assert.True(beforeScoped.Equals(await ScopedAsync(data, target.UserId)), "Account suspension must preserve exact scoped membership/grant rows.");
        Assert.Equal("stale_or_inactive_actor", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Store.ReadCurrentAsync(target))).Code);
        Assert.Equal("stale_or_inactive_actor", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Store.ReadCurrentAsync(target with { GrantVersion = saved.GrantVersion }))).Code);
        Assert.Equal(1L, await Rows(data, "authorizationHistory").CountDocumentsAsync(new BsonDocument("operation", "access.identity.suspended")));
        Assert.Equal(1L, await Rows(data, "idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "access.identity.suspend")));
    }

    [Fact]
    public async Task Stale_MFA_expired_or_revoked_system_authority_cannot_advertise_or_execute_account_suspension()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var actor = await data.AddActorAsync(2200, new(GrantScopeKind.System), Permissions.AccessAuditRead);
        await data.AddGrantAsync(actor, 2203, new(GrantScopeKind.System), [Permissions.AccessRevoke]);
        var guardian = await data.AddActorAsync(2201, new(GrantScopeKind.System), Permissions.AccessRevoke);
        var target = await data.AddActorAsync(2202, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        Assert.Equal(new[] { "suspend" }, (await data.Administration.ReadSubjectAsync(actor, target.UserId)).AllowedActions.ToArray());

        var stale = actor with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) };
        Assert.Empty((await data.Administration.ReadSubjectAsync(stale, target.UserId)).AllowedActions);
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Administration.SuspendIdentityAsync(stale, target.UserId, 1, Meta("stale-mfa")))).Code);

        // Explicit finite expiry belongs to this synthetic grant before the read/command being checked.
        Assert.Equal(1L, (await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 2203L),
            Builders<BsonDocument>.Update.Set("expiresAt", data.Clock.UtcNow.AddSeconds(1).UtcDateTime))).ModifiedCount);
        data.Clock.UtcNow = data.Clock.UtcNow.AddSeconds(1);
        Assert.Empty((await data.Administration.ReadSubjectAsync(actor, target.UserId)).AllowedActions);
        Assert.Equal("permission_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Administration.SuspendIdentityAsync(actor, target.UserId, 1, Meta("expired-grant")))).Code);

        var revoked = await data.Administration.RevokeGrantAsync(guardian, 2203, 1, Meta("revoke-authority"));
        Assert.Equal("stale_or_inactive_actor", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Administration.ReadSubjectAsync(actor, target.UserId))).Code);
        var current = actor with { GrantVersion = revoked.GrantVersion };
        Assert.Empty((await data.Administration.ReadSubjectAsync(current, target.UserId)).AllowedActions);
        Assert.Equal("permission_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Administration.SuspendIdentityAsync(current, target.UserId, 1, Meta("revoked-grant")))).Code);
        var identity = await Rows(data, "identities").Find(new BsonDocument("_id", target.UserId)).SingleAsync();
        Assert.Equal("active", identity["status"]); Assert.Equal(1L, identity["grantVersion"].AsInt64);
        Assert.Equal(0L, await Rows(data, "authorizationHistory").CountDocumentsAsync(new BsonDocument("operation", "access.identity.suspended")));
        Assert.Equal(0L, await Rows(data, "idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "access.identity.suspend")));
    }

    [Fact]
    public async Task Self_suspension_requires_real_authority_then_refuses_before_identity_history_or_receipt_mutation()
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var system = await data.AddActorAsync(2300, new(GrantScopeKind.System), Permissions.AccessRevoke, Permissions.AccessAuditRead);
        var branch = await data.AddActorAsync(2301, new(GrantScopeKind.BuyingBranch, 10, 11), Permissions.AccessRevoke);
        Assert.Empty((await data.Administration.ReadSubjectAsync(system, system.UserId)).AllowedActions);
        Assert.Empty((await data.Administration.ReadSubjectAsync(branch, branch.UserId)).AllowedActions);
        var before = await PreservedAsync(data);
        Assert.Equal("scope_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Administration.SuspendIdentityAsync(branch, branch.UserId, 1, Meta("scoped-self")))).Code);
        Assert.Equal("identity_self_suspension_forbidden", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            data.Administration.SuspendIdentityAsync(system, system.UserId, 1, Meta("system-self")))).Code);
        var after = await PreservedAsync(data);
        foreach (var field in before.Names)
            Assert.True(before[field].Equals(after[field]), $"{field} changed during refused self-suspension.");
    }

    [Theory]
    [InlineData("mfa", "step_up_required")]
    [InlineData("grant", "permission_denied")]
    public async Task Expiry_after_actual_suspension_audit_rolls_back_all_effects_then_original_key_commits_once(string boundary, string expectedCode)
    {
        await using var data = await StoreTestData.CreateAsync(fixture);
        var system = await data.AddActorAsync(2500, new(GrantScopeKind.System), Permissions.AccessRevoke, Permissions.AccessAuditRead);
        var target = await data.AddActorAsync(2501, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderRead);
        var originalClock = data.Clock.UtcNow;
        if (boundary == "grant")
            Assert.Equal(1L, (await Rows(data, "authorizationGrants").UpdateOneAsync(new BsonDocument("_id", system.UserId),
                Builders<BsonDocument>.Update.Set("expiresAt", originalClock.AddMinutes(1).UtcDateTime))).ModifiedCount);
        Assert.Equal(new[] { "suspend" }, (await data.Administration.ReadSubjectAsync(system, target.UserId)).AllowedActions.ToArray());
        var before = await PreservedAsync(data);
        var previousObserver = fixture.CommandCompleted; var fired = 0;
        var command = Meta("late-" + boundary);
        fixture.CommandCompleted = (name, duration) => {
            previousObserver?.Invoke(name, duration);
            if (name == "insert/authorizationHistory" && Interlocked.CompareExchange(ref fired, 1, 0) == 0)
                data.Clock.UtcNow = originalClock.AddMinutes(boundary == "mfa" ? 6 : 2);
        };
        try {
            Assert.Equal(expectedCode, (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
                data.Administration.SuspendIdentityAsync(system, target.UserId, 1, command))).Code);
            Assert.Equal(1, fired);
        } finally { fixture.CommandCompleted = previousObserver; data.Clock.UtcNow = originalClock; }
        var after = await PreservedAsync(data);
        foreach (var field in before.Names)
            Assert.True(before[field].Equals(after[field]), $"{field} survived a refused final {boundary} boundary.");

        var saved = await data.Administration.SuspendIdentityAsync(system, target.UserId, 1, command);
        Assert.Equal("suspended", saved.Status); Assert.Equal(2L, saved.GrantVersion);
        Assert.Equal(saved, await data.Administration.SuspendIdentityAsync(system, target.UserId, 1, command));
        Assert.Equal(1L, await Rows(data, "authorizationHistory").CountDocumentsAsync(new BsonDocument("operation", "access.identity.suspended")));
        Assert.Equal(1L, await Rows(data, "idempotencyRecords").CountDocumentsAsync(new BsonDocument {
            { "operation", "access.identity.suspend" }, { "key", command.IdempotencyKey } }));
        var identity = await Rows(data, "identities").Find(new BsonDocument("_id", target.UserId)).SingleAsync();
        Assert.Equal("suspended", identity["status"]); Assert.Equal(2L, identity["grantVersion"].AsInt64);
        var recovered = await PreservedAsync(data);
        foreach (var field in new[] { "memberships", "authorizationGrants", "payments", "paymentAllocations", "bankReceipts", "journalEntries", "stockBalances", "movements" })
            Assert.True(before[field].Equals(recovered[field]), $"{field} changed during account suspension recovery.");
    }

    private static IMongoCollection<BsonDocument> Rows(StoreTestData data, string name) => data.Database.GetCollection<BsonDocument>(name);
    private static async Task<BsonDocument> ScopedAsync(StoreTestData data, long userId) => new() {
        { "memberships", new BsonArray(await Rows(data, "memberships").Find(new BsonDocument("userId", userId)).Sort(new BsonDocument("_id", 1)).ToListAsync()) },
        { "grants", new BsonArray(await Rows(data, "authorizationGrants").Find(new BsonDocument("userId", userId)).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
    private static async Task<BsonDocument> PreservedAsync(StoreTestData data)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "identities", "memberships", "authorizationGrants", "authorizationHistory", "idempotencyRecords",
            "payments", "paymentAllocations", "bankReceipts", "journalEntries", "stockBalances", "movements" })
            result[name] = new BsonArray(await Rows(data, name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
}
