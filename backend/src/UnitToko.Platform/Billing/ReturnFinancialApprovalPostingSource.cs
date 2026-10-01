using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;

namespace UnitToko.Platform.Billing;
internal sealed class ReturnFinancialApprovalPostingSource(IMongoDatabase database, BsonDocument expected) : IAccountingSourceReader
{
    public string Namespace => "billing.return-financial";
    public IMongoDatabase Database => database;
    internal static JournalSource Source(long id) => new("billing.return-financial", id, "approved", 1);
    internal static SourceSnapshot Snapshot(BsonDocument row) {
        var party = B.Party(row["party"].AsBsonDocument); var book = new OperationalBookKey(party.FulfillmentOrganizationId.Value, party.ShopId.Value, party.ChannelId.Value, "IDR");
        var zero = new Money(0); var lines = new List<PostingLine>();
        foreach (var component in new[] { "net", "tax", "charges" }) {
            var value = InvoiceBson.Money(row["request"][component]); if (value.Value == 0) continue;
            var original = row["request"]["source"]["originalAccounts"].AsBsonArray.Single(account => account["component"] == component);
            lines.Add(new(original["mappingKey"].AsString, value, zero));
        }
        var credit = InvoiceBson.Money(row["credit"]); var reserved = InvoiceBson.Money(row["reservedRefund"]);
        if (credit.Value > reserved.Value) lines.Add(new(row["policy"]["arMapping"].AsString, zero, new Money(credit.Value - reserved.Value)));
        if (reserved.Value > 0) lines.Add(new(row["policy"]["refundLiabilityMapping"].AsString, zero, reserved));
        return new(Source(row["_id"].AsInt64), 1, book, new(book.OrganizationId, book.ShopId, book.ChannelId, null, party.BuyingOrganizationId.Value, null), InvoiceBson.Date(row["postingDate"]), [.. lines]);
    }
    internal static void RequireAccounts(BsonDocument row, JournalDraft draft) {
        foreach (var line in draft.Lines) {
            var original = row["request"]["source"]["originalAccounts"].AsBsonArray.SingleOrDefault(account => account["mappingKey"] == line.MappingKey);
            if (line.MappingKey == row["policy"]["refundLiabilityMapping"].AsString) {
                if (original is not null || line.AccountId != row["policy"]["refundLiabilityAccountId"].AsInt64 || line.AccountCode != row["policy"]["refundLiabilityAccountCode"].AsString) throw B.Changed("return_financial_accounting_mapping_changed");
            } else if (original is null || line.AccountId != original["accountId"].AsInt64 || line.AccountCode != original["accountCode"].AsString) throw B.Changed("return_financial_accounting_mapping_changed");
        }
    }
    public async Task<SourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken ct) {
        AccountingPersistence.RequireSession(database, tx);
        if (expectedRevision != 1 || source != Source(expected["_id"].AsInt64) || actor.UserId != expected["actorId"].AsInt64 || expected["postingFence"] != 0L || !expected["journalId"].IsBsonNull) throw B.Broken();
        if ((await database.GetCollection<BsonDocument>(A.Events).UpdateOneAsync(tx, expected, Builders<BsonDocument>.Update.Inc("postingFence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        return Snapshot(expected);
    }
    public Task<ReversalSourceSnapshot> ReadReversalAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken ct) => throw B.Broken();
}
