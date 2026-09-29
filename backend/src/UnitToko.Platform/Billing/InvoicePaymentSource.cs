using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Platform.Billing;
public sealed partial class InvoiceService
{
    internal IMongoDatabase PaymentSourceDatabase => database;
    internal Task PaymentStorageAsync(CancellationToken ct) => StorageAsync(ct);
    // Narrow owning-module adapter. A declaration references an issued invoice but does
    // not alter its amount, claim a balance, or confer permission to allocate money.
    internal async Task<BsonDocument> PaymentSourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long invoiceId, CancellationToken ct) {
        var row = await AuthorizedInvoiceAsync(actor, authority, tx, invoiceId, Permissions.InvoiceRead, ct);
        var view = View(row);
        return new() { { "invoiceId", row["_id"] }, { "snapshotHash", row["snapshotHash"] },
            { "numberEntity", row["numberEntity"] }, { "numberChannel", row["numberChannel"] }, { "numberYear", row["numberYear"] }, { "number", row["number"] },
            { "total", row["totals"]["total"] }, { "buyerName", view.Detail.BuyerName }, { "sellerName", view.Detail.SellerName },
            { "scopes", row["scopes"].DeepClone() } };
    }
}
