using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using D = UnitToko.Tests.Procurement.PurchaseOrderTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class PurchaseArrivalTests(MongoFixture fixture)
{
    private static PurchaseArrivalInput Input(D data, BsonDocument order, long revision = 1) => new(order["orderId"].AsInt64,
        order["orderVersion"].AsInt64, order["decisionId"].AsInt64, data.Procurement.Sku.Id.Value,
        data.Procurement.Sku.CurrentDefinitionId!.Value.Value, revision, data.Clock.UtcNow.AddDays(3), "SYNTHETIC supplier confirmation 00017");
    [Fact]
    public async Task Real_order_arrival_needs_independent_review_and_retains_original_key_and_explicit_withdrawal()
    {
        await using var data = await D.CreateAsync(fixture);
        var order = await data.DecideAsync(await data.ProposeAsync("arrival-po"), "arrival-po-approved");
        var input = Input(data, order); var before = await data.StateAsync();
        var empty = await data.Service.ReadArrivalAsync(data.Reader, input.OrderId, input.SkuId);
        Assert.Equal("unrecorded", empty["state"]); Assert.True(empty["expectedAt"].IsBsonNull);
        Assert.NotEqual(input.ExpectedAt!.Value.Date, data.Input().PaymentTerms.DueDate!.Value.ToDateTime(TimeOnly.MinValue));
        var preview = await data.Service.PreviewArrivalAsync(data.Maker, input);
        var pending = await data.Service.ProposeArrivalAsync(data.Maker, input, preview["binding"].AsString, D.Meta("arrival-propose"));
        Assert.Equal(pending, await data.Service.ProposeArrivalAsync(data.Maker, input, preview["binding"].AsString, D.Meta("arrival-propose")));
        var pendingView = await data.Service.ReadArrivalAsync(data.Reader, input.OrderId, input.SkuId);
        Assert.Equal("pending", pendingView["state"]); Assert.True(pendingView["expectedAt"].IsBsonNull);
        var proposalId = pendingView["proposalId"].AsInt64;
        var makerChecker = await data.Base.AddActorAsync(639, ProcurementRequestTestData.Scope, Permissions.ProcurementRead, Permissions.ProcurementApprove);
        await UnitToko.Tests.Authorization.PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, makerChecker.UserId);
        await UnitToko.Tests.Authorization.PersonTestData.SetEquivalenceAsync(data.Unit, data.Database, data.Maker.UserId, makerChecker.UserId, true);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewArrivalDecisionAsync(makerChecker, proposalId, "approve"));
        await UnitToko.Tests.Authorization.PersonTestData.SetEquivalenceAsync(data.Unit, data.Database, data.Maker.UserId, makerChecker.UserId, false);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadArrivalAsync(data.Outsider, input.OrderId, input.SkuId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewArrivalAsync(data.Maker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, input with { ExpectedRevision = 2 }));
        var decision = await data.Service.PreviewArrivalDecisionAsync(data.Checker, proposalId, "approve");
        var approved = await data.Service.DecideArrivalAsync(data.Checker, proposalId, 2, "approve", decision["binding"].AsString, D.Meta("arrival-approve"));
        Assert.Equal(approved, await data.Service.RecoverArrivalAsync(data.Checker, "approve", "arrival-approve"));
        var current = await data.Service.ReadArrivalAsync(data.Reader, input.OrderId, input.SkuId);
        Assert.Equal("approved", current["state"]); Assert.Equal(input.ExpectedAt.Value.UtcDateTime, current["expectedAt"].ToUniversalTime());
        Assert.Equal(3L, current["revision"].AsInt64);
        Assert.Equal(pending, await data.Service.RecoverArrivalAsync(data.Maker, "propose", "arrival-propose"));
        var withdrawal = input with { ExpectedRevision = 3, ExpectedAt = null, ConfirmationReference = "SYNTHETIC supplier cannot confirm revised date" };
        var withdrawPreview = await data.Service.PreviewArrivalAsync(data.Maker, withdrawal);
        await data.Service.ProposeArrivalAsync(data.Maker, withdrawal, withdrawPreview["binding"].AsString, D.Meta("arrival-withdraw"));
        var withdrawnPending = await data.Service.ReadArrivalAsync(data.Reader, input.OrderId, input.SkuId);
        Assert.True(withdrawnPending["expectedAt"].IsBsonNull);
        var withdrawId = withdrawnPending["proposalId"].AsInt64;
        var withdrawDecision = await data.Service.PreviewArrivalDecisionAsync(data.Checker, withdrawId, "approve");
        await data.Service.DecideArrivalAsync(data.Checker, withdrawId, 4, "approve", withdrawDecision["binding"].AsString, D.Meta("arrival-withdraw-approved"));
        Assert.True((await data.Service.ReadArrivalAsync(data.Reader, input.OrderId, input.SkuId))["expectedAt"].IsBsonNull);
        Assert.Equal(before, await data.StateAsync()); await data.NoFinancialOrReceiptEffectsAsync();
        Assert.Equal(4, await data.Rows(PurchaseArrivalRecords.Events).CountDocumentsAsync(new BsonDocument()));
        await data.Rows(PurchaseArrivalRecords.Audits).DeleteOneAsync(new BsonDocument("_id", approved["eventId"]));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.ReadArrivalAsync(data.Reader, input.OrderId, input.SkuId));
    }
    [Fact]
    public async Task Concurrent_revisions_have_one_winner_and_wrong_definition_has_no_business_effect()
    {
        await using var data = await D.CreateAsync(fixture);
        var order = await data.DecideAsync(await data.ProposeAsync("arrival-race-po"), "arrival-race-po-approved");
        var input = Input(data, order);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewArrivalAsync(data.Maker, input with { DefinitionId = input.DefinitionId + 1000 }));
        var preview = await data.Service.PreviewArrivalAsync(data.Maker, input);
        async Task<bool> Attempt(string key) {
            try { await data.Service.ProposeArrivalAsync(data.Maker, input, preview["binding"].AsString, D.Meta(key)); return true; }
            catch (PersistenceConflictException) { return false; }
        }
        var results = await Task.WhenAll(Attempt("arrival-race-a"), Attempt("arrival-race-b"));
        Assert.Single(results, success => success);
        Assert.Equal(1, await data.Rows(PurchaseArrivalRecords.Events).CountDocumentsAsync(new BsonDocument()));
        await data.NoFinancialOrReceiptEffectsAsync();
    }
}
