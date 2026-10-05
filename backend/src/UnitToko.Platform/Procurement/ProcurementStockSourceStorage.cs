using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Procurement;

internal static class ProcurementStockSourceStorage
{
    internal static async Task RequireRetainedAsync(IMongoDatabase database, CancellationToken ct)
    {
        await OrderStockSourceStorage.RequireAsync(database, ct);
        await ReconciliationStockSourceStorage.RequireAsync(database, ct);
        await UnitToko.Platform.Fulfillment.FulfillmentStockSourceStorage.RequireAsync(database, ct);
        foreach (var name in ProcurementStockReceiptSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, ProcurementStockReceiptSchemas.Validator(name), ProcurementStockReceiptSchemas.Keys(name), ct);
        foreach (var name in ProcurementQualityAcceptanceSchemas.Collections) {
            await InventoryRepository.RequireStorageAsync(database, name, ProcurementQualityAcceptanceSchemas.Validator(name), ProcurementQualityAcceptanceSchemas.Keys(name), ct);
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            foreach (var key in ProcurementQualityAcceptanceSchemas.SupportKeys(name))
                if (!indexes.Any(index => index["key"] == key && !index.GetValue("unique", false).AsBoolean &&
                    !UnitToko.Platform.Billing.InvoiceStorage.Unsafe(index) && !index.Contains("partialFilterExpression")))
                    throw ProcurementStockSourceRecords.Unavailable();
        }
        await ProcurementStockReceiptSchemas.RequireDispositionStorageAsync(database, ct);
        await InventoryRepository.RequireStorageAsync(database, "lots", InventoryLotSchemas.Validator(), new BsonDocument("_id", 1), ct);
        await InventoryRepository.RequireStorageAsync(database, "stockBalances", InventorySchemas.BalanceValidator(), InventorySchemas.BalanceKey(), ct);
        await InventoryRepository.RequireStorageAsync(database, "movements", InventoryMovementSchemas.Validator(), InventoryMovementSchemas.Key(), ct);
    }
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct)
    {
        await RequireRetainedAsync(database, ct);
        foreach (var name in ProcurementStockSourceSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw ProcurementStockSourceRecords.Unavailable();
            if (!AccountContactStorage.SafeDefinition(definition, ProcurementStockSourceSchemas.Validator(name))) throw ProcurementStockSourceRecords.Unavailable();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var required = ProcurementStockSourceSchemas.Keys(name);
            if (!OrderStockSourceStorage.SafeIndexes(indexes, required) || required.Any(key => !indexes.Any(row => AccountContactStorage.Matches(row, key))))
                throw ProcurementStockSourceRecords.Unavailable();
        }
    }
}
