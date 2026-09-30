using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Fulfillment;
using UnitToko.Platform.Billing;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentAllocationSharedFundsTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task One_receipt_allocates_to_two_actual_issued_invoices_and_keeps_unapplied_cash() {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture);
        var invoice = data.Source.Read.Payment.Invoice; var dispatch = invoice.Dispatch;
        await dispatch.PrepareAsync(1, 1, "allocation-second-prepare");
        var shipment = (await dispatch.DispatchAsync(await dispatch.PreviewAsync(), "allocation-second-dispatch"))["shipmentId"].AsInt64;
        var document = await ShipmentDocumentTests.Service(dispatch).IssueAsync(dispatch.Picker, shipment, 1, CatalogPriceTestData.Meta("allocation-second-document"));
        var receipts = ShipmentReceiptTests.Service(dispatch);
        var challenge = await receipts.IssueChallengeAsync(dispatch.Courier, shipment, document.Id.Value, dispatch.Order.Buyer.UserId, CatalogPriceTestData.Meta("allocation-second-challenge"));
        var review = await receipts.ReviewAsync(dispatch.Order.Buyer, challenge.Id.Value, challenge.Token!);
        var accepted = await receipts.ConfirmAsync(dispatch.Order.Buyer, challenge.Id.Value, challenge.Token!, DeliveryReceiptPolicy.ConsentVersion, true,
            [new(review.Lines[0].SkuId, new(0, 6), new(0, 4), new(0, 2), "Synthetic second partial receipt")], CatalogPriceTestData.Meta("allocation-second-receipt"));
        var selections = System.Collections.Immutable.ImmutableArray.Create(new InvoiceReceiptSelection(accepted.ShipmentId, accepted.Id));
        var issue = await invoice.Service.PreviewAsync(invoice.Finance, selections);
        var second = await invoice.Service.IssueAsync(invoice.Finance, selections, issue.Review, CatalogPriceTestData.Meta("allocation-second-invoice"));
        var input = new PaymentAllocationInput(0, [new(new(data.InvoiceId), 0, new(1000m)), new(second.Id, 0, new(1500m))]);
        var preview = await data.Service.PreviewAsync(data.Actor, data.VerificationId, input);
        Assert.Equal(1500m, preview.Result.RemainingReceipt.Value);
        var saved = await data.Service.AllocateAsync(data.Actor, data.VerificationId, preview.Binding, input, PaymentAllocationTests.Data.Meta("two-invoices"));
        Assert.Equal(2, saved["invoices"].AsBsonArray.Count);
        var balances = await data.Service.ReadAsync(data.Actor, data.VerificationId, [data.InvoiceId, second.Id.Value]);
        Assert.Equal(2500m, balances.ReceiptBalance["allocated"].AsDecimal);
        Assert.Equal(new[] { 1000m, 1500m }, balances.Invoices.Select(line => line["balance"]["paid"].AsDecimal));
        Assert.Equal(1, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument())); Assert.Equal(4, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Linked_declarations_share_receipt_funds_and_distinct_receipts_cannot_overpay_one_invoice(bool distinctReceipt) {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture); var receiptId = data.Source.ReceiptId;
        if (distinctReceipt) {
            var bank = data.Source.Bank;
            var import = await bank.Statements.StageAsync("second-bank-import", BankStatementTestData.Original("0002", "4000.00"));
            var bankPreview = await bank.PreviewAsync(import["importId"].AsInt64);
            receiptId = (await bank.ConfirmAsync(bankPreview, "second-bank-confirm", BankReceiptTestData.Observation("0002", 4000m)))["receiptId"].AsInt64;
        }
        var second = await AdditionalVerification(data.Source, receiptId);
        var amount = distinctReceipt ? Math.Min(4000m, data.Source.Read.Payment.Issued.Detail.Total.Value) : 3000m;
        Assert.True(distinctReceipt ? amount * 2 > data.Source.Read.Payment.Issued.Detail.Total.Value : amount * 2 > 4000m);
        var input = data.Input(0, amount);
        var firstPreview = await data.Service.PreviewAsync(data.Actor, data.VerificationId, input);
        var secondPreview = await data.Service.PreviewAsync(data.Actor, second, input);
        var results = await Task.WhenAll(Record.ExceptionAsync(() => data.Service.AllocateAsync(data.Actor, data.VerificationId, firstPreview.Binding, input, PaymentAllocationTests.Data.Meta("shared-first"))),
            Record.ExceptionAsync(() => data.Service.AllocateAsync(data.Actor, second, secondPreview.Binding, input, PaymentAllocationTests.Data.Meta("shared-second"))));
        Assert.Single(results, value => value is null); Assert.Single(results, value => value is not null);
        var balance = await data.Service.ReadAsync(data.Actor, second, [data.InvoiceId]); Assert.Equal(amount, balance.Invoices[0]["balance"]["paid"].AsDecimal);
        Assert.Equal(1, await data.Rows(PaymentAllocationBson.Events).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(distinctReceipt ? 2 : 1, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(distinctReceipt ? 4 : 3, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    private static async Task<long> AdditionalVerification(PaymentVerificationTestData data, long receiptId) {
        var read = new PaymentEvidenceReadTests.ReadData { Files = data.Read.Files, Payment = data.Read.Payment, Service = data.Read.Service };
        read.DeclarationId = (await read.Payment.Save(null, 0, read.Payment.Input, "allocation-linked-declaration"))["declarationId"].AsInt64;
        read.Reservation = await read.Service.ReserveAsync(read.Actor, read.DeclarationId, 1, EvidenceAdmissionTestData.Meta("allocation-linked-reservation"));
        using var stream = new MemoryStream(read.Bytes);
        read.Reference = (await read.Service.UploadAsync(read.Actor, read.DeclarationId, read.Reservation.ReservationId, 1, stream, EvidenceAdmissionTestData.Meta("allocation-linked-upload"))).Evidence!.Reference;
        var validator = new UnitToko.Platform.DocumentProcessing.EvidenceValidationProcessor(read.Database, new(read.Catalog.Client), new(read.Database), read.Catalog.Clock, read.Files.Registration,
            EvidenceValidationTests.Consumer, new string('c', 64), EvidenceValidationTests.ClaimPolicy, new(1024, 100, 100, 10000),
            new EvidenceValidationTests.FixtureScanner("clean", signaturesAt: read.Catalog.Clock.UtcNow.AddHours(-1)), new UnitToko.Platform.DocumentProcessing.WindowsImageDecoder(ImageDecoderTests.Options()));
        Assert.Equal("completed", await validator.RunOnceAsync());
        await read.Service.SubmitAsync(read.Actor, read.DeclarationId, 1, read.Reservation.ReservationId, read.Reference, EvidenceAdmissionTestData.Meta("allocation-linked-submit"));
        Assert.Equal("completed", await PaymentOcrProcessorTests.Processor(read, new PaymentOcrProcessorTests.FixtureEngine()).RunOnceAsync());
        var extraction = await read.Service.ReadReviewExtractionAsync(data.Reviewer, read.DeclarationId, 1, read.Reference, new("payment-ocr-view", "allocation-linked-review"));
        await read.Service.CorrectReviewAsync(data.Reviewer, read.DeclarationId, 0, new(1, extraction.SubmissionId, read.Reference, extraction.ResultId!.Value), data.Corrections.Fields, PaymentVerificationTestData.Meta("allocation-linked-correction"));
        var preview = await data.Service.PreviewAsync(data.Reviewer, read.DeclarationId, receiptId);
        return (await data.Service.VerifyAsync(data.Reviewer, read.DeclarationId, receiptId, PaymentVerificationTestData.Input(preview), PaymentVerificationTestData.Meta("allocation-linked-verification")))["verificationId"].AsInt64;
    }
}
