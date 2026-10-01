using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using C = UnitToko.Platform.Billing.PaymentAllocationCorrectionBson;

namespace UnitToko.Platform.Billing;

internal sealed class PaymentAllocationPostingSource(IMongoDatabase database, BsonDocument expected) : IAccountingSourceReader
{
    public string Namespace => "billing.payment-allocation";
    public IMongoDatabase Database => database;
    internal static JournalSource Source(long id, bool correction = false) => new("billing.payment-allocation", id, correction ? "unallocated" : "allocated", 1);
    internal static SourceSnapshot Snapshot(BsonDocument row) {
        var party = B.Party(row["party"].AsBsonDocument);
        var book = new OperationalBookKey(party.FulfillmentOrganizationId.Value, party.ShopId.Value, party.ChannelId.Value, "IDR");
        var amount = InvoiceBson.Money(row["amount"]); var zero = new Money(0);
        var correction = row["operation"] == C.Operation;
        return new(Source(row["_id"].AsInt64, correction), 1, book, new(book.OrganizationId, book.ShopId, book.ChannelId, null, party.BuyingOrganizationId.Value, null),
            InvoiceBson.Date(row["postingDate"]), [new(row["policy"]["suspenseMapping"].AsString, correction ? zero : amount, correction ? amount : zero), new(row["policy"]["arMapping"].AsString, correction ? amount : zero, correction ? zero : amount)]);
    }
    public async Task<SourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken ct) {
        AccountingPersistence.RequireSession(database, tx);
        var correction = expected["operation"] == C.Operation;
        if (expectedRevision != 1 || source != Source(expected["_id"].AsInt64, correction) || actor.UserId != expected["actorId"].AsInt64 || expected["postingFence"] != 0L || !expected["journalId"].IsBsonNull) throw B.Broken();
        if ((await database.GetCollection<BsonDocument>(correction ? C.Events : B.Events).UpdateOneAsync(tx, expected, Builders<BsonDocument>.Update.Inc("postingFence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        return Snapshot(expected);
    }
    public Task<ReversalSourceSnapshot> ReadReversalAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken ct) => throw B.Broken();
}
