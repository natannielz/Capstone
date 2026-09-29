using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Catalog;

namespace UnitToko.Platform.Billing;

// Created only by the invoice owner for one verified, uncommitted source. Neither HTTP nor
// a reusable caller can ask Accounting to post an arbitrary amount or a different invoice.
internal sealed class InvoicePostingSource(IMongoDatabase database, BsonDocument expected) : IAccountingSourceReader
{
    public string Namespace => "billing.invoice";
    public IMongoDatabase Database => database;
    internal static JournalSource Source(long id) => new("billing.invoice", id, "issued", 1);
    internal static SourceSnapshot Snapshot(BsonDocument invoice) {
        var resources = InvoiceBson.Sources(invoice["sources"].AsBsonArray).Select(source => source.Resource).ToArray();
        var first = resources[0]; var terms = InvoiceBson.Terms(invoice["terms"].AsBsonDocument);
        var book = new OperationalBookKey(first.FulfillmentOrganizationId!.Value, first.ShopId!.Value, first.ChannelId!.Value, "IDR");
        var dimensions = new JournalDimensions(book.OrganizationId, book.ShopId, book.ChannelId,
            resources.Select(scope => scope.WarehouseId).Distinct().Count() == 1 ? first.WarehouseId : null, first.BuyingOrganizationId,
            resources.Select(scope => scope.BranchId).Distinct().Count() == 1 ? first.BranchId : null);
        var totals = invoice["totals"];
        var lines = new List<PostingLine> { new(terms.ReceivableMapping, InvoiceBson.Money(totals["total"]), new(0m)) };
        foreach (var (key, field) in new[] { (terms.RevenueMapping, "net"), (terms.TaxMapping, "tax"), (terms.ChargesMapping, "charges") }) {
            var value = InvoiceBson.Money(totals[field]); if (value.Value > 0) lines.Add(new(key, new(0m), value));
        }
        return new(Source(invoice["_id"].AsInt64), 1, book, dimensions, InvoiceBson.Date(invoice["issueDate"]), lines);
    }
    public async Task<SourceSnapshot> ReadAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || expectedRevision != 1 || source != Source(expected["_id"].AsInt64) || expected["issuedBy"] != actor.UserId)
            throw InvoiceRecords.Invalid();
        var rows = database.GetCollection<BsonDocument>(InvoiceBson.Invoices);
        var current = await rows.Find(tx, new BsonDocument("_id", expected["_id"])).SingleOrDefaultAsync(ct);
        if (current is null || !CatalogSchemasEqual(current, expected) || !current["journalId"].IsBsonNull || current["postingFence"] != 0L)
            throw InvoiceRecords.Invalid();
        if ((await rows.UpdateOneAsync(tx, current, Builders<BsonDocument>.Update.Inc("postingFence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw InvoiceRecords.Invalid();
        return Snapshot(current);
    }
    private static bool CatalogSchemasEqual(BsonDocument left, BsonDocument right) => CatalogRepository.Digest(left) == CatalogRepository.Digest(right);
    public Task<ReversalSourceSnapshot> ReadReversalAndFenceAsync(IClientSessionHandle tx, ActorContext actor, JournalSource source, long expectedRevision, CancellationToken ct) =>
        throw InvoiceRecords.Invalid();
}
