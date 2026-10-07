using System.Globalization;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Accounting;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Procurement;

// Genuine clean video inspection, scanned private invoice, local OCR, human match, held receipt and book setup.
// Finance policy, personnel and scanner approvals are explicit local synthetic inputs, never operational acceptance.
[SupportedOSPlatform("windows")]
internal sealed class ProcurementStockValuationTestData(SupplierDocumentTestData documents) : IAsyncDisposable
{
    internal const string Prefix = "procurement.stockvaluation.", ProposeOperation = Prefix + "propose", DecideOperation = Prefix + "decide";
    internal const string Policies = "procurementStockValuationPolicies", Proposals = "procurementStockValuationProposals", Decisions = "procurementStockValuationDecisions",
        ReceiptHeads = "procurementStockValuationReceiptHeads", MatchHeads = "procurementStockValuationMatchHeads", Allocations = "procurementStockValuationAllocations";
    internal const string Reason = "Synthetic independent valuation of received held goods; no stock release or supplier payment";
    internal SupplierDocumentTestData Documents { get; } = documents;
    internal ProcurementQualityAcceptanceTestData? MixedQuality { get; private set; }
    internal BsonDocument? Quality { get; private set; }
    internal SupplierDocumentMatchInput? MixedMatchInput { get; private set; }
    internal ProcurementInspectionTestData Inspection => Documents.Inspection;
    internal StoreTestData Base => Documents.Base;
    internal IMongoDatabase Database => Documents.Database;
    internal MongoUnitOfWork Unit => Documents.Unit;
    internal MutableAuthorizationClock Clock => Documents.Clock;
    internal ActorContext Maker = null!, Checker = null!, Reader = null!, Outsider = null!, SourceMaker = null!, SourceChecker = null!, BookMaker = null!, BookChecker = null!;
    internal BsonDocument Receipt = null!, Match = null!, Book = null!, Policy = null!;
    internal long ReceiptId => Receipt["receiptId"].AsInt64;
    internal long MatchId => Match["matchId"].AsInt64;
    internal long PolicyId => Policy["_id"].AsInt64;
    internal long PoolId;
    internal int ReceiptPackages { get; private init; } = 3;
    internal StockSourceSetupService Sources => new(Database, Unit, new(Database), Clock);
    internal ProcurementStockReceiptService Receipts => new(Database, Unit, new(Database), Clock, Inspection.Receiving.Procurement.Catalog,
        Inspection.Receiving.Orders.Service, Inspection.Receiving.Service, Sources);
    internal ProcurementStockValuationService Service => new(Database, Unit, new(Database), Clock, Inspection.Receiving.Procurement.Catalog,
        Inspection.Receiving.Orders.Service, Inspection.Receiving.Service, Sources);
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal static AuthorizationCommandMetadata Meta(string key, string reason = Reason) => new(key, reason, "synthetic-stock-valuation-test");
    internal ProcurementStockValuationInput Input(int packages = 1, long? invoiceMatchId = null) => new(ProcurementRequestTestData.Location, ReceiptId, 1, Documents.DocumentId, 1, invoiceMatchId ?? MatchId, 1,
        packages, new Money(10000 * packages), new Money(1000 * packages), new Money(250 * packages), new Money(11250 * packages), PolicyId, 1,
        AccountingCalendar.BusinessDate(Clock.UtcNow), Reason);

