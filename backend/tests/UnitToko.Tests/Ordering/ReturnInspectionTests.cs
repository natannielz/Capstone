using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using I = UnitToko.Platform.Inventory.ReturnInspectionRecords;
using Q = UnitToko.Platform.Inventory.ReturnQuarantineLedger;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class ReturnInspectionTests(MongoFixture fixture)
{
    internal static async Task<(AfterSalesCaseService Service, long CaseId, long ReturnId, ReturnedStockInspectionInput Input)> Setup(PaymentDraftData data) {
        var (service, caseId, line) = await ReturnQuarantineTests.Setup(data); var actor = data.Invoice.Finance;
        await data.Invoice.Order.Prices.Catalog.Base.AddGrantAsync(actor, 99832, CatalogAuthoringTestData.Shop, [Permissions.StockCount]);
        var view = await service.ReadReturnsAsync(actor, caseId, true);
        var saved = await service.ReceiveReturnAsync(actor, caseId, 0, view.Binding, new("Diterima dalam karantina", [new(line.ReceiptId.Value, line.SkuId.Value, 1, "opened")]), CatalogPriceTestData.Meta("inspection-intake"));
        return (service, caseId, saved["returnId"].AsInt64, new("Barang telah diperiksa, kemasan terbuka", [new(line.ReceiptId.Value, line.SkuId.Value, 1, "matched", "opened", "Label produk sesuai SKU pada invoice", "LOT-TERLIHAT-PRIVAT")]));
    }
    [Fact]
    public void Inspection_matches_exact_intake_and_keeps_unknown_or_different_identity_and_mixed_condition_explicit() {
        var received = new ReturnedStockInput("Diterima", [new(1, 2, 1, "uninspected")]); var input = new ReturnedStockInspectionInput("Telah diperiksa", [new(1, 2, 1, "unverified", "mixed", "Label tidak terbaca", null)]);
        ReturnedStockInspectionPolicy.RequireReceipt(input, received); Assert.Equal("unverified", ReturnedStockInspectionPolicy.Copy(input).Lines[0].Identity);
        foreach (var changed in new[] { input with { Lines = [] }, input with { Lines = [input.Lines[0] with { InspectedBaseUnits = 12 }] }, input with { Lines = [input.Lines[0] with { SkuId = 3 }] },
            input with { Lines = [input.Lines[0] with { Condition = "restock" }] }, input with { Lines = [input.Lines[0] with { Identity = "refunded" }] }, input with { Lines = [input.Lines[0] with { ObservedIdentity = "\u200bLabel" }] },
            input with { Lines = [input.Lines[0] with { ObservedLotLabel = " " }] }, input with { Lines = [.. input.Lines, .. input.Lines] } }) Assert.Throws<DomainValidationException>(() => ReturnedStockInspectionPolicy.RequireReceipt(changed, received));
        ReturnedStockInspectionPolicy.RequireReceipt(input with { Lines = [input.Lines[0] with { Identity = "different", Condition = "damaged" }] }, received);
    }
    [Fact]
    public async Task Inspection_recovers_exact_original_receipt_and_public_projection_without_restocking_or_financial_side_effects() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var (service, caseId, returnId, input) = await Setup(data); var actor = data.Invoice.Finance;
        var tables = new[] { Q.Collection, "stockBalances", "lots", "movements", "reservations", "journalEntries", UnitToko.Platform.Billing.InvoiceBson.Invoices, UnitToko.Platform.Billing.InvoiceBson.Allocations };
        async Task<List<BsonDocument>> Read(string name) { var rows = await data.Invoice.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) { row.Remove("fence"); row.Remove("postingFence"); } return rows; }
        var before = new Dictionary<string, List<BsonDocument>>(); foreach (var table in tables) { before[table] = await Read(table); Assert.NotEmpty(before[table]); }
        var preview = await service.ReadInspectionAsync(actor, caseId, returnId, true); Assert.Equal(0, preview.Revision); Assert.Null(preview.Inspection); Assert.Equal(returnId, preview.Receipt.Id.Value);
        var metadata = CatalogPriceTestData.Meta("ci-original") with { Reason = "Contrôle physique privé" };
        var saved = await service.InspectReturnAsync(actor, caseId, returnId, 0, preview.Binding, input, metadata);
        Assert.Equal(saved, await service.InspectReturnAsync(actor, caseId, returnId, 0, preview.Binding, input, metadata)); Assert.Equal(saved, await service.RecoverInspectionAsync(actor, caseId, returnId, metadata.IdempotencyKey));
        var buyer = await service.ReadInspectionAsync(data.Invoice.Order.Buyer, caseId, returnId); Assert.Equal(1, buyer.Revision); Assert.Equal(input.PublicNote, buyer.Inspection!.PublicNote); Assert.Equal("matched", Assert.Single(buyer.Inspection.Lines).Identity);
        var publicText = System.Text.Json.JsonSerializer.Serialize(buyer); foreach (var secret in new[] { "PRIVAT", "privé", "ObservedIdentity", "ObservedLotLabel", "actorId", "people" }) Assert.DoesNotContain(secret, publicText, StringComparison.OrdinalIgnoreCase);
        var row = await data.Invoice.Rows(I.Collection).Find(new BsonDocument()).SingleAsync(); Assert.Equal("inspected_quarantine", row["state"]); Assert.Equal("unverified", row["lotIdentity"]); Assert.Equal(input.Lines[0].ObservedLotLabel, row["input"]["lines"][0]["observedLotLabel"]);
        Assert.Equal(1, await data.Invoice.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", I.Operation))); Assert.Equal(1, await data.Invoice.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", I.Operation)));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.InspectReturnAsync(actor, caseId, returnId, 0, preview.Binding, input, CatalogPriceTestData.Meta("ci-new-does-not-overwrite")));
        foreach (var table in tables) Assert.Equal(before[table], await Read(table));
    }
    [Fact]
    public async Task Concurrent_inspections_have_one_winner_and_missing_durable_assessment_never_means_uninspected() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var (service, caseId, returnId, input) = await Setup(data); var actor = data.Invoice.Finance; var view = await service.ReadInspectionAsync(actor, caseId, returnId, true);
        var attempts = await Task.WhenAll(Record.ExceptionAsync(() => service.InspectReturnAsync(actor, caseId, returnId, 0, view.Binding, input, CatalogPriceTestData.Meta("inspection-race-a"))), Record.ExceptionAsync(() => service.InspectReturnAsync(actor, caseId, returnId, 0, view.Binding, input, CatalogPriceTestData.Meta("inspection-race-b"))));
        Assert.Single(attempts, error => error is null); var original = await data.Invoice.Rows(I.Collection).Find(new BsonDocument()).SingleAsync(); await data.Invoice.Rows(I.Collection).DeleteOneAsync(new BsonDocument("_id", returnId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadInspectionAsync(actor, caseId, returnId)); await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverInspectionAsync(actor, caseId, returnId, original["requestKey"].AsString));
        Assert.NotNull(await Record.ExceptionAsync(() => service.InspectReturnAsync(actor, caseId, returnId, 0, view.Binding, input, CatalogPriceTestData.Meta("no-inspection-recreation")))); Assert.Equal(0, await data.Invoice.Count(I.Collection));
    }
    [Theory]
    [InlineData("count-grant")] [InlineData("person")]
    public async Task Current_stock_count_grant_independent_verified_person_and_fresh_MFA_are_required_even_for_original_recovery(string fault) {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var (service, caseId, returnId, input) = await Setup(data); var actor = data.Invoice.Finance; var catalog = data.Invoice.Order.Prices.Catalog;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadInspectionAsync(actor with { AuthenticatedAt = catalog.Clock.UtcNow.AddMinutes(-6) }, caseId, returnId, true));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadInspectionAsync(data.Invoice.Order.Buyer, caseId, returnId, true));
        var restricted = await catalog.ActorAsync(99845, CatalogAuthoringTestData.Shop, Permissions.CaseRead, Permissions.CaseManage, Permissions.StockReceive); await PersonTestData.SeedDistinctPeopleAsync(data.Invoice.Order.Database, catalog.Clock, restricted.UserId);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadInspectionAsync(restricted, caseId, returnId, true));
        var view = await service.ReadInspectionAsync(actor, caseId, returnId, true); var metadata = CatalogPriceTestData.Meta("ci-revoked"); await service.InspectReturnAsync(actor, caseId, returnId, 0, view.Binding, input, metadata);
        if (fault == "person") await data.Invoice.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        else await data.Invoice.Rows("authorizationGrants").DeleteOneAsync(new BsonDocument("_id", 99832L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverInspectionAsync(actor, caseId, returnId, metadata.IdempotencyKey)); Assert.Equal(1, await data.Invoice.Count(I.Collection));
    }
}
