using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Catalog.CatalogPriceBson;

namespace UnitToko.Platform.Catalog;

/// <summary>Only call after the owning query has selected an entitled SKU in this transaction.
/// The caller fences current catalog, entitlement, membership and session before returning.</summary>
internal sealed class CatalogApprovedPriceReader(IMongoDatabase database)
{
    private readonly CatalogPriceGraph graph = new(database);
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    internal async Task<PriceVersion?> ReadAsync(IClientSessionHandle tx, BsonDocument sku, DateTimeOffset now, CancellationToken ct)
    {
        CatalogRepository.Require("skus", sku);
        var heads = database.GetCollection<BsonDocument>(B.Heads);
        var head = await heads.Find(tx, F.Eq("_id", sku["_id"])).SingleOrDefaultAsync(ct);
        var latest = await database.GetCollection<BsonDocument>(B.Versions).Find(tx, F.Eq("skuId", sku["_id"]))
            .Sort(new BsonDocument("version", -1)).FirstOrDefaultAsync(ct);
        if (head is null) {
            if (latest is not null) throw B.Invalid();
            return null;
        }
        B.Head(head);
        if (latest is null || head["priceId"] != latest["_id"] || head["version"] != latest["version"]) throw B.Invalid();
        var price = await graph.PriceAsync(tx, sku, latest, ct);
        if (price.EffectiveAt > now || head["fence"].AsInt64 == long.MaxValue) throw B.Invalid();
        var filter = F.Eq("_id", head["_id"]) & F.Eq("priceId", head["priceId"]) & F.Eq("version", head["version"]) &
            F.Lt("fence", long.MaxValue) & new BsonDocumentFilterDefinition<BsonDocument>(CatalogPriceSchemas.Validator(B.Heads));
        if ((await heads.UpdateOneAsync(tx, filter, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1)
            throw B.Invalid("catalog_price_changed");
        return price;
    }
}
