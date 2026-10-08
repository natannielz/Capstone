using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using H = UnitToko.Platform.Billing.PaymentReviewHandoverRecords;
using W = UnitToko.Platform.Billing.PaymentReviewActionRecords;
using R = UnitToko.Platform.Authorization.TemporaryDelegationRecords;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentHandoverTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Revoked_delegation_handover_uses_current_reviewer_and_recovers_original_owner_action_after_later_history() {
        await using var d = await PaymentHandoverTestData.CreateAsync(fixture); await d.RevokeAsync();
        var before = await d.BusinessAsync(); var page = await d.Data.Service.ReadHandoversAsync(d.Claimant, d.Id, null);
        Assert.True(page.CanClaim); Assert.Equal("revoked", Assert.Single(page.Items).State);
        var request = d.Input(page); var meta = PaymentVerificationTestData.Meta("handover-original");
        var result = await d.Data.Service.ClaimHandoverAsync(d.Claimant, d.Id, page.ReviewRevision, page.Binding, request, meta);
        Assert.Equal(d.Claimant.UserId, result["assigneeId"].AsInt64); Assert.Equal(2L, result["reviewRevision"].AsInt64);
        await d.Delegations.ChangeAsync(d.Giver, "handover", d.DelegationId, 3, null, PaymentVerificationTestData.Meta("later-delegation-note"));
        Assert.Equal(result, await d.Data.Service.RecoverHandoverAsync(d.Claimant, d.Id, meta.IdempotencyKey));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => d.Data.Service.ClaimHandoverAsync(d.Claimant, d.Id, page.ReviewRevision,
            page.Binding, request, PaymentVerificationTestData.Meta("stale-review")));
        Assert.Equal(result, await d.Data.Service.ClaimHandoverAsync(d.Claimant, d.Id, page.ReviewRevision, page.Binding, request, meta));
        Assert.Equal(1, await d.Data.Rows(H.Collection).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await d.Data.Rows(W.Versions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(before, await d.BusinessAsync());
        var proof = await d.Data.Rows(H.Collection).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(d.Recipient.UserId, proof["originalAssigneeId"].AsInt64);
        Assert.Equal(result["actionId"], proof["_id"]); Assert.Equal(meta.IdempotencyKey, proof["requestKey"]);
        Assert.False((await d.Data.Service.ReadHandoversAsync(d.Claimant, d.Id, null)).CanClaim);
    }
    [WindowsStorageFact]
    public async Task Expired_delegation_does_not_grant_claim_but_independently_authorized_reviewer_can_take_the_exact_task() {
        await using var d = await PaymentHandoverTestData.CreateAsync(fixture);
        d.Data.Read.Catalog.Clock.UtcNow = d.ExpiresAt;
        var current = d.Recipient with { Session = null, AuthenticatedAt = d.ExpiresAt };
        Assert.False(ScopePolicy.Compile(current, await d.Data.Read.Catalog.Base.Store.ReadCurrentAsync(current), Permissions.PaymentReviewManage, d.ExpiresAt).IsAllowed);
        var page = await d.Data.Service.ReadHandoversAsync(d.Claimant, d.Id, null); Assert.Equal("expired", Assert.Single(page.Items).State);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Data.Service.ClaimHandoverAsync(current, d.Id, page.ReviewRevision, page.Binding,
            d.Input(page), PaymentVerificationTestData.Meta("expired-claim")));
        Assert.Equal(d.Claimant.UserId, (await d.Data.Service.ClaimHandoverAsync(d.Claimant, d.Id, page.ReviewRevision, page.Binding,
            d.Input(page), PaymentVerificationTestData.Meta("ordinary-claim")))["assigneeId"].AsInt64);
        Assert.Equal(1, await d.Data.Rows(H.Collection).CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Exact_source_binding_scope_person_and_current_caller_revocation_remain_required() {
        await using var d = await PaymentHandoverTestData.CreateAsync(fixture); var page = await d.Data.Service.ReadHandoversAsync(d.Claimant, d.Id, null);
        var request = d.Input(page); var before = await d.BusinessAsync();
        foreach (var changed in new[] {request with {OriginalAssigneeId = d.Giver.UserId}, request with {DelegationRevision = request.DelegationRevision + 1},
            request with {DeclarationRevision = request.DeclarationRevision + 1}, request with {DelegationEventId = request.DelegationEventId + 1000}})
            await Assert.ThrowsAsync<PersistenceConflictException>(() => d.Data.Service.ClaimHandoverAsync(d.Claimant, d.Id, page.ReviewRevision,
                page.Binding, changed, PaymentVerificationTestData.Meta("bad-" + Guid.NewGuid())));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Data.Service.ClaimHandoverAsync(d.Data.Reader, d.Id, page.ReviewRevision,
            page.Binding, request, PaymentVerificationTestData.Meta("reader-claim")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Data.Service.ReadHandoversAsync(d.Outsider, d.Id, null));
        var maker = d.Data.Read.Actor;
        await d.Data.Read.Catalog.Base.AddGrantAsync(maker, 75601, CatalogAuthoringTestData.Shop,
            [Permissions.PaymentReviewRead, Permissions.InvoiceRead, Permissions.PaymentReviewManage]);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Data.Service.ClaimHandoverAsync(maker, d.Id, page.ReviewRevision,
            page.Binding, request, PaymentVerificationTestData.Meta("maker-claim")));
        await PersonTestData.SetEquivalenceAsync(new(d.Data.Read.Catalog.Client), d.Data.Read.Database, maker.UserId, d.Claimant.UserId, true);
        var samePerson = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Data.Service.ClaimHandoverAsync(d.Claimant, d.Id, page.ReviewRevision,
            page.Binding, request, PaymentVerificationTestData.Meta("maker-alias-claim")));
        Assert.Equal("independent_person_required", samePerson.Code);
        await PersonTestData.SetEquivalenceAsync(new(d.Data.Read.Catalog.Client), d.Data.Read.Database, maker.UserId, d.Claimant.UserId, false);
        await d.RevokeAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() => d.Data.Service.ClaimHandoverAsync(d.Claimant, d.Id, page.ReviewRevision,
            page.Binding, request, PaymentVerificationTestData.Meta("changed-delegation")));
        page = await d.Data.Service.ReadHandoversAsync(d.Claimant, d.Id, null); request = d.Input(page);
        var key = "current-owner"; await d.Data.Service.ClaimHandoverAsync(d.Claimant, d.Id, page.ReviewRevision, page.Binding, request, PaymentVerificationTestData.Meta(key));
        await d.Data.Read.Catalog.Base.Administration.RevokeGrantAsync(d.Checker, d.Claimant.UserId, 1, PaymentVerificationTestData.Meta("revoke-caller"));
        var current = d.Claimant with {GrantVersion = 2, Session = null};
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Data.Service.RecoverHandoverAsync(current, d.Id, key));
        Assert.Equal(before, await d.BusinessAsync()); Assert.Equal(1, await d.Data.Rows(H.Collection).CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Missing_or_corrupted_handover_owner_receipt_audit_and_delegation_proof_never_fall_back_to_normal_claim() {
        await using var d = await PaymentHandoverTestData.CreateAsync(fixture); var page = await d.Data.Service.ReadHandoversAsync(d.Claimant, d.Id, null);
        var result = await d.Data.Service.ClaimHandoverAsync(d.Claimant, d.Id, page.ReviewRevision, page.Binding, d.Input(page), PaymentVerificationTestData.Meta("integrity"));
        var action = await d.Data.Rows(W.Versions).Find(new BsonDocument("_id", result["actionId"])).SingleAsync();
        var proof = await d.Data.Rows(H.Collection).Find(new BsonDocument()).SingleAsync();
        foreach (var (name, filter) in new[] {(H.Collection, new BsonDocument("_id", proof["_id"])),
            (W.Versions, new BsonDocument("_id", action["_id"])), ("auditEvents", new BsonDocument("_id", action["auditId"])),
            ("idempotencyRecords", new BsonDocument { { "operation", W.Operation }, { "key", "integrity" } }),
            (R.Events, new BsonDocument("_id", proof["delegationEventId"]))}) {
            var saved = await d.Data.Rows(name).Find(filter).SingleAsync(); await d.Data.Rows(name).DeleteOneAsync(filter);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Data.Service.RecoverHandoverAsync(d.Claimant, d.Id, "integrity"));
            await d.Data.Rows(name).InsertOneAsync(saved);
        }
        await d.Data.Rows(H.Collection).UpdateOneAsync(new BsonDocument("_id", proof["_id"]), Builders<BsonDocument>.Update.Set("originalAssigneeId", d.Giver.UserId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Data.Service.RecoverReviewActionAsync(d.Claimant, d.Id, "integrity"));
        await d.Data.Rows(H.Collection).ReplaceOneAsync(new BsonDocument("_id", proof["_id"]), proof);
        Assert.Equal(result, await d.Data.Service.RecoverHandoverAsync(d.Claimant, d.Id, "integrity"));
        Assert.Null(await d.Data.Service.RecoverHandoverAsync(d.Claimant, d.Id, "never-sent"));
        await d.Data.Rows(H.Collection).DeleteOneAsync(new BsonDocument("_id", proof["_id"]));
        await d.Data.Rows(W.Versions).DeleteOneAsync(new BsonDocument("_id", action["_id"]));
        await d.Data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument {{"operation", W.Operation}, {"key", "integrity"}});
        await d.Data.Rows("outboxEvents").DeleteOneAsync(new BsonDocument("sourceKey", W.Operation + ":" + action["_id"].AsInt64));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => d.Data.Service.RecoverHandoverAsync(d.Claimant, d.Id, "integrity"));
    }
    [WindowsStorageFact]
    public async Task Current_claimant_authority_expiring_after_handover_insert_rolls_back_every_owner_effect() {
        await using var d = await PaymentHandoverTestData.CreateAsync(fixture); var page = await d.Data.Service.ReadHandoversAsync(d.Claimant, d.Id, null);
        var before = d.Data.Read.Catalog.Clock.UtcNow; var expiry = before.AddMinutes(1);
        await d.Data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", d.Claimant.UserId), Builders<BsonDocument>.Update.Set("expiresAt", expiry.UtcDateTime));
        var fired = false; var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = cb => cb.Subscribe<CommandStartedEvent>(e => {
            if (!fired && e.CommandName == "insert" && e.Command.GetValue("insert", "") == H.Collection) {fired = true; d.Data.Read.Catalog.Clock.UtcNow = expiry;}
        });
        try {
            var service = d.Data.NewService(new MongoClient(settings));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ClaimHandoverAsync(d.Claimant, d.Id, page.ReviewRevision,
                page.Binding, d.Input(page), PaymentVerificationTestData.Meta("late-expiry")));
            Assert.True(fired); Assert.Equal(0, await d.Data.Rows(H.Collection).CountDocumentsAsync(new BsonDocument()));
            Assert.Equal(1, await d.Data.Rows(W.Versions).CountDocumentsAsync(new BsonDocument()));
            Assert.Equal(0, await d.Data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("key", "late-expiry")));
        } finally {d.Data.Read.Catalog.Clock.UtcNow = before;}
    }
}