    internal static async Task<ProcurementStockValuationTestData> CreateAsync(MongoFixture fixture, bool createValuationPolicy = true, int receiptPackages = 3,
        decimal requiredAdvance = 0, bool includePayableMappings = false, bool includeCustomerMappings = false,
        bool mixedQuality = false, bool matchInvoice = true, bool activateEmptySource = false, bool useSystemClock = false, DateOnly? inspectedExpiry = null)
    {
        Assert.InRange(receiptPackages, 1, 3);
        if (mixedQuality && activateEmptySource) throw new ArgumentException("This minimal activation seam is for the clean receipt fixture", nameof(activateEmptySource));
        if (mixedQuality && useSystemClock) throw new ArgumentException("The browser live clock seam is for the clean receipt fixture", nameof(useSystemClock));
        if (mixedQuality && inspectedExpiry is not null) throw new ArgumentException("Explicit inspected expiry is for the clean receipt fixture", nameof(inspectedExpiry));
        if (mixedQuality && requiredAdvance != 0) throw new ArgumentException("Mixed fixture uses its genuine M77 order terms", nameof(requiredAdvance));
        ProcurementQualityAcceptanceTestData? quality = mixedQuality ? await ProcurementQualityAcceptanceTestData.CreateAsync(fixture, receiptPackages: receiptPackages) : null;
        SupplierDocumentTestData documents;
        try { documents = quality is null ? await SupplierDocumentTestData.CreateAsync(fixture, requiredAdvance: requiredAdvance, boundStock: true, activateEmptySource: activateEmptySource, useSystemClock: useSystemClock, inspectedExpiry: inspectedExpiry)
                : await SupplierDocumentTestData.CreateFromInspectionAsync(quality.Inspection); }
        catch { if (quality is not null) await quality.DisposeAsync(); throw; }
        var data = new ProcurementStockValuationTestData(documents) { ReceiptPackages = receiptPackages, MixedQuality = quality };
        try
        {
            // The shared synthetic OCR installer uses policy ID 1; reserve its real allocator ID before installation.
            var ocrPolicyId = (await new IdAllocator(data.Database).NextAsync("supplierDocumentProcessingPolicies")).Value;
            var ocr = await SupplierDocumentOcrTestData.PrepareNativeAsync(documents);
            Assert.Equal(ocrPolicyId, (await data.Rows("supplierDocumentProcessingPolicies").Find(new BsonDocument()).SingleAsync())["_id"].AsInt64);
            await documents.SubmitOriginalAsync();
            var ocrOutcome = await ocr.RunOnceAsync();
            var failureCodes = await data.Rows("supplierDocumentProcessingFailures").Find(new BsonDocument())
                .Project(new BsonDocument { { "_id", 0 }, { "safeFailure", 1 } }).ToListAsync();
            Assert.True(ocrOutcome == "completed", $"Native supplier OCR outcome: {ocrOutcome}; safe failures: {string.Join(", ", failureCodes.Select(row => row["safeFailure"].AsString))}");
            var extraction = await documents.Evidence.ReadExtractionAsync(documents.EvidenceReader, documents.DraftId, 1, documents.Reference,
                new("supplier-document-ocr-view", "synthetic-valuation-human-invoice-review"));
            Assert.Equal("succeeded", extraction.State); Assert.NotNull(extraction.ResultId);
            Assert.Equal("34000.00", Assert.Single(extraction.Fields, field => field.Field == "total_amount").Value);
            if (quality is null) data.Match = await documents.MatchAsync("valuation-human-invoice-match", documents.MatchInput() with { OcrResultId = extraction.ResultId });
            else {
                var accepted = quality.Input(packages: 2);
                var proposed = await quality.ProposeAsync("mixed-quality-valuation-propose", accepted);
                data.Quality = await quality.DecideAsync(proposed["proposalId"].AsInt64, "mixed-quality-valuation-approve", accepted);
                var matchInput = documents.MatchInput(packages: 2) with { OcrResultId = extraction.ResultId };
                data.MixedMatchInput = matchInput with { Lines = [matchInput.Lines[0] with { QualityAllocationId = data.Quality["allocationId"].AsInt64 }] };
                if (matchInvoice) data.Match = await documents.MatchAsync("valuation-human-mixed-invoice-match", data.MixedMatchInput);
            }
            var scope = ProcurementRequestTestData.Scope;
            data.Maker = await data.Base.AddActorAsync(730, scope, Permissions.ProcurementRead, Permissions.StockRead, Permissions.StockReceive,
                Permissions.ProcurementStockValuationRead, Permissions.ProcurementStockValuationPropose);
            data.Checker = await data.Base.AddActorAsync(731, scope, Permissions.ProcurementRead, Permissions.StockRead, Permissions.ProcurementStockValuationRead,
                Permissions.ProcurementStockValuationDecide, Permissions.JournalPost);
            data.Reader = await data.Base.AddActorAsync(732, scope, Permissions.ProcurementRead, Permissions.StockRead, Permissions.ProcurementStockValuationRead);
            data.Outsider = await data.Base.AddActorAsync(733, new(GrantScopeKind.FulfillmentWarehouse, 100, ShopId: data.Inspection.Receiving.Procurement.ForeignShopId,
                WarehouseId: data.Inspection.Receiving.Procurement.ForeignWarehouseId, ChannelId: 103), Permissions.ProcurementRead, Permissions.StockRead,
                Permissions.ProcurementStockValuationRead, Permissions.ProcurementStockValuationPropose, Permissions.ProcurementStockValuationDecide, Permissions.JournalPost);
            data.SourceMaker = await data.Base.AddActorAsync(734, scope, Permissions.StockSourceRead, Permissions.StockSourcePropose, Permissions.StockSourcePolicyPropose);
            data.SourceChecker = await data.Base.AddActorAsync(735, scope, Permissions.StockSourceRead, Permissions.StockSourceApprove, Permissions.StockSourcePolicyApprove);
            data.BookMaker = await data.Base.AddActorAsync(736, AccountingBookSetupTestData.Scope, Permissions.AccountingBookRead, Permissions.AccountingBookPropose);
            data.BookChecker = await data.Base.AddActorAsync(737, AccountingBookSetupTestData.Scope, Permissions.AccountingBookRead, Permissions.AccountingBookApprove);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Maker.UserId, data.Checker.UserId, data.Reader.UserId, data.Outsider.UserId,
                data.SourceMaker.UserId, data.SourceChecker.UserId, data.BookMaker.UserId, data.BookChecker.UserId);
            if (quality is null) await data.CreateHeldReceiptAsync();
            else { data.Receipt = quality.Receipt; data.PoolId = quality.Stock.PoolId; }
            await data.CreateBookAndPolicyAsync(createValuationPolicy, includePayableMappings, includeCustomerMappings);
            Assert.False(await data.Rows("journalEntries").Find(new BsonDocument()).AnyAsync());
            return data;
        }
        catch { await data.DisposeAsync(); throw; }
    }

    private async Task CreateHeldReceiptAsync()
    {
        PoolId = Inspection.Receiving.PoolId;
        Assert.True(PoolId > 0);
        var inspection = await Rows("procurementInspectionSubmissions").Find(new BsonDocument("_id", Documents.InspectionId)).SingleAsync();
        var line = inspection["input"]["lines"][0];
        Assert.Equal(0, line["damagedPackages"].AsInt32); Assert.Equal(0, line["expiredPackages"].AsInt32); Assert.Equal(0, line["openedPackages"].AsInt32);
        var input = new ProcurementStockReceiptInput(ProcurementRequestTestData.Location, Inspection.Receiving.OrderId, 1, Inspection.Receiving.ApprovalId,
            Documents.InspectionId, 1, ReceiptPackages, PoolId, 1, "SYNTHETIC-VALUATION-RECEIPT", line["lotCode"].AsString,
            DateOnly.ParseExact(line["expiryDate"].AsString, "yyyy-MM-dd", CultureInfo.InvariantCulture), Reason);
        var receiptPreview = await Receipts.PreviewAsync(Maker, input);
        Receipt = await Receipts.ReceiveAsync(Maker, input, receiptPreview["binding"].AsString, Meta("valuation-held-receipt"));
    }

    private async Task CreateBookAndPolicyAsync(bool createPolicy, bool includePayableMappings, bool includeCustomerMappings)
    {
        var setup = new AccountingBookSetupService(Database, Unit, new(Database), Clock);
        var input = new AccountingBookSetupInput(AccountingBookSetupTestData.Key, AccountingCalendar.BusinessDate(Clock.UtcNow).ToString("yyyy-MM", CultureInfo.InvariantCulture),
            includePayableMappings
                ? [new("inventory", "A1200"), new("receipt_accrual", "L2100"), new("supplier_payable", "L2200"),
                    new("supplier_advance", "A1201"), new("bank_cash", "A1101"), new("unidentified_cash", "L2900")]
                : [new("inventory", "A1200"), new("receipt_accrual", "L2100")]);
        if (includeCustomerMappings)
            input = input with { Mappings = [.. input.Mappings, new("invoice_ar", "A1102"), new("invoice_revenue", "R4101"),
                new("invoice_tax", "L2101"), new("invoice_charges", "R4102")] };
        var preview = await setup.PreviewAsync(BookMaker, input);
        var proposal = await setup.ProposeAsync(BookMaker, input, preview["binding"].AsString, Meta("valuation-book"));
        var review = await setup.PreviewDecisionAsync(BookChecker, proposal["proposalId"].AsInt64, AccountingBookSetupDecision.Approve);
        Book = await setup.DecideAsync(BookChecker, proposal["proposalId"].AsInt64, 1, AccountingBookSetupDecision.Approve, review["binding"].AsString, Meta("valuation-book-approved"));
        if (!createPolicy) return;
        var chart = await Rows("accountingChartVersions").Find(new BsonDocument("_id", Book["chartId"])).SingleAsync();
        var inventory = chart["mappings"].AsBsonArray.Single(row => row["mappingKey"] == "inventory");
        var accrual = chart["mappings"].AsBsonArray.Single(row => row["mappingKey"] == "receipt_accrual");
        var people = await Unit.ExecuteAsync((tx, ct) => new PersonRegistryService(Database, Clock).ResolveAsync(tx, [BookMaker.UserId, BookChecker.UserId], ct));
        Policy = new BsonDocument {
            { "_id", (await new IdAllocator(Database).NextAsync(Policies)).Value }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L }, { "active", true },
            { "location", ProcurementRequestBson.Location(ProcurementRequestTestData.Location) }, { "currency", "IDR" },
            { "approvedAt", Clock.UtcNow.UtcDateTime }, { "expiresAt", Clock.UtcNow.AddDays(1).UtcDateTime },
            { "proposedBy", BookMaker.UserId }, { "approvedBy", BookChecker.UserId }, { "people", new BsonArray(people.Select(ProcurementRequestBson.Person)) },
            { "sourceRecognition", "receipt_accrual" }, { "bookId", Book["bookId"] }, { "chartVersionId", Book["chartId"] },
            { "inventoryMapping", "inventory" }, { "inventoryAccountId", inventory["accountId"] }, { "inventoryAccountCode", inventory["accountCode"] },
            { "receiptAccrualMapping", "receipt_accrual" }, { "receiptAccrualAccountId", accrual["accountId"] }, { "receiptAccrualAccountCode", accrual["accountCode"] },
            { "capitalizeTax", true }, { "capitalizeCharge", true }, { "operatingOwnerReference", "SYNTHETIC-FINANCE-OWNER-NOT-LIVE-APPROVAL" },
            { "approvalReference", "SYNTHETIC-INDEPENDENT-FINANCE-POLICY-NOT-PRODUCTION-ACCEPTANCE" }
        };
        await Rows(Policies).InsertOneAsync(Policy);
    }

    internal async Task<BsonDocument> ProposeAsync(string key, ProcurementStockValuationInput? input = null)
    {
        input ??= Input(); var preview = await Service.PreviewAsync(Maker, input);
        return await Service.ProposeAsync(Maker, input, preview["binding"].AsString, Meta(key, input.Reason));
    }
    internal async Task<BsonDocument> DecideAsync(long proposalId, string key, ProcurementStockValuationDecision decision = ProcurementStockValuationDecision.Approve)
    {
        var preview = await Service.PreviewDecisionAsync(Checker, proposalId, decision);
        return await Service.DecideAsync(Checker, proposalId, 1, decision, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> HeldAsync()
    {
        var result = new BsonDocument();
        foreach (var table in new[] { "procurementStockReceipts", "lots", "stockBalances", "movements", "procurementQuarantineDispositionHeads", "procurementQuarantineDispositions" })
            result[table] = new BsonArray(await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    internal async Task<BsonDocument> PreservedAsync() => new() {
        { "held", await HeldAsync() }, { "documents", await Documents.StateAsync() }, { "inspection", await Inspection.StateAsync() },
        { "receiving", await Inspection.Receiving.StateAsync() }, { "orders", await Inspection.Receiving.Orders.StateAsync() }
    };
    internal async Task<BsonDocument> StateAsync()
    {
        var result = new BsonDocument();
        foreach (var table in new[] { Policies, Proposals, Decisions, ReceiptHeads, MatchHeads, Allocations, "journalEntries", "accountingAuditEvents", "accountingPeriodHistory", "periods", "accountingBooks" }) {
            var rows = await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(); foreach (var row in rows) row.Remove("fence"); result[table] = new BsonArray(rows);
        }
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            result[table] = new BsonArray(await Rows(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.stockvaluation\."))).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
    internal async Task NoPaymentOrReleaseAsync()
    {
        foreach (var table in new[] { "cashMovements", "paymentAllocations", "procurementFundingAllocations", "procurementDisbursements", "procurementSettlements", "reservations" })
            Assert.False(await Rows(table).Find(new BsonDocument()).AnyAsync());
        var balance = await Rows("stockBalances").Find(new BsonDocument("_id", Receipt["balanceId"])).SingleAsync();
        Assert.Equal((long)ReceiptPackages, balance["onHand"].AsInt64); Assert.Equal((long)ReceiptPackages, balance["held"].AsInt64); Assert.Equal(0L, balance["reserved"].AsInt64);
    }
    public async ValueTask DisposeAsync() { await Documents.DisposeAsync(); if (MixedQuality is not null) await MixedQuality.DisposeAsync(); }
}
