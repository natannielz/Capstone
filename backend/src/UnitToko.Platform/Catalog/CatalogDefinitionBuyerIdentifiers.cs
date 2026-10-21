using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Catalog;

// Buyer-only projections. The original Sku.Code and all commercial owner snapshots stay unchanged.
public sealed record CatalogBuyerCode(LongId IdentifierId, long IdentifierRevision, string Code);
public sealed record CatalogBuyerIdentity(LongId SkuId, long SkuRevision, LongId DefinitionId,
    long DefinitionVersion, CatalogBuyerCode CurrentPrimary);
public sealed record CatalogBuyerVariant(CatalogPublishedVariant Variant, CatalogBuyerIdentity BuyerIdentity);
public sealed record CatalogBuyerMatchedIdentifier(LongId IdentifierId, long IdentifierRevision, string Code,
    LongId SkuId, long SkuRevision, LongId DefinitionId, long DefinitionVersion);
public sealed record CatalogBuyerCodeMatch(string RequestedCode, CatalogBuyerMatchedIdentifier MatchedIdentifier,
    CatalogBuyerVariant Variant);
/// <summary>A category chip: how many visible packages match the search in this category.</summary>
public sealed record CatalogBuyerCategoryCount(string Code, string Name, long Count);
/// <summary>One page of the buyer catalog, plus the per-category counts for the same search (ignoring the category filter).</summary>
public sealed record CatalogBuyerPage(long Total, ImmutableArray<CatalogBuyerVariant> Items, ImmutableArray<CatalogBuyerCategoryCount> Categories);
public enum CatalogBuyerSort { Added, Name }

public sealed partial class CatalogDefinitionQueryService
{
    public async Task<CatalogBuyerPage> QueryBuyerAsync(ActorContext actor, int offset, int limit,
        string? search, string? category = null, CatalogBuyerSort sort = CatalogBuyerSort.Added, CancellationToken cancellationToken = default)
    {
        AuthorizationStore.ValidatePage(offset, limit);
        if (search is not null && search.Length > 128) throw new DomainValidationException("catalog_query_invalid", "Use a bounded catalog search");
        if (!Enum.IsDefined(sort)) throw new DomainValidationException("catalog_query_invalid", "Choose a supported catalog order");
        var categoryCode = string.IsNullOrWhiteSpace(category) ? null : CatalogPolicy.NormalizeSkuCode(category);
        await CatalogIdentifierRecords.RequireStorageAsync(database, cancellationToken);
        return await visibility.ReadCurrentAsync(actor, F.Eq("active", true), async (tx, filter, ct) => {
            var pipeline = VisiblePipeline(filter); AddBuyerIdentifierLookup(pipeline);
            if (!string.IsNullOrWhiteSpace(search)) {
                // displayName contains the immutable original SKU. It must not revive a retired alias.
                pipeline.Add(new("$match", CatalogSearchWords.Match(search, "buyerIdentifiers.canonicalValue", "family.name", "definition.packageLabel")));
            }
            // Category counts cover the whole search result; the category filter narrows only the count and the page.
            var inCategory = new BsonDocument("$match", categoryCode is null ? new BsonDocument() : new BsonDocument("category.code", categoryCode));
            var order = sort == CatalogBuyerSort.Name
                ? new BsonDocument { { "sortName", 1 }, { "sortLabel", 1 }, { "_id", 1 } } : new BsonDocument("_id", 1);
            pipeline.Add(new("$facet", new BsonDocument {
                { "count", new BsonArray { inCategory, new BsonDocument("$count", "total") } },
                { "page", new BsonArray { inCategory,
                    new BsonDocument("$addFields", new BsonDocument { { "sortName", new BsonDocument("$toLower", "$family.name") },
                        { "sortLabel", new BsonDocument("$toLower", "$definition.packageLabel") } }),
                    new BsonDocument("$sort", order), new BsonDocument("$skip", offset), new BsonDocument("$limit", limit),
                    new BsonDocument("$project", new BsonDocument("_id", 1)) } },
                { "categories", new BsonArray {
                    new BsonDocument("$group", new BsonDocument { { "_id", "$category.code" }, { "name", new BsonDocument("$first", "$category.name") },
                        { "count", new BsonDocument("$sum", 1) } }),
                    new BsonDocument("$sort", new BsonDocument("_id", 1)), new BsonDocument("$limit", 200) } } }));
            var page = await repository.Rows("skus").Aggregate<BsonDocument>(tx, pipeline).SingleAsync(ct);
            var total = page["count"].AsBsonArray.Count == 0 ? 0L : page["count"][0]["total"].ToInt64();
            var ids = page["page"].AsBsonArray.Select(item => item["_id"].AsInt64).ToArray();
            var rows = (await repository.Rows("skus").Find(tx, filter & F.In("_id", ids)).ToListAsync(ct)).ToDictionary(row => row["_id"].AsInt64);
            if (rows.Count != ids.Length) throw CatalogRepository.Invalid();
            var result = ImmutableArray.CreateBuilder<CatalogBuyerVariant>();
            foreach (var id in ids) result.Add((await ReadBuyerProjectionAsync(tx, rows[id], ct)).View);
            var categories = page["categories"].AsBsonArray.Select(item =>
                new CatalogBuyerCategoryCount(item["_id"].AsString, item["name"].AsString, item["count"].ToInt64()));
            return new CatalogBuyerPage(total, result.ToImmutable(), [.. categories]);
        }, cancellationToken);
    }

