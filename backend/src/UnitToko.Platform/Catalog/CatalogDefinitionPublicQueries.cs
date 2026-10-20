using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Catalog;

/// <summary>The signed-out catalog: products listed in assortments approved as public. It shows what a product is and how it is
/// packed, nothing commercial: prices, stock, owners and buyer identifiers are never read into it. No account, session or lease
/// takes part, so anonymous traffic causes no writes.</summary>
public sealed partial class CatalogDefinitionQueryService
{
    public const int PublicPageLimit = 48;

    public async Task<PublicCatalogPage> QueryPublicAsync(int offset, int limit, string? search, string? category = null,
        PublicCatalogSort sort = PublicCatalogSort.Added, CancellationToken cancellationToken = default)
    {
        if (offset is < 0 or > 5000 || limit is < 1 or > PublicPageLimit || search is { Length: > 64 } || !Enum.IsDefined(sort))
            throw new DomainValidationException("public_catalog_query_invalid", "Use a bounded public catalog search");
        var categoryCode = string.IsNullOrWhiteSpace(category) ? null : CatalogPolicy.NormalizeSkuCode(category);
        var filter = await PublicFilterAsync(F.Empty, cancellationToken);
        if (filter is null) return new(0, [], []);
        var pipeline = PublicPipeline(filter);
        if (!string.IsNullOrWhiteSpace(search)) {
            var literal = new BsonRegularExpression(Regex.Escape(search.Trim()), "i");
            pipeline.Add(new("$match", new BsonDocument("$or", new BsonArray {
                new BsonDocument("primaryCode", literal), new BsonDocument("family.name", literal), new BsonDocument("definition.packageLabel", literal) })));
        }
        var inCategory = new BsonDocument("$match", categoryCode is null ? new BsonDocument() : new BsonDocument("category.code", categoryCode));
        var order = sort == PublicCatalogSort.Name ? new BsonDocument { { "sortName", 1 }, { "sortLabel", 1 }, { "_id", 1 } } : new BsonDocument("_id", -1);
        pipeline.Add(new("$facet", new BsonDocument {
            { "count", new BsonArray { inCategory, new BsonDocument("$count", "total") } },
            { "page", new BsonArray { inCategory,
                new BsonDocument("$addFields", new BsonDocument { { "sortName", new BsonDocument("$toLower", "$family.name") },
                    { "sortLabel", new BsonDocument("$toLower", "$definition.packageLabel") } }),
                new BsonDocument("$sort", order), new BsonDocument("$skip", offset), new BsonDocument("$limit", limit) } },
            { "categories", new BsonArray {
                new BsonDocument("$group", new BsonDocument { { "_id", "$category.code" }, { "name", new BsonDocument("$first", "$category.name") },
                    { "count", new BsonDocument("$sum", 1) } }),
                new BsonDocument("$sort", new BsonDocument("_id", 1)), new BsonDocument("$limit", 200) } } }));
        var page = await repository.Rows("skus").Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken).SingleAsync(cancellationToken);
        var total = page["count"].AsBsonArray.Count == 0 ? 0L : page["count"][0]["total"].ToInt64();
        var items = page["page"].AsBsonArray.Select(row => PublicItem(row.AsBsonDocument)).ToImmutableArray();
        var categories = page["categories"].AsBsonArray.Select(item => new PublicCatalogCategory(item["_id"].AsString, item["name"].AsString, item["count"].ToInt64()));
        return new(total, items, [.. categories]);
    }

    public async Task<PublicCatalogItem?> ReadPublicItemAsync(string publicId, CancellationToken cancellationToken = default)
    {
        if (publicId is null || !Regex.IsMatch(publicId, "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\\z", RegexOptions.CultureInvariant))
            throw new DomainValidationException("invalid_public_id", "A canonical UUID version 4 is required");
        var filter = await PublicFilterAsync(F.Eq("publicId", publicId), cancellationToken);
        if (filter is null) return null;
        var pipeline = PublicPipeline(filter); pipeline.Add(new("$limit", 2));
        var rows = await repository.Rows("skus").Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken).ToListAsync(cancellationToken);
        return rows.Count switch { 0 => null, 1 => PublicItem(rows[0]), _ => throw CatalogRepository.Invalid("ambiguous_catalog_identifier") };
    }

    /// <summary>Active products listed in an active assortment whose public head is approved and true. Null when none is public.</summary>
    private async Task<FilterDefinition<BsonDocument>?> PublicFilterAsync(FilterDefinition<BsonDocument> selector, CancellationToken ct)
    {
        await CatalogPublicShowcaseBson.RequireStorageAsync(database, ct);
        var heads = await repository.Rows(CatalogPublicShowcaseBson.Heads).Find(F.Eq("isPublic", true)).Limit(201).ToListAsync(ct);
        if (heads.Count > 200) throw CatalogPublicShowcaseBson.Invalid("public_catalog_limit");
        if (heads.Count == 0) return null;
        var assortments = await repository.Rows("catalogAssortments").Find(F.In("_id", heads.Select(head => head["_id"])) & F.Eq("status", "active")).ToListAsync(ct);
        if (assortments.Count == 0) return null;
        var clauses = assortments.Select(CatalogRepository.Assortment).Select(row => {
            var owner = CatalogRepository.Owner(row["ownerScope"].AsBsonDocument);
            return F.And(F.Eq("organizationId", owner.OrganizationId!.Value), F.ElemMatch<BsonDocument>("catalogListings",
                F.And(F.Eq("assortmentId", row["_id"]), UnitToko.Platform.Authorization.CatalogVisibilityQuery.Owner("ownerScope", owner))));
        });
        return F.And(F.Eq("active", true), F.Or(clauses), selector);
    }

    private List<BsonDocument> PublicPipeline(FilterDefinition<BsonDocument> filter)
    {
        var pipeline = VisiblePipeline(filter); AddBuyerIdentifierLookup(pipeline);
        pipeline.Add(new("$addFields", new BsonDocument("primaryCode", new BsonDocument("$let", new BsonDocument {
            { "vars", new BsonDocument("primary", new BsonDocument("$filter", new BsonDocument { { "input", "$buyerIdentifiers" }, { "as", "item" },
                { "cond", new BsonDocument("$eq", new BsonArray { "$$item.isPrimary", true }) } })) },
            { "in", new BsonDocument("$ifNull", new BsonArray { new BsonDocument("$arrayElemAt", new BsonArray { "$$primary.canonicalValue", 0 }), "$sku" }) } }))));
        // Only display facts leave the database; the price and owner fields are never projected.
        pipeline.Add(new("$project", new BsonDocument { { "_id", 1 }, { "publicId", 1 }, { "primaryCode", 1 },
            { "family.name", 1 }, { "family.description", 1 }, { "category.code", 1 }, { "category.name", 1 },
            { "definition.packageLabel", 1 }, { "definition.packageKind", 1 }, { "definition.baseUnitCode", 1 }, { "definition.baseUnitsPerPackage", 1 },
            { "definition.attributes", 1 }, { "definition.minimumOrderPackages", 1 }, { "definition.orderIncrementPackages", 1 } }));
        return pipeline;
    }

    private static PublicCatalogItem PublicItem(BsonDocument row)
    {
        var definition = row["definition"].AsBsonDocument;
        return new(row["publicId"].AsString, row["primaryCode"].AsString, row["family"]["name"].AsString, row["family"]["description"].AsString,
            row["category"]["code"].AsString, row["category"]["name"].AsString, definition["packageLabel"].AsString, definition["packageKind"].AsString,
            definition["baseUnitCode"].AsString, definition["baseUnitsPerPackage"].AsInt32,
            [.. definition["attributes"].AsBsonArray.Select(pair => new PublicCatalogAttribute(pair["key"].AsString, pair["value"].AsString))],
            definition["minimumOrderPackages"].AsInt32, definition["orderIncrementPackages"].AsInt32);
    }
}
