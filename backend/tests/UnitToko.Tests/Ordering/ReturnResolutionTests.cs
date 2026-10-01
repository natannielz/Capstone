using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;
using P = UnitToko.Platform.Fulfillment.ReturnResolutionRecords;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class ReturnResolutionTests(MongoFixture fixture)
{
    internal static async Task<(AfterSalesCaseService Service, long CaseId, long ReturnId)> Setup(PaymentDraftData data) {
        var (service, caseId, returnId, input) = await ReturnInspectionTests.Setup(data); var actor = data.Invoice.Finance;
        var view = await service.ReadInspectionAsync(actor, caseId, returnId, true); await service.InspectReturnAsync(actor, caseId, returnId, 0, view.Binding, input, CatalogPriceTestData.Meta("resolution-inspection"));
        return (service, caseId, returnId);
    }
    [Fact]
    public void Resolution_names_are_explicit_and_never_imply_credit_or_payment_approval() {
        foreach (var action in new[] { "replacement", "credit", "refund" }) { var value = ReturnResolutionPolicy.Copy(new(action, "Ajukan penyelesaian")); Assert.Equal(action, value.Action); }
        Assert.Equal(Permissions.CaseManage, ReturnResolutionPolicy.Permission("replacement")); Assert.Equal(Permissions.CreditRequest, ReturnResolutionPolicy.Permission("credit")); Assert.Equal(Permissions.RefundRequest, ReturnResolutionPolicy.Permission("refund"));
        foreach (var action in new[] { "paid", "approved", "restock", "refund_approved", "", " credit" }) Assert.Throws<DomainValidationException>(() => ReturnResolutionPolicy.Copy(new(action, "Pengajuan")));
        Assert.Throws<DomainValidationException>(() => ReturnResolutionPolicy.Copy(new("credit", "\u200bCatatan")));
    }
    [Theory]
    [InlineData("replacement")] [InlineData("credit")] [InlineData("refund")]
    public async Task Exact_inspected_resolution_requests_require_their_own_current_permission_and_recover_without_stock_or_money_changes(string action) {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var (service, caseId, returnId) = await Setup(data); var actor = data.Invoice.Finance; var catalog = data.Invoice.Order.Prices.Catalog;
        if (action != "replacement") {
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadResolutionAsync(actor, caseId, returnId, action));
            await catalog.Base.AddGrantAsync(actor, 99833, CatalogAuthoringTestData.Shop, [ReturnResolutionPolicy.Permission(action)]);
        }
        var tables = new[] { "returnQuarantineReceipts", "returnInspections", "stockBalances", "lots", "movements", "reservations", "journalEntries", UnitToko.Platform.Billing.InvoiceBson.Invoices, UnitToko.Platform.Billing.InvoiceBson.Allocations };
        async Task<List<BsonDocument>> Business(string table) { var rows = await data.Invoice.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) { row.Remove("fence"); row.Remove("postingFence"); } return rows; }
        var before = new Dictionary<string, List<BsonDocument>>(); foreach (var table in tables) { before[table] = await Business(table); Assert.NotEmpty(before[table]); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadResolutionAsync(actor with { AuthenticatedAt = catalog.Clock.UtcNow.AddMinutes(-6) }, caseId, returnId, action));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadResolutionAsync(data.Invoice.Order.Buyer, caseId, returnId, action));
        var preview = await service.ReadResolutionAsync(actor, caseId, returnId, action); Assert.Equal(0, preview.Revision); Assert.Null(preview.Request); Assert.Equal(1, Assert.Single(preview.Inspection.Lines).InspectedBaseUnits);
        var financeFence = action == "replacement" ? 0 : (await data.Invoice.Rows("authorizationGrants").Find(new BsonDocument("_id", 99833L)).SingleAsync()).GetValue("fence", 0L).AsInt64;
        var input = new ReturnResolutionInput(action, "Ajukan penanganan barang yang telah diperiksa"); var metadata = CatalogPriceTestData.Meta("cq-original") with { Reason = "Catatan penyelesaian privat" };
        var saved = await service.RequestResolutionAsync(actor, caseId, returnId, 0, preview.Binding, input, metadata); Assert.Equal(saved, await service.RecoverResolutionAsync(actor, caseId, returnId, metadata.IdempotencyKey));
        if (action != "replacement") Assert.True((await data.Invoice.Rows("authorizationGrants").Find(new BsonDocument("_id", 99833L)).SingleAsync())["fence"].AsInt64 > financeFence);
        Assert.Equal(saved, await service.RequestResolutionAsync(actor, caseId, returnId, 0, preview.Binding, input, metadata));
        var buyer = await service.ReadResolutionAsync(data.Invoice.Order.Buyer, caseId, returnId); Assert.Equal(1, buyer.Revision); Assert.Equal("resolution_requested", buyer.Request!.State); Assert.Equal(input.PublicNote, buyer.Request.PublicNote);
        var text = System.Text.Json.JsonSerializer.Serialize(buyer); foreach (var secret in new[] { "privat", "PRIVAT", "ObservedIdentity", "ObservedLotLabel", "actorId", "people", "paidAmount", "stockLot" }) Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        foreach (var table in tables) Assert.Equal(before[table], await Business(table)); Assert.Equal(1, await data.Invoice.Count(P.Collection));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.RequestResolutionAsync(actor, caseId, returnId, 0, preview.Binding, input, CatalogPriceTestData.Meta("no-proposal-overwrite")));
        if (action != "replacement") {
            await data.Invoice.Rows("authorizationGrants").DeleteOneAsync(new BsonDocument("_id", 99833L));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverResolutionAsync(actor, caseId, returnId, metadata.IdempotencyKey));
        }
    }
    [Fact]
    public async Task Concurrent_proposals_have_one_winner_and_missing_request_history_cannot_recreate_a_resolution() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var (service, caseId, returnId) = await Setup(data); var actor = data.Invoice.Finance;
        var preview = await service.ReadResolutionAsync(actor, caseId, returnId, "replacement"); var input = new ReturnResolutionInput("replacement", "Ajukan penggantian barang");
        var attempts = await Task.WhenAll(Record.ExceptionAsync(() => service.RequestResolutionAsync(actor, caseId, returnId, 0, preview.Binding, input, CatalogPriceTestData.Meta("resolution-race-a"))), Record.ExceptionAsync(() => service.RequestResolutionAsync(actor, caseId, returnId, 0, preview.Binding, input, CatalogPriceTestData.Meta("resolution-race-b"))));
        Assert.Single(attempts, error => error is null); var row = await data.Invoice.Rows(P.Collection).Find(new BsonDocument()).SingleAsync(); await data.Invoice.Rows(P.Collection).DeleteOneAsync(new BsonDocument("_id", returnId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadResolutionAsync(actor, caseId, returnId)); await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverResolutionAsync(actor, caseId, returnId, row["requestKey"].AsString));
        Assert.NotNull(await Record.ExceptionAsync(() => service.RequestResolutionAsync(actor, caseId, returnId, 0, preview.Binding, input, CatalogPriceTestData.Meta("no-resolution-recreation")))); Assert.Equal(0, await data.Invoice.Count(P.Collection));
    }
}
