using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Procurement;

internal static class SupplierDocumentPdfMatchStorage
{
    internal static async Task RequireAsync(IMongoDatabase database, string name, CancellationToken ct, bool requireAnnotated = false)
    {
        if (name != SupplierDocumentBson.Matches) {
            await InventoryRepository.RequireStorageAsync(database, name, SupplierDocumentSchemas.Validator(name), SupplierDocumentSchemas.Keys(name), ct);
            return;
        }
        var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw SupplierDocumentBson.Broken();
        var validator = definition.GetValue("options", new BsonDocument()).AsBsonDocument.GetValue("validator", new BsonDocument()).AsBsonDocument;
        var current = SupplierDocumentPdfMatchSchemas.Validator(name);
        var previous = ProcurementQualityValuationSchemas.PrePdfDocumentValidator(name);
        var legacy = SupplierDocumentSchemas.Validator(name);
        var required = InventorySchemas.Contains(validator, current) ? current
            : !requireAnnotated && InventorySchemas.Contains(validator, previous) ? previous
            : !requireAnnotated && InventorySchemas.Contains(validator, legacy) ? legacy : throw SupplierDocumentBson.Broken();
        if (!AccountContactStorage.SafeDefinition(definition, required)) throw SupplierDocumentBson.Broken();
        await InventoryRepository.RequireStorageAsync(database, name, required, SupplierDocumentSchemas.Keys(name), ct);
        var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
        if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression"))) throw SupplierDocumentBson.Broken();
        if (InventorySchemas.Contains(validator, previous) || InventorySchemas.Contains(validator, current))
            foreach (var (table, indexName, key) in ProcurementQualityValuationSchemas.LookupIndexes.Where(value => value.Table == name))
                if (table != name || !indexes.Any(index => index["name"] == indexName && index["key"] == key &&
                    !index.GetValue("unique", false).AsBoolean && !InvoiceStorage.Unsafe(index) && !index.Contains("partialFilterExpression")))
                    throw SupplierDocumentBson.Broken();
    }
}
