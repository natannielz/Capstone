using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Inventory;

internal static class ReconciliationStockSourceStorage
{
    internal static async Task RequireRetainedAsync(IMongoDatabase database, CancellationToken ct)
    {
        foreach (var name in StockSourceSetupSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, StockSourceSetupSchemas.Validator(name), StockSourceSetupSchemas.Key(name), ct);
        foreach (var name in StockSourceReconciliationSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, StockSourceReconciliationSchemas.Validator(name), StockSourceReconciliationSchemas.Keys(name), ct);
    }
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct)
    {
        await RequireRetainedAsync(database, ct);
        foreach (var name in ReconciliationStockSourceSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw ReconciliationStockSourceRecords.Unavailable();
            if (!AccountContactStorage.SafeDefinition(definition, ReconciliationStockSourceSchemas.Validator(name))) throw ReconciliationStockSourceRecords.Unavailable();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var keys = ReconciliationStockSourceSchemas.Keys(name);
            if (!OrderStockSourceStorage.SafeIndexes(indexes, keys) || keys.Any(key => !indexes.Any(row => AccountContactStorage.Matches(row, key))))
                throw ReconciliationStockSourceRecords.Unavailable();
        }
    }
}
