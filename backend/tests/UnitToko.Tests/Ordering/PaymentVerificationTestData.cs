using System.Collections.Immutable;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Tests.Ordering;

[SupportedOSPlatform("windows")]
internal sealed class PaymentVerificationTestData(PaymentEvidenceReadTests.ReadData read) : IAsyncDisposable
{
    internal PaymentEvidenceReadTests.ReadData Read { get; } = read;
    internal BankReceiptTestData Bank = null!;
    internal ActorContext Reviewer = null!;
    internal ActorContext Reader = null!;
    internal long ReceiptId;
    internal PaymentCorrectionView Corrections = null!;
    internal PaymentOcrProcessorTests.FixtureEngine? FailedEngine;
    internal PaymentVerificationService Service => new(Read.Database, new(Read.Catalog.Client), new(Read.Database), Read.Catalog.Clock, Read.Payment.Service, Read.Service, Bank.Service);
    internal PaymentVerificationService NewService(MongoClient client) => NewService(client, out _);
    internal PaymentVerificationService NewService(MongoClient client, out InvoiceService invoices) {
        var db = client.GetDatabase(Read.Database.DatabaseNamespace.DatabaseName); var clock = Read.Catalog.Clock;
        var orders = new UnitToko.Platform.Ordering.OrderSubmissionService(db, new(client), new(db), clock);
        var preparations = new UnitToko.Platform.Fulfillment.ShipmentPreparationService(db, new(client), new(db), clock, orders);
        var shipments = new UnitToko.Platform.Fulfillment.ShipmentQueryService(db, new(client), clock, orders, preparations);
        var documents = new UnitToko.Platform.Fulfillment.ShipmentDocumentService(db, new(client), new(db), clock, shipments);
        var received = new UnitToko.Platform.Fulfillment.ShipmentReceiptService(db, new(client), new(db), clock, shipments, documents);
        invoices = new InvoiceService(db, new(client), new(db), clock, received);
        var declarations = new PaymentDraftService(db, new(client), new(db), clock, invoices);
        var evidence = new PaymentEvidenceService(db, new(client), new(db), clock, declarations, Read.Files.Registration);
        return new(db, new(client), new(db), clock, declarations, evidence, Bank.NewService(client));
    }
    internal IMongoCollection<BsonDocument> Rows(string name) => Read.Rows(name);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic independent payer and image review", "synthetic-payment-verification");
    internal static async Task<PaymentVerificationTestData> CreateAsync(MongoFixture fixture, string? ocrFailure = null,
        PaymentEvidenceReadTests.ReadData? attachedRead = null, BankReceiptTestData? attachedBank = null, long? attachedReceiptId = null) {
        if ((attachedRead is null) != (attachedBank is null) || (attachedRead is null) != (attachedReceiptId is null))
            throw new ArgumentException("Attached customer evidence requires its actual bank and receipt");
        var data = new PaymentVerificationTestData(attachedRead ?? await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture));
        try {
            if (attachedBank is not null) {
                data.Bank = attachedBank; data.ReceiptId = attachedReceiptId!.Value;
                if (!data.Bank.Statements.Accounts.Catalog.Database.DatabaseNamespace.Equals(data.Read.Database.DatabaseNamespace))
                    throw new ArgumentException("Customer verification and bank receipt must share one database");
            } else {
                var accounts = await ReceivingAccountTestData.AttachAsync(data.Read.Catalog);
                var statements = await BankStatementTestData.AttachAsync(accounts);
                data.Bank = await BankReceiptTestData.AttachAsync(statements, false, BankStatementTestData.Original(amount: "4000.00"));
                data.ReceiptId = (await data.Bank.ConfirmAsync(await data.Bank.PreviewAsync(), observation: BankReceiptTestData.Observation(amount: 4000m)))["receiptId"].AsInt64;
            }
            string[] readPermissions = [Permissions.PaymentReviewRead, Permissions.InvoiceRead, Permissions.BankReceiptRead, Permissions.BankStatementRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead];
            data.Reviewer = await data.Read.Catalog.ActorAsync(7410, CatalogAuthoringTestData.Shop, [.. readPermissions, Permissions.PaymentVerify, Permissions.PaymentReviewCorrect]);
            if (ocrFailure is not null) await data.Read.Catalog.Base.AddGrantAsync(data.Reviewer, 74500, CatalogAuthoringTestData.Shop, [Permissions.PaymentReviewManual]);
            data.Reader = await data.Read.Catalog.ActorAsync(7420, CatalogAuthoringTestData.Shop, readPermissions);
            await PersonTestData.SeedDistinctPeopleAsync(data.Read.Database, data.Read.Catalog.Clock, data.Reviewer.UserId);
            if (ocrFailure is null) {
            data.Corrections = await PaymentCorrectionTests.Prepare(data.Read, data.Reviewer);
            var fields = data.Corrections.Fields.Select(field => field.Field switch {
                "amount" => field with { Value = "4000.00" }, "currency" => field with { Value = "IDR" }, "account" => field with { Value = "0000123456789" }, _ => field }).ToImmutableArray();
            await data.Read.Service.CorrectReviewAsync(data.Reviewer, data.Read.DeclarationId, 0, data.Corrections.Binding, fields, Meta("verification-image-correction"));
            data.Corrections = await data.Read.Service.ReadCorrectionsAsync(data.Reviewer, data.Read.DeclarationId, data.Corrections.Binding, new("payment-ocr-view", "verification-image-correction"));
            } else {
                await PaymentOcrProcessorTests.Submit(data.Read); data.FailedEngine = new() { Failure = ocrFailure };
                var worker = PaymentOcrProcessorTests.Processor(data.Read, data.FailedEngine); await PaymentOcrProcessorTests.Policy(data.Read, worker); Assert.Equal("retry", await worker.RunOnceAsync());
            }
            var receipt = await data.Rows(BankReceiptBson.Receipts).Find(new BsonDocument("_id", data.ReceiptId)).SingleAsync();
            await data.Rows(B.Policies).InsertOneAsync(new BsonDocument { { "_id", 940L }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L }, { "scope", receipt["scope"] },
                { "payerVerificationProcedureReference", "synthetic-independently-established-payer-procedure" }, { "operatingOwnerReference", "synthetic-finance-owner" }, { "approvalReference", "synthetic-payer-policy-approval" },
                { "active", true }, { "approvedAt", data.Read.Catalog.Clock.UtcNow.AddDays(-1).UtcDateTime }, { "expiresAt", data.Read.Catalog.Clock.UtcNow.AddDays(1).UtcDateTime } });
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    internal Task<PaymentVerificationPreview> PreviewAsync() => Service.PreviewAsync(Reviewer, Read.DeclarationId, ReceiptId);
    internal async Task RecoverOcrAsync() {
        FailedEngine!.Failure = null; Read.Catalog.Clock.UtcNow = Read.Catalog.Clock.UtcNow.AddMinutes(1);
        Assert.Equal("completed", await PaymentOcrProcessorTests.Processor(Read, FailedEngine).RunOnceAsync());
        var extraction = await Read.Service.ReadReviewExtractionAsync(Reviewer, Read.DeclarationId, 1, Read.Reference, new("payment-ocr-view", "synthetic-recovered-OCR"));
        Corrections = await Read.Service.ReadCorrectionsAsync(Reviewer, Read.DeclarationId, new(1, extraction.SubmissionId, Read.Reference, extraction.ResultId!.Value), new("payment-ocr-view", "synthetic-recovered-OCR"));
        var fields = Corrections.Fields.Select(field => field.Field switch {
            "amount" => field with { Value = "4000.00" }, "currency" => field with { Value = "IDR" }, "account" => field with { Value = "0000123456789" }, _ => field }).ToImmutableArray();
        await Read.Service.CorrectReviewAsync(Reviewer, Read.DeclarationId, 0, Corrections.Binding, fields, Meta("recovered-image-correction"));
        Corrections = await Read.Service.ReadCorrectionsAsync(Reviewer, Read.DeclarationId, Corrections.Binding, new("payment-ocr-view", "synthetic-recovered-OCR"));
    }
    internal static PaymentVerificationInput Input(PaymentVerificationPreview preview) => new(preview.Binding, "synthetic-bank-payer-and-organization-confirmation", true, true, true,
        [.. preview.Discrepancies.Select(item => new PaymentDiscrepancyResolution(item.Field, "Synthetic reviewed source difference; no automatic inference"))]);
    internal Task<BsonDocument> VerifyAsync(PaymentVerificationPreview preview, string key = "verify-payment") => Service.VerifyAsync(Reviewer, Read.DeclarationId, ReceiptId, Input(preview), Meta(key));
    public ValueTask DisposeAsync() => Read.DisposeAsync();
}
