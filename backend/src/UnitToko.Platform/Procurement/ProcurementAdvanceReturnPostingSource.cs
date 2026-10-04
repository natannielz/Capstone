using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Billing;
using B = UnitToko.Platform.Procurement.ProcurementAdvanceReturnBson;

namespace UnitToko.Platform.Procurement;

internal sealed class ProcurementAdvanceReturnPostingSource(IMongoDatabase database, BsonDocument expected) : IAccountingSourceReader
{
    public string Namespace => B.DecideOperation;
    public IMongoDatabase Database => database;
    internal static JournalSource Source(long id) => new(B.DecideOperation, id, "supplier-advance-return", 1);
    internal static SourceSnapshot Snapshot(BsonDocument row)
    {
        var input = B.Input(row["input"]); var amount = Money.Parse(row["amount"].AsString); var zero = new Money(0m);
        var key = new OperationalBookKey(input.Location.OrganizationId, input.Location.ShopId, input.Location.ChannelId, "IDR");
        var accounting = row["accounting"];
        return new(Source(row["_id"].AsInt64), 1, key,
            new(key.OrganizationId, key.ShopId, key.ChannelId, input.Location.WarehouseId, null, null), input.PostingDate,
            [new PostingLine(accounting["suspense"]["mappingKey"].AsString, amount, zero),
                new PostingLine(accounting["supplierAdvance"]["mappingKey"].AsString, zero, amount)]);
    }
    internal static void RequireAccounts(BsonDocument row, JournalDraft draft)
    {
        var accounting = row["accounting"]; var expected = Snapshot(row);
        if (draft.ChartId != accounting["chartVersionId"].AsInt64 || draft.Lines.Length != 2 ||
            draft.Source != expected.Source || draft.SourceRevision != 1 || draft.Book != expected.Book ||
            draft.Dimensions != expected.Dimensions || draft.Date != expected.Date || draft.ReversalOf is not null ||
            !draft.Lines.Select(value => new PostingLine(value.MappingKey, value.Debit, value.Credit)).SequenceEqual(expected.Lines)) throw B.Broken();
        foreach (var line in draft.Lines) {
            var role = new[] { "suspense", "supplierAdvance" }.SingleOrDefault(value => accounting[value]["mappingKey"].AsString == line.MappingKey)
                ?? throw B.Broken();
            if (line.AccountId != accounting[role]["accountId"].AsInt64 || line.AccountCode != accounting[role]["accountCode"].AsString)
                throw B.Broken();
        }
    }
    public async Task<SourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source,
        long expectedRevision, CancellationToken ct)
    {
        AccountingPersistence.RequireSession(database, tx);
        if (expectedRevision != 1 || source != Source(expected["_id"].AsInt64) || actor.UserId != expected["actorId"].AsInt64 ||
            expected["postingFence"] != 0L || !expected["journalId"].IsBsonNull) throw B.Broken();
        if ((await database.GetCollection<BsonDocument>(B.Decisions).UpdateOneAsync(tx, expected,
                Builders<BsonDocument>.Update.Inc("postingFence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        return Snapshot(expected);
    }
    public Task<ReversalSourceSnapshot> ReadReversalAndFenceAsync(IClientSessionHandle tx, ActorContext actor,
        JournalSource source, long expectedRevision, CancellationToken ct) => throw B.Broken();
}
