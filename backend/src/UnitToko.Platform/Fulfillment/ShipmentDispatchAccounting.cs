using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Fulfillment;

/// <summary>Reads the existing independently approved accounting setup in the caller's
/// transaction. This binding supplies no shipment, stock consumption or journal authority.</summary>
internal sealed class ShipmentDispatchAccounting(IMongoDatabase database, IClock clock)
{
    private readonly AccountingPersistence persistence = new(database, clock);
    private static PersistenceConflictException Changed() => new("shipment_dispatch_accounting_changed",
        "The reviewed dispatch book, accounts or posting period changed or are unavailable");
    private static void Equal(BsonValue actual, BsonValue expected) { if (actual != expected) throw Changed(); }

    internal sealed class Binding
    {
        private readonly ShipmentDispatchAccounting owner;
        private readonly IClientSessionHandle transaction;
        private readonly long transactionNumber;
        private readonly DateTimeOffset capturedAt;
        private readonly BsonDocument snapshot;
        private readonly BsonDocument? expectedInventory;
        internal OperationalBookKey Key { get; }
        internal DateOnly PostingDate { get; }
        internal BsonDocument Snapshot => snapshot.DeepClone().AsBsonDocument;
        internal Binding(ShipmentDispatchAccounting owner, IClientSessionHandle transaction, DateTimeOffset capturedAt,
            OperationalBookKey key, DateOnly date, BsonDocument snapshot, BsonDocument? expectedInventory)
        {
            this.owner = owner; this.transaction = transaction;
            transactionNumber = transaction.WrappedCoreSession.CurrentTransaction.TransactionNumber;
            this.capturedAt = capturedAt; Key = key with { }; PostingDate = date;
            this.snapshot = snapshot.DeepClone().AsBsonDocument;
            this.expectedInventory = expectedInventory?.DeepClone().AsBsonDocument;
        }
        internal BsonDocument? ExpectedInventory => expectedInventory?.DeepClone().AsBsonDocument;
        internal void Require(ShipmentDispatchAccounting current, IClientSessionHandle tx, DateTimeOffset now)
        {
            if (!ReferenceEquals(owner, current) || !ReferenceEquals(transaction, tx) || !tx.IsInTransaction ||
                tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber || now < capturedAt) throw Changed();
        }
    }

    internal async Task<Binding> ReadAndFenceAsync(IClientSessionHandle tx, OperationalBookKey key, DateOnly postingDate,
        BsonDocument? expectedInventoryAccount = null, CancellationToken ct = default)
    {
        AccountingPersistence.RequireSession(database, tx);
        AccountingBookSetupRules.Key(key); AccountingCalendar.Validate(postingDate);
        var beganAt = clock.UtcNow;
        if (postingDate > AccountingCalendar.BusinessDate(beganAt)) throw Changed();
        var expectedInventory = expectedInventoryAccount?.DeepClone().AsBsonDocument;
        if (expectedInventory is not null && (!expectedInventory.Names.Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { "accountCode", "accountId", "mappingKey" }) ||
            !expectedInventory["accountId"].IsInt64 || expectedInventory["accountId"].AsInt64 <= 0 ||
            !expectedInventory["mappingKey"].IsString || expectedInventory["mappingKey"] != "inventory" || !expectedInventory["accountCode"].IsString)) throw Changed();
        var setup = new AccountingBookSetupRecords(database);
        var proposal = await setup.ApprovedAsync(tx, key, ct) ?? throw Changed();
        var decision = await setup.DecisionForAsync(tx, proposal, ct) ?? throw Changed();
        if (decision["decision"] != "approve") throw Changed();
        var book = await persistence.BookAsync(tx, key, ct);
        Equal(proposal["key"], book["key"]); Equal(decision["key"], book["key"]);
        Equal(decision["bookId"], book["_id"]); Equal(decision["chartId"], book["chartVersionId"]);
        var chart = await persistence.Collection("accountingChartVersions").Find(tx, new BsonDocument {
            { "_id", book["chartVersionId"] }, { "bookId", book["_id"] }, { "active", true }
        }).SingleOrDefaultAsync(ct) ?? throw Changed();
        var currentChart = AccountingBson.ReadChart(chart);
        BsonDocument Account(string role)
        {
            var mapping = currentChart.Mappings.SingleOrDefault(value => value.MappingKey == role && value.Active) ?? throw Changed();
            return new() { { "mappingKey", mapping.MappingKey }, { "accountId", mapping.AccountId }, { "accountCode", mapping.AccountCode } };
        }
        var inventory = Account("inventory"); var cogs = Account("cogs");
        if (inventory["accountId"] == cogs["accountId"] || inventory["accountCode"] == cogs["accountCode"]) throw Changed();
        if (expectedInventory is not null) Equal(inventory, expectedInventory);
        var month = AccountingCalendar.Month(postingDate);
        var period = await persistence.PeriodAsync(tx, book["_id"].AsInt64, month, ct);
        AccountingPersistence.RequireOpen(book, period, month);
        if (period["state"] != "open" || period["bookId"] != book["_id"] || period["month"] != month) throw Changed();
        void Current()
        {
            AccountingPersistence.RequireSession(database, tx);
            var now = clock.UtcNow;
            if (now < beganAt || postingDate > AccountingCalendar.BusinessDate(now) ||
                proposal["recordedAt"].ToUniversalTime() > now.UtcDateTime || decision["recordedAt"].ToUniversalTime() > now.UtcDateTime) throw Changed();
        }
        Current();
        // The exact chart rewrite fences removal/replacement; TouchBook serializes
        // approved setup and period changes with the eventual owning journal write.
        if ((await persistence.Collection("accountingChartVersions").DeleteOneAsync(tx, chart, cancellationToken: ct)).DeletedCount != 1) throw Changed();
        await persistence.Collection("accountingChartVersions").InsertOneAsync(tx, chart, cancellationToken: ct);
        await persistence.TouchBookAsync(tx, book, ct); Current();
        var bookBinding = book.DeepClone().AsBsonDocument; bookBinding.Remove("fence");
        var snapshot = new BsonDocument {
            { "key", AccountingBson.Key(key) }, { "postingDate", postingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            { "setupProposalId", proposal["_id"] }, { "setupProposalHash", proposal["snapshotHash"] },
            { "setupDecisionId", decision["_id"] }, { "setupDecisionHash", decision["snapshotHash"] },
            { "setupProposedAt", proposal["recordedAt"] }, { "setupApprovedAt", decision["recordedAt"] },
            { "book", bookBinding }, { "chart", chart.DeepClone() },
            { "periodId", period["_id"] }, { "periodMonth", month }, { "inventory", inventory }, { "cogs", cogs }
        };
        return new(this, tx, beganAt, key, postingDate, snapshot, expectedInventory);
    }

    /// <summary>The owning shipment command calls this after its awaited work. The
    /// original approval, mapping and period are compared; a changed setup is never rebased.</summary>
    internal async Task RequireCurrentAsync(IClientSessionHandle tx, Binding binding, CancellationToken ct = default)
    {
        AccountingPersistence.RequireSession(database, tx); binding.Require(this, tx, clock.UtcNow);
        var current = await ReadAndFenceAsync(tx, binding.Key, binding.PostingDate, binding.ExpectedInventory, ct);
        binding.Require(this, tx, clock.UtcNow); Equal(current.Snapshot, binding.Snapshot);
    }
}
