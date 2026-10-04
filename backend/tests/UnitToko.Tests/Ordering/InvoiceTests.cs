using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Accounting;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class InvoiceTests(MongoFixture fixture)
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Later_partial_delivery_conserves_original_order_and_revalidates_prior_receipt(bool damagedPriorReceipt) {
        await using var data = await InvoiceData.CreateAsync(fixture);
        var first = await data.Service.PreviewAsync(data.Finance, data.Selection);
        await data.Service.IssueAsync(data.Finance, data.Selection, first.Review, CatalogPriceTestData.Meta("invoice-first-part"));
        await data.Dispatch.PrepareAsync(1, data.Dispatch.Prepared.PreparationRevision, "invoice-second-prepare");
        var nextDispatch = await data.Dispatch.DispatchAsync(await data.Dispatch.PreviewAsync(), "invoice-second-dispatch"); var shipmentId = nextDispatch["shipmentId"].AsInt64;
        var document = await ShipmentDocumentTests.Service(data.Dispatch).IssueAsync(data.Dispatch.Picker, shipmentId, 1, CatalogPriceTestData.Meta("invoice-second-document"));
        var receipts = ShipmentReceiptTests.Service(data.Dispatch);
        var challenge = await receipts.IssueChallengeAsync(data.Dispatch.Courier, shipmentId, document.Id.Value, data.Order.Buyer.UserId, CatalogPriceTestData.Meta("invoice-second-qr"));
        var review = await receipts.ReviewAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!);
        var second = await receipts.ConfirmAsync(data.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true,
            [new ReceiptLineDeclaration(review.Lines[0].SkuId, new(1, 0), new(0, 0), new(0, 0), null)], CatalogPriceTestData.Meta("invoice-second-receipt"));
        var selected = new[] { new InvoiceReceiptSelection(second.ShipmentId, second.Id) };
        if (damagedPriorReceipt) {
            await data.Rows("shipmentReceipts").DeleteOneAsync(new BsonDocument("_id", data.Receipt.Id.Value));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Finance, selected));
            Assert.Equal(1, await data.Count(InvoiceBson.Invoices)); return;
        }
        var next = await data.Service.PreviewAsync(data.Finance, selected); Assert.Equal(12L, Assert.Single(next.Lines).AcceptedBaseUnits);
        var issued = await data.Service.IssueAsync(data.Finance, selected, next.Review, CatalogPriceTestData.Meta("invoice-second-part")); Assert.Equal(2L, issued.Number);
        var line = (await data.Rows(InvoiceBson.Invoices).Find(new BsonDocument("_id", issued.Id.Value)).SingleAsync())["lines"][0];
        Assert.Equal(6L, line["previousBaseUnits"].AsInt64); Assert.Equal(first.Net.Value, line["previousNet"].AsDecimal);
        Assert.Equal(2, await data.Count("journalEntries")); Assert.Equal(2, await data.Count(InvoiceBson.Allocations));
    }
    [Fact]
    public async Task Accepted_partial_receipt_issues_once_with_original_price_balanced_journal_and_private_document() {
        await using var data = await InvoiceData.CreateAsync(fixture); var service = data.Service;
        var candidate = Assert.Single((await service.CandidatesAsync(data.Finance)).Items); Assert.False(candidate.Invoiced);
        Assert.Equal(data.Receipt.Id, candidate.ReceiptId);
        var review = await service.PreviewAsync(data.Finance, data.Selection); var line = Assert.Single(review.Lines);
        Assert.Equal(6L, line.AcceptedBaseUnits); Assert.Equal(12, line.BaseUnitsPerPackage); Assert.Equal(30, review.TermDays);
        Assert.Equal(review.IssueDate.AddDays(30), review.DueDate);
        var meta = CatalogPriceTestData.Meta("invoice-once"); var result = await service.IssueAsync(data.Finance, data.Selection, review.Review, meta);
        Assert.Equal(result.Id, (await service.IssueAsync(data.Finance, data.Selection, review.Review, meta)).Id);
        Assert.Equal(result.Id, (await service.RecoverAsync(data.Finance, meta.IdempotencyKey))!.Id);
        Assert.True(Assert.Single((await service.CandidatesAsync(data.Finance)).Items).Invoiced);
        Assert.Equal(1, await data.Count(InvoiceBson.Invoices)); Assert.Equal(1, await data.Count(InvoiceBson.Allocations)); Assert.Equal(1, await data.Count("journalEntries"));
        var journal = await data.Rows("journalEntries").Find(new BsonDocument()).SingleAsync(); var draft = AccountingBson.ReadDraft(journal);
        Assert.Equal(result.Detail.Total, draft.Total); Assert.Equal("billing.invoice", draft.Source.Namespace);
        Assert.Equal(result.Id, Assert.Single((await service.ListAsync(data.Order.Buyer)).Items).Id);
        var document = await service.DownloadAsync(data.Order.Buyer, result.Id.Value);
        Assert.Equal(result.DocumentHash, document.Hash); Assert.Contains("Penjual fixture", document.Content);
        Assert.Contains("Cabang fixture", document.Content); Assert.Contains("Alamat badan fixture", document.Content);
        Assert.DoesNotContain("7000.25", document.Content); Assert.DoesNotContain("recordHash", document.Content);
        Assert.Equal(2, await data.Count("movements"));
        await data.Rows("fixtureInvoiceApprovals").UpdateOneAsync(new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Set("approved", false));
        Assert.Equal(result.Id, (await service.RecoverAsync(data.Finance, meta.IdempotencyKey))!.Id);
        Assert.Equal(result.Id, (await service.IssueAsync(data.Finance, data.Selection, review.Review, meta)).Id);
    }
    [Theory]
    [InlineData("closed-book")] [InlineData("missing-chart")] [InlineData("audit-failure")] [InlineData("final-policy")] [InlineData("final-session")]
    public async Task Failed_required_effect_or_final_authority_rolls_back_every_invoice_effect(string failure) {
        await using var data = await InvoiceData.CreateAsync(fixture);
        if (failure == "final-session") await data.Rows("fixtureInvoiceApprovals").UpdateOneAsync(new BsonDocument("_id", 1L),
            Builders<BsonDocument>.Update.Set("expiresAt", data.Order.Prices.Catalog.Clock.UtcNow.AddDays(1).UtcDateTime));
        var review = await data.Service.PreviewAsync(data.Finance, data.Selection);
        if (failure == "closed-book") {
            var closed = await data.Rows("accountingBooks").UpdateOneAsync(new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Set("closedThrough", review.IssueDate.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)));
            Assert.Equal(1, closed.MatchedCount);
        }
        if (failure == "missing-chart") await data.Rows("accountingChartVersions").DeleteManyAsync(new BsonDocument());
        if (failure == "audit-failure") await data.Order.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", new BsonDocument("operation", new BsonDocument("$ne", InvoiceBson.Operation)) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        if (failure == "final-policy") data.Admission.BeforeComplete = (_, _) => throw InvoicePolicy.Invalid();
        if (failure == "final-session") data.Admission.BeforeComplete = (_, _) => {
            data.Order.Prices.Catalog.Clock.UtcNow = data.Order.Prices.Catalog.Clock.UtcNow.AddHours(9); return Task.CompletedTask;
        };
        var error = await Record.ExceptionAsync(() => data.Service.IssueAsync(data.Finance, data.Selection, review.Review, CatalogPriceTestData.Meta("invoice-rollback")));
        if (failure == "audit-failure") Assert.IsType<MongoWriteException>(error);
        else if (failure == "closed-book") Assert.Equal("accounting_period_closed", Assert.IsType<PersistenceConflictException>(error).Code);
        else if (failure == "final-session") Assert.IsType<AuthorizationDeniedException>(error);
        else Assert.IsType<DomainValidationException>(error);
        Assert.Equal(0, await data.Count(InvoiceBson.Invoices)); Assert.Equal(0, await data.Count(InvoiceBson.Allocations)); Assert.Equal(0, await data.Count("journalEntries"));
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", InvoiceBson.Operation)).AnyAsync());
        Assert.False(await data.Rows("auditEvents").Find(new BsonDocument("operation", InvoiceBson.Operation)).AnyAsync());
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", InvoiceBson.Operation)).AnyAsync());
        Assert.False(await data.Rows("idCounters").Find(new BsonDocument("_id", new BsonRegularExpression(@"^invoice-number\."))).AnyAsync());
        Assert.Equal(2, await data.Count("movements"));
    }
    [Fact]
    public async Task Competing_issuers_cannot_consume_receipt_twice() {
        await using var data = await InvoiceData.CreateAsync(fixture); var review = await data.Service.PreviewAsync(data.Finance, data.Selection);
        async Task<Exception?> Attempt(string key) { try { await data.Service.IssueAsync(data.Finance, data.Selection, review.Review, CatalogPriceTestData.Meta(key)); return null; } catch (Exception error) { return error; } }
        var results = await Task.WhenAll(Attempt("invoice-race-a"), Attempt("invoice-race-b")); Assert.Single(results, result => result is null);
        Assert.Single(results, result => result is PersistenceConflictException conflict && conflict.Code is "receipt_already_invoiced" or "version_conflict");
        Assert.Equal(1, await data.Count(InvoiceBson.Invoices)); Assert.Equal(1, await data.Count(InvoiceBson.Allocations)); Assert.Equal(1, await data.Count("journalEntries"));
    }
    [Fact]
    public async Task Missing_producer_denies_issuance_and_outsiders_cannot_read_or_recover() {
        await using var data = await InvoiceData.CreateAsync(fixture);
        var closed = new InvoiceService(data.Order.Database, new(data.Order.Prices.Catalog.Client), new(data.Order.Database), data.Order.Prices.Catalog.Clock, ShipmentReceiptTests.Service(data.Dispatch));
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => closed.PreviewAsync(data.Finance, data.Selection)); Assert.Equal("invoice_policy_unavailable", error.Code);
        var review = await data.Service.PreviewAsync(data.Finance, data.Selection); var result = await data.Service.IssueAsync(data.Finance, data.Selection, review.Review, CatalogPriceTestData.Meta("invoice-scoped"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Dispatch.Courier, result.Id.Value));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.DownloadAsync(data.Dispatch.Courier, result.Id.Value));
        var other = await data.Order.Prices.Catalog.ActorAsync(9941, CatalogAuthoringTestData.OtherOwner, Permissions.InvoiceRead, Permissions.InvoiceIssue);
        Assert.Empty((await data.Service.ListAsync(other)).Items); Assert.Null(await data.Service.RecoverAsync(other, "invoice-scoped"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(other, result.Id.Value));
        await data.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.Finance.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(data.Finance, "invoice-scoped"));
    }
    [Theory]
    [InlineData("allocation")] [InlineData("journal")] [InlineData("document")] [InlineData("invoice-and-allocations")]
    public async Task Damaged_financial_provenance_cannot_be_read_or_treated_as_an_unbilled_receipt(string fault) {
        await using var data = await InvoiceData.CreateAsync(fixture); var review = await data.Service.PreviewAsync(data.Finance, data.Selection);
        var result = await data.Service.IssueAsync(data.Finance, data.Selection, review.Review, CatalogPriceTestData.Meta("invoice-corruption"));
        if (fault == "allocation") await data.Rows(InvoiceBson.Allocations).DeleteManyAsync(new BsonDocument());
        if (fault == "journal") await data.Rows("journalEntries").DeleteManyAsync(new BsonDocument());
        if (fault == "document") await data.Rows(InvoiceBson.Invoices).UpdateOneAsync(new BsonDocument("_id", result.Id.Value), Builders<BsonDocument>.Update.Set("document", "tampered"));
        if (fault == "invoice-and-allocations") { await data.Rows(InvoiceBson.Allocations).DeleteManyAsync(new BsonDocument()); await data.Rows(InvoiceBson.Invoices).DeleteManyAsync(new BsonDocument()); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Finance, data.Selection));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(data.Finance, "invoice-corruption"));
    }
}

