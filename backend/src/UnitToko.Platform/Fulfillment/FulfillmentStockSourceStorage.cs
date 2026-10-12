using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Fulfillment;

internal static class FulfillmentStockSourceStorage
{
    internal static async Task RequireRetainedAsync(IMongoDatabase database, CancellationToken ct, BsonDocument? requiredShipmentValidator = null)
    {
        foreach (var name in StockSourceSetupSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, StockSourceSetupSchemas.Validator(name), StockSourceSetupSchemas.Key(name), ct);
        await ShipmentDispatchStorage.RequireAsync(database, ct, requiredShipmentValidator);
        // Historical migration callers explicitly bind the exact legacy dispatch contract.
        // Keep that historical receipt contract here; the default runtime still requires114.
        if (requiredShipmentValidator is not null && CatalogSchemas.SemanticEquals(requiredShipmentValidator, ShipmentDispatchSchemas.LegacyValidator())) {
            foreach (var name in ShipmentReceiptSchemas.Collections)
                await InventoryRepository.RequireStorageAsync(database, name, ShipmentReceiptSchemas.Validator(name), ShipmentReceiptSchemas.Key(name), ct);
        } else await new ShipmentReceiptRecords(database).StorageAsync(ct);
        await new DeliveryAttemptRecords(database).StorageAsync(ct);
        await OrderStockSourceStorage.RequireAsync(database, ct, requiredShipmentValidator);
        await ReconciliationStockSourceStorage.RequireAsync(database, ct);
    }
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct, BsonDocument? requiredShipmentValidator = null)
    {
        await RequireRetainedAsync(database, ct, requiredShipmentValidator);
        foreach (var name in FulfillmentStockSourceSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw FulfillmentStockSourceRecords.Unavailable();
            if (!AccountContactStorage.SafeDefinition(definition, FulfillmentStockSourceSchemas.Validator(name))) throw FulfillmentStockSourceRecords.Unavailable();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var keys = FulfillmentStockSourceSchemas.Keys(name);
            if (!OrderStockSourceStorage.SafeIndexes(indexes, keys) || keys.Any(key => !indexes.Any(row => AccountContactStorage.Matches(row, key))))
                throw FulfillmentStockSourceRecords.Unavailable();
        }
    }
}
