using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using Q = UnitToko.Platform.Inventory.ReturnQuarantineLedger;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class ReturnQuarantineTests(MongoFixture fixture)
{
    [Fact]
    public void Physical_intake_copies_exact_base_units_and_rejects_duplicate_inferred_or_financial_conditions() {
        var input = new ReturnedStockInput("Barang diterima untuk pemeriksaan", [new(3, 4, 1, "opened")]); var copied = ReturnedStockPolicy.Copy(input); Assert.Equal(input.PublicNote, copied.PublicNote); Assert.Equal(input.Lines.ToArray(), copied.Lines.ToArray());
        foreach (var changed in new[] { input with { Lines = [] }, input with { Lines = [new(3, 4, 0, "opened")] }, input with { Lines = [new(3, 4, 1000001, "sealed")] },
            input with { Lines = [new(3, 4, 1, "restock")] }, input with { Lines = [new(3, 4, 1, "refunded")] }, input with { Lines = [.. input.Lines, .. input.Lines] }, input with { PublicNote = "Privat\u200b" } })
            Assert.Throws<UnitToko.Modules.Contracts.DomainValidationException>(() => ReturnedStockPolicy.Copy(changed));
    }
    internal static async Task<(AfterSalesCaseService Service, long Id, AfterSalesSourceLine Line)> Setup(PaymentDraftData data, long quantity = 2) {
        var catalog = data.Invoice.Order.Prices.Catalog; var buyer = data.Invoice.Order.Buyer; var reviewer = data.Invoice.Finance;
        await catalog.Base.AddGrantAsync(buyer, 99810, CatalogAuthoringTestData.Audience, [Permissions.CaseRead, Permissions.CaseRequest]);
        await catalog.Base.AddGrantAsync(reviewer, 99830, CatalogAuthoringTestData.Shop, [Permissions.CaseRead, Permissions.CaseManage, Permissions.StockReceive]);
        foreach (var person in new[] { buyer.UserId, reviewer.UserId }) if (!await data.Invoice.Rows("personRecords").Find(new BsonDocument("_id", PersonTestData.PersonId(person))).AnyAsync()) await PersonTestData.SeedDistinctPeopleAsync(data.Invoice.Order.Database, catalog.Clock, person);
        var service = new AfterSalesCaseService(data.Invoice.Order.Database, new(catalog.Client), new(data.Invoice.Order.Database), catalog.Clock, data.Invoice.Service);
        var sale = await service.SourceAsync(buyer, data.Issued.Id.Value); var line = sale.Lines[0];
        var id = (await service.RequestAsync(buyer, new(sale.InvoiceId.Value, sale.Binding, "damaged", [new(line.ReceiptId.Value, line.SkuId.Value, quantity)]), CatalogPriceTestData.Meta("return-case")))["caseId"].AsInt64;
        var review = await service.ReadDecisionAsync(reviewer, id); await service.DecideAsync(reviewer, id, 1, review.Binding, new("authorize_return", "Pengembalian untuk pemeriksaan kondisi", [new(line.ReceiptId.Value, line.SkuId.Value, quantity)]), CatalogPriceTestData.Meta("return-authorization"));
        return (service, id, line);
    }
    [Fact]
    public async Task Partial_returns_remain_exact_quarantined_base_units_and_old_receipts_recover_after_later_batches_without_stock_or_money_changes() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var (service, id, line) = await Setup(data); var reviewer = data.Invoice.Finance;
        var names = new[] { UnitToko.Platform.Billing.InvoiceBson.Invoices, UnitToko.Platform.Billing.InvoiceBson.Allocations, "journalEntries", "movements", "reservations", "stockBalances", "lots" };
        async Task<List<BsonDocument>> Business(string name) { var rows = await data.Invoice.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) { row.Remove("fence"); row.Remove("postingFence"); } return rows; }
        var before = new Dictionary<string, List<BsonDocument>>(); foreach (var table in names) { before[table] = await Business(table); Assert.NotEmpty(before[table]); }
        Assert.True(line.BaseUnitsPerPackage > 1); var first = await service.ReadReturnsAsync(reviewer, id, true); Assert.Equal(0, first.Revision); Assert.Equal(0, Assert.Single(first.Limits).ReceivedBaseUnits);
        var input = new ReturnedStockInput("Satu barang diterima dalam karantina", [new(line.ReceiptId.Value, line.SkuId.Value, 1, "opened")]); var metadata = CatalogPriceTestData.Meta("cr-first") with { Reason = "Catatan penerimaan privat" };
        var saved = await service.ReceiveReturnAsync(reviewer, id, 0, first.Binding, input, metadata); Assert.Equal(saved, await service.RecoverReturnAsync(reviewer, id, metadata.IdempotencyKey));
        var second = await service.ReadReturnsAsync(reviewer, id, true); Assert.Equal(1, second.Revision); Assert.Equal(1, Assert.Single(second.Limits).ReceivedBaseUnits);
        await service.ReceiveReturnAsync(reviewer, id, 1, second.Binding, input with { Lines = [input.Lines[0] with { ObservedCondition = "damaged" }] }, CatalogPriceTestData.Meta("cr-second"));
        Assert.Equal(saved, await service.ReceiveReturnAsync(reviewer, id, 0, first.Binding, input, metadata)); Assert.Equal(saved, await service.RecoverReturnAsync(reviewer, id, metadata.IdempotencyKey));
        var buyer = await service.ReadReturnsAsync(data.Invoice.Order.Buyer, id); Assert.Equal(2, buyer.Revision); Assert.Equal(2, Assert.Single(buyer.Limits).ReceivedBaseUnits); Assert.Equal(2, buyer.Recent.Length);
        Assert.DoesNotContain("privat", System.Text.Json.JsonSerializer.Serialize(buyer)); var last = await service.ReadReturnsAsync(reviewer, id, true);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.ReceiveReturnAsync(reviewer, id, 2, last.Binding, input, CatalogPriceTestData.Meta("cr-excess")));
        Assert.Equal(2, await data.Invoice.Count(Q.Collection)); var rows = await data.Invoice.Rows(Q.Collection).Find(new BsonDocument()).ToListAsync();
        Assert.All(rows, row => { Assert.Equal("quarantined", row["state"]); Assert.Equal("unverified", row["lotIdentity"]); Assert.Equal(1L, row["input"]["lines"][0]["receivedBaseUnits"].AsInt64); Assert.Equal(line.BaseUnitsPerPackage, row["source"]["stock"][0]["baseUnitsPerPackage"].AsInt32); Assert.NotEmpty(row["source"]["stock"][0]["originalLots"].AsBsonArray); });
        foreach (var table in names) Assert.Equal(before[table], await Business(table));
    }
    [Fact]
    public async Task Concurrent_receipts_have_one_winner_and_missing_middle_history_cannot_be_recreated_or_reported_as_empty() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var (service, id, line) = await Setup(data); var actor = data.Invoice.Finance;
        var source = await service.ReadReturnsAsync(actor, id, true); var input = new ReturnedStockInput("Diterima untuk pemeriksaan", [new(line.ReceiptId.Value, line.SkuId.Value, 1, "uninspected")]);
        var attempts = await Task.WhenAll(Record.ExceptionAsync(() => service.ReceiveReturnAsync(actor, id, 0, source.Binding, input, CatalogPriceTestData.Meta("race-return-a"))), Record.ExceptionAsync(() => service.ReceiveReturnAsync(actor, id, 0, source.Binding, input, CatalogPriceTestData.Meta("race-return-b"))));
        Assert.Single(attempts, error => error is null); Assert.Equal(1, await data.Invoice.Count(Q.Collection)); var winner = await data.Invoice.Rows(Q.Collection).Find(new BsonDocument()).SingleAsync();
        var next = await service.ReadReturnsAsync(actor, id, true); await service.ReceiveReturnAsync(actor, id, 1, next.Binding, input, CatalogPriceTestData.Meta("race-return-next"));
        await data.Invoice.Rows(Q.Collection).DeleteOneAsync(new BsonDocument("_id", winner["_id"]));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadReturnsAsync(actor, id, true));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverReturnAsync(actor, id, winner["requestKey"].AsString));
        Assert.NotNull(await Record.ExceptionAsync(() => service.ReceiveReturnAsync(actor, id, 0, source.Binding, input, CatalogPriceTestData.Meta("no-missing-receipt-recreation")))); Assert.Equal(1, await data.Invoice.Count(Q.Collection));
    }
    [Fact]
    public async Task Stock_receiving_permission_current_people_and_fresh_independent_account_are_required_for_preview_commit_and_recovery() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var (service, id, line) = await Setup(data); var actor = data.Invoice.Finance; var catalog = data.Invoice.Order.Prices.Catalog;
        var input = new ReturnedStockInput("Diterima untuk pemeriksaan", [new(line.ReceiptId.Value, line.SkuId.Value, 1, "sealed")]); var source = await service.ReadReturnsAsync(actor, id, true);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadReturnsAsync(actor with { AuthenticatedAt = catalog.Clock.UtcNow.AddMinutes(-6) }, id, true));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadReturnsAsync(data.Invoice.Order.Buyer, id, true));
        var restricted = await catalog.ActorAsync(99840, CatalogAuthoringTestData.Shop, Permissions.CaseRead, Permissions.CaseManage); await PersonTestData.SeedDistinctPeopleAsync(data.Invoice.Order.Database, catalog.Clock, restricted.UserId);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadReturnsAsync(restricted, id, true));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReceiveReturnAsync(restricted, id, 0, source.Binding, input, CatalogPriceTestData.Meta("no-stock-receive")));
        var metadata = CatalogPriceTestData.Meta("revoked-return"); await service.ReceiveReturnAsync(actor, id, 0, source.Binding, input, metadata);
        await data.Invoice.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", actor.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverReturnAsync(actor, id, metadata.IdempotencyKey)); Assert.Equal(1, await data.Invoice.Count(Q.Collection));
    }
    [Theory]
    [InlineData("missing-request-key")] [InlineData("unknown-unique")]
    public async Task Runtime_requires_both_unique_keys_and_rejects_a_new_incompatible_unique_constraint(string fault) {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var (service, id, _) = await Setup(data);
        if (fault == "missing-request-key") await data.Invoice.Rows(Q.Collection).Indexes.DropOneAsync("ux_return_quarantine_request");
        else await data.Invoice.Rows(Q.Collection).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("reason", 1), new CreateIndexOptions { Unique = true }));
        var failure = await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => service.ReadReturnsAsync(data.Invoice.Finance, id, true)); Assert.Equal("inventory_storage_unavailable", failure.Code); Assert.Equal(0, await data.Invoice.Count(Q.Collection));
    }
}
