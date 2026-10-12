using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Ordering;

internal static class OrderStockSourceStorage
{
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct, BsonDocument? requiredShipmentValidator = null)
    {
        foreach (var name in OrderStockSourceSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw OrderStockSourceRecords.Unavailable();
            // Historical migration callers explicitly request the original shipment contract.
            var expectedValidator = requiredShipmentValidator is null ? OrderSubstitutionSchemas.StockSourceValidator(name) : OrderStockSourceSchemas.Validator(name);
            if (!AccountContactStorage.SafeDefinition(definition, expectedValidator)) throw OrderStockSourceRecords.Unavailable();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var required = OrderStockSourceSchemas.Keys(name);
            if (!SafeIndexes(indexes, required) || required.Any(key => !indexes.Any(row => AccountContactStorage.Matches(row, key))))
                throw OrderStockSourceRecords.Unavailable();
        }
        foreach (var name in OrderSubmissionSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name,
                requiredShipmentValidator is null ? OrderLineageSchemas.OrderValidator(name) : OrderSubmissionSchemas.Validator(name),
                requiredShipmentValidator is null ? OrderLineageSchemas.OrderKey(name) : OrderSubmissionSchemas.Key(name), ct);
        foreach (var name in StockSourceSetupSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, StockSourceSetupSchemas.Validator(name), StockSourceSetupSchemas.Key(name), ct);
        await ShipmentDispatchStorage.RequireAsync(database, ct, requiredShipmentValidator);
    }
    internal static bool SafeIndexes(IReadOnlyList<BsonDocument> rows, (string Name, BsonDocument Key, bool Unique)[] required) =>
        rows.Any(row => row.GetValue("name", "") == "_id_" && row.GetValue("key", BsonNull.Value) == new BsonDocument("_id", 1)) &&
        rows.All(row => !InvoiceStorage.Unsafe(row) && !row.Contains("partialFilterExpression") &&
            (!row.GetValue("unique", false).AsBoolean || row["name"] == "_id_" || required.Any(key => AccountContactStorage.Matches(row, key)))) &&
        required.All(key => rows.Where(row => row.GetValue("name", "") == key.Name || row.GetValue("key", BsonNull.Value) == key.Key)
            .All(row => AccountContactStorage.Matches(row, key)));
}
