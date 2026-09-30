using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using B = UnitToko.Platform.Billing.BankReceiptBson;

namespace UnitToko.Platform.Billing;

internal sealed class BankReceiptPostingSource(IMongoDatabase database, BsonDocument expected) : IAccountingSourceReader
{
    public string Namespace => "billing.bank-receipt";
    public IMongoDatabase Database => database;
    internal static JournalSource Source(long id) => new("billing.bank-receipt", id, "confirmed", 1);
    internal static SourceSnapshot Snapshot(BsonDocument row) {
        var scope = ReceivingAccountBson.Scope(row["scope"].AsBsonDocument);
        var book = new OperationalBookKey(scope.FulfillmentOrganizationId!.Value, scope.ShopId!.Value, scope.ChannelId!.Value, "IDR");
        var amount = InvoiceBson.Money(row["amount"]); var zero = new Money(0m);
        return new(Source(row["_id"].AsInt64), 1, book, new(book.OrganizationId, book.ShopId, book.ChannelId, null, null, null), InvoiceBson.Date(row["bookedDate"]),
            [new(row["policy"]["cashMapping"].AsString, amount, zero), new(row["policy"]["suspenseMapping"].AsString, zero, amount)]);
    }
    public async Task<SourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken ct) {
        AccountingPersistence.RequireSession(database, tx);
        if (expectedRevision != 1 || source != Source(expected["_id"].AsInt64) || actor.UserId != expected["actorId"].AsInt64 || expected["postingFence"] != 0L || !expected["journalId"].IsBsonNull) throw B.Broken();
        var rows = database.GetCollection<BsonDocument>(B.Receipts);
        if ((await rows.UpdateOneAsync(tx, expected, Builders<BsonDocument>.Update.Inc("postingFence",1L), cancellationToken:ct)).ModifiedCount != 1) throw B.Changed();
        return Snapshot(expected);
    }
    public Task<ReversalSourceSnapshot> ReadReversalAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken ct) => throw B.Broken();
}
