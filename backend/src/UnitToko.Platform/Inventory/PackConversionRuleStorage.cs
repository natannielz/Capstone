using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

internal static class PackConversionRuleStorage
{
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct) {
        await InventoryCountStockSourceStorage.RequireAsync(database, ct);
        foreach (var name in PackConversionRuleSchemas.Collections) {
            var keys = PackConversionRuleSchemas.Keys(name);
            await InventoryRepository.RequireStorageAsync(database, name, PackConversionRuleSchemas.Validator(name),
                [new BsonDocument("_id", 1), .. keys.Where(key => key.Unique).Select(key => key.Key)], ct);
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression")) ||
                keys.Any(key => !indexes.Any(index => AccountContactStorage.Matches(index, key)))) throw PackConversionRuleBson.Broken();
        }
    }
}
