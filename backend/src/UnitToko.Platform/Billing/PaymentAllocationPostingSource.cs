using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;

namespace UnitToko.Platform.Billing;

internal sealed class PaymentAllocationPostingSource(IMongoDatabase database, BsonDocument expected) : IAccountingSourceReader
{
    public string Namespace => "billing.payment-allocation";
    public IMongoDatabase Database => database;
    internal static JournalSource Source(long id) => new("billing.payment-allocation", id, "allocated", 1);
    internal static SourceSnapshot Snapshot(BsonDocument row) {
        var party = B.Party(row["party"].AsBsonDocument);
        var book = new OperationalBookKey(party.FulfillmentOrganizationId.Value, party.ShopId.Value, party.ChannelId.Value, "IDR");
        var amount = InvoiceBson.Money(row["amount"]); var zero = new Money(0);
        return new(Source(row["_id"].AsInt64), 1, book, new(book.OrganizationId, book.ShopId, book.ChannelId, null, party.BuyingOrganizationId.Value, null),
            InvoiceBson.Date(row["postingDate"]), [new(row["policy"]["suspenseMapping"].AsString, amount, zero), new(row["policy"]["arMapping"].AsString, zero, amount)]);
    }
    public async Task<SourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken ct) {
        AccountingPersistence.RequireSession(database, tx);
        if (expectedRevision != 1 || source != Source(expected["_id"].AsInt64) || actor.UserId != expected["actorId"].AsInt64 || expected["postingFence"] != 0L || !expected["journalId"].IsBsonNull) throw B.Broken();
        if ((await database.GetCollection<BsonDocument>(B.Events).UpdateOneAsync(tx, expected, Builders<BsonDocument>.Update.Inc("postingFence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        return Snapshot(expected);
    }
    public Task<ReversalSourceSnapshot> ReadReversalAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken ct) => throw B.Broken();
}
