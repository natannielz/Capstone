using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Billing;
using B = UnitToko.Platform.Procurement.ProcurementSettlementBson;

namespace UnitToko.Platform.Procurement;

internal sealed class ProcurementSettlementPostingSource(IMongoDatabase database, BsonDocument expected) : IAccountingSourceReader
{
    public string Namespace => B.Operation;
    public IMongoDatabase Database => database;
    internal static JournalSource Source(long id) => new(B.Operation, id, "procurement.bank-debit-settled", 1);
    internal static SourceSnapshot Snapshot(BsonDocument row) {
        var location = ProcurementRequestBson.Location(row["location"]); var key = new OperationalBookKey(location.OrganizationId, location.ShopId, location.ChannelId, "IDR");
        var amount = Money.Parse(row["amount"].AsString); var zero = new Money(0m);
        return new(Source(row["_id"].AsInt64), 1, key, new(key.OrganizationId, key.ShopId, key.ChannelId, location.WarehouseId, null, null), InvoiceBson.Date(row["postingDate"]),
            [new(row["accounting"]["supplierAdvance"]["mappingKey"].AsString, amount, zero), new(row["accounting"]["cash"]["mappingKey"].AsString, zero, amount)]);
    }
    internal static void RequireAccounts(BsonDocument row, JournalDraft draft) {
        var accounting = row["accounting"];
        if (draft.ChartId != accounting["chartVersionId"].AsInt64 || draft.Lines.Length != 2 || accounting["supplierAdvance"]["accountId"] == accounting["cash"]["accountId"] ||
            accounting["supplierAdvance"]["mappingKey"] == accounting["cash"]["mappingKey"]) throw B.Broken();
        foreach (var line in draft.Lines) {
            var account = line.MappingKey == accounting["supplierAdvance"]["mappingKey"].AsString ? accounting["supplierAdvance"] : accounting["cash"];
            if (line.MappingKey != account["mappingKey"].AsString || line.AccountId != account["accountId"].AsInt64 || line.AccountCode != account["accountCode"].AsString) throw B.Changed("procurement_settlement_accounting_changed");
        }
    }
    public async Task<SourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken ct) {
        AccountingPersistence.RequireSession(database, tx);
        if (expectedRevision != 1 || source != Source(expected["_id"].AsInt64) || actor.UserId != expected["actorId"].AsInt64 || expected["postingFence"] != 0L || !expected["journalId"].IsBsonNull) throw B.Broken();
        if ((await database.GetCollection<BsonDocument>(B.Events).UpdateOneAsync(tx, expected, Builders<BsonDocument>.Update.Inc("postingFence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        return Snapshot(expected);
    }
    public Task<ReversalSourceSnapshot> ReadReversalAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken ct) => throw B.Broken();
}
