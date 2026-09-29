using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
public sealed class PaymentDraftTests(MongoFixture fixture)
{
    [Fact]
    public async Task Private_draft_without_image_retains_original_revision_and_never_recognizes_cash() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var actor = data.Invoice.Order.Buyer;
        var first = await data.Save(null, 0, data.Input, "payment-save"); var id = first["declarationId"].AsInt64;
        var second = await data.Save(id, 1, data.Input with { DeclaredAmount = new(1234.56m), Note = "Diperbaiki" }, "payment-edit");
        Assert.Equal(2L, second["revision"].AsInt64); Assert.Equal(first, await data.Save(null, 0, data.Input, "payment-save"));
        Assert.Equal(first, await data.Service.RecoverAsync(actor, "payment-save"));
        var view = await data.Service.ReadAsync(actor, id); Assert.Equal(2L, view.Revision); Assert.Equal("draft", view.State); Assert.Equal(1234.56m, view.Input.DeclaredAmount.Value);
        Assert.Equal(view.Id, Assert.Single((await data.Service.ListAsync(actor)).Items).Id);
        Assert.Equal(1, await data.Invoice.Count(PaymentDraftBson.Heads)); Assert.Equal(2, await data.Invoice.Count(PaymentDraftBson.Versions));
        Assert.Equal(0, await data.Invoice.Count("documentVersions")); Assert.Equal(0, await data.Invoice.Count("payments")); Assert.Equal(0, await data.Invoice.Count("bankReceipts"));
        Assert.Equal(1, await data.Invoice.Count("journalEntries")); Assert.Equal(1, await data.Invoice.Count(InvoiceBson.Allocations));
        Assert.Null(await data.Service.RecoverAsync(actor, "never-sent"));
    }
    [Fact]
    public async Task Concurrent_edits_keep_one_winner_and_stale_revision_cannot_overwrite_it() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var first = await data.Save(null, 0, data.Input, "payment-race-new"); var id = first["declarationId"].AsInt64;
        async Task<Exception?> Attempt(string key, decimal amount) { try { await data.Save(id, 1, data.Input with { DeclaredAmount = new(amount) }, key); return null; } catch (Exception error) { return error; } }
        var results = await Task.WhenAll(Attempt("payment-race-a", 1000m), Attempt("payment-race-b", 2000m)); Assert.Single(results, result => result is null);
        Assert.Single(results, result => result is PersistenceConflictException conflict && conflict.Code is "payment_draft_changed" or "version_conflict");
        Assert.Equal(2, await data.Invoice.Count(PaymentDraftBson.Versions));
        Assert.Equal("payment_draft_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Save(id, 1, data.Input, "payment-stale"))).Code);
    }
    [Theory]
    [InlineData("audit")] [InlineData("operation")] [InlineData("head")] [InlineData("old-version")]
    public async Task Missing_history_never_recreates_or_confirms_a_declaration(string fault) {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var first = await data.Save(null, 0, data.Input, "payment-history"); var id = first["declarationId"].AsInt64;
        await data.Save(id, 1, data.Input with { DeclaredAmount = new(2000m) }, "payment-history-edit");
        var version = await data.Invoice.Rows(PaymentDraftBson.Versions).Find(new BsonDocument("_id", first["versionId"])).SingleAsync();
        if (fault == "audit") await data.Invoice.Rows("auditEvents").DeleteOneAsync(new BsonDocument("_id", version["auditId"]));
        if (fault == "operation") await data.Invoice.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "operation", PaymentDraftBson.Operation }, { "key", "payment-history" } });
        if (fault == "head") await data.Invoice.Rows(PaymentDraftBson.Heads).DeleteOneAsync(new BsonDocument("_id", id));
        if (fault == "old-version") await data.Invoice.Rows(PaymentDraftBson.Versions).DeleteOneAsync(new BsonDocument("_id", first["versionId"]));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(data.Invoice.Order.Buyer, "payment-history"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Save(id, 2, data.Input, "payment-after-damage"));
    }
    [Fact]
    public async Task Audit_failure_rolls_back_draft_version_receipt_and_outbox() {
        await using var data = await PaymentDraftData.CreateAsync(fixture);
        await data.Invoice.Order.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" },
            { "validator", new BsonDocument("operation", new BsonDocument("$ne", PaymentDraftBson.Operation)) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Save(null, 0, data.Input, "payment-rollback"));
        Assert.Equal(0, await data.Invoice.Count(PaymentDraftBson.Heads)); Assert.Equal(0, await data.Invoice.Count(PaymentDraftBson.Versions));
        Assert.False(await data.Invoice.Rows("idempotencyRecords").Find(new BsonDocument("operation", PaymentDraftBson.Operation)).AnyAsync());
        Assert.False(await data.Invoice.Rows("outboxEvents").Find(new BsonDocument("kind", PaymentDraftBson.Operation)).AnyAsync());
        Assert.Equal(1, await data.Invoice.Count("journalEntries"));
    }
    [Fact]
    public async Task Another_buyer_or_finance_cannot_inspect_private_draft_and_revoked_membership_denies_recovery() {
        await using var data = await PaymentDraftData.CreateAsync(fixture); var actor = data.Invoice.Order.Buyer;
        var first = await data.Save(null, 0, data.Input, "payment-private");
        var other = await data.Invoice.Order.Prices.Catalog.ActorAsync(9950, CatalogAuthoringTestData.Audience, Permissions.PaymentEvidence, Permissions.InvoiceRead);
        Assert.Empty((await data.Service.ListAsync(other)).Items); Assert.Null(await data.Service.RecoverAsync(other, "payment-private"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(other, first["declarationId"].AsInt64));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Invoice.Finance, first["declarationId"].AsInt64));
        await data.Invoice.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", actor.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(actor, "payment-private"));
    }
}
internal sealed class PaymentDraftData(InvoiceData invoice, InvoiceView issued) : IAsyncDisposable
{
    internal InvoiceData Invoice { get; } = invoice;
    internal InvoiceView Issued { get; } = issued;
    internal PaymentDraftInput Input => new([Issued.Id], new(4000m), AccountingCalendar.BusinessDate(Invoice.Order.Prices.Catalog.Clock.UtcNow), "Pembayar fixture", "SYNTHETIC-TRANSFER", null);
    internal PaymentDraftService Service => new(Invoice.Order.Database, new(Invoice.Order.Prices.Catalog.Client), new(Invoice.Order.Database), Invoice.Order.Prices.Catalog.Clock, Invoice.Service);
    internal Task<BsonDocument> Save(long? id, long revision, PaymentDraftInput input, string key) => Service.SaveAsync(Invoice.Order.Buyer, id, revision, input, CatalogPriceTestData.Meta(key));
    internal static async Task<PaymentDraftData> CreateAsync(MongoFixture fixture) {
        var invoice = await InvoiceData.CreateAsync(fixture);
        try {
            await invoice.Order.Prices.Catalog.Base.AddGrantAsync(invoice.Order.Buyer, 99210, CatalogAuthoringTestData.Audience, [Permissions.PaymentEvidence]);
            var preview = await invoice.Service.PreviewAsync(invoice.Finance, invoice.Selection);
            var issued = await invoice.Service.IssueAsync(invoice.Finance, invoice.Selection, preview.Review, CatalogPriceTestData.Meta("payment-invoice"));
            return new(invoice, issued);
        } catch { await invoice.DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync() => Invoice.DisposeAsync();
}
