using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Ordering;
using B = UnitToko.Platform.Procurement.ProcurementAdvanceReturnBson;

namespace UnitToko.Tests.Procurement;

// Actual private supplier invoice, held receipt, PO, bank debits/credit and journal owners.
// People, observations and finite Finance policy are synthetic local inputs, not production acceptance.
[SupportedOSPlatform("windows")]
internal sealed class ProcurementAdvanceReturnTestData(ProcurementSupplierPayableTestData payables) : IAsyncDisposable
{
    internal const string Reason = "Synthetic independently reviewed supplier advance return; no transfer or payable reduction";
    internal ProcurementSupplierPayableTestData Payables { get; } = payables;
    internal StoreTestData Base => Payables.Base;
    internal IMongoDatabase Database => Payables.Database;
    internal MongoUnitOfWork Unit => Payables.Unit;
    internal MutableAuthorizationClock Clock => Payables.Clock;
    internal ActorContext Maker = null!, Checker = null!, Reader = null!, Outsider = null!;
    internal BsonDocument Receipt = null!, Policy = null!;
    internal BsonDocument? SecondSettlement;
    internal long ReceiptId => Receipt["receiptId"].AsInt64;
    internal long PolicyId => Policy["_id"].AsInt64;
    internal long SettlementId => Payables.SettlementId;
    internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
    internal ProcurementAdvanceReturnService Service => new(Database, Unit, new(Database), Clock, Payables.Settlements, Payables.BankService, Payables.Service,
        Payables.Receiving.Procurement.Service);
    internal static AuthorizationCommandMetadata Meta(string key, string reason = Reason) => new(key, reason, "synthetic-advance-return-test");
    internal ProcurementAdvanceReturnInput Input(decimal amount = 3000) => new(ProcurementRequestTestData.Location, ReceiptId,
        PolicyId, 1, AccountingCalendar.BusinessDate(Clock.UtcNow), Reason, [new(SettlementId, new Money(amount))]);

