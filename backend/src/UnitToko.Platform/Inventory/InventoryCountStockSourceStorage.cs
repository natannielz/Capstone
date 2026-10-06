using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

internal static class InventoryCountStockSourceStorage
{
    internal static async Task RequireCoverageAsync(IMongoDatabase database, CancellationToken ct) {
        foreach (var name in InventoryCountCoverageSchemas.Collections) {
            var keys = InventoryCountCoverageSchemas.Keys(name);
            await InventoryRepository.RequireStorageAsync(database, name, InventoryCountCoverageSchemas.Validator(name),
                [new BsonDocument("_id", 1), .. keys.Where(key => key.Unique).Select(key => key.Key)], ct);
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(row => InvoiceStorage.Unsafe(row) || row.Contains("partialFilterExpression")) ||
                keys.Any(key => !indexes.Any(row => AccountContactStorage.Matches(row, key)))) throw InventoryCountStockSourceRecords.Unavailable();
        }
    }
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct) {
        await InventoryCountStockSourceMigration.RequireCountStorageAsync(database, ct, requireBound: true);
        foreach (var name in InventoryCountStockSourceSchemas.Collections) {
            var keys = InventoryCountStockSourceSchemas.Keys(name);
            await InventoryRepository.RequireStorageAsync(database, name, InventoryCountStockSourceSchemas.Validator(name),
                [new BsonDocument("_id", 1), .. keys.Where(key => key.Unique).Select(key => key.Key)], ct);
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(row => InvoiceStorage.Unsafe(row) || row.Contains("partialFilterExpression")) ||
                keys.Any(key => !indexes.Any(row => AccountContactStorage.Matches(row, key))))
                throw InventoryCountStockSourceRecords.Unavailable();
        }
    }
}
