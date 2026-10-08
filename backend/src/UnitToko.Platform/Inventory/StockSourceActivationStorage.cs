using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

public sealed partial class StockSourceSetupService
{
    private async Task ActivationStorageAsync(CancellationToken ct) {
        await OperatingPolicyStorageAsync(ct); await ActivationOwnerStorageAsync(ct);
    }
    private async Task ActivationOwnerStorageAsync(CancellationToken ct) {
        foreach (var name in StockSourceActivationSchemas.Collections) {
            var keys = StockSourceActivationSchemas.Keys(name);
            var indexes = await InventoryRepository.RequireStorageAsync(database, name, StockSourceActivationSchemas.Validator(name),
                [new BsonDocument("_id", 1), .. keys.Where(key => key.Unique).Select(key => key.Key)], ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression")) ||
                keys.Any(key => !indexes.Any(index => AccountContactStorage.Matches(index, key)))) throw StockSourceActivationBson.Unavailable();
        }
    }
}
