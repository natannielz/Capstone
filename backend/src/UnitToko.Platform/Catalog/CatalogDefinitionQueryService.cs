using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Catalog;

/// <summary>Joins package facts only inside the existing current catalog-entitlement transaction.</summary>
public sealed class CatalogDefinitionQueryService(IMongoDatabase database, AuthorizationStore authorization, IClock clock)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private readonly CatalogRepository repository = new(database);
    private readonly CatalogApprovedPriceReader prices = new(database);
    private readonly CatalogVisibilityStore visibility = new(database, authorization, clock);

    /// <summary>Current selected catalog facts for an owning order transaction. No stock or commercial commitment.</summary>
    internal Task<ImmutableArray<CatalogPublishedVariant>> ReadSelectedAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        IClientSessionHandle tx, GrantScope buyingScope, ImmutableArray<long> skuIds,
        Func<CancellationToken, Task> completeOwner, CancellationToken ct)
    {
        if (skuIds.IsDefaultOrEmpty || skuIds.Length > 100 || skuIds.Any(id => id <= 0) || skuIds.Distinct().Count() != skuIds.Length)
            throw new DomainValidationException("invalid_order_quote", "Choose up to 100 distinct packages");
        return visibility.ReadWithinAsync(actor, snapshot, tx, F.In("_id", skuIds) & F.Eq("active", true), buyingScope,
            async (session, filter, token) => {
                var rows = await repository.Rows("skus").Find(session, filter).Limit(101).ToListAsync(token);
                if (rows.Count != skuIds.Length) throw new AuthorizationDeniedException("order_package_unavailable");
                var items = ImmutableArray.CreateBuilder<CatalogPublishedVariant>();
                foreach (var row in rows) items.Add(await JoinAsync(session, row, token) ?? throw new AuthorizationDeniedException("order_package_unavailable"));
                await completeOwner(token);
                return items.ToImmutable();
            }, ct);
    }
    public Task<CatalogPage<CatalogPublishedVariant>> QueryAsync(ActorContext actor, int offset, int limit, string? search, CancellationToken cancellationToken = default)
    {
        AuthorizationStore.ValidatePage(offset, limit); var selector = F.Eq("active", true) & CatalogAuthoringService.SearchFilter("displayName", search);
        return visibility.ReadCurrentAsync(actor, selector, async (tx, filter, ct) =>
        {
            // Filter publication authority first, then join only the current active catalog hierarchy before counts/pages.
            var pipeline = VisiblePipeline(filter);
            pipeline.Add(new("$facet", new BsonDocument
            {
                { "count", new BsonArray { new BsonDocument("$count", "total") } },
                { "page", new BsonArray { new BsonDocument("$sort", new BsonDocument("_id", 1)), new BsonDocument("$skip", offset), new BsonDocument("$limit", limit), new BsonDocument("$project", new BsonDocument("_id", 1)) } }
            }));
            var selection = await repository.Rows("skus").Aggregate<BsonDocument>(tx, pipeline).SingleAsync(ct);
            var total = selection["count"].AsBsonArray.Count == 0 ? 0L : selection["count"][0]["total"].ToInt64();
            var ids = selection["page"].AsBsonArray.Select(row => row["_id"].AsInt64).ToArray();
            // Fetch actual heads, retaining the authority filter, so all returned joins can be fenced together.
            var rows = await repository.Rows("skus").Find(tx, filter & F.In("_id", ids)).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
            var result = ImmutableArray.CreateBuilder<CatalogPublishedVariant>();
            foreach (var row in rows)
            {
                var item = await JoinAsync(tx, row, ct);
                if (item is null) throw CatalogRepository.Invalid(); result.Add(item);
            }
            if (result.Count != ids.Length) throw CatalogRepository.Invalid();
            return new CatalogPage<CatalogPublishedVariant>(total, result.ToImmutable());
        }, cancellationToken);
    }
    /// <summary>Request-only picker: the chosen buyer branch narrows publication entitlement in this transaction.</summary>
    internal Task<CatalogPage<CatalogPublishedVariant>> QueryForBranchAsync(ActorContext actor, AuthorizationSnapshot snapshot,
        IClientSessionHandle tx, GrantScope buyingScope, int offset, int limit, string? search,
        Func<CancellationToken, Task> completeOwner, CancellationToken ct)
    {
        AuthorizationStore.ValidatePage(offset, limit);
        var selector = F.Eq("active", true) & CatalogAuthoringService.SearchFilter("displayName", search);
        return visibility.ReadWithinAsync(actor, snapshot, tx, selector, buyingScope, async (session, filter, token) => {
            var pipeline = VisiblePipeline(filter);
            pipeline.Add(new("$facet", new BsonDocument {
                { "count", new BsonArray { new BsonDocument("$count", "total") } },
                { "page", new BsonArray { new BsonDocument("$sort", new BsonDocument("_id", 1)), new BsonDocument("$skip", offset),
                    new BsonDocument("$limit", limit), new BsonDocument("$project", new BsonDocument("_id", 1)) } } }));
            var selection = await repository.Rows("skus").Aggregate<BsonDocument>(session, pipeline).SingleAsync(token);
            var total = selection["count"].AsBsonArray.Count == 0 ? 0L : selection["count"][0]["total"].ToInt64();
            var ids = selection["page"].AsBsonArray.Select(row => row["_id"].AsInt64).ToArray();
            var rows = await repository.Rows("skus").Find(session, filter & F.In("_id", ids)).Sort(new BsonDocument("_id", 1)).ToListAsync(token);
            var items = ImmutableArray.CreateBuilder<CatalogPublishedVariant>();
            foreach (var row in rows) items.Add(await JoinAsync(session, row, token) ?? throw CatalogRepository.Invalid());
            if (items.Count != ids.Length) throw CatalogRepository.Invalid();
            await completeOwner(token);
            return new CatalogPage<CatalogPublishedVariant>(total, items.ToImmutable());
        }, ct);
    }
    public Task<CatalogPublishedVariant?> ReadPublicAsync(ActorContext actor, string publicId, CancellationToken cancellationToken = default)
    {
        if (publicId is null || !Regex.IsMatch(publicId, "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\\z", RegexOptions.CultureInvariant))
            throw new DomainValidationException("invalid_public_id", "A canonical UUID version 4 is required");
        return visibility.ReadCurrentAsync<CatalogPublishedVariant?>(actor, F.Eq("publicId", publicId) & F.Eq("active", true), async (tx, filter, ct) =>
        {
            var rows = await repository.Rows("skus").Find(tx, filter).Limit(2).ToListAsync(ct);
            if (rows.Count > 1) throw CatalogRepository.Invalid("ambiguous_catalog_identifier");
            return rows.Count == 0 ? null : await JoinAsync(tx, rows[0], ct);
        }, cancellationToken);
    }
    private async Task<CatalogPublishedVariant?> JoinAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct)
    {
        CatalogRepository.Require("skus", row);
        var definition = await repository.GetAsync(tx, "variantDefinitions", row["currentDefinitionId"].AsInt64, ct);
        var family = await repository.GetAsync(tx, "productFamilies", row["familyId"].AsInt64, ct);
        var category = await repository.GetAsync(tx, "catalogCategories", family["categoryId"].AsInt64, ct);
        if (CatalogRepository.Owner(row["authoringOwner"].AsBsonDocument) != CatalogRepository.Owner(family["authoringOwner"].AsBsonDocument) ||
            CatalogRepository.Owner(family["authoringOwner"].AsBsonDocument) != CatalogRepository.Owner(category["authoringOwner"].AsBsonDocument) || category["code"] != family["categoryCode"]) throw CatalogRepository.Invalid();
        CatalogAuthoringService.RequirePair(row, definition);
        var active = row["active"].AsBoolean && family["active"].AsBoolean && category["active"].AsBoolean;
        var price = active ? await prices.ReadAsync(tx, row, clock.UtcNow, ct) : null;
        await repository.TouchAsync(tx, "skus", row, ct);
        await repository.TouchAsync(tx, "productFamilies", family, ct);
        await repository.TouchAsync(tx, "catalogCategories", category, ct);
        if (!active) return null;
        return new(CatalogRepository.Family(family), CatalogRepository.Sku(row), CatalogRepository.Definition(definition), price);
    }
    private static List<BsonDocument> VisiblePipeline(FilterDefinition<BsonDocument> filter)
    {
        var result = new List<BsonDocument> { new("$match", (filter & CatalogSchemas.Validator("skus")).Render(new(BsonDocumentSerializer.Instance, BsonSerializer.SerializerRegistry))) };
        void Join(string from, string local, string name)
        {
            result.Add(new("$lookup", new BsonDocument { { "from", from }, { "localField", local }, { "foreignField", "_id" },
                { "pipeline", new BsonArray { new BsonDocument("$match", CatalogSchemas.Validator(from)) } }, { "as", name } }));
            result.Add(new("$unwind", "$" + name));
        }
        Join("productFamilies", "familyId", "family"); Join("catalogCategories", "family.categoryId", "category");
        Join("variantDefinitions", "currentDefinitionId", "definition");
        var predicates = new BsonArray
        {
            new BsonDocument("$eq", new BsonArray { "$_id", "$definition.skuId" }),
            new BsonDocument("$eq", new BsonArray { "$currentDefinitionId", "$definition._id" }),
            new BsonDocument("$eq", new BsonArray { "$displayName", new BsonDocument("$concat", new BsonArray { "$sku", " · ", "$definition.packageLabel" }) }),
            new BsonDocument("$eq", new BsonArray { "$organizationId", "$family.organizationId" }),
            new BsonDocument("$eq", new BsonArray { "$family.categoryCode", "$category.code" })
        };
        foreach (var field in new[] { "kind", "organizationId", "shopId", "channelId", "warehouseId" })
            foreach (var parent in new[] { "family", "category" })
                predicates.Add(new BsonDocument("$eq", new BsonArray { "$authoringOwner." + field, "$" + parent + ".authoringOwner." + field }));
        result.Add(new("$match", new BsonDocument { { "family.active", true }, { "category.active", true }, { "$expr", new BsonDocument("$and", predicates) } }));
        return result;
    }
}