    public async Task<CatalogBuyerVariant?> ReadBuyerPublicAsync(ActorContext actor, string publicId,
        CancellationToken cancellationToken = default)
    {
        if (publicId is null || !Regex.IsMatch(publicId, "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\\z", RegexOptions.CultureInvariant))
            throw new DomainValidationException("invalid_public_id", "A canonical UUID version 4 is required");
        await CatalogIdentifierRecords.RequireStorageAsync(database, cancellationToken);
        return await visibility.ReadCurrentAsync<CatalogBuyerVariant?>(actor, F.Eq("publicId", publicId) & F.Eq("active", true), async (tx, filter, ct) => {
            var pipeline = VisiblePipeline(filter); pipeline.Add(new("$limit", 2));
            var selected = await repository.Rows("skus").Aggregate<BsonDocument>(tx, pipeline).ToListAsync(ct);
            if (selected.Count > 1) throw CatalogRepository.Invalid("ambiguous_catalog_identifier");
            if (selected.Count == 0) return null;
            var row = await repository.Rows("skus").Find(tx, filter & F.Eq("_id", selected[0]["_id"])).SingleAsync(ct);
            return (await ReadBuyerProjectionAsync(tx, row, ct)).View;
        }, cancellationToken);
    }

    /// <summary>The current buyer view of packages already in a cart. A package that is no longer active or visible to this buyer
    /// is simply absent, so the cart can point at the one line that needs attention instead of refusing the whole read.</summary>
    public async Task<ImmutableArray<CatalogBuyerVariant>> ReadBuyerSkusAsync(ActorContext actor, ImmutableArray<long> skuIds,
        CancellationToken cancellationToken = default)
    {
        if (skuIds.IsDefaultOrEmpty || skuIds.Length > 100 || skuIds.Any(id => id <= 0) || skuIds.Distinct().Count() != skuIds.Length)
            throw new DomainValidationException("invalid_cart_sku_query", "Choose up to 100 distinct packages");
        await CatalogIdentifierRecords.RequireStorageAsync(database, cancellationToken);
        return await visibility.ReadCurrentAsync(actor, F.In("_id", skuIds) & F.Eq("active", true), async (tx, filter, ct) => {
            var pipeline = VisiblePipeline(filter); pipeline.Add(new("$project", new BsonDocument("_id", 1)));
            var ids = (await repository.Rows("skus").Aggregate<BsonDocument>(tx, pipeline).ToListAsync(ct)).Select(row => row["_id"].AsInt64).ToArray();
            var rows = await repository.Rows("skus").Find(tx, filter & F.In("_id", ids)).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
            if (rows.Count != ids.Length) throw CatalogRepository.Invalid();
            var items = ImmutableArray.CreateBuilder<CatalogBuyerVariant>();
            foreach (var row in rows) items.Add((await ReadBuyerProjectionAsync(tx, row, ct)).View);
            return items.ToImmutable();
        }, cancellationToken);
    }