// These independent approvals and chart inputs are isolated synthetic test data, not production policy.
internal sealed class InvoiceData(ShipmentDispatchTestData dispatch) : IAsyncDisposable
{
    internal ShipmentDispatchTestData Dispatch { get; } = dispatch;
    internal OrderSubmissionTestData Order => Dispatch.Order;
    internal ActorContext Finance { get; private set; } = null!;
    internal ShipmentReceiptView Receipt { get; private set; } = null!;
    internal ImmutableArray<InvoiceReceiptSelection> Selection => [new(Receipt.ShipmentId, Receipt.Id)];
    internal FixtureAdmission Admission { get; private set; } = null!;
    internal InvoiceService Service => new(Order.Database, new(Order.Prices.Catalog.Client), new(Order.Database), Order.Prices.Catalog.Clock, ShipmentReceiptTests.Service(Dispatch), Admission);
    internal IMongoCollection<BsonDocument> Rows(string name) => Order.Rows(name);
    internal Task<long> Count(string name) => Rows(name).CountDocumentsAsync(new BsonDocument());
    internal static async Task<InvoiceData> CreateAsync(MongoFixture fixture, ShipmentDispatchTestData? attachedDispatch = null,
        bool preserveApprovedAccounting = false) {
        var data = new InvoiceData(attachedDispatch ?? await ShipmentDispatchTestData.CreateAsync(fixture));
        try {
            data.Receipt = await ShipmentReceiptProofTests.Accept(data.Dispatch);
            data.Finance = await data.Order.Prices.Catalog.ActorAsync(9940, CatalogAuthoringTestData.Shop, Permissions.InvoiceRead, Permissions.InvoiceIssue);
            await data.Rows("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", data.Finance.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Finance fixture" }, { "verifiedEmail", "finance@example.invalid" }, { "verifiedAt", data.Order.Prices.Catalog.Clock.UtcNow.UtcDateTime }, { "updatedAt", data.Order.Prices.Catalog.Clock.UtcNow.UtcDateTime } });
            await data.Order.Prices.Catalog.Base.AddGrantAsync(data.Order.Buyer, 99201, CatalogAuthoringTestData.Audience, [Permissions.InvoiceRead]);
            if (preserveApprovedAccounting) {
                var book = Assert.Single(await data.Rows("accountingBooks").Find(new BsonDocument()).ToListAsync());
                var chart = await data.Rows("accountingChartVersions").Find(new BsonDocument("_id", book["chartVersionId"])).SingleAsync();
                foreach (var key in new[] { "invoice_ar", "invoice_revenue", "invoice_tax", "invoice_charges" })
                    Assert.Single(chart["mappings"].AsBsonArray, mapping => mapping["mappingKey"] == key);
            } else {
                await AccountingTestDocuments.ConfigureAsync(data.Order.Database);
                await data.Rows("accountingChartVersions").UpdateOneAsync(new BsonDocument("_id", 20L), Builders<BsonDocument>.Update.Set("mappings", new BsonArray {
                    AccountingTestDocuments.Mapping("invoice_ar", 801, "A1101"), AccountingTestDocuments.Mapping("invoice_revenue", 802, "A4101"),
                    AccountingTestDocuments.Mapping("invoice_tax", 803, "A2101"), AccountingTestDocuments.Mapping("invoice_charges", 804, "A4102") }));
            }
            await data.Rows("fixtureInvoiceApprovals").InsertOneAsync(new BsonDocument { { "_id", 1L }, { "revision", 1L }, { "fence", 0L }, { "approved", true }, { "expiresAt", data.Order.Prices.Catalog.Clock.UtcNow.AddMinutes(30).UtcDateTime }, { "termDays", 30 } });
            data.Admission = new(data); return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    public ValueTask DisposeAsync() => Dispatch.DisposeAsync();
    internal sealed class FixtureAdmission(InvoiceData data) : IInvoiceAdmissionReader
    {
        public IMongoDatabase Database => data.Order.Database;
        internal Func<IClientSessionHandle, CancellationToken, Task>? BeforeComplete { get; set; }
        private async Task<BsonDocument> FenceAsync(IClientSessionHandle tx, CancellationToken ct) {
            var row = await data.Rows("fixtureInvoiceApprovals").Find(tx, new BsonDocument("_id", 1L)).SingleAsync(ct);
            if (!row["approved"].AsBoolean || ShipmentReceiptRecords.Time(row["expiresAt"]) <= data.Order.Prices.Catalog.Clock.UtcNow) throw InvoicePolicy.Invalid();
            if ((await data.Rows("fixtureInvoiceApprovals").UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw InvoicePolicy.Invalid();
            return row;
        }
        public async Task<InvoiceTerms> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, ImmutableArray<InvoiceReceiptSource> sources, DateOnly issueDate, CancellationToken ct) {
            var row = await FenceAsync(tx, ct);
            return new(new(1, row["revision"].AsInt64), new(2, 1), new(3, 1), new(4, 1), new(5, 1), new(6, 1), "buying_org_period_channel",
                new(issueDate.Year, issueDate.Month, 1), new(issueDate.Year, issueDate.Month, DateTime.DaysInMonth(issueDate.Year, issueDate.Month)), "issue_date", row["termDays"].AsInt32,
                "calendar_days", [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], [], "half_even",
                "invoice_ar", "invoice_revenue", "invoice_tax", "invoice_charges", "Proses pajak sintetis untuk uji lokal; bukan persetujuan operasional.", ShipmentReceiptRecords.Time(row["expiresAt"]),
                [.. sources.SelectMany(source => source.Snapshot["receipt"]["lines"].AsBsonArray.Select(line => new InvoiceApprovedLine(source.Snapshot["receipt"]["id"].AsInt64, line["skuId"].AsInt64, new(0m), new(0m))))]);
        }
        public async Task CompleteAsync(IClientSessionHandle tx, ActorContext actor, InvoiceTerms approved, CancellationToken ct) {
            if (BeforeComplete is not null) await BeforeComplete(tx, ct); var row = await FenceAsync(tx, ct);
            if (row["revision"] != approved.Policy.Revision || row["termDays"] != approved.TermDays) throw InvoicePolicy.Invalid();
        }
    }
}
