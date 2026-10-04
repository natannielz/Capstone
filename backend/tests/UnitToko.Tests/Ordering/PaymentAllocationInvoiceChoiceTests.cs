using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentAllocationInvoiceChoiceTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Verified_party_can_choose_a_second_genuine_invoice_with_exact_outstanding_and_stable_cursor() {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture);
        var secondId = await IssueSecondInvoiceAsync(data);
        var beforeAllocations = await data.Rows(PaymentAllocationBson.Events).CountDocumentsAsync(new BsonDocument());
        var beforeJournals = await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument());

        var firstPage = await data.Service.InvoiceChoicesAsync(data.Actor, data.VerificationId, limit: 1);
        var first = Assert.Single(firstPage.Items);
        Assert.Equal(secondId, first.InvoiceId);
        Assert.Equal(0L, first.Version);
        Assert.Equal(first.Total, first.Outstanding);
        Assert.Equal(0m, first.Paid.Value);
        Assert.Equal(0m, first.Credited.Value);
        Assert.Equal(secondId, firstPage.NextBeforeInvoiceId);
        var issued = await data.Rows(InvoiceBson.Invoices).Find(new BsonDocument("_id", secondId)).SingleAsync();
        Assert.Equal(FormattableString.Invariant($"{issued["numberEntity"].AsInt64}/{issued["numberChannel"].AsInt64}/{issued["numberYear"].AsInt64}/{issued["number"].AsInt64}"), first.Number);

        var secondPage = await data.Service.InvoiceChoicesAsync(data.Actor, data.VerificationId, firstPage.NextBeforeInvoiceId, limit: 1);
        Assert.Equal(data.InvoiceId, Assert.Single(secondPage.Items).InvoiceId);
        Assert.Null(secondPage.NextBeforeInvoiceId);
        Assert.Empty((await data.Service.InvoiceChoicesAsync(data.Actor, data.VerificationId, data.InvoiceId, limit: 1)).Items);
        Assert.Equal(beforeAllocations, await data.Rows(PaymentAllocationBson.Events).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(beforeJournals, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Current_invoice_balance_is_exact_and_broken_invoice_or_revoked_allocation_authority_denies() {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture);
        var before = await data.Service.InvoiceChoicesAsync(data.Actor, data.VerificationId);
        var invoice = Assert.Single(before.Items);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.InvoiceChoicesAsync(data.Source.Reader, data.VerificationId));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.InvoiceChoicesAsync(data.Actor, data.VerificationId, beforeInvoiceId: 0));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.InvoiceChoicesAsync(data.Actor, data.VerificationId, limit: 11));
        var invoiceRow = await data.Rows(InvoiceBson.Invoices).Find(new BsonDocument("_id", data.InvoiceId)).SingleAsync();
        var journal = await data.Rows("journalEntries").Find(new BsonDocument("_id", invoiceRow["journalId"])).SingleAsync();
        Assert.Equal(1L, (await data.Rows("journalEntries").DeleteOneAsync(new BsonDocument("_id", journal["_id"]))).DeletedCount);
        try {
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.InvoiceChoicesAsync(data.Actor, data.VerificationId));
        } finally { await data.Rows("journalEntries").InsertOneAsync(journal); }

        var grantFilter = new BsonDocument("_id", data.Actor.UserId);
        var grant = await data.Rows("authorizationGrants").Find(grantFilter).SingleAsync();
        Assert.Equal(1L, (await data.Rows("authorizationGrants").UpdateOneAsync(grantFilter,
            Builders<BsonDocument>.Update.Set("status", "revoked"))).ModifiedCount);
        try { await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.InvoiceChoicesAsync(data.Actor, data.VerificationId)); }
        finally { await data.Rows("authorizationGrants").ReplaceOneAsync(grantFilter, grant); }

        var input = data.Input(0, 1000m);
        var preview = await data.Service.PreviewAsync(data.Actor, data.VerificationId, input);
        await data.Service.AllocateAsync(data.Actor, data.VerificationId, preview.Binding, input, PaymentAllocationTests.Data.Meta("invoice-choice-current-balance"));
        var paid = await data.Service.InvoiceChoicesAsync(data.Actor, data.VerificationId);
        var current = Assert.Single(paid.Items);
        Assert.Equal(data.InvoiceId, current.InvoiceId);
        Assert.Equal(1L, current.Version);
        Assert.Equal(1000m, current.Paid.Value);
        Assert.Equal(invoice.Total.Value - 1000m, current.Outstanding.Value);
        Assert.Null(paid.NextBeforeInvoiceId);
        Assert.Equal(1L, await data.Rows(PaymentAllocationBson.Events).CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Fully_paid_genuine_invoice_is_omitted_but_raw_cursor_advances_without_read_money_effects() {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture);
        var secondId = await IssueSecondInvoiceAsync(data, acceptedPieces: 1);
        var before = await data.Service.ReadAsync(data.Actor, data.VerificationId, [secondId]);
        var invoice = Assert.Single(before.Invoices).AsBsonDocument;
        var total = InvoiceBson.Money(invoice["source"]["total"]);
        var available = before.Receipt["amount"].AsDecimal - before.ReceiptBalance["allocated"].AsDecimal
            - before.ReceiptBalance["reservedForRefund"].AsDecimal;
        Assert.Equal(secondId, invoice["source"]["invoiceId"].AsInt64);
        Assert.Equal(4000m, available);
        Assert.Equal(0m, invoice["balance"]["paid"].AsDecimal);
        Assert.Equal(0m, invoice["balance"]["credited"].AsDecimal);
        Assert.InRange(total.Value, 0.01m, available);

        var input = new PaymentAllocationInput(before.ReceiptBalance["version"].AsInt64,
            [new(new(secondId), invoice["balance"]["version"].AsInt64, total)]);
        var preview = await data.Service.PreviewAsync(data.Actor, data.VerificationId, input);
        Assert.Equal(total, preview.Result.Total);
        Assert.Equal(0m, Assert.Single(preview.Result.Invoices).Remaining.Value);
        var saved = await data.Service.AllocateAsync(data.Actor, data.VerificationId, preview.Binding, input,
            PaymentAllocationTests.Data.Meta("invoice-choice-full-second-payment"));
        Assert.Equal(total.Value, saved["amount"].AsDecimal);
        var savedInvoice = Assert.Single(saved["invoices"].AsBsonArray);
        Assert.Equal(secondId, savedInvoice["invoiceId"].AsInt64);
        Assert.Equal(0m, savedInvoice["remaining"].AsDecimal);
        var paid = await data.Service.ReadAsync(data.Actor, data.VerificationId, [secondId]);
        Assert.Equal(total.Value, paid.Invoices[0]["balance"]["paid"].AsDecimal);
        Assert.Equal(total.Value, paid.ReceiptBalance["allocated"].AsDecimal);
        Assert.Equal(1L, await data.Rows(PaymentAllocationBson.Events).CountDocumentsAsync(new BsonDocument()));
        var beforeChoiceReads = await MoneyEffectsAsync();

        var firstPage = await data.Service.InvoiceChoicesAsync(data.Actor, data.VerificationId, limit: 1);
        Assert.Empty(firstPage.Items);
        Assert.Equal(secondId, firstPage.NextBeforeInvoiceId);
        var secondPage = await data.Service.InvoiceChoicesAsync(data.Actor, data.VerificationId, firstPage.NextBeforeInvoiceId, limit: 1);
        var stillUnpaid = Assert.Single(secondPage.Items);
        Assert.Equal(data.InvoiceId, stillUnpaid.InvoiceId);
        Assert.Equal(0L, stillUnpaid.Version);
        Assert.Equal(0m, stillUnpaid.Paid.Value);
        Assert.Equal(0m, stillUnpaid.Credited.Value);
        Assert.Equal(stillUnpaid.Total, stillUnpaid.Outstanding);
        Assert.Null(secondPage.NextBeforeInvoiceId);
        Assert.Equal(beforeChoiceReads, await MoneyEffectsAsync());

        async Task<BsonDocument> MoneyEffectsAsync() {
            var snapshot = new BsonDocument();
            foreach (var name in new[] { PaymentAllocationBson.Events, PaymentAllocationBson.Receipts,
                PaymentAllocationBson.Invoices, "journalEntries", BankReceiptBson.Receipts }) {
                // Reads may fence sources; their immutable hashes, money and balance revisions must stay exact.
                snapshot[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1))
                    .Project(Builders<BsonDocument>.Projection.Exclude("fence")).ToListAsync());
            }
            return snapshot;
        }
    }

    internal static async Task<long> IssueSecondInvoiceAsync(PaymentAllocationTests.Data data, int acceptedPieces = 6) {
        var invoice = data.Source.Read.Payment.Invoice;
        var dispatch = invoice.Dispatch;
        await dispatch.PrepareAsync(1, 1, "invoice-choice-second-prepare");
        var shipment = (await dispatch.DispatchAsync(await dispatch.PreviewAsync(), "invoice-choice-second-dispatch"))["shipmentId"].AsInt64;
        var document = await ShipmentDocumentTests.Service(dispatch).IssueAsync(dispatch.Picker, shipment, 1,
            CatalogPriceTestData.Meta("invoice-choice-second-document"));
        var receipts = ShipmentReceiptTests.Service(dispatch);
        var challenge = await receipts.IssueChallengeAsync(dispatch.Courier, shipment, document.Id.Value, dispatch.Order.Buyer.UserId,
            CatalogPriceTestData.Meta("invoice-choice-second-challenge"));
        var review = await receipts.ReviewAsync(dispatch.Order.Buyer, challenge.Id.Value, challenge.Token!);
        var accepted = await receipts.ConfirmAsync(dispatch.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true,
            [new(review.Lines[0].SkuId, new(0, acceptedPieces), new(0, 4), new(0, 8 - acceptedPieces), "Synthetic second partial receipt")],
            CatalogPriceTestData.Meta("invoice-choice-second-receipt"));
        var selected = System.Collections.Immutable.ImmutableArray.Create(new InvoiceReceiptSelection(accepted.ShipmentId, accepted.Id));
        var issue = await invoice.Service.PreviewAsync(invoice.Finance, selected);
        return (await invoice.Service.IssueAsync(invoice.Finance, selected, issue.Review,
            CatalogPriceTestData.Meta("invoice-choice-second-invoice"))).Id.Value;
    }
}