    /// <summary>Exact active internal identifiers only. No supplier/GTIN namespace, fuzzy match, partial batch or quantity merge.</summary>
    public async Task<ImmutableArray<CatalogBuyerCodeMatch>> ReadExactBuyerIdentifiersAsync(ActorContext actor,
        ImmutableArray<string> codes, CancellationToken cancellationToken = default)
    {
        if (codes.IsDefaultOrEmpty || codes.Length > 100)
            throw new DomainValidationException("invalid_bulk_sku_query", "Choose up to 100 distinct internal SKU codes");
        var normalized = codes.Select(CatalogPolicy.NormalizeSkuCode).ToImmutableArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new DomainValidationException("invalid_bulk_sku_query", "Choose each internal SKU only once");
        await CatalogIdentifierRecords.RequireStorageAsync(database, cancellationToken);
        return await visibility.ReadCurrentAsync(actor, F.Eq("active", true), async (tx, filter, ct) => {
            // Publication authority precedes identifier lookup, including counts and the bounded selection.
            var pipeline = VisiblePipeline(filter); AddBuyerIdentifierLookup(pipeline);
            pipeline.Add(new("$match", new BsonDocument("buyerIdentifiers.canonicalValue", new BsonDocument("$in", new BsonArray(normalized)))));
            pipeline.Add(new("$project", new BsonDocument("_id", 1))); pipeline.Add(new("$limit", 101));
            var selected = await repository.Rows("skus").Aggregate<BsonDocument>(tx, pipeline).ToListAsync(ct);
            if (selected.Count != normalized.Length) throw new AuthorizationDeniedException("order_package_unavailable");
            var ids = selected.Select(row => row["_id"].AsInt64).ToArray();
            var rows = await repository.Rows("skus").Find(tx, filter & F.In("_id", ids)).ToListAsync(ct);
            if (rows.Count != ids.Length) throw new AuthorizationDeniedException("order_package_unavailable");
            var matches = new Dictionary<string, CatalogBuyerCodeMatch>(StringComparer.Ordinal);
            foreach (var row in rows) {
                var projection = await ReadBuyerProjectionAsync(tx, row, ct);
                var aliases = projection.Codes.Where(item => normalized.Contains(item.Code, StringComparer.Ordinal)).ToArray();
                // Two accepted aliases for one physical SKU are never silently added or merged.
                if (aliases.Length != 1) throw new AuthorizationDeniedException("order_package_unavailable");
                var alias = aliases[0]; var identity = projection.View.BuyerIdentity;
                var matched = new CatalogBuyerMatchedIdentifier(alias.IdentifierId, alias.IdentifierRevision, alias.Code,
                    identity.SkuId, identity.SkuRevision, identity.DefinitionId, identity.DefinitionVersion);
                if (!matches.TryAdd(alias.Code, new(alias.Code, matched, projection.View)))
                    throw new AuthorizationDeniedException("order_package_unavailable");
            }
            if (matches.Count != normalized.Length || normalized.Any(code => !matches.ContainsKey(code)))
                throw new AuthorizationDeniedException("order_package_unavailable");
            return normalized.Select(code => matches[code]).ToImmutableArray();
        }, cancellationToken);
    }

    private void AddBuyerIdentifierLookup(List<BsonDocument> pipeline)
    {
        pipeline.Add(new("$lookup", new BsonDocument {
            { "from", CatalogIdentifierRecords.Collection }, { "let", new BsonDocument { { "sku", "$_id" }, { "organization", "$organizationId" } } },
            { "pipeline", new BsonArray { new BsonDocument("$match", new BsonDocument("$and", new BsonArray {
                CatalogIdentifierSchemas.Validator(CatalogIdentifierRecords.Collection),
                new BsonDocument { { "kind", "skuCode" }, { "namespace", "internal" }, { "retiredAt", BsonNull.Value },
                    { "effectiveAt", new BsonDocument("$lte", clock.UtcNow.UtcDateTime) },
                    { "$expr", new BsonDocument("$and", new BsonArray {
                        new BsonDocument("$eq", new BsonArray { "$skuId", "$$sku" }),
                        new BsonDocument("$eq", new BsonArray { "$organizationId", "$$organization" }) }) } } })) } },
            { "as", "buyerIdentifiers" } }));
    }

    private async Task<(CatalogBuyerVariant View, CatalogBuyerCode[] Codes)> ReadBuyerProjectionAsync(
        IClientSessionHandle tx, BsonDocument row, CancellationToken ct)
    {
        var definition = await repository.GetAsync(tx, "variantDefinitions", row["currentDefinitionId"].AsInt64, ct);
        var proof = await new CatalogIdentifierGraph(database).ReadSkuAsync(tx, row, definition, ct);
        var identifiers = proof.Identifiers.Select(CatalogIdentifierRecords.Read)
            .Where(item => item.Kind == "skuCode" && item.Namespace == "internal" && item.RetiredAt is null && item.EffectiveAt <= clock.UtcNow).ToArray();
        var primary = identifiers.SingleOrDefault(item => item.IsPrimary) ?? throw CatalogIdentifierRecords.Broken();
        var item = await JoinAsync(tx, row, ct) ?? throw new AuthorizationDeniedException("order_package_unavailable");
        // Includes the selected mapping, its immutable assignment history/audit/receipt and physical definition.
        // This runs after the catalog/price awaits; visibility closes current authority/publication afterward.
        await proof.CompleteAsync(tx, ct);
        CatalogBuyerCode Code(CatalogIdentifier value) => new(value.Id, value.Revision, value.CanonicalValue);
        return (new(item, new(item.Sku.Id, item.Sku.Revision, item.Definition.Id, item.Definition.Version, Code(primary))), identifiers.Select(Code).ToArray());
    }
}
