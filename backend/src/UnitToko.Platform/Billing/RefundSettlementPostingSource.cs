using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using D = UnitToko.Platform.Billing.RefundSettlementBson;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;

namespace UnitToko.Platform.Billing;

internal sealed class RefundSettlementPostingSource(IMongoDatabase database, BsonDocument expected) : IAccountingSourceReader
{
    public string Namespace => D.Operation;
    public IMongoDatabase Database => database;
    internal static JournalSource Source(long id) => new(D.Operation, id, "refund.bank-debit-settled", 1);
    internal static SourceSnapshot Snapshot(BsonDocument row) {
        var party = B.Party(row["party"].AsBsonDocument); var book = new OperationalBookKey(party.FulfillmentOrganizationId.Value, party.ShopId.Value, party.ChannelId.Value, "IDR");
        var amount = InvoiceBson.Money(row["amount"]); var zero = new Money(0m);
        return new(Source(row["_id"].AsInt64), 1, book, new(book.OrganizationId, book.ShopId, book.ChannelId, null, party.BuyingOrganizationId.Value, null),
            InvoiceBson.Date(row["postingDate"]), [new(row["liabilityAccount"]["mappingKey"].AsString, amount, zero), new(row["cashAccount"]["mappingKey"].AsString, zero, amount)]);
    }
    internal static void RequireAccounts(BsonDocument row, JournalDraft draft) {
        if (draft.Lines.Length != 2 || row["liabilityAccount"]["accountId"] == row["cashAccount"]["accountId"] ||
            row["liabilityAccount"]["mappingKey"] == row["cashAccount"]["mappingKey"]) throw B.Broken();
        foreach (var line in draft.Lines) {
            var account = line.MappingKey == row["liabilityAccount"]["mappingKey"].AsString ? row["liabilityAccount"] : row["cashAccount"];
            if (line.MappingKey != account["mappingKey"].AsString || line.AccountId != account["accountId"].AsInt64 || line.AccountCode != account["accountCode"].AsString) throw B.Changed("refund_settlement_accounting_mapping_changed");
        }
    }
    public async Task<SourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken ct) {
        AccountingPersistence.RequireSession(database, tx);
        if (expectedRevision != 1 || source != Source(expected["_id"].AsInt64) || actor.UserId != expected["actorId"].AsInt64 || expected["postingFence"] != 0L || !expected["journalId"].IsBsonNull) throw B.Broken();
        if ((await database.GetCollection<BsonDocument>(D.Events).UpdateOneAsync(tx, expected, Builders<BsonDocument>.Update.Inc("postingFence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        return Snapshot(expected);
    }
    public Task<ReversalSourceSnapshot> ReadReversalAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken ct) => throw B.Broken();
}
