using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Procurement;

internal static class PurchaseOrderInboundBindingStorage
{
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct) {
        foreach (var name in PurchaseOrderInboundBindingSchemas.Collections) {
            await InventoryRepository.RequireStorageAsync(database, name, PurchaseOrderInboundBindingSchemas.Validator(name), PurchaseOrderInboundBindingSchemas.Keys(name), ct);
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var permitted = PurchaseOrderInboundBindingSchemas.Keys(name).Append(new BsonDocument("_id", 1)).ToArray();
            if (indexes.Any(row => UnitToko.Platform.Billing.InvoiceStorage.Unsafe(row) || row.Contains("partialFilterExpression") ||
                row.GetValue("unique", false).AsBoolean && !permitted.Any(key => key.Equals(row["key"])))) throw PurchaseOrderBson.Broken();
        }
    }
}
