using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using S = UnitToko.Platform.Mongo.Migrations.CatalogPublicSampleSchemas;

namespace UnitToko.Platform.Catalog;

/// <summary>The signed-out catalog: the sample products staff approved for guests, one entry per product with the packages it
/// publishes now. It shows what a product is and how it is packed, nothing commercial: prices, stock, owners and buyer identifiers
/// are never read into it. No account, session or lease takes part, so anonymous traffic causes no writes.</summary>
public sealed partial class CatalogDefinitionQueryService
{
    public const int PublicPageLimit = 48, PublicSearchLimit = 128;
    private const string PublicIdPattern = "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\\z";

    public async Task<PublicCatalogPage> QueryPublicAsync(int offset, int limit, string? search, string? category = null,
        PublicCatalogSort sort = PublicCatalogSort.Added, CancellationToken cancellationToken = default)
    {
        if (offset is < 0 or > 5000 || limit is < 1 or > PublicPageLimit || search is { Length: > PublicSearchLimit } || !Enum.IsDefined(sort))
            throw new DomainValidationException("public_catalog_query_invalid", "Use a bounded public catalog search");
        var categoryCode = string.IsNullOrWhiteSpace(category) ? null : CatalogPolicy.NormalizeSkuCode(category);
        var filter = await PublicFilterAsync(F.Empty, cancellationToken);
        if (filter is null) return new(0, [], []);
        var pipeline = PublicProductPipeline(filter);
        if (!string.IsNullOrWhiteSpace(search)) pipeline.Add(new("$match", CatalogSearchWords.Match(search, "name", "packages.code", "packages.packageLabel")));
        var inCategory = new BsonDocument("$match", categoryCode is null ? new BsonDocument() : new BsonDocument("categoryCode", categoryCode));
        var order = sort == PublicCatalogSort.Name ? new BsonDocument { { "sortName", 1 }, { "_id", 1 } } : new BsonDocument("_id", -1);
        pipeline.Add(new("$facet", new BsonDocument {
            { "count", new BsonArray { inCategory, new BsonDocument("$count", "total") } },
            { "page", new BsonArray { inCategory,
                new BsonDocument("$addFields", new BsonDocument("sortName", new BsonDocument("$toLower", "$name"))),
                new BsonDocument("$sort", order), new BsonDocument("$skip", offset), new BsonDocument("$limit", limit) } },
            { "categories", new BsonArray {
                new BsonDocument("$group", new BsonDocument { { "_id", "$categoryCode" }, { "name", new BsonDocument("$first", "$categoryName") },
                    { "count", new BsonDocument("$sum", 1) } }),
                new BsonDocument("$sort", new BsonDocument("_id", 1)), new BsonDocument("$limit", 200) } } }));
        var page = await repository.Rows("skus").Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken).SingleAsync(cancellationToken);
        var total = page["count"].AsBsonArray.Count == 0 ? 0L : page["count"][0]["total"].ToInt64();
        var items = page["page"].AsBsonArray.Select(row => PublicItem(row.AsBsonDocument)).ToImmutableArray();
        var categories = page["categories"].AsBsonArray.Select(item => new PublicCatalogCategory(item["_id"].AsString, item["name"].AsString, item["count"].ToInt64()));
        return new(total, items, [.. categories]);
    }

    /// <summary>A product by its own public id or by one of its packages' public ids, so shared package links keep working.
    /// Unknown, unpublished and non-sample ids all answer null alike.</summary>
    public async Task<PublicCatalogItem?> ReadPublicItemAsync(string publicId, CancellationToken cancellationToken = default)
    {
        if (publicId is null || !Regex.IsMatch(publicId, PublicIdPattern, RegexOptions.CultureInvariant))
            throw new DomainValidationException("invalid_public_id", "A canonical UUID version 4 is required");
        var families = await repository.Rows("productFamilies").Find(F.Eq("publicId", publicId)).Project(new BsonDocument("_id", 1)).Limit(2).ToListAsync(cancellationToken);
        var packages = await repository.Rows("skus").Find(F.Eq("publicId", publicId)).Project(new BsonDocument("familyId", 1)).Limit(2).ToListAsync(cancellationToken);
        if (families.Count + packages.Count > 1) throw CatalogRepository.Invalid("ambiguous_catalog_identifier");
        var familyId = families.Count == 1 ? families[0]["_id"] : packages.Count == 1 ? packages[0]["familyId"] : null;
        if (familyId is null || !familyId.IsInt64) return null;
        var filter = await PublicFilterAsync(F.Eq("familyId", familyId), cancellationToken);
        if (filter is null) return null;
        var pipeline = PublicProductPipeline(filter); pipeline.Add(new("$limit", 2));
        var rows = await repository.Rows("skus").Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken).ToListAsync(cancellationToken);
        return rows.Count switch { 0 => null, 1 => PublicItem(rows[0]), _ => throw CatalogRepository.Invalid("ambiguous_catalog_identifier") };
    }

    /// <summary>Active packages of sample products, published now in an active assortment. Null when guests can see nothing.</summary>
    private async Task<FilterDefinition<BsonDocument>?> PublicFilterAsync(FilterDefinition<BsonDocument> selector, CancellationToken ct)
    {
        await CatalogPublicSampleBson.RequireStorageAsync(database, ct);
        var samples = await repository.Rows(S.Samples).Find(F.Empty).Limit(S.Limit + 1).ToListAsync(ct);
        if (samples.Count > S.Limit) throw CatalogPublicSampleBson.Invalid("public_catalog_limit");
        if (samples.Count == 0) return null;
        var published = await CatalogPublicSampleBson.PublishedAsync(database, null,
            samples.Select(row => CatalogRepository.Owner(row["ownerScope"].AsBsonDocument).OrganizationId!.Value), ct);
        if (published is null) return null;
        return F.And(published, F.In("familyId", samples.Select(row => row["_id"])), selector);
    }

    /// <summary>One row per product: its display facts and its published packages in creation order.</summary>
    private List<BsonDocument> PublicProductPipeline(FilterDefinition<BsonDocument> filter)
    {
        var pipeline = VisiblePipeline(filter); AddBuyerIdentifierLookup(pipeline);
        pipeline.Add(new("$addFields", new BsonDocument("primaryCode", new BsonDocument("$let", new BsonDocument {
            { "vars", new BsonDocument("primary", new BsonDocument("$filter", new BsonDocument { { "input", "$buyerIdentifiers" }, { "as", "item" },
                { "cond", new BsonDocument("$eq", new BsonArray { "$$item.isPrimary", true }) } })) },
            { "in", new BsonDocument("$ifNull", new BsonArray { new BsonDocument("$arrayElemAt", new BsonArray { "$$primary.canonicalValue", 0 }), "$sku" }) } }))));
        // Only display facts leave the database; the price and owner fields are never projected.
        pipeline.Add(new("$project", new BsonDocument { { "_id", 1 }, { "familyId", 1 }, { "publicId", 1 }, { "primaryCode", 1 },
            { "family.publicId", 1 }, { "family.name", 1 }, { "family.description", 1 }, { "category.code", 1 }, { "category.name", 1 },
            { "definition.packageLabel", 1 }, { "definition.packageKind", 1 }, { "definition.baseUnitCode", 1 }, { "definition.baseUnitsPerPackage", 1 },
            { "definition.attributes", 1 }, { "definition.minimumOrderPackages", 1 }, { "definition.orderIncrementPackages", 1 } }));
        pipeline.Add(new("$sort", new BsonDocument("_id", 1)));
        pipeline.Add(new("$group", new BsonDocument {
            { "_id", "$familyId" }, { "publicId", new BsonDocument("$first", "$family.publicId") }, { "name", new BsonDocument("$first", "$family.name") },
            { "description", new BsonDocument("$first", "$family.description") }, { "categoryCode", new BsonDocument("$first", "$category.code") },
            { "categoryName", new BsonDocument("$first", "$category.name") },
            { "packages", new BsonDocument("$push", new BsonDocument { { "publicId", "$publicId" }, { "code", "$primaryCode" },
                { "packageLabel", "$definition.packageLabel" }, { "packageKind", "$definition.packageKind" }, { "baseUnitCode", "$definition.baseUnitCode" },
                { "baseUnitsPerPackage", "$definition.baseUnitsPerPackage" }, { "attributes", "$definition.attributes" },
                { "minimumOrderPackages", "$definition.minimumOrderPackages" }, { "orderIncrementPackages", "$definition.orderIncrementPackages" } }) } }));
        return pipeline;
    }

    private static PublicCatalogItem PublicItem(BsonDocument row) => new(row["publicId"].AsString, row["name"].AsString, row["description"].AsString,
        row["categoryCode"].AsString, row["categoryName"].AsString, [.. row["packages"].AsBsonArray.Select(value => {
            var package = value.AsBsonDocument;
            return new PublicCatalogPackage(package["publicId"].AsString, package["code"].AsString, package["packageLabel"].AsString, package["packageKind"].AsString,
                package["baseUnitCode"].AsString, package["baseUnitsPerPackage"].AsInt32,
                [.. package["attributes"].AsBsonArray.Select(pair => new PublicCatalogAttribute(pair["key"].AsString, pair["value"].AsString))],
                package["minimumOrderPackages"].AsInt32, package["orderIncrementPackages"].AsInt32);
        })]);
}
