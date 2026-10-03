using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal sealed record ProcurementSupplierPayableSource(BsonDocument Facts, BsonDocument? Valuation,
    BsonDocument? Settlement, BsonDocument? Payable, BsonDocument Capacity);

public sealed partial class ProcurementSupplierPayableService(
    IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
    ProcurementStockValuationService valuations, ProcurementSettlementService settlements)
{
    // A source proof is reusable only inside the transaction that verified its full owner graph.
    private sealed record HistoricalProof(IClientSessionHandle Transaction, long TransactionNumber,
        BsonDocument? Input, BsonDocument Facts, ProcurementPayableValuationSource? Valuation,
        BsonDocument? Payable, ProcurementPayableAdvanceSource? Advance)
    {
        internal void Require(IClientSessionHandle tx, ProcurementSupplierPayableInput? input = null)
        {
            if (!ReferenceEquals(Transaction, tx) || !tx.IsInTransaction ||
                tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != TransactionNumber ||
                input is not null && (Input is null || Input != B.Input(input))) throw B.Broken();
        }
    }
    private static HistoricalProof Proof(IClientSessionHandle tx, BsonDocument? input, BsonDocument facts,
        ProcurementPayableValuationSource? valuation = null, BsonDocument? payable = null,
        ProcurementPayableAdvanceSource? advance = null) =>
        new(tx, tx.WrappedCoreSession.CurrentTransaction.TransactionNumber, input, facts, valuation, payable, advance);
    private readonly AuthorizationStore authorization = new(database, unit, clock);
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
    private static ResourceScope Resource(ProcurementLocation location) => new(FulfillmentOrganizationId: location.OrganizationId,
        ShopId: location.ShopId, WarehouseId: location.WarehouseId, ChannelId: location.ChannelId);

    private async Task StorageAsync(CancellationToken ct)
    {
        await valuations.RequirePayableSourceStorageAsync(ct); await settlements.RequirePayableSourceStorageAsync(ct);
        await AccountingMigration.RequireInstalledStorageAsync(database, ct);
        foreach (var name in B.Collections) {
            await InventoryRepository.RequireStorageAsync(database, name, ProcurementSupplierPayableSchemas.Validator(name),
                ProcurementSupplierPayableSchemas.Keys(name), ct);
            if ((await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).Any(index => InvoiceStorage.Unsafe(index) ||
                index.Contains("partialFilterExpression"))) throw B.Broken();
        }
    }
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    private static void Metadata(AuthorizationCommandMetadata metadata)
    {
        AuthorizationStore.ValidateMetadata(metadata);
        ProcurementRequestRules.Text(metadata.IdempotencyKey, 128);
        ProcurementRequestRules.Text(metadata.CorrelationId, 128);
        ProcurementRequestRules.Text(metadata.Reason, 1000);
    }
    private static void Require(ActorContext actor, AuthorizationSnapshot authority, string permission, ProcurementLocation location, DateTimeOffset now)
    {
        var decision = ScopePolicy.Authorize(actor, authority, permission, Resource(location), now);
        if (!decision.Allowed) throw new AuthorizationDeniedException(decision.Code);
    }
    private async Task<BsonDocument> PolicyAsync(IClientSessionHandle tx, ProcurementSupplierPayableInput input, CancellationToken ct)
    {
        var row = await Rows(B.Policies).Find(tx, F.Eq("_id", input.PolicyId) &
            new BsonDocument("$and", new BsonArray { ProcurementSupplierPayableSchemas.Validator(B.Policies) })).SingleOrDefaultAsync(ct)
            ?? throw B.Changed("procurement_supplier_payable_policy_unavailable");
        if (row["revision"] != input.PolicyRevision || row["location"] != R.Location(input.Location) ||
            !row["active"].AsBoolean || row["currency"] != "IDR" ||
            row["sourceTreatment"] != "receipt_accrual_to_payable" || row["applicationTreatment"] != "verified_advance_to_payable" ||
            row["approvedAt"].ToUniversalTime() > Now.UtcDateTime || row["expiresAt"].ToUniversalTime() <= Now.UtcDateTime ||
            row["proposedBy"] == row["approvedBy"] || row["people"].AsBsonArray.Count != 2) throw B.Changed("procurement_supplier_payable_policy_unavailable");
        var evidence = row["people"].AsBsonArray.Select(R.Person).ToArray();
        if (evidence.Select(value => value.UserId).Distinct().Count() != evidence.Length) throw B.Changed("procurement_supplier_payable_policy_unavailable");
        var people = evidence.ToDictionary(value => value.UserId);
        if (!people.TryGetValue(row["proposedBy"].AsInt64, out var maker) || !people.TryGetValue(row["approvedBy"].AsInt64, out var checker) ||
            maker.Verification != PersonVerification.Verified || checker.Verification != PersonVerification.Verified ||
            !PersonIndependencePolicy.AreIndependent(maker, checker)) throw B.Changed("procurement_supplier_payable_policy_unavailable");
        foreach (var field in new[] { "operatingOwnerReference", "approvalReference" }) ProcurementRequestRules.Text(row[field].AsString, 256);
        if (new[] { "receiptAccrual", "supplierPayable", "supplierAdvance" }.Select(role => row[role + "AccountId"].AsInt64).Distinct().Count() != 3 ||
            new[] { "receiptAccrual", "supplierPayable", "supplierAdvance" }.Select(role => row[role + "Mapping"].AsString).Distinct().Count() != 3)
            throw B.Changed("procurement_supplier_payable_policy_unavailable");
        return row;
    }
    private async Task<BsonDocument> AccountingAsync(IClientSessionHandle tx, ProcurementSupplierPayableInput input, BsonDocument policy, CancellationToken ct)
    {
        var key = new OperationalBookKey(input.Location.OrganizationId, input.Location.ShopId, input.Location.ChannelId, "IDR");
        if (await new AccountingBookSetupRecords(database).ApprovedAsync(tx, key, ct) is null)
            throw B.Changed("procurement_supplier_payable_accounting_unavailable");
        var persistence = new AccountingPersistence(database, clock); var book = await persistence.BookAsync(tx, key, ct);
        Equal(book["_id"], policy["bookId"]); Equal(book["chartVersionId"], policy["chartVersionId"]);
        var chart = await Rows("accountingChartVersions").Find(tx, F.Eq("_id", book["chartVersionId"]) & F.Eq("bookId", book["_id"]) & F.Eq("active", true))
            .SingleOrDefaultAsync(ct) ?? throw B.Changed("procurement_supplier_payable_accounting_unavailable");
        var mapping = AccountingBson.ReadChart(chart).Mappings;
        var month = AccountingCalendar.Month(input.PostingDate);
        var period = await persistence.PeriodAsync(tx, book["_id"].AsInt64, month, ct);
        AccountingPersistence.RequireOpen(book, period, month);
        BsonDocument Account(string role)
        {
            var selected = mapping.SingleOrDefault(value => value.Active && value.MappingKey == policy[role + "Mapping"].AsString)
                ?? throw B.Changed("procurement_supplier_payable_accounting_unavailable");
            if (selected.AccountId != policy[role + "AccountId"].AsInt64 || selected.AccountCode != policy[role + "AccountCode"].AsString)
                throw B.Changed("procurement_supplier_payable_accounting_unavailable");
            return new() { { "mappingKey", selected.MappingKey }, { "accountId", selected.AccountId }, { "accountCode", selected.AccountCode } };
        }
        var result = new BsonDocument { { "bookId", book["_id"] }, { "chartVersionId", chart["_id"] },
            { "periodId", period["_id"] }, { "periodMonth", month }, { "receiptAccrual", Account("receiptAccrual") },
            { "supplierPayable", Account("supplierPayable") }, { "supplierAdvance", Account("supplierAdvance") } };
        if ((await Rows("accountingChartVersions").DeleteOneAsync(tx, chart, cancellationToken: ct)).DeletedCount != 1) throw B.Changed();
        await Rows("accountingChartVersions").InsertOneAsync(tx, chart, cancellationToken: ct);
        await persistence.TouchBookAsync(tx, book, ct); return result;
    }
}
