using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class AfterSalesCaseTests(MongoFixture fixture)
{
    [Fact]
    public void Intake_keeps_exact_positive_unique_receipt_SKU_quantities_and_does_not_invent_return_entitlement() {
        var input = new AfterSalesCaseInput(9, new string('a', 64), "damaged", [new(3, 4, 1), new(1, 4, 2)]);
        Assert.Equal(1, AfterSalesCasePolicy.Copy(input).Lines[0].ReceiptId);
        foreach (var changed in new[] { input with { InvoiceId = 0 }, input with { Binding = new string('A', 64) }, input with { Issue = "approved_refund" },
            input with { Lines = [] }, input with { Lines = [new(3, 4, 0)] }, input with { Lines = [new(3, 4, 1000001)] }, input with { Lines = [new(3, 4, 1), new(3, 4, 2)] } })
            Assert.Throws<UnitToko.Modules.Contracts.DomainValidationException>(() => AfterSalesCasePolicy.Copy(changed));
    }
    private static async Task<AfterSalesCaseService> Service(PaymentDraftData data) {
        await data.Invoice.Order.Prices.Catalog.Base.AddGrantAsync(data.Invoice.Order.Buyer, 99810, CatalogAuthoringTestData.Audience, [Permissions.CaseRead, Permissions.CaseRequest]);
        return new(data.Invoice.Order.Database, new(data.Invoice.Order.Prices.Catalog.Client), new(data.Invoice.Order.Database), data.Invoice.Order.Prices.Catalog.Clock, data.Invoice.Service);
    }
    [Fact]
    public async Task Finalized_sale_case_recovers_one_original_concurrent_request_and_preserves_invoice_stock_and_money() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var service = await Service(data); var buyer = data.Invoice.Order.Buyer;
        var source = await service.SourceAsync(buyer, data.Issued.Id.Value); Assert.Equal(data.Issued.Detail.Lines.Length, source.Lines.Length);
        var line = source.Lines[0]; Assert.Equal(data.Issued.Detail.Lines[0].PackageLabel, line.PackageLabel);
        var input = new AfterSalesCaseInput(source.InvoiceId.Value, source.Binding, "damaged", [new(line.ReceiptId.Value, line.SkuId.Value, 1)]);
        var meta = CatalogPriceTestData.Meta("case-original-concurrent");
        var names = new[] { UnitToko.Platform.Billing.InvoiceBson.Invoices, UnitToko.Platform.Billing.InvoiceBson.Allocations, "journalEntries", "movements", "reservations", "stockBalances" };
        async Task<List<BsonDocument>> BusinessRows(string name) {
            var rows = await data.Invoice.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); // Reads advance concurrency fences, never original business values.
            return rows;
        }
        var before = new Dictionary<string, List<BsonDocument>>(); foreach (var name in names) before[name] = await BusinessRows(name);
        foreach (var name in names.Where(name => name != "reservations")) Assert.NotEmpty(before[name]);
        var attempts = await Task.WhenAll(Record.ExceptionAsync(() => service.RequestAsync(buyer, input, meta)), Record.ExceptionAsync(() => service.RequestAsync(buyer, input, meta)));
        Assert.Contains(attempts, error => error is null);
        foreach (var error in attempts.Where(error => error is not null)) Assert.Equal("version_conflict", Assert.IsType<PersistenceConflictException>(error).Code);
        // A known exhausted write conflict permits a deliberate same-key retry; an unknown commit is never relabelled as that conflict.
        var result = await service.RequestAsync(buyer, input, meta); Assert.Equal(result, await service.RecoverAsync(buyer, meta.IdempotencyKey));
        var shown = await service.ReadAsync(buyer, result["caseId"].AsInt64); Assert.Equal("requested", shown.State); Assert.Equal(meta.Reason, shown.ReportedProblem);
        Assert.Equal(source.Binding, shown.Source.Binding); Assert.Equal(source.Lines.ToArray(), shown.Source.Lines.ToArray()); Assert.Equal(input.Lines.ToArray(), shown.Lines.ToArray());
        Assert.Equal(shown.Id, Assert.Single((await service.ListAsync(buyer)).Items).Id);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => service.RequestAsync(buyer, input with { Issue = "other" }, meta));
        Assert.Equal(1, await data.Invoice.Count(R.Collection));
        Assert.Equal(1, await data.Invoice.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", R.Operation)));
        Assert.Equal(1, await data.Invoice.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", R.Operation)));
        foreach (var name in names) Assert.Equal(before[name], await BusinessRows(name));
    }
    [Theory]
    [InlineData("source")] [InlineData("quantity")] [InlineData("item")]
    public async Task Changed_finalized_source_and_unaccepted_quantities_cannot_create_a_case(string fault) {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var service = await Service(data); var buyer = data.Invoice.Order.Buyer;
        var source = await service.SourceAsync(buyer, data.Issued.Id.Value); var line = source.Lines[0];
        var input = new AfterSalesCaseInput(source.InvoiceId.Value, fault == "source" ? new string('a', 64) : source.Binding, "damaged",
            [new(line.ReceiptId.Value, fault == "item" ? long.MaxValue : line.SkuId.Value, fault == "quantity" ? line.AcceptedBaseUnits + 1 : 1)]);
        Assert.NotNull(await Record.ExceptionAsync(() => service.RequestAsync(buyer, input, CatalogPriceTestData.Meta("invalid-case"))));
        Assert.Equal(0, await data.Invoice.Count(R.Collection)); Assert.False(await data.Invoice.Rows("outboxEvents").Find(new BsonDocument("kind", R.Operation)).AnyAsync());
    }
    [Fact]
    public async Task Current_ownership_revocation_and_incomplete_migration_or_receipt_history_deny_case_access_and_recreation() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var service = await Service(data); var buyer = data.Invoice.Order.Buyer; var db = data.Invoice.Order.Database;
        // Isolated empty interrupted installation, before any case writer is enabled.
        await data.Invoice.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument("_id", CollectionMigrations.CurrentVersion));
        var unavailable = await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => service.SourceAsync(buyer, data.Issued.Id.Value)); Assert.Equal("order_storage_unavailable", unavailable.Code);
        await new CollectionMigrations(db, data.Invoice.Order.Prices.Catalog.Clock).ApplyAsync();
        var source = await service.SourceAsync(buyer, data.Issued.Id.Value); var line = source.Lines[0]; var input = new AfterSalesCaseInput(source.InvoiceId.Value, source.Binding, "wrong_item", [new(line.ReceiptId.Value, line.SkuId.Value, 1)]);
        var metadata = CatalogPriceTestData.Meta("private-case"); var result = await service.RequestAsync(buyer, input, metadata);
        var other = await data.Invoice.Order.Prices.Catalog.ActorAsync(99820, CatalogAuthoringTestData.Audience, Permissions.CaseRead, Permissions.CaseRequest);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.SourceAsync(other, data.Issued.Id.Value));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ReadAsync(other, result["caseId"].AsInt64)); Assert.Null(await service.RecoverAsync(other, metadata.IdempotencyKey));
        Assert.Empty((await service.ListAsync(other)).Items);
        await data.Invoice.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", buyer.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverAsync(buyer, metadata.IdempotencyKey));
    }
    [Fact]
    public async Task Orphan_audit_alone_cannot_recreate_a_missing_original_case() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var service = await Service(data); var buyer = data.Invoice.Order.Buyer;
        var source = await service.SourceAsync(buyer, data.Issued.Id.Value); var line = source.Lines[0]; var input = new AfterSalesCaseInput(source.InvoiceId.Value, source.Binding, "missing", [new(line.ReceiptId.Value, line.SkuId.Value, 1)]);
        var metadata = CatalogPriceTestData.Meta("case-damaged-history"); await service.RequestAsync(buyer, input, metadata);
        await data.Invoice.Rows(R.Collection).DeleteManyAsync(new BsonDocument());
        await data.Invoice.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "operation", R.Operation }, { "key", metadata.IdempotencyKey } });
        await data.Invoice.Rows("outboxEvents").DeleteOneAsync(new BsonDocument("kind", R.Operation));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RecoverAsync(buyer, metadata.IdempotencyKey));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RequestAsync(buyer, input, metadata)); Assert.Equal(0, await data.Invoice.Count(R.Collection));
    }
}
