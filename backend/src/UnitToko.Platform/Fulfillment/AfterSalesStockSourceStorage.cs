using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;

namespace UnitToko.Platform.Fulfillment;
internal static class AfterSalesStockSourceStorage
{
    internal static async Task RequireRetainedAsync(IMongoDatabase database, CancellationToken ct)
    {
        await FulfillmentStockSourceStorage.RequireAsync(database, ct);
        await InventoryRepository.RequireStorageAsync(database, AfterSalesCaseRecords.Collection, AfterSalesCaseSchemas.Validator(), AfterSalesCaseSchemas.Key(), ct);
        await InventoryRepository.RequireStorageAsync(database, AfterSalesDecisionRecords.Collection, AfterSalesDecisionSchemas.Validator(), AfterSalesDecisionSchemas.Key(), ct);
        await InventoryRepository.RequireStorageAsync(database, ReturnQuarantineLedger.Collection, ReturnQuarantineSchemas.Validator(), [ReturnQuarantineSchemas.Key(), ReturnQuarantineSchemas.RequestKey()], ct);
        await InventoryRepository.RequireStorageAsync(database, ReturnInspectionRecords.Collection, ReturnInspectionSchemas.Validator(), ReturnInspectionSchemas.Key(), ct);
        await InventoryRepository.RequireStorageAsync(database, ReturnResolutionRecords.Collection, ReturnResolutionSchemas.Validator(), ReturnResolutionSchemas.Key(), ct);
    }
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct)
    {
        await RequireRetainedAsync(database, ct);
        await InventoryRepository.RequireStorageAsync(database, ReturnedLotVerificationRecords.Collection, ReturnedLotVerificationSchemas.Validator(), ReturnedLotVerificationSchemas.Key(), ct);
        foreach (var name in AfterSalesStockSourceSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw AfterSalesStockSourceRecords.Unavailable();
            if (!AccountContactStorage.SafeDefinition(definition, AfterSalesStockSourceSchemas.Validator(name))) throw AfterSalesStockSourceRecords.Unavailable();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct); var keys = AfterSalesStockSourceSchemas.Keys(name);
            if (!OrderStockSourceStorage.SafeIndexes(indexes, keys) || keys.Any(key => !indexes.Any(row => AccountContactStorage.Matches(row, key)))) throw AfterSalesStockSourceRecords.Unavailable();
        }
    }
}
