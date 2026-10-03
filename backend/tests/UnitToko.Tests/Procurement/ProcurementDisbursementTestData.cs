using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Procurement;

// All order/funding/beneficiary/account/statement/cash history is produced by the real owners.
// Policies, people, provider proofs and image/scanner inputs are explicitly synthetic local inputs.
[SupportedOSPlatform("windows")]
internal sealed class ProcurementDisbursementTestData(ProcurementFundingTestData funding, MongoClient client) : IAsyncDisposable {
    internal const string Policies = "procurementDisbursementPolicies", Drafts = "procurementDisbursementDrafts", Records = "procurementDisbursementRecords",
        Heads = "procurementDisbursementHeads", Allocations = "procurementDisbursementAllocations", Prefix = "procurement.disbursement.";
    internal const long PolicyId = 8201;
    internal ProcurementFundingTestData Funding { get; } = funding;
    internal CatalogAuthoringTestData Catalog { get; } = new(funding.Base, client);
    internal SupplierBankTestData Beneficiaries = null!;
    internal BankReceiptTestData Bank = null!;
    internal EvidenceAdmissionTestData Files = null!;
    internal StoreTestData Base => Funding.Base;
    internal IMongoDatabase Database => Base.Database;
    internal MutableAuthorizationClock Clock => Base.Clock;
    internal MongoUnitOfWork Unit => Funding.Unit;
    internal ActorContext Actor = null!, Reader = null!, EvidenceReader = null!, Outsider = null!;
    internal BsonDocument FundingRequest = null!, FundingDecision = null!, Beneficiary = null!, Draft = null!;
    internal long DraftId => Draft["draftId"].AsInt64;
    internal ReservationResult Reservation = null!;
    internal EvidenceRef Reference = null!;
    internal readonly byte[] Bytes = ImageDecoderTests.Png(13, 7);
    internal ProcurementDisbursementService Service => new(Database, Unit, new(Database), Clock, Funding.Procurement.Catalog,
        Funding.Service, Beneficiaries.Service, Bank.Service);
    internal ProcurementDisbursementEvidenceService Evidence => new(Database, Unit, new(Database), Clock, Service, Files.Registration);
    internal EvidenceService Core => new(Database, Unit, new(Database), Clock, Files.Registration, procurementDisbursementTargets: Service);
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic actual-source disbursement recording; no real transfer", "procurement-disbursement-test");
    internal static AuthorizationReadMetadata ReadMetadata => new("procurement-disbursement-evidence-view", "synthetic-procurement-private-original");
    internal ProcurementDisbursementInput Input(decimal amount = 6000, string transferReference = "SYNTHETIC-TRANSFER-0001") =>
        new(ProcurementRequestTestData.Location, FundingRequest["requestId"].AsInt64, FundingDecision["decisionId"].AsInt64,
            FundingDecision["authorizationId"].AsInt64, Funding.Orders.Supplier["supplierId"].AsInt64, Beneficiary["versionId"].AsInt64,
            Beneficiary["version"].AsInt64, Bank.Statements.Selection.AccountId, Bank.Statements.Selection.AccountVersionId,
            PolicyId, 1, new Money(amount), AccountingCalendar.BusinessDate(Clock.UtcNow), transferReference);
    internal BsonDocument Policy() => new() {
        { "_id", PolicyId }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L },
        { "location", new BsonDocument { { "organizationId", 100L }, { "shopId", 101L }, { "warehouseId", 102L }, { "channelId", 103L } } },
        { "payingAccountId", Bank.Statements.Selection.AccountId }, { "payingAccountVersionId", Bank.Statements.Selection.AccountVersionId },
        { "allowPartialRecordings", true }, { "reserveUnverified", true },
        { "payoutRecordingProcedureReference", "SYNTHETIC-FINANCE-PRIVATE-ORIGINAL-PROCEDURE" },
        { "beneficiaryVerificationProcedureReference", "SYNTHETIC-INDEPENDENT-SUPPLIER-BANK-PROCEDURE" },
        { "operatingOwnerReference", "SYNTHETIC-FINANCE-OWNER" }, { "approvalReference", "SYNTHETIC-DISBURSEMENT-POLICY-APPROVAL" },
        { "approvedAt", Clock.UtcNow.AddHours(-1).UtcDateTime }, { "expiresAt", Clock.UtcNow.AddDays(1).UtcDateTime }, { "active", true }
    };
    internal static async Task<ProcurementDisbursementTestData> CreateAsync(MongoFixture fixture, bool createDraft = true) {
        var funding = await ProcurementFundingTestData.CreateAsync(fixture); var data = new ProcurementDisbursementTestData(funding, fixture.Client);
        try {
            data.FundingRequest = await funding.RequestAsync("disbursement-source-funding", funding.Input(10000));
            data.FundingDecision = await funding.DecideAsync(data.FundingRequest, "disbursement-source-funding-approved");
            data.Beneficiaries = await SupplierBankTestData.PrepareAsync(funding.Orders.Suppliers, funding.Orders.Supplier);
            var proposed = await data.Beneficiaries.ProposeAsync("disbursement-bank-proposed");
            var observed = await data.Beneficiaries.ObserveAsync(proposed, "disbursement-bank-observed");
            data.Beneficiary = await data.Beneficiaries.DecideAsync(proposed, "disbursement-bank-approved", observed["recordId"].AsInt64);
            data.Bank = await BankReceiptTestData.AttachAsync(await BankStatementTestData.AttachAsync(await ReceivingAccountTestData.AttachAsync(data.Catalog)),
                original: BankStatementTestData.Original(amount: "25000.00"));
            _ = await data.Bank.ConfirmAsync(await data.Bank.PreviewAsync(), "disbursement-actual-cash-anchor", BankReceiptTestData.Observation(amount: 25000));
            string[] sources = [Permissions.FundingRead, Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead, Permissions.BankReceiptRead,
                Permissions.BankStatementRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead];
            data.Actor = await data.Catalog.ActorAsync(660, CatalogAuthoringTestData.Shop, [Permissions.ProcurementDisbursementRead,
                Permissions.ProcurementDisbursementEvidenceRead, Permissions.DisbursementRecord, .. sources]);
            data.Reader = await data.Catalog.ActorAsync(661, CatalogAuthoringTestData.Shop, Permissions.ProcurementDisbursementRead);
            data.EvidenceReader = await data.Catalog.ActorAsync(662, CatalogAuthoringTestData.Shop, Permissions.ProcurementDisbursementRead, Permissions.ProcurementDisbursementEvidenceRead);
            data.Outsider = await data.Catalog.ActorAsync(663, CatalogAuthoringTestData.Shop with { ShopId = funding.Procurement.ForeignShopId },
                [Permissions.ProcurementDisbursementRead, Permissions.ProcurementDisbursementEvidenceRead, Permissions.DisbursementRecord, .. sources]);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Actor.UserId, data.Reader.UserId, data.EvidenceReader.UserId, data.Outsider.UserId);
            await data.Rows(Policies).InsertOneAsync(data.Policy());
            data.Files = await EvidenceAdmissionTestData.CreateAsync(fixture);
            var storage = EvidenceAdmissionSchemaTests.Policy(); storage["_id"] = (await new IdAllocator(data.Database).NextAsync("evidenceStorePolicies")).Value;
            await data.Rows("evidenceStorePolicies").InsertOneAsync(storage);
            await PaymentEvidenceReadTests.ReadData.PolicyAsync(data.Database, data.Files.StoreId, data.Clock.UtcNow);
            if (createDraft) data.Draft = await data.CreateDraftAsync("disbursement-draft");
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    internal async Task<BsonDocument> CreateDraftAsync(string key, ProcurementDisbursementInput? input = null) {
        input ??= Input(); var preview = await Service.PreviewAsync(Actor, input);
        return await Service.CreateDraftAsync(Actor, input, preview["binding"].AsString, Meta(key));
    }
    internal async Task<(ReservationResult Reservation, EvidenceRef Reference)> UploadForAsync(long draftId, string key) {
        var reservation = await Evidence.ReserveAsync(Actor, draftId, 1, Meta(key + "-reserve"));
        Assert.Equal(reservation, await Evidence.RecoverReservationAsync(Actor, draftId, key + "-reserve"));
        using var source = new MemoryStream(Bytes);
        var uploaded = await Evidence.UploadAsync(Actor, draftId, reservation.ReservationId, reservation.Revision, source, Meta(key + "-upload"));
        Assert.Equal("quarantined", uploaded.State); return (reservation, uploaded.Evidence!.Reference);
    }
    internal async Task UploadAsync() { (Reservation, Reference) = await UploadForAsync(DraftId, "disbursement-image"); }
    internal async Task ValidateAsync(string status = "clean") {
        var processor = new EvidenceValidationProcessor(Database, Unit, new(Database), Clock, Files.Registration,
            EvidenceValidationTests.Consumer, new string('c', 64), EvidenceValidationTests.ClaimPolicy, new(1024, 100, 100, 10000),
            new EvidenceValidationTests.FixtureScanner(status, signaturesAt: Clock.UtcNow.AddHours(-1)), new WindowsImageDecoder(ImageDecoderTests.Options()));
        if (!await Rows("evidenceValidationPolicies").Find(new BsonDocument()).AnyAsync())
            await EvidenceValidationTests.Policy(Database, Clock, Files.StoreId, processor.PipelineId);
        Assert.Equal("completed", await processor.RunOnceAsync());
    }
    internal Task<BsonDocument> PreviewRecordAsync() => Service.PreviewRecordAsync(Actor, DraftId, 1, Reservation.ReservationId, Reference);
    internal Task<BsonDocument> RecordAsync(BsonDocument preview, AuthorizationCommandMetadata metadata) =>
        Evidence.RecordAsync(Actor, DraftId, 1, Reservation.ReservationId, Reference, preview["binding"].AsString, metadata);
    internal async Task<BsonDocument> PreservedAsync() {
        var result = new BsonDocument();
        foreach (var name in new[] { "procurementRequests", "procurementRequestDecisions", "purchaseOrders", "purchaseOrderProposals", "purchaseOrderDecisions",
            ProcurementFundingTestData.Requests, ProcurementFundingTestData.Decisions, ProcurementFundingTestData.Heads, ProcurementFundingTestData.Allocations,
            SupplierBankTestData.Heads, SupplierBankTestData.Versions, "bankReceipts", "bankTransactionClaims", "journalEntries", "goodsReceipts", "stockBalances",
            "movements", "reservations", "paymentAllocations", "refundPayouts", "refundSettlements" }) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); result[name] = new BsonArray(rows);
        }
        return result;
    }
    internal async Task<BsonDocument> StateAsync() {
        var result = new BsonDocument();
        foreach (var name in new[] { Drafts, Records, Heads, Allocations }) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); result[name] = new BsonArray(rows);
        }
        foreach (var (name, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            result[name] = new BsonArray(await Rows(name).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.disbursement\."))).Sort(new BsonDocument("_id", 1)).ToListAsync());
        result["pins"] = new BsonArray(await Rows("evidencePins").Find(new BsonDocument("associationKind", "ProcurementDisbursement")).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    public async ValueTask DisposeAsync() {
        try { await Funding.DisposeAsync(); } finally { if (Files is not null) await Files.DisposeAsync(); }
    }
}
