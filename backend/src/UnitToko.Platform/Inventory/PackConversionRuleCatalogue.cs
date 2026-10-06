using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Catalog;

namespace UnitToko.Platform.Inventory;

internal sealed class PackConversionRuleCatalogue(IMongoDatabase database)
{
    private readonly CatalogRepository catalog = new(database);
    private static BsonDocument Semantic(BsonDocument row) { var copy = row.DeepClone().AsBsonDocument; if (copy.Contains("fence")) copy["fence"] = 0L; return copy; }
    internal async Task<BsonDocument> CurrentAsync(IClientSessionHandle tx, PackConversionRuleInput input, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx);
        async Task<BsonDocument> Package(long skuId, long revision, long definitionId) {
            var sku = await catalog.GetAsync(tx, "skus", skuId, ct); var owner = CatalogRepository.Owner(sku["authoringOwner"].AsBsonDocument);
            if (!sku["active"].AsBoolean || sku["revision"] != revision || sku["currentDefinitionId"] != definitionId ||
                !UnitToko.Modules.Organizations.ScopePolicy.ContainsScope(owner, input.Location)) throw PackConversionRuleBson.Changed();
            var family = await catalog.GetAsync(tx, "productFamilies", sku["familyId"].AsInt64, ct);
            var category = await catalog.GetAsync(tx, "catalogCategories", family["categoryId"].AsInt64, ct);
            var definition = await catalog.GetAsync(tx, "variantDefinitions", definitionId, ct);
            if (!family["active"].AsBoolean || !category["active"].AsBoolean || CatalogRepository.Owner(family["authoringOwner"].AsBsonDocument) != owner ||
                CatalogRepository.Owner(category["authoringOwner"].AsBsonDocument) != owner || family["categoryCode"] != category["code"] ||
                definition["skuId"] != skuId) throw PackConversionRuleBson.Changed();
            return new() { { "sku", Semantic(sku) }, { "family", Semantic(family) }, { "category", Semantic(category) }, { "definition", Semantic(definition) } };
        }
        var pair = new BsonDocument { { "source", await Package(input.SourceSkuId, input.SourceSkuRevision, input.SourceDefinitionId) },
            { "destination", await Package(input.DestinationSkuId, input.DestinationSkuRevision, input.DestinationDefinitionId) } };
        _ = Quantities(input, pair, 1, 1); return pair;
    }
    internal static ConversionQuantities Quantities(PackConversionRuleInput input, BsonDocument pair, long ruleId, int steps) {
        StockPackageDefinition Package(string name) {
            var value = pair[name].AsBsonDocument; var sku = CatalogRepository.Sku(value["sku"].AsBsonDocument);
            var definition = CatalogRepository.Definition(value["definition"].AsBsonDocument);
            return new(sku, definition, definition);
        }
        var source = Package("source"); var destination = Package("destination");
        if (source.StoredDefinition.PackageKind != "sealed" || destination.StoredDefinition.PackageKind != "loose") throw PackConversionRulePolicy.Invalid();
        return PackConversionPolicy.Quantities(new(new(ruleId), 1, new(input.SourceSkuId), new(input.SourceDefinitionId),
            new(input.DestinationSkuId), new(input.DestinationDefinitionId), input.SourcePackagesPerStep, input.DestinationPackagesPerStep), source, destination, steps);
    }
    internal async Task HistoricalAsync(IClientSessionHandle tx, PackConversionRuleInput input, BsonDocument pair, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx);
        foreach (var side in new[] { "source", "destination" }) {
            var snapshot = pair[side].AsBsonDocument;
            foreach (var (table, field) in new[] { ("skus", "sku"), ("productFamilies", "family"), ("catalogCategories", "category") }) {
                var stored = CatalogRepository.Require(table, snapshot[field].AsBsonDocument); var current = await catalog.GetAsync(tx, table, stored["_id"].AsInt64, ct);
                if (current["revision"].AsInt64 < stored["revision"].AsInt64 || current["organizationId"] != stored["organizationId"] ||
                    current["authoringOwner"] != stored["authoringOwner"]) throw PackConversionRuleBson.Broken();
                if (table == "skus" && (current["sku"] != stored["sku"] || current["physicalItemKey"] != stored["physicalItemKey"] ||
                    current["familyId"] != stored["familyId"])) throw PackConversionRuleBson.Broken();
            }
            var definition = snapshot["definition"].AsBsonDocument;
            if (Semantic(await catalog.GetAsync(tx, "variantDefinitions", definition["_id"].AsInt64, ct)) != definition) throw PackConversionRuleBson.Broken();
            if (snapshot["sku"]["familyId"] != snapshot["family"]["_id"] || snapshot["family"]["categoryId"] != snapshot["category"]["_id"] ||
                snapshot["family"]["categoryCode"] != snapshot["category"]["code"] || snapshot["sku"]["currentDefinitionId"] != definition["_id"] ||
                !UnitToko.Modules.Organizations.ScopePolicy.ContainsScope(CatalogRepository.Owner(snapshot["sku"]["authoringOwner"].AsBsonDocument), input.Location)) throw PackConversionRuleBson.Broken();
        }
        _ = Quantities(input, pair, 1, 1);
    }
    internal async Task FenceAsync(IClientSessionHandle tx, PackConversionRuleInput input, BsonDocument expected, CancellationToken ct) {
        if (await CurrentAsync(tx, input, ct) != expected) throw PackConversionRuleBson.Changed();
        var seen = new HashSet<(string, long)>();
        foreach (var side in new[] { "source", "destination" }) foreach (var (table, field) in new[] {
            ("skus", "sku"), ("productFamilies", "family"), ("catalogCategories", "category"), ("variantDefinitions", "definition") }) {
            var snapshot = expected[side][field].AsBsonDocument; var id = snapshot["_id"].AsInt64;
            if (!seen.Add((table, id))) continue; var current = await catalog.GetAsync(tx, table, id, ct);
            if (Semantic(current) != snapshot) throw PackConversionRuleBson.Changed();
            if (table == "variantDefinitions") {
                if ((await database.GetCollection<BsonDocument>(table).DeleteOneAsync(tx, current, cancellationToken: ct)).DeletedCount != 1) throw PackConversionRuleBson.Changed();
                await database.GetCollection<BsonDocument>(table).InsertOneAsync(tx, current, cancellationToken: ct);
            } else await catalog.TouchAsync(tx, table, current, ct);
        }
        if (await CurrentAsync(tx, input, ct) != expected) throw PackConversionRuleBson.Changed();
    }
}
