using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Procurement;

// Real request, approval, private-original record, bank import and journal owners;
// local Finance policy, identities and scanner configuration remain synthetic inputs.
[SupportedOSPlatform("windows")]
internal sealed class ProcurementSettlementTestData(ProcurementDisbursementTestData disbursement) : IAsyncDisposable
{
    internal const string Policies = "procurementSettlementPolicies", Events = "procurementSettlements", Operation = "procurement.settlement.verify";
    internal const long PolicyId = 8301;
    internal ProcurementDisbursementTestData Disbursement { get; } = disbursement;
    internal IMongoDatabase Database => Disbursement.Database;
    internal MutableAuthorizationClock Clock => Disbursement.Clock;
    internal MongoUnitOfWork Unit => Disbursement.Unit;
    internal ActorContext Verifier = null!, Reader = null!, Outsider = null!;
    internal BsonDocument Recorded = null!;
    internal long DisbursementId => Recorded["disbursementId"].AsInt64;
    internal long BankImportId;
    internal ProcurementSettlementService Service => new(Database, Unit, new(Database), Clock, Disbursement.Funding.Procurement.Catalog,
        Disbursement.Service, Disbursement.Bank.Service);
    internal ProcurementSettlementService NewService(MongoClient client)
    {
        var database = client.GetDatabase(Database.DatabaseNamespace.DatabaseName);
        var unit = new MongoUnitOfWork(client);
        var catalog = new CatalogAuthoringService(database, unit, new(database), Clock);
        var requests = new ProcurementRequestService(database, unit, new(database), Clock, catalog);
        var suppliers = new SupplierService(database, unit, new(database), Clock, catalog);
        var orders = new PurchaseOrderService(database, unit, new(database), Clock, catalog, requests, suppliers);
        var funding = new ProcurementFundingService(database, unit, new(database), Clock, catalog, orders);
        var beneficiaries = new SupplierBankService(database, unit, new(database), Clock, catalog);
        var bank = Disbursement.Bank.NewService(client);
        var disbursement = new ProcurementDisbursementService(database, unit, new(database), Clock, catalog, funding, beneficiaries, bank);
        return new(database, unit, new(database), Clock, catalog, disbursement, bank);
    }
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal static AuthorizationCommandMetadata Meta(string key) => new(key,
        "Synthetic independent actual-bank-debit settlement; no real transfer", "procurement-settlement-test");
    internal ProcurementSettlementInput Input() => new(PolicyId, 1, BankImportId, 2, SupplierBankTestData.Details,
        "SYNTHETIC-INDEPENDENT-BENEFICIARY-OBSERVATION", "SYNTHETIC-INDEPENDENT-DEBIT-OBSERVATION");
    internal BsonDocument Policy() => new() {
        { "_id", PolicyId }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L }, { "active", true },
        { "location", ProcurementRequestBson.Location(ProcurementRequestTestData.Location) },
        { "approvedAt", Clock.UtcNow.AddHours(-1).UtcDateTime }, { "expiresAt", Clock.UtcNow.AddDays(1).UtcDateTime },
        { "rule", "independent_exact_bank_debit" }, { "allowReferenceOnly", true },
        { "payingAccountId", Disbursement.Bank.Statements.Selection.AccountId },
        { "payingAccountVersionId", Disbursement.Bank.Statements.Selection.AccountVersionId },
        { "currency", "IDR" }, { "bookId", 1L }, { "chartVersionId", 20L },
        { "supplierAdvanceMapping", "supplier_advance" }, { "supplierAdvanceAccountId", 803L }, { "supplierAdvanceAccountCode", "A1201" },
        { "cashMapping", "bank_cash" }, { "cashAccountId", 801L }, { "cashAccountCode", "A1101" },
        { "bankDebitVerificationProcedureReference", "SYNTHETIC-INDEPENDENT-DEBIT-PROCEDURE" },
        { "beneficiaryVerificationProcedureReference", "SYNTHETIC-INDEPENDENT-BENEFICIARY-PROCEDURE" },
        { "trustedBankChannelReference", "SYNTHETIC-INDEPENDENT-BANK-PORTAL" },
        { "operatingOwnerReference", "SYNTHETIC-FINANCE-OWNER" }, { "approvalReference", "SYNTHETIC-FINANCE-ADVANCE-ASSET-MAPPING-APPROVAL" }
    };
    internal byte[] DebitOriginal(string transactionId = "ADV-DEBIT-0001", string amount = "6000.00", DateOnly? date = null) =>
        Encoding.UTF8.GetBytes(string.Join(',', BankStatementParser.Fields) + "\r\n" + transactionId + "," +
            (date ?? AccountingCalendar.BusinessDate(Clock.UtcNow)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
            ",D," + amount + ",Synthetic supplier,SYNTHETIC-TRANSFER-0001,0000123456789,IDR\r\n");
    internal async Task<long> ImportDebitAsync(string key = "settlement-actual-debit", byte[]? original = null) =>
        (await Disbursement.Bank.Statements.StageAsync(key, original ?? DebitOriginal(), "synthetic-advance-debit.csv"))["importId"].AsInt64;
    internal static async Task<ProcurementSettlementTestData> CreateAsync(MongoFixture fixture)
    {
        var source = await ProcurementDisbursementTestData.CreateAsync(fixture, includeSupplierAdvanceMapping: true);
        var data = new ProcurementSettlementTestData(source);
        try {
            await source.UploadAsync(); await source.ValidateAsync();
            data.Recorded = await source.RecordAsync(await source.PreviewRecordAsync(), ProcurementDisbursementTestData.Meta("settlement-source-record"));
            data.BankImportId = await data.ImportDebitAsync();
            string[] reads = [Permissions.ProcurementSettlementRead, Permissions.ProcurementDisbursementRead,
                Permissions.ProcurementDisbursementEvidenceRead, Permissions.SupplierBankRead, Permissions.SupplierBankDetailsRead,
                Permissions.BankReceiptRead, Permissions.BankStatementRead,
                Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead];
            data.Verifier = await source.Catalog.ActorAsync(670, CatalogAuthoringTestData.Shop, [.. reads, Permissions.DisbursementVerify]);
            data.Reader = await source.Catalog.ActorAsync(671, CatalogAuthoringTestData.Shop,
                Permissions.ProcurementSettlementRead, Permissions.ProcurementDisbursementRead);
            data.Outsider = await source.Catalog.ActorAsync(672, CatalogAuthoringTestData.Shop with { ShopId = source.Funding.Procurement.ForeignShopId },
                [.. reads, Permissions.DisbursementVerify]);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Verifier.UserId, data.Reader.UserId, data.Outsider.UserId);
            await data.Rows(Policies).InsertOneAsync(data.Policy());
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }
    internal Task<BsonDocument> PreviewAsync(ActorContext? actor = null, ProcurementSettlementInput? input = null) =>
        Service.PreviewAsync(actor ?? Verifier, DisbursementId, 1, input ?? Input());
    internal Task<BsonDocument> VerifyAsync(BsonDocument preview, string key = "settlement-verify", ActorContext? actor = null,
        ProcurementSettlementInput? input = null) => Service.VerifyAsync(actor ?? Verifier, DisbursementId, 1, input ?? Input(),
            preview["binding"].AsString, true, Meta(key));
    internal async Task<BsonDocument> StateAsync()
    {
        var state = new BsonDocument();
        foreach (var table in new[] { Events, "bankTransactionClaims", "journalEntries", "accountingAuditEvents" })
            state[table] = new BsonArray(await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            state[table] = new BsonArray(await Rows(table).Find(new BsonDocument(field, Operation)).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
    public ValueTask DisposeAsync() => Disbursement.DisposeAsync();
}
