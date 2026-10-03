using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using B = UnitToko.Platform.Procurement.ProcurementStockValuationBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal sealed class ProcurementStockValuationPostingSource(IMongoDatabase database, BsonDocument expected) : IAccountingSourceReader
{
    public string Namespace => B.DecideOperation;
    public IMongoDatabase Database => database;
    internal static JournalSource Source(long id) => new(B.DecideOperation, id, "held-receipt-valued", 1);
    internal static SourceSnapshot Snapshot(BsonDocument row) {
        var location = R.Location(row["location"]); var book = new OperationalBookKey(location.OrganizationId, location.ShopId, location.ChannelId, "IDR");
        var amount = Money.Parse(row["input"]["capitalizedAmount"].AsString); var zero = new Money(0m);
        return new(Source(row["_id"].AsInt64), 1, book,
            new(book.OrganizationId, book.ShopId, book.ChannelId, location.WarehouseId, null, null),
            UnitToko.Platform.Billing.InvoiceBson.Date(row["input"]["postingDate"]),
            [new(row["accounting"]["inventory"]["mappingKey"].AsString, amount, zero),
             new(row["accounting"]["receiptAccrual"]["mappingKey"].AsString, zero, amount)]);
    }
    internal static void RequireAccounts(BsonDocument row, JournalDraft draft) {
        var accounting = row["accounting"]; if (draft.ChartId != accounting["chartVersionId"].AsInt64 || draft.Lines.Length != 2 ||
            accounting["inventory"]["accountId"] == accounting["receiptAccrual"]["accountId"]) throw B.Broken();
        foreach (var line in draft.Lines) {
            var role = line.MappingKey == accounting["inventory"]["mappingKey"].AsString ? "inventory" : "receiptAccrual";
            if (line.MappingKey != accounting[role]["mappingKey"].AsString || line.AccountId != accounting[role]["accountId"].AsInt64 ||
                line.AccountCode != accounting[role]["accountCode"].AsString) throw B.Changed("procurement_stock_valuation_accounting_changed");
        }
    }
    public async Task<SourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken ct) {
        AccountingPersistence.RequireSession(database, tx);
        if (expectedRevision != 1 || source != Source(expected["_id"].AsInt64) || actor.UserId != expected["actorId"].AsInt64 ||
            expected["decision"] != "approve" || expected["postingFence"] != 0L || !expected["journalId"].IsBsonNull) throw B.Broken();
        if ((await database.GetCollection<BsonDocument>(B.Decisions).UpdateOneAsync(tx, expected,
            Builders<BsonDocument>.Update.Inc("postingFence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        return Snapshot(expected);
    }
    public Task<ReversalSourceSnapshot> ReadReversalAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source,
        long expectedRevision, CancellationToken ct) => throw B.Broken();
}
