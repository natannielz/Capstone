using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Platform.Billing;
public sealed partial class InvoiceService
{
    internal IMongoDatabase PaymentSourceDatabase => database;
    internal Task PaymentStorageAsync(CancellationToken ct) => StorageAsync(ct);
    internal async Task<BsonDocument> AllocationSourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long invoiceId, CancellationToken ct) {
        var row = await AuthorizedInvoiceAsync(actor, authority, tx, invoiceId, Permissions.InvoiceRead, ct);
        return await AllocationSnapshotAsync(database, tx, row, ct);
    }
    internal static async Task<BsonDocument> AllocationSnapshotAsync(IMongoDatabase database, IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        var source = PaymentSnapshot(row);
        source.Add("arMapping", InvoiceBson.Terms(row["terms"].AsBsonDocument).ReceivableMapping);
        var journal = await database.GetCollection<BsonDocument>("journalEntries").Find(tx, new BsonDocument("_id", row["journalId"])).SingleOrDefaultAsync(ct) ?? throw PaymentAllocationBson.Broken();
        var draft = UnitToko.Platform.Accounting.AccountingBson.ReadDraft(journal);
        if (draft.Fingerprint != row["journalFingerprint"].AsString) throw PaymentAllocationBson.Broken();
        var ar = draft.Lines.Single(line => line.MappingKey == source["arMapping"].AsString);
        source.Add("arAccountId", ar.AccountId); source.Add("arAccountCode", ar.AccountCode);
        source.Add("dueDate", row["dueDate"]); return source;
    }
    // Narrow owning-module adapter. A declaration references an issued invoice but does
    // not alter its amount, claim a balance, or confer permission to allocate money.
    internal async Task<BsonDocument> PaymentSourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long invoiceId, CancellationToken ct) {
        var row = await AuthorizedInvoiceAsync(actor, authority, tx, invoiceId, Permissions.InvoiceRead, ct);
        return PaymentSnapshot(row);
    }
    private static BsonDocument PaymentSnapshot(BsonDocument row) {
        var view = View(row);
        return new() { { "invoiceId", row["_id"] }, { "snapshotHash", row["snapshotHash"] },
            { "numberEntity", row["numberEntity"] }, { "numberChannel", row["numberChannel"] }, { "numberYear", row["numberYear"] }, { "number", row["number"] },
            { "total", row["totals"]["total"] }, { "buyerName", view.Detail.BuyerName }, { "sellerName", view.Detail.SellerName },
            { "scopes", row["scopes"].DeepClone() } };
    }
}