    internal static async Task<ProcurementAdvanceReturnTestData> CreateAsync(MongoFixture fixture, decimal receiptAmount = 3000,
        bool multiplePayouts = false, bool createPolicy = true, bool includeCustomerMappings = false, Action<string>? diagnostics = null)
    {
        var data = new ProcurementAdvanceReturnTestData(await ProcurementSupplierPayableTestData.CreateAsync(fixture,
            includeCustomerMappings: includeCustomerMappings, diagnostics: diagnostics));
        try {
            string[] reads = [Permissions.ProcurementRead, Permissions.ProcurementAdvanceReturnRead];
            string[] bankReads = [Permissions.BankReceiptRead, Permissions.BankStatementRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead];
            data.Maker = await data.Base.AddActorAsync(760, ProcurementRequestTestData.Scope, [.. reads, Permissions.ProcurementAdvanceReturnPropose]);
            data.Checker = await data.Base.AddActorAsync(761, ProcurementRequestTestData.Scope, [.. reads, Permissions.ProcurementAdvanceReturnDecide, Permissions.JournalPost]);
            await data.Base.AddGrantAsync(data.Maker, 76001, CatalogAuthoringTestData.Shop, bankReads);
            await data.Base.AddGrantAsync(data.Checker, 76101, CatalogAuthoringTestData.Shop, bankReads);
            data.Reader = await data.Base.AddActorAsync(762, ProcurementRequestTestData.Scope, reads);
            data.Outsider = await data.Base.AddActorAsync(763, new(GrantScopeKind.FulfillmentWarehouse, 100,
                ShopId: data.Payables.Receiving.Procurement.ForeignShopId, WarehouseId: data.Payables.Receiving.Procurement.ForeignWarehouseId, ChannelId: 103),
                [.. reads, .. bankReads, Permissions.ProcurementAdvanceReturnPropose, Permissions.ProcurementAdvanceReturnDecide, Permissions.JournalPost]);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, data.Maker.UserId, data.Checker.UserId, data.Reader.UserId, data.Outsider.UserId);
            if (multiplePayouts) data.SecondSettlement = await data.Payables.CreateAdditionalVerifiedAdvanceAsync("return-second-payout");
            data.Receipt = await data.CreateCreditAsync("RETURN-CREDIT-0001", receiptAmount);
            if (createPolicy) await data.InstallPolicyAsync();
            Assert.False(await data.Rows("procurementSupplierPayables").Find(new BsonDocument()).AnyAsync());
            Assert.False(await data.Rows(B.Proposals).Find(new BsonDocument()).AnyAsync());
            return data;
        } catch { await data.DisposeAsync(); throw; }
    }

    internal async Task<BsonDocument> CreateCreditAsync(string reference, decimal amount)
    {
        var bank = Payables.Bank ?? throw new InvalidOperationException("Actual paying account setup required");
        var day = AccountingCalendar.BusinessDate(Clock.UtcNow);
        var original = Encoding.UTF8.GetBytes(string.Join(',', BankStatementParser.Fields) + "\r\n" + reference + "," +
            day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ",C," + new Money(amount) +
            ",Synthetic supplier," + reference + ",0000123456789,IDR\r\n");
        var import = await bank.Statements.StageAsync(reference + "-import", original, "synthetic-" + reference + ".csv");
        var preview = await bank.PreviewAsync(import["importId"].AsInt64);
        return await bank.ConfirmAsync(preview, reference + "-confirmed", BankReceiptTestData.Observation(reference, amount) with { BookedDate = day });
    }

    private async Task InstallPolicyAsync()
    {
        var chart = await Rows("accountingChartVersions").Find(new BsonDocument("_id", Payables.Valuation.Book["chartId"])).SingleAsync();
        var people = await Unit.ExecuteAsync((tx, ct) => new PersonRegistryService(Database, Clock).ResolveAsync(tx,
            [Payables.Valuation.BookMaker.UserId, Payables.Valuation.BookChecker.UserId], ct));
        Policy = new BsonDocument {
            { "_id", (await new IdAllocator(Database).NextAsync(B.Policies)).Value }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L }, { "active", true },
            { "location", ProcurementRequestBson.Location(ProcurementRequestTestData.Location) }, { "currency", "IDR" },
            { "approvedAt", Clock.UtcNow.UtcDateTime }, { "expiresAt", Clock.UtcNow.AddDays(1).UtcDateTime },
            { "proposedBy", Payables.Valuation.BookMaker.UserId }, { "approvedBy", Payables.Valuation.BookChecker.UserId },
            { "people", new BsonArray(people.Select(ProcurementRequestBson.Person)) }, { "treatment", "whole_verified_supplier_advance_return" },
            { "bookId", Payables.Valuation.Book["bookId"] }, { "chartVersionId", Payables.Valuation.Book["chartId"] },
            { "operatingOwnerReference", "SYNTHETIC-FINANCE-OWNER-NOT-LIVE-ACCEPTANCE" }, { "approvalReference", "SYNTHETIC-INDEPENDENT-ADVANCE-RETURN-POLICY" }
        };
        foreach (var (role, key) in new[] { ("suspense", "unidentified_cash"), ("supplierAdvance", "supplier_advance") }) {
            var mapping = Assert.Single(chart["mappings"].AsBsonArray, row => row["mappingKey"] == key);
            Policy[role + "Mapping"] = key; Policy[role + "AccountId"] = mapping["accountId"]; Policy[role + "AccountCode"] = mapping["accountCode"];
        }
        await Rows(B.Policies).InsertOneAsync(Policy);
    }

    internal async Task<BsonDocument> ProposeAsync(string key, ProcurementAdvanceReturnInput? input = null)
    {
        input ??= Input(); var preview = await Service.PreviewAsync(Maker, input);
        return await Service.ProposeAsync(Maker, input, preview["binding"].AsString, Meta(key, input.Reason));
    }
    internal async Task<BsonDocument> DecideAsync(long proposalId, string key, ProcurementAdvanceReturnDecision decision = ProcurementAdvanceReturnDecision.Approve)
    {
        var preview = await Service.PreviewDecisionAsync(Checker, proposalId, decision);
        return await Service.DecideAsync(Checker, proposalId, 1, decision, preview["binding"].AsString, Meta(key));
    }
    internal async Task<BsonDocument> StateAsync()
    {
        var state = new BsonDocument();
        foreach (var name in B.Collections.Concat(["procurementSupplierAdvanceHeads", "paymentReceiptParties", "journalEntries",
            "accountingAuditEvents", "accountingPeriodHistory", "periods", "accountingBooks"])) {
            var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            foreach (var row in rows) row.Remove("fence"); state[name] = new BsonArray(rows);
        }
        foreach (var (name, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            state[name] = new BsonArray(await Rows(name).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.advancereturn\."))).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
    internal async Task<BsonDocument> PreservedAsync()
    {
        var state = await ProtectedAsync();
        foreach (var name in new[] { "procurementSupplierPayables", "procurementSupplierPayableApplications", "procurementSupplierPayableHeads" })
            state[name] = new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return state;
    }
    internal async Task<BsonDocument> ProtectedAsync()
    {
        var state = await Payables.ProtectedAsync();
        // Current bank source admission touches only this coordination fence.
        // Keep all immutable receipt fields, hashes, cash and journal references.
        foreach (var row in state["bankReceipts"].AsBsonArray) row.AsBsonDocument.Remove("fence");
        return state;
    }
    internal ProcurementAdvanceReturnService NewService(MongoClient client)
    {
        var db = client.GetDatabase(Database.DatabaseNamespace.DatabaseName); var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(db);
        var catalog = new CatalogAuthoringService(db, unit, ids, Clock); var requests = new ProcurementRequestService(db, unit, ids, Clock, catalog);
        var orders = new PurchaseOrderService(db, unit, ids, Clock, catalog, requests, new(db, unit, ids, Clock, catalog));
        var receiving = new ProcurementReceivingService(db, unit, ids, Clock, catalog, orders, requests);
        var valuation = new ProcurementStockValuationService(db, unit, ids, Clock, catalog, orders, receiving, new(db, unit, ids, Clock));
        var bank = new BankReceiptService(db, unit, ids, Clock, new(db, unit, ids, Clock, new(db, unit, ids, Clock)));
        var disbursements = new ProcurementDisbursementService(db, unit, ids, Clock, catalog,
            new(db, unit, ids, Clock, catalog, orders), new(db, unit, ids, Clock, catalog), bank);
        var settlements = new ProcurementSettlementService(db, unit, ids, Clock, catalog, disbursements, bank);
        return new(db, unit, ids, Clock, settlements, bank, new(db, unit, ids, Clock, valuation, settlements), requests);
    }
    public ValueTask DisposeAsync() => Payables.DisposeAsync();
}
