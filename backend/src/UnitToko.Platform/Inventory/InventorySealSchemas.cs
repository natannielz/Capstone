using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Inventory;

/// <summary>Inventory-owned provenance seal stores (migration 116): completed pack conversions and stock-release
/// decisions. Same strict shape as every provenance seal; each store admits only its own kind.</summary>
internal static class InventorySealSchemas
{
    internal const string Conversions = "inventoryConversionSeals", ConversionKind = "inventory.packConversion";
    internal const string Releases = "inventoryReleaseSeals", ReleaseKind = "inventory.releaseDecision";
    internal static readonly (string Collection, string Kind)[] Stores = [(Conversions, ConversionKind), (Releases, ReleaseKind)];

    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct)
    {
        foreach (var (name, kind) in Stores) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            var indexes = definition is null ? [] : (await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (definition is null || !AccountContactStorage.SafeDefinition(definition, ProvenanceSealSchemas.Validator(kind)) ||
                !AccountContactStorage.SafeIndexes(indexes, ProvenanceSealSchemas.Keys) ||
                ProvenanceSealSchemas.Keys.Any(key => !indexes.Any(index => AccountContactStorage.Matches(index, key))))
                throw new InvalidOperationException("Inventory provenance seal storage is unavailable");
        }
    }
}