[SupportedOSPlatform("windows")]
internal sealed class PaymentHandoverTestData(PaymentVerificationTestData data, ActorContext giver, ActorContext recipient,
    ActorContext claimant, ActorContext checker, ActorContext outsider, long delegationId, DateTimeOffset expiresAt) : IAsyncDisposable
{
    internal PaymentVerificationTestData Data {get;} = data;
    internal ActorContext Giver {get;} = giver;
    internal ActorContext Recipient {get;} = recipient;
    internal ActorContext Claimant {get;} = claimant;
    internal ActorContext Checker {get;} = checker;
    internal ActorContext Outsider {get;} = outsider;
    internal long DelegationId {get;} = delegationId;
    internal DateTimeOffset ExpiresAt {get;} = expiresAt;
    internal long Id => Data.Read.DeclarationId;
    internal TemporaryDelegationService Delegations => new(Data.Read.Database, new(Data.Read.Catalog.Client), new(Data.Read.Database), Data.Read.Catalog.Clock);
    internal PaymentReviewHandoverInput Input(PaymentHandoverPage page) {var item = Assert.Single(page.Items); return new(item.DelegationId, item.DelegationRevision, item.DelegationEventId, page.AssigneeId!.Value, page.DeclarationRevision);}
    internal Task<BsonDocument> RevokeAsync() => Delegations.ChangeAsync(Giver, "revoke", DelegationId, 2, null, PaymentVerificationTestData.Meta("revoke-delegation"));
    internal async Task<BsonDocument> BusinessAsync() {
        var result = new BsonDocument(); foreach (var name in new[] {"orders", "orderSuborders", "reservations", "stockBalances", "stockMovements", "movements", "inventoryPools", "cashMovements", "bankReceipts", "journalEntries", "paymentVerifications", "paymentAllocations", "paymentDeclarationVersions"})
            result.Add(name, new BsonArray((await Data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).Select(WithoutFences))); return result;
    }
    private static BsonValue WithoutFences(BsonValue value) => value is BsonDocument document
        ? new BsonDocument(document.Elements.Where(element => element.Name != "fence").Select(element => new BsonElement(element.Name, WithoutFences(element.Value))))
        : value is BsonArray array ? new BsonArray(array.Select(WithoutFences)) : value;
    internal static async Task<PaymentHandoverTestData> CreateAsync(MongoFixture fixture) {
        Assert.True(CollectionMigrations.CurrentVersion >= 113, "Compose real110–113 before handover acceptance");
        var data = await PaymentVerificationTestData.CreateAsync(fixture);
        try {
            var catalog = data.Read.Catalog; var giver = await catalog.Base.AddActorAsync(7510, new(GrantScopeKind.System), Permissions.AccessPropose);
            await catalog.Base.AddGrantAsync(giver, 7511, CatalogAuthoringTestData.Shop, [Permissions.PaymentReviewManage], [Permissions.PaymentReviewManage]);
            var claimant = await catalog.ActorAsync(7520, CatalogAuthoringTestData.Shop, Permissions.PaymentReviewRead, Permissions.InvoiceRead, Permissions.PaymentReviewManage);
            var checker = await catalog.Base.AddActorAsync(7530, CatalogAuthoringTestData.Shop, Permissions.AccessApprove, Permissions.AccessRevoke);
            await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", checker.UserId), Builders<BsonDocument>.Update.Set("delegablePermissions", new BsonArray {Permissions.PaymentReviewManage}));
            var outsider = await catalog.ActorAsync(7540, CatalogAuthoringTestData.OtherOwner, Permissions.PaymentReviewRead, Permissions.InvoiceRead, Permissions.PaymentReviewManage);
            await PersonTestData.SeedDistinctPeopleAsync(data.Read.Database, catalog.Clock, giver.UserId, claimant.UserId, checker.UserId, outsider.UserId);
            var delegation = new TemporaryDelegationService(data.Read.Database, new(catalog.Client), new(data.Read.Database), catalog.Clock);
            var expiresAt = catalog.Clock.UtcNow.AddMinutes(2); var request = new TemporaryDelegationRequest(7511, 1, data.Reviewer.UserId, data.Reviewer.UserId,
                CatalogAuthoringTestData.Shop, [Permissions.PaymentReviewManage], catalog.Clock.UtcNow, expiresAt);
            var proposed = await delegation.ChangeAsync(giver, "propose", null, 0, request, PaymentVerificationTestData.Meta("propose-review-delegation"));
            var id = proposed["delegationId"].AsInt64; await delegation.ChangeAsync(checker, "approve", id, 1, null, PaymentVerificationTestData.Meta("approve-review-delegation"));
            var recipient = data.Reviewer with {GrantVersion = 2, Session = null}; var initial = await data.Service.ReadReviewActionsAsync(data.Reader, data.Read.DeclarationId);
            await data.Service.RecordReviewActionAsync(recipient, data.Read.DeclarationId, 0, new(initial.Binding, "claim"), PaymentVerificationTestData.Meta("delegated-initial-assignment"));
            return new(data, giver, recipient, claimant, checker, outsider, id, expiresAt);
        } catch {await data.DisposeAsync(); throw;}
    }
    public ValueTask DisposeAsync() => Data.DisposeAsync();
}
