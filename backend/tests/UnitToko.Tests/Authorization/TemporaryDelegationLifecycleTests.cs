using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class TemporaryDelegationLifecycleTests(MongoFixture fixture)
{
    private static readonly GrantScope Shop = new(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
    private static AuthorizationCommandMetadata Meta(string key) => new(key, "Reviewed synthetic bounded temporary delegation", "delegation-native");
    private async Task<Data> CreateAsync() {
        Assert.True(CollectionMigrations.CurrentVersion >= 113, "Compose the genuine 110–113 migration prefix before running R10 acceptance");
        var data = await StoreTestData.CreateAsync(fixture);
        try {
            var giver = await data.AddActorAsync(5100, new(GrantScopeKind.System), Permissions.AccessPropose);
            await data.AddGrantAsync(giver, 5101, Shop, [Permissions.StockRead, Permissions.StockCount], [Permissions.StockRead]);
            var recipient = await data.AddActorAsync(5200, Shop, Permissions.CatalogRead);
            var checker = await data.AddActorAsync(5300, Shop, Permissions.AccessApprove, Permissions.AccessRevoke);
            await data.Database.GetCollection<BsonDocument>("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", checker.UserId),
                Builders<BsonDocument>.Update.Set("delegablePermissions", new BsonArray { Permissions.StockRead }));
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, giver.UserId, recipient.UserId, checker.UserId);
            var request = new TemporaryDelegationRequest(5101, 1, recipient.UserId, recipient.UserId, Shop, [Permissions.StockRead], data.Clock.UtcNow, data.Clock.UtcNow.AddHours(1));
            return new(data, giver, recipient, checker, request);
        } catch {await data.DisposeAsync(); throw;}
    }
    [Fact]
    public async Task Advancing_clock_keeps_first_immutable_state_and_event_timestamp_identical() {
        await using var d = await CreateAsync();
        var clock = new AdvancingClock(d.Base.Clock.UtcNow);
        var service = new TemporaryDelegationService(d.Base.Database, new MongoUnitOfWork((MongoClient)d.Base.Database.Client), new(d.Base.Database), clock);
        var result = await service.ChangeAsync(d.Giver, "propose", null, 0, d.Request, Meta("advancing-clock"));
        var detail = await service.ReadAsync(d.Giver, result["delegationId"].AsInt64);
        var original = await d.Rows(TemporaryDelegationRecords.Events).Find(new BsonDocument("_id", result["eventId"])).SingleAsync();
        Assert.Equal(original["recordedAt"], original["after"]["createdAt"]);
        Assert.Equal(original["recordedAt"], original["after"]["updatedAt"]);
        Assert.Equal(result, await service.RecoverAsync(d.Giver, "advancing-clock"));
        Assert.Single(detail.History);
    }
    [Fact]
    public async Task Governed_lifecycle_preserves_original_results_and_handover_without_business_effects() {
        await using var d = await CreateAsync();
        var proposed = await d.Service.ChangeAsync(d.Giver, "propose", null, 0, d.Request, Meta("propose"));
        Assert.Equal(proposed, await d.Service.ChangeAsync(d.Giver, "propose", null, 0, d.Request, Meta("propose")));
        var id = proposed["delegationId"].AsInt64;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.ChangeAsync(d.Giver, "approve", id, 1, null, Meta("self")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.ChangeAsync(d.Recipient, "approve", id, 1, null, Meta("beneficiary")));
        var approved = await d.Service.ChangeAsync(d.Checker, "approve", id, 1, null, Meta("approve"));
        var recipient = d.Recipient with { GrantVersion = 2 };
        Assert.True(ScopePolicy.Compile(recipient, await d.Base.Store.ReadCurrentAsync(recipient), Permissions.StockRead, d.Base.Clock.UtcNow).IsAllowed);
        var handover = await d.Service.ChangeAsync(recipient, "handover", id, 2, null, Meta("handover"));
        Assert.Equal("active", handover["status"].AsString);
        Assert.Equal("unavailable", (await d.Service.ReadAsync(recipient, id)).Item.TaskReassignmentState);
        var revoked = await d.Service.ChangeAsync(d.Giver, "revoke", id, 3, null, Meta("revoke"));
        Assert.Equal("revoked", revoked["status"].AsString);
        Assert.Equal(proposed, await d.Service.RecoverAsync(d.Giver, "propose"));
        Assert.Equal(approved, await d.Service.RecoverAsync(d.Checker, "approve"));
        Assert.Equal(4, (await d.Service.ReadAsync(d.Giver, id)).History.Length);
        Assert.False(ScopePolicy.Compile(recipient with { GrantVersion = 3 }, await d.Base.Store.ReadCurrentAsync(recipient with { GrantVersion = 3 }), Permissions.StockRead, d.Base.Clock.UtcNow).IsAllowed);
        Assert.Equal(4, await d.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", TemporaryDelegationRecords.Operation)));
        foreach (var collection in new[] { "orders", "stockMovements", "payments" }) Assert.Equal(0, await d.Rows(collection).CountDocumentsAsync(new BsonDocument()));
    }
    [Theory]
    [InlineData("grant")]
    [InlineData("membership")]
    [InlineData("identity")]
    [InlineData("scope")]
    public async Task Existing_source_revocation_owners_invalidate_beneficiary_sessions_and_derived_access(string kind) {
        await using var d = await CreateAsync(); var id = await d.ActivateAsync();
        var recipient = d.Recipient with { GrantVersion = 2 };
        if (kind == "grant") await d.Base.Administration.RevokeGrantAsync(d.Checker, 5101, 1, Meta("revoke-source"));
        else if (kind == "membership") await d.Base.Administration.SuspendMembershipAsync(d.Checker, 5101, 1, Meta("suspend-source-membership"));
        else {
            var administrator = await d.Base.AddActorAsync(5400, new(GrantScopeKind.System), Permissions.AccessRevoke);
            if (kind == "identity") await d.Base.Administration.SuspendIdentityAsync(administrator, d.Giver.UserId, 1, Meta("suspend-source-account"));
            else await d.Base.Administration.SuspendScopeAsync(administrator, 101, 1, Meta("suspend-source-shop"));
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Base.Store.ReadCurrentAsync(recipient));
        var identity = await d.Rows("identities").Find(new BsonDocument("_id", recipient.UserId)).SingleAsync();
        var current = recipient with { GrantVersion = identity["grantVersion"].AsInt64 };
        Assert.False(ScopePolicy.Compile(current, await d.Base.Store.ReadCurrentAsync(current), Permissions.StockRead, d.Base.Clock.UtcNow).IsAllowed);
        var giverIdentity = await d.Rows("identities").Find(new BsonDocument("_id", d.Giver.UserId)).SingleAsync();
        if (giverIdentity["status"] == "active") Assert.Equal("unavailable", (await d.Service.ReadAsync(d.Giver with { GrantVersion = giverIdentity["grantVersion"].AsInt64 }, id)).Item.SourceState);
        Assert.Equal(0, await d.Rows("fixtureBusinessRecords").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Exact_expiry_after_owner_write_rolls_back_business_and_receipt() {
        await using var d = await CreateAsync(); await d.ActivateAsync(); var recipient = d.Recipient with { GrantVersion = 2 };
        var failure = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Base.Store.ExecuteAuthorizedAsync(recipient, "fixture.delegation.expiry", Permissions.StockRead,
            Meta("expiry-write"), new { id = 1 }, (_, _) => Task.FromResult(new AuthorizationTarget(new(FulfillmentOrganizationId: 100, ShopId: 101, ChannelId: 103))),
            async (tx, ct) => {await d.Rows("fixtureBusinessRecords").InsertOneAsync(tx, new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 } }, cancellationToken: ct);
                d.Base.Clock.UtcNow = d.Request.ExpiresAt; return new BsonDocument("id", 1L);}));
        Assert.Equal("delegation_expired", failure.Code);
        Assert.Equal(0, await d.Rows("fixtureBusinessRecords").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await d.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "expiry-write")));
        Assert.False(ScopePolicy.Compile(recipient, await d.Base.Store.ReadCurrentAsync(recipient), Permissions.StockRead, d.Base.Clock.UtcNow).IsAllowed);
    }
    [Fact]
    public async Task Clock_rollback_after_initial_authorization_cannot_preserve_a_captured_grant() {
        await using var d = await CreateAsync(); await d.ActivateAsync(); var recipient = d.Recipient with { GrantVersion = 2 };
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Base.Store.ExecuteAuthorizedAsync(recipient,
            "fixture.delegation.clock", Permissions.StockRead, Meta("clock-write"), new { id = 3 },
            (_, _) => Task.FromResult(new AuthorizationTarget(new(FulfillmentOrganizationId: 100, ShopId: 101, ChannelId: 103))),
            async (tx, ct) => {await d.Rows("fixtureBusinessRecords").InsertOneAsync(tx, new BsonDocument { { "_id", 3L }, { "schemaVersion", 1 } }, cancellationToken: ct);
                d.Base.Clock.UtcNow = d.Base.Clock.UtcNow.AddMilliseconds(-1); return new BsonDocument("id", 3L);}));
        Assert.Equal("delegation_expired", denied.Code);
        Assert.Equal(0, await d.Rows("fixtureBusinessRecords").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await d.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "clock-write")));
    }
    [Fact]
    public async Task Expired_source_person_evidence_removes_access_but_keeps_original_history_and_revocation_available() {
        await using var d = await CreateAsync(); var expiry = d.Base.Clock.UtcNow.AddMinutes(2);
        await d.Rows("personEvidenceVersions").UpdateOneAsync(new BsonDocument("_id", PersonTestData.EvidenceId(d.Giver.UserId)),
            Builders<BsonDocument>.Update.Set("expiresAt", expiry.UtcDateTime));
        var id = await d.ActivateAsync(); d.Base.Clock.UtcNow = expiry;
        var recipient = d.Recipient with { GrantVersion = 2 };
        Assert.False(ScopePolicy.Compile(recipient, await d.Base.Store.ReadCurrentAsync(recipient), Permissions.StockRead, expiry).IsAllowed);
        var detail = await d.Service.ReadAsync(d.Giver, id);
        Assert.Equal("unavailable", detail.Item.SourceState); Assert.Equal(2, detail.History.Length);
        Assert.NotNull(await d.Service.RecoverAsync(d.Giver, "create"));
        Assert.Equal("revoked", (await d.Service.ChangeAsync(d.Giver, "revoke", id, 2, null, Meta("revoke-expired-source")))["status"].AsString);
    }
    [Fact]
    public async Task Receipt_orphan_and_history_tampering_never_recover_or_materialize_access() {
        await using var d = await CreateAsync(); var id = await d.ActivateAsync();
        var receipt = await d.Rows("idempotencyRecords").Find(new BsonDocument { { "operation", TemporaryDelegationRecords.Operation }, { "key", "activate" } }).SingleAsync();
        await d.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument("_id", receipt["_id"]));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.RecoverAsync(d.Checker, "activate"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Base.Store.ReadCurrentAsync(d.Recipient with { GrantVersion = 2 }));
        await d.Rows("idempotencyRecords").InsertOneAsync(receipt);
        var head = await d.Rows(TemporaryDelegationRecords.Heads).Find(new BsonDocument("_id", id)).SingleAsync();
        await d.Rows(TemporaryDelegationRecords.Heads).UpdateOneAsync(new BsonDocument("_id", id), Builders<BsonDocument>.Update.Set("proposalReason", "Changed without an owner event"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.ReadAsync(d.Giver, id));
        await d.Rows(TemporaryDelegationRecords.Heads).ReplaceOneAsync(new BsonDocument("_id", id), head);
        Assert.NotNull(await d.Service.RecoverAsync(d.Checker, "activate"));
        Assert.Null(await d.Service.RecoverAsync(d.Giver, "activate"));
        Assert.Equal(2, await d.Rows(TemporaryDelegationRecords.Events).CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Same_key_changed_permissions_and_stale_decision_do_not_create_second_effect() {
        await using var d = await CreateAsync();
        var proposed = await d.Service.ChangeAsync(d.Giver, "propose", null, 0, d.Request, Meta("original")); var id = proposed["delegationId"].AsInt64;
        await Assert.ThrowsAsync<PersistenceConflictException>(() => d.Service.ChangeAsync(d.Giver, "propose", null, 0, d.Request with { ExpiresAt = d.Request.ExpiresAt.AddMinutes(-1) }, Meta("original")));
        var approved = await d.Service.ChangeAsync(d.Checker, "approve", id, 1, null, Meta("approved"));
        Assert.Equal(approved, await d.Service.ChangeAsync(d.Checker, "approve", id, 1, null, Meta("approved")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => d.Service.ChangeAsync(d.Checker, "approve", id, 1, null, Meta("another")));
        Assert.Equal(1, await d.Rows("authorizationGrants").CountDocumentsAsync(new BsonDocument("templateId", TemporaryDelegationPolicy.TemplateId)));
        Assert.Equal(2, await d.Rows(TemporaryDelegationRecords.Events).CountDocumentsAsync(new BsonDocument()));
    }
    [Theory]
    [InlineData("grant")]
    [InlineData("membership")]
    [InlineData("scope")]
    [InlineData("identity")]
    public async Task A_revoked_callers_independent_authority_cannot_read_recover_or_acknowledge_history(string loss) {
        await using var d = await CreateAsync(); var id = await d.ActivateAsync();
        var recipient = d.Recipient with { GrantVersion = 2 };
        await d.Service.ChangeAsync(recipient, "handover", id, 2, null, Meta("recipient-handover"));
        Assert.NotNull(await d.Service.RecoverAsync(recipient, "recipient-handover"));
        if (loss == "grant") await d.Base.Administration.RevokeGrantAsync(d.Checker, recipient.UserId, 1, Meta("revoke-caller-grant"));
        else if (loss == "membership") await d.Base.Administration.SuspendMembershipAsync(d.Checker, recipient.UserId, 1, Meta("suspend-caller-membership"));
        else {
            var administrator = await d.Base.AddActorAsync(5400, new(GrantScopeKind.System), Permissions.AccessRevoke);
            if (loss == "scope") await d.Base.Administration.SuspendScopeAsync(administrator, 101, 1, Meta("suspend-caller-scope"));
            else await d.Base.Administration.SuspendIdentityAsync(administrator, recipient.UserId, 2, Meta("suspend-caller-identity"));
        }
        var identity = await d.Rows("identities").Find(new BsonDocument("_id", recipient.UserId)).SingleAsync();
        recipient = recipient with { GrantVersion = identity["grantVersion"].AsInt64 };
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.ReadAsync(recipient, id));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.ListAsync(recipient));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.RecoverAsync(recipient, "recipient-handover"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.ChangeAsync(recipient, "handover", id, 3, null, Meta("stale-caller-handover")));
        Assert.Equal(3, await d.Rows(TemporaryDelegationRecords.Events).CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Source_expiry_does_not_turn_an_old_receipt_into_current_caller_permission() {
        await using var d = await CreateAsync(); var id = await d.ActivateAsync();
        var administrator = await d.Base.AddActorAsync(5400, new(GrantScopeKind.System), Permissions.AccessRevoke);
        await d.Base.Administration.RevokeGrantAsync(administrator, d.Giver.UserId, 1, Meta("revoke-proposal-owner"));
        var row = await d.Rows("identities").Find(new BsonDocument("_id", d.Giver.UserId)).SingleAsync();
        var giver = d.Giver with { GrantVersion = row["grantVersion"].AsInt64 };
        // The ordinary stock source is still active. Its permission is not a substitute for
        // the explicit current administrative owner permission to inspect or revoke delegation.
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.ReadAsync(giver, id));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.RecoverAsync(giver, "create"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.ChangeAsync(giver, "revoke", id, 2, null, Meta("stale-owner-revoke")));
        Assert.Equal(2, await d.Rows(TemporaryDelegationRecords.Events).CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Different_account_for_the_same_person_cannot_approve_or_receive_its_own_delegation() {
        await using var d = await CreateAsync();
        var proposed = await d.Service.ChangeAsync(d.Giver, "propose", null, 0, d.Request, Meta("alias-proposal"));
        var unit = new MongoUnitOfWork((MongoClient)d.Base.Database.Client);
        await PersonTestData.SetEquivalenceAsync(unit, d.Base.Database, d.Giver.UserId, d.Checker.UserId, true);
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.ChangeAsync(d.Checker, "approve", proposed["delegationId"].AsInt64, 1, null, Meta("same-person-approval")));
        Assert.Equal("independent_person_required", denied.Code);
        await PersonTestData.SetEquivalenceAsync(unit, d.Base.Database, d.Giver.UserId, d.Checker.UserId, false);
        await PersonTestData.SetEquivalenceAsync(unit, d.Base.Database, d.Giver.UserId, d.Recipient.UserId, true);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.ChangeAsync(d.Giver, "propose", null, 0, d.Request, Meta("same-person-beneficiary")));
        Assert.Equal(1, await d.Rows(TemporaryDelegationRecords.Events).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await d.Rows("authorizationGrants").CountDocumentsAsync(new BsonDocument("templateId", TemporaryDelegationPolicy.TemplateId)));
    }
    [Fact]
    public async Task Actual_owner_refuses_privilege_widening_and_derived_grant_as_source_even_with_proposal_permission() {
        await using var d = await CreateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.ChangeAsync(d.Giver, "propose", null, 0,
            d.Request with { Permissions = [Permissions.StockCount] }, Meta("not-explicitly-delegable")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.ChangeAsync(d.Giver, "propose", null, 0,
            d.Request with { Scope = new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 999, ChannelId: 103) }, Meta("foreign-warehouse")));
        await d.Base.AddGrantAsync(d.Recipient, 5201, new(GrantScopeKind.System), [Permissions.AccessPropose]);
        var id = await d.ActivateAsync();
        var other = await d.Base.AddActorAsync(5500, Shop, Permissions.CatalogRead);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.ChangeAsync(d.Recipient with { GrantVersion = 2 }, "propose", null, 0,
            d.Request with { SourceGrantId = id, BeneficiaryUserId = other.UserId, BeneficiaryMembershipId = other.UserId }, Meta("nested-delegation")));
        Assert.Equal(2, await d.Rows(TemporaryDelegationRecords.Events).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await d.Rows("authorizationGrants").CountDocumentsAsync(new BsonDocument("templateId", TemporaryDelegationPolicy.TemplateId)));
    }
    [Fact]
    public async Task Narrower_live_target_grants_exact_runtime_access_and_actual_suspension_refuses_new_and_existing_authority() {
        await using var d = await CreateAsync();
        var scope = new GrantScope(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: 101, WarehouseId: 102, ChannelId: 103);
        var request = d.Request with { Scope = scope };
        var proposal = await d.Service.ChangeAsync(d.Giver, "propose", null, 0, request, Meta("live-target-proposal"));
        var id = proposal["delegationId"].AsInt64;
        await d.Service.ChangeAsync(d.Checker, "approve", id, 1, null, Meta("live-target-approval"));
        var recipient = d.Recipient with { GrantVersion = 2 };
        var authority = ScopePolicy.Compile(recipient, await d.Base.Store.ReadCurrentAsync(recipient), Permissions.StockRead, d.Base.Clock.UtcNow);
        Assert.True(authority.Matches(new(FulfillmentOrganizationId: 100, ShopId: 101, WarehouseId: 102, ChannelId: 103)));
        Assert.False(authority.Matches(new(FulfillmentOrganizationId: 100, ShopId: 101, WarehouseId: 999, ChannelId: 103)));
        Assert.False(authority.Matches(new(FulfillmentOrganizationId: 100, ShopId: 101, ChannelId: 103)));
        Assert.Equal("current", (await d.Service.ReadAsync(d.Giver, id)).Item.SourceState);

        var administrator = await d.Base.AddActorAsync(5400, new(GrantScopeKind.System), Permissions.AccessRevoke);
        await d.Base.Administration.SuspendScopeAsync(administrator, 102, 1, Meta("suspend-target-warehouse"));
        var identity = await d.Rows("identities").Find(new BsonDocument("_id", recipient.UserId)).SingleAsync();
        recipient = recipient with { GrantVersion = identity["grantVersion"].AsInt64 };
        Assert.False(ScopePolicy.Compile(recipient, await d.Base.Store.ReadCurrentAsync(recipient), Permissions.StockRead, d.Base.Clock.UtcNow).IsAllowed);
        Assert.Equal("unavailable", (await d.Service.ReadAsync(d.Giver, id)).Item.SourceState);
        Assert.Equal(proposal, await d.Service.RecoverAsync(d.Giver, "live-target-proposal"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.ChangeAsync(d.Giver, "propose", null, 0, request, Meta("inactive-target-proposal")));
        Assert.Equal(2, await d.Rows(TemporaryDelegationRecords.Events).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await d.Rows("authorizationGrants").CountDocumentsAsync(new BsonDocument("templateId", TemporaryDelegationPolicy.TemplateId)));
        Assert.Equal(0, await d.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "inactive-target-proposal")));
        Assert.Equal(0, await d.Rows("fixtureBusinessRecords").CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Ordinary_participant_read_and_derived_only_approve_cannot_authorize_another_delegation() {
        await using var d = await CreateAsync();
        var (checker, revoker, derivedId) = await ApprovalDelegationAsync(d);
        var proposed = await d.Service.ChangeAsync(d.Giver, "propose", null, 0, d.Request, Meta("target-proposal"));
        var id = proposed["delegationId"].AsInt64;
        await d.Base.Administration.RevokeGrantAsync(revoker, d.Checker.UserId, 1, Meta("revoke-ordinary-approval"));
        checker = checker with { GrantVersion = 3 };
        var snapshot = await d.Base.Store.ReadCurrentAsync(checker);
        var approval = ScopePolicy.Compile(checker, snapshot, Permissions.AccessApprove, d.Base.Clock.UtcNow);
        Assert.True(approval.IsAllowed); Assert.All(approval.Clauses, clause => Assert.Equal(derivedId, clause.GrantId));
        Assert.Equal("pending", (await d.Service.ReadAsync(checker, id)).Item.Status);
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Service.ChangeAsync(checker, "approve", id, 1, null, Meta("derived-only-approval")));
        Assert.Equal("permission_denied", denied.Code);
        await AssertUnapprovedAsync(d, id, "derived-only-approval");
    }
    [Theory]
    [InlineData("owner-write")]
    [InlineData("final-person-fence")]
    public async Task Ordinary_approval_expiry_after_authorization_cannot_be_masked_by_a_live_derived_approval(string boundary) {
        await using var d = await CreateAsync();
        var (checker, _, derivedId) = await ApprovalDelegationAsync(d);
        var expiry = d.Base.Clock.UtcNow.AddMinutes(2);
        await d.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", d.Checker.UserId), Builders<BsonDocument>.Update.Set("expiresAt", expiry.UtcDateTime));
        var proposed = await d.Service.ChangeAsync(d.Giver, "propose", null, 0, d.Request, Meta("expiry-target-proposal"));
        var id = proposed["delegationId"].AsInt64; const string key = "approval-expires-after-write";
        var ordinary = ScopePolicy.Compile(checker, await d.Base.Store.ReadCurrentAsync(checker), Permissions.AccessApprove, d.Base.Clock.UtcNow);
        Assert.Contains(ordinary.Clauses, clause => clause.GrantId == d.Checker.UserId);
        Assert.Contains(ordinary.Clauses, clause => clause.GrantId == derivedId);
        var intercepted = 0; var ownerWritten = false; var ownerHeadFenced = false; var callerFencesAfterHead = 0;
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command => {
            if (command.CommandName == "insert" && command.Command.GetValue("insert", "") == TemporaryDelegationRecords.Events &&
                command.Command["documents"].AsBsonArray.Any(value => value.AsBsonDocument.GetValue("requestKey", "") == key)) {
                ownerWritten = true;
                if (boundary == "owner-write" && Interlocked.CompareExchange(ref intercepted, 1, 0) == 0) d.Base.Clock.UtcNow = expiry;
            }
            if (boundary != "final-person-fence" || !ownerWritten || command.CommandName != "update") return;
            foreach (var value in command.Command["updates"].AsBsonArray) {
                var update = value.AsBsonDocument; var query = update["q"].AsBsonDocument;
                if (command.Command.GetValue("update", "") == TemporaryDelegationRecords.Heads && query.GetValue("_id", BsonNull.Value) == id &&
                    update["u"].AsBsonDocument.Contains("$inc")) ownerHeadFenced = true;
                // The owner finishes its own caller fence before returning to the generic
                // person-completion fence. Expire only at that later actual database await.
                if (ownerHeadFenced && command.Command.GetValue("update", "") == "identities" && query.GetValue("_id", BsonNull.Value) == checker.UserId &&
                    ++callerFencesAfterHead == 2 && Interlocked.CompareExchange(ref intercepted, 1, 0) == 0) d.Base.Clock.UtcNow = expiry;
            }
        });
        var client = new MongoClient(settings); var database = client.GetDatabase(d.Base.Database.DatabaseNamespace.DatabaseName);
        var service = new TemporaryDelegationService(database, new MongoUnitOfWork(client), new(database), d.Base.Clock);
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ChangeAsync(checker, "approve", id, 1, null, Meta(key)));
        Assert.Equal(1, intercepted); Assert.Equal("permission_denied", denied.Code);
        var current = ScopePolicy.Compile(checker, await d.Base.Store.ReadCurrentAsync(checker), Permissions.AccessApprove, expiry);
        Assert.True(current.IsAllowed); Assert.All(current.Clauses, clause => Assert.Equal(derivedId, clause.GrantId));
        await AssertUnapprovedAsync(d, id, key);
    }
    private static async Task<(ActorContext Checker, ActorContext Revoker, long DerivedId)> ApprovalDelegationAsync(Data d) {
        // Keep a separately valid ordinary reader so denial must come from the approval
        // policy, rather than disappearance of all participant/read authority.
        await d.Base.AddGrantAsync(d.Checker, 5301, new(GrantScopeKind.System), [Permissions.AccessAuditRead]);
        var giver = await d.Base.AddActorAsync(5400, new(GrantScopeKind.System), Permissions.AccessPropose);
        await d.Base.AddGrantAsync(giver, 5401, Shop, [Permissions.AccessApprove], [Permissions.AccessApprove]);
        var reviewer = await d.Base.AddActorAsync(5500, Shop, Permissions.AccessApprove, Permissions.AccessRevoke);
        await d.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", reviewer.UserId),
            Builders<BsonDocument>.Update.Set("delegablePermissions", new BsonArray { Permissions.AccessApprove }));
        await PersonTestData.SeedDistinctPeopleAsync(d.Base.Database, d.Base.Clock, giver.UserId, reviewer.UserId);
        var request = new TemporaryDelegationRequest(5401, 1, d.Checker.UserId, d.Checker.UserId, Shop,
            [Permissions.AccessApprove], d.Base.Clock.UtcNow, d.Base.Clock.UtcNow.AddHours(1));
        var proposed = await d.Service.ChangeAsync(giver, "propose", null, 0, request, Meta("approval-source-proposal"));
        var id = proposed["delegationId"].AsInt64;
        await d.Service.ChangeAsync(reviewer, "approve", id, 1, null, Meta("approval-source-approval"));
        return (d.Checker with { GrantVersion = 2 }, reviewer, id);
    }
    private static async Task AssertUnapprovedAsync(Data d, long id, string key) {
        var head = await d.Rows(TemporaryDelegationRecords.Heads).Find(new BsonDocument("_id", id)).SingleAsync();
        Assert.Equal("pending", head["status"].AsString); Assert.Equal(1L, head["revision"].AsInt64);
        Assert.Equal(0, await d.Rows("authorizationGrants").CountDocumentsAsync(new BsonDocument("_id", id)));
        Assert.Equal(1, await d.Rows(TemporaryDelegationRecords.Events).CountDocumentsAsync(new BsonDocument("delegationId", id)));
        Assert.Equal(0, await d.Rows(TemporaryDelegationRecords.Events).CountDocumentsAsync(new BsonDocument("requestKey", key)));
        Assert.Equal(0, await d.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("requestKey", key)));
        Assert.Equal(0, await d.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", key)));
        Assert.Equal(1L, (await d.Rows("identities").Find(new BsonDocument("_id", d.Recipient.UserId)).SingleAsync())["grantVersion"].AsInt64);
        Assert.Equal(1, await d.Rows("authorizationGrants").CountDocumentsAsync(new BsonDocument("templateId", TemporaryDelegationPolicy.TemplateId)));
    }
    [Theory]
    [InlineData("source-grant")]
    [InlineData("source-person-evidence")]
    public async Task Source_lifetime_expiring_after_initial_authorization_aborts_the_ordinary_owner_write(string lifetime) {
        await using var d = await CreateAsync();
        var expiry = d.Base.Clock.UtcNow.AddMinutes(2);
        TemporaryDelegationRequest request = d.Request;
        if (lifetime == "source-grant") {
            await d.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 5101L), Builders<BsonDocument>.Update.Set("expiresAt", expiry.UtcDateTime));
            request = request with { ExpiresAt = expiry };
        } else {
            // Synthetic reviewed evidence is prepared before the actual delegation owner captures it.
            await d.Rows("personEvidenceVersions").UpdateOneAsync(new BsonDocument("_id", PersonTestData.EvidenceId(d.Giver.UserId)),
                Builders<BsonDocument>.Update.Set("expiresAt", expiry.UtcDateTime));
        }
        var proposed = await d.Service.ChangeAsync(d.Giver, "propose", null, 0, request, Meta("lifetime-proposal"));
        await d.Service.ChangeAsync(d.Checker, "approve", proposed["delegationId"].AsInt64, 1, null, Meta("lifetime-approval"));
        var recipient = d.Recipient with { GrantVersion = 2 };
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Base.Store.ExecuteAuthorizedAsync(recipient,
            "fixture.delegation.source-expiry", Permissions.StockRead, Meta("source-expiry-write"), new { id = 2 },
            (_, _) => Task.FromResult(new AuthorizationTarget(new(FulfillmentOrganizationId: 100, ShopId: 101, ChannelId: 103))),
            async (tx, ct) => {await d.Rows("fixtureBusinessRecords").InsertOneAsync(tx, new BsonDocument { { "_id", 2L }, { "schemaVersion", 1 } }, cancellationToken: ct);
                d.Base.Clock.UtcNow = expiry; return new BsonDocument("id", 2L);}));
        Assert.Equal("delegation_expired", denied.Code);
        Assert.Equal(0, await d.Rows("fixtureBusinessRecords").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await d.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "source-expiry-write")));
    }
    private sealed class AdvancingClock(DateTimeOffset initial) : IClock {
        private long reads;
        public DateTimeOffset UtcNow => initial.AddMilliseconds(Interlocked.Increment(ref reads));
    }
    private sealed class Data(StoreTestData data, ActorContext giver, ActorContext recipient, ActorContext checker, TemporaryDelegationRequest request) : IAsyncDisposable {
        internal StoreTestData Base => data; internal ActorContext Giver => giver; internal ActorContext Recipient => recipient; internal ActorContext Checker => checker;
        internal TemporaryDelegationRequest Request => request;
        internal TemporaryDelegationService Service { get; } = new(data.Database, new MongoUnitOfWork((MongoClient)data.Database.Client), new(data.Database), data.Clock);
        internal IMongoCollection<BsonDocument> Rows(string name) => data.Database.GetCollection<BsonDocument>(name);
        internal async Task<long> ActivateAsync() {var row = await Service.ChangeAsync(giver, "propose", null, 0, request, Meta("create"));
            await Service.ChangeAsync(checker, "approve", row["delegationId"].AsInt64, 1, null, Meta("activate")); return row["delegationId"].AsInt64;}
        public ValueTask DisposeAsync() => data.DisposeAsync();
    }
}
