using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Catalog;

/// <summary>Staff may propose only current packages in their authorized fulfillment owner.
/// This never assumes the buyer's identity or grants publication, commercial or stock authority.
/// Acceptance reads the selection again through the buyer catalog and admission owners.</summary>
internal sealed class CatalogSubstitutionReader(IMongoDatabase database, IClock clock)
{
    internal Task<OrderQuotedLine> ReadAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        GrantScope owner, OrderPackageSelection selection, Func<CancellationToken, Task> completeOwner, CancellationToken ct) =>
        ReadCoreAsync(actor, authority, tx, owner, selection.SkuId.Value, selection, completeOwner, ct);

    internal async Task<(OrderQuotedLine[] Items, bool HasMore)> QueryAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        GrantScope owner, string? search, long? afterId, Func<CancellationToken, Task> completeOwner, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx);
        var query = ScopePolicy.Compile(actor, authority, Permissions.SubstitutionPropose, clock.UtcNow); AuthorizationStore.Require(query);
        if (!ScopePolicy.IsScopeActive(owner, authority.Directory) || !query.Matches(CatalogRepository.Resource(owner)) || afterId is <= 0)
            throw new AuthorizationDeniedException("scope_denied");
        var filter = CatalogRepository.OwnerFilter("authoringOwner", owner) & Builders<BsonDocument>.Filter.Eq("active", true);
        if (afterId is { } after) filter &= Builders<BsonDocument>.Filter.Gt("_id", after);
        var pipeline = CatalogAuthoringService.SubstitutionChoicePipeline(filter);
        if (!string.IsNullOrWhiteSpace(search)) {
            if (search.Trim() != search || search.Length > 128 || search.Any(char.IsControl)) throw CatalogRepository.Invalid();
            pipeline.Add(new("$match", new BsonDocument("$or", new BsonArray(new[] { "sku", "family.name", "definition.packageLabel" }
                .Select(field => new BsonDocument(field, new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(search), "i")))))));
        }
        // A genuinely unpriced package is not a proposal option. Either a head or a
        // retained price makes it a candidate; the owner below rejects orphan/malformed history.
        pipeline.Add(new("$lookup", new BsonDocument { { "from", CatalogPriceBson.Heads }, { "localField", "_id" }, { "foreignField", "_id" }, { "as", "priceHead" } }));
        pipeline.Add(new("$lookup", new BsonDocument { { "from", CatalogPriceBson.Versions }, { "localField", "_id" }, { "foreignField", "skuId" },
            { "pipeline", new BsonArray { new BsonDocument("$limit", 1) } }, { "as", "priceVersion" } }));
        pipeline.Add(new("$match", new BsonDocument("$or", new BsonArray { new BsonDocument("priceHead.0", new BsonDocument("$exists", true)),
            new BsonDocument("priceVersion.0", new BsonDocument("$exists", true)) })));
        pipeline.Add(new("$sort", new BsonDocument("_id", 1))); pipeline.Add(new("$limit", 26));
        var rows = await database.GetCollection<BsonDocument>("skus").Aggregate<BsonDocument>(tx, pipeline).ToListAsync(ct);
        var items = new List<OrderQuotedLine>();
        foreach (var row in rows.Take(25)) items.Add(await ReadCoreAsync(actor, authority, tx, owner, row["_id"].AsInt64, null, completeOwner, ct));
        await completeOwner(ct); return (items.ToArray(), rows.Count > 25);
    }
    private async Task<OrderQuotedLine> ReadCoreAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        GrantScope owner, long skuId, OrderPackageSelection? selection, Func<CancellationToken, Task> completeOwner, CancellationToken ct) {
        new InventoryRepository(database).RequireSession(tx);
        var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        var at = clock.UtcNow;
        void Current() {
            new InventoryRepository(database).RequireSession(tx);
            if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || clock.UtcNow < at)
                throw new AuthorizationDeniedException("order_substitution_unavailable");
            var query = ScopePolicy.Compile(actor, authority, Permissions.SubstitutionPropose, clock.UtcNow);
            AuthorizationStore.Require(query);
            if (!ScopePolicy.IsScopeActive(owner, authority.Directory) || !query.Matches(CatalogRepository.Resource(owner)))
                throw new AuthorizationDeniedException("scope_denied");
        }
        Current();
        var repository = new CatalogRepository(database);
        var sku = await repository.GetAsync(tx, "skus", skuId, ct);
        if (CatalogRepository.Owner(sku["authoringOwner"].AsBsonDocument) != owner) throw new AuthorizationDeniedException("scope_denied");
        var definition = await repository.GetAsync(tx, "variantDefinitions", sku["currentDefinitionId"].AsInt64, ct);
        var family = await repository.GetAsync(tx, "productFamilies", sku["familyId"].AsInt64, ct);
        var category = await repository.GetAsync(tx, "catalogCategories", family["categoryId"].AsInt64, ct);
        if (family["authoringOwner"] != sku["authoringOwner"] || category["authoringOwner"] != sku["authoringOwner"] ||
            category["code"] != family["categoryCode"] || !sku["active"].AsBoolean || !family["active"].AsBoolean || !category["active"].AsBoolean)
            throw CatalogRepository.Invalid();
        CatalogAuthoringService.RequirePair(sku, definition);
        var price = await new CatalogApprovedPriceReader(database).ReadAsync(tx, sku, clock.UtcNow, ct) ?? throw CatalogRepository.Invalid();
        var package = CatalogRepository.Definition(definition);
        selection ??= new(new(skuId), package.Id, package.Version, price.Id, price.Version, package.MinimumOrderPackages);
        var quote = OrderQuotePolicy.Create([selection], [new(CatalogRepository.Family(family), CatalogRepository.Sku(sku), package, price)]);
        foreach (var (name, row) in new[] { ("skus", sku), ("productFamilies", family), ("catalogCategories", category) })
            await repository.TouchAsync(tx, name, row, ct);
        await completeOwner(ct); Current(); ct.ThrowIfCancellationRequested();
        return quote.Lines.Single();
    }
}

public sealed partial class CatalogAuthoringService
{
    internal static List<BsonDocument> SubstitutionChoicePipeline(FilterDefinition<BsonDocument> filter) => StockChoicePipeline(filter);
}
