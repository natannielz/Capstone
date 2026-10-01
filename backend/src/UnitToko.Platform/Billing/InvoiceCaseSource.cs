using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Platform.Billing;
public sealed partial class InvoiceService
{
    internal Task CaseFenceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, BsonArray scopes, string permission, CancellationToken ct) =>
        receipts.InvoiceFenceAsync(actor, authority, tx, PaymentDraftBson.Scopes(scopes), permission, ct);
    internal async Task<BsonDocument> CaseSourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, string permission, CancellationToken ct) {
        if (permission is not (Permissions.CaseRead or Permissions.CaseRequest or Permissions.CaseManage)) throw InvoiceRecords.Invalid();
        var row = await AuthorizedInvoiceAsync(actor, authority, tx, id, permission, ct); var view = View(row);
        return new() { { "invoiceId", row["_id"] }, { "invoiceHash", row["snapshotHash"] }, { "issuedAt", row["issuedAt"] },
            { "number", FormattableString.Invariant($"{view.NumberEntity.Value}/{view.NumberChannel.Value}/{view.NumberYear}/{view.Number}") },
            { "buyerName", view.Detail.BuyerName }, { "sellerName", view.Detail.SellerName }, { "scopes", row["scopes"].DeepClone() },
            { "lines", new BsonArray(view.Detail.Lines.Select(line => new BsonDocument { { "orderId", line.OrderId.Value }, { "shipmentId", line.ShipmentId.Value },
                { "receiptId", line.ReceiptId.Value }, { "skuId", line.SkuId.Value }, { "skuCode", line.SkuCode }, { "productName", line.ProductName },
                { "packageLabel", line.PackageLabel }, { "baseUnitsPerPackage", line.BaseUnitsPerPackage }, { "baseUnitCode", line.BaseUnitCode }, { "acceptedBaseUnits", line.AcceptedBaseUnits } })) } };
    }
}
