using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Billing;
public sealed partial class InvoiceService
{
    internal const string CaseReturnFences = "invoiceCaseReturnFences";
    internal Task CaseReturnStorageAsync(CancellationToken ct) => InventoryRepository.RequireStorageAsync(database, CaseReturnFences, InvoiceCaseReturnSchemas.Validator(), new BsonDocument("_id", 1), ct);
    internal Task CaseFenceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, BsonArray scopes, string permission, CancellationToken ct) =>
        receipts.InvoiceFenceAsync(actor, authority, tx, PaymentDraftBson.Scopes(scopes), permission, ct);
    internal async Task CaseReturnFenceAsync(IClientSessionHandle tx, BsonDocument source, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw InvoiceRecords.Invalid();
        var touched = await database.GetCollection<BsonDocument>(CaseReturnFences).UpdateOneAsync(tx,
            new BsonDocument { { "_id", source["invoiceId"] }, { "invoiceHash", source["invoiceHash"] }, { "fence", new BsonDocument("$lt", long.MaxValue - 1) } },
            Builders<BsonDocument>.Update.SetOnInsert("schemaVersion", 1).SetOnInsert("invoiceHash", source["invoiceHash"]).Inc("fence", 1L), new UpdateOptions { IsUpsert = true }, ct);
        if (touched.ModifiedCount != 1 && touched.UpsertedId is null) throw InvoiceRecords.Invalid();
    }
    private async Task<BsonDocument> CaseInvoiceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, string permission,
        CancellationToken ct, FinancialInvoiceSource? issuedInvoice, string? expectedHash = null) {
        if (issuedInvoice is null) return await AuthorizedInvoiceAsync(actor, authority, tx, id, permission, ct);
        // Only the financial read adapter can consume this actual Invoice-owned observation.
        // Its enclosing M53 consumer must finish the full issued-invoice fence in this tx.
        if (permission != Permissions.CaseRead) throw InvoiceRecords.Invalid();
        issuedInvoice.RequireContext(actor, authority, tx, id, expectedHash ?? issuedInvoice.Invoice["snapshotHash"].AsString);
        var query = ScopePolicy.Compile(actor, authority, permission, clock.UtcNow); AuthorizationStore.Require(query);
        var resources = InvoiceBson.Sources(issuedInvoice.Invoice["sources"].AsBsonArray).Select(source => source.Resource).ToArray();
        if (resources.Length == 0 || resources.Any(resource => !query.Matches(resource) || !ScopePolicy.IsResourceActive(resource, authority.Directory))) throw InvoiceRecords.Invalid();
        // Preserve the actual CaseRead grant/session/scope fence for this completed
        // owning-module read; the enclosing return fence repeats its final admission.
        await receipts.InvoiceFenceAsync(actor, authority, tx, resources, permission, ct);
        issuedInvoice.RequireContext(actor, authority, tx, id, expectedHash ?? issuedInvoice.Invoice["snapshotHash"].AsString);
        ct.ThrowIfCancellationRequested(); return issuedInvoice.Invoice.DeepClone().AsBsonDocument;
    }
    internal async Task<BsonDocument> CaseSourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, long id, string permission, CancellationToken ct,
        FinancialInvoiceSource? issuedInvoice = null) {
        if (permission is not (Permissions.CaseRead or Permissions.CaseRequest or Permissions.CaseManage)) throw InvoiceRecords.Invalid();
        var row = await CaseInvoiceAsync(actor, authority, tx, id, permission, ct, issuedInvoice); return CaseSnapshot(row);
    }
    internal static BsonDocument CaseSnapshot(BsonDocument row) {
        var view = View(row);
        return new() { { "invoiceId", row["_id"] }, { "invoiceHash", row["snapshotHash"] }, { "issuedAt", row["issuedAt"] },
            { "number", FormattableString.Invariant($"{view.NumberEntity.Value}/{view.NumberChannel.Value}/{view.NumberYear}/{view.Number}") },
            { "buyerName", view.Detail.BuyerName }, { "sellerName", view.Detail.SellerName }, { "scopes", row["scopes"].DeepClone() },
            { "lines", new BsonArray(view.Detail.Lines.Select(line => new BsonDocument { { "orderId", line.OrderId.Value }, { "shipmentId", line.ShipmentId.Value },
                { "receiptId", line.ReceiptId.Value }, { "skuId", line.SkuId.Value }, { "skuCode", line.SkuCode }, { "productName", line.ProductName },
                { "packageLabel", line.PackageLabel }, { "baseUnitsPerPackage", line.BaseUnitsPerPackage }, { "baseUnitCode", line.BaseUnitCode }, { "acceptedBaseUnits", line.AcceptedBaseUnits } })) } };
    }
    internal async Task<BsonArray> CaseStockSourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, BsonDocument expected, string permission, CancellationToken ct,
        FinancialInvoiceSource? issuedInvoice = null) {
        var invoice = await CaseInvoiceAsync(actor, authority, tx, expected["invoiceId"].AsInt64, permission, ct, issuedInvoice,
            issuedInvoice is null ? null : expected["invoiceHash"].AsString);
        if (!InvoiceRecords.Equal(CaseSnapshot(invoice), expected)) throw InvoiceRecords.Invalid();
        return await CaseDispatchStockAsync(database, tx, expected, ct);
    }
    internal static async Task<BsonArray> CaseDispatchStockAsync(IMongoDatabase database, IClientSessionHandle tx, BsonDocument expected, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw InvoiceRecords.Invalid();
        var result = new BsonArray();
        foreach (var line in expected["lines"].AsBsonArray) {
            var shipment = await new ShipmentDispatchRecords(database).ReadAsync(tx, line["shipmentId"].AsInt64, ct);
            // One SKU per immutable shipment is retained. The receipt/shipment key therefore
            // resolves its exact accepted line without changing historical case snapshots.
            var dispatched = shipment["lines"].AsBsonArray.SingleOrDefault(value => value["skuId"] == line["skuId"]) ?? throw InvoiceRecords.Invalid();
            var allocations = shipment["stockEffects"].AsBsonArray.Select(effect => OrderSubmissionBson.Allocation(effect["allocation"].AsBsonDocument))
                .Where(item => item.SkuId == line["skuId"].AsInt64 && item.LineRevision == OrderLineBson.Revision(dispatched)).ToArray();
            if (allocations.Length == 0 || allocations.Select(item => item.Change.Key.DefinitionId).Distinct().Count() != 1 || allocations.Select(item => item.Pool.Location).Distinct().Count() != 1) throw InvoiceRecords.Invalid();
            var item = allocations[0];
            result.Add(new BsonDocument { { "receiptId", line["receiptId"] }, { "skuId", line["skuId"] }, { "definitionId", item.Change.Key.DefinitionId.Value },
                { "packageLabel", line["packageLabel"] }, { "baseUnitsPerPackage", line["baseUnitsPerPackage"] }, { "baseUnitCode", line["baseUnitCode"] },
                { "location", OrderSubmissionBson.Allocation(item)["location"].DeepClone() }, { "dispatchId", shipment["_id"] }, { "dispatchReview", shipment["review"] },
                // Candidate lots are original provenance, never a claim that the returned loose item belongs to a particular lot.
                { "originalLots", new BsonArray(allocations.OrderBy(value => value.Change.Key.LotId.Value).Select(value => new BsonDocument {
                    { "lotId", value.Change.Key.LotId.Value }, { "poolId", value.Pool.Id }, { "dispatchedPackages", value.Change.Packages } })) } });
        }
        return result;
    }
}
