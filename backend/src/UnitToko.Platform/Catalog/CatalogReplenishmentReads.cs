using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Catalog;

public sealed partial class CatalogAuthoringService
{
    public Task<ImmutableArray<StockWarehouseOption>> ReplenishmentWarehousesAsync(ActorContext actor,
        AuthorizationReadMetadata audit, CancellationToken ct = default)
    {
        ReplenishmentRead(audit);
        return StockWarehouseOptionsCoreAsync(actor, Permissions.StockRead, audit, "inventory.replenishment.warehouses", ct);
    }

    public Task<CatalogPage<StockCountPackageOption>> ReplenishmentPackagesAsync(ActorContext actor, GrantScope location,
        int offset, int limit, string? search, AuthorizationReadMetadata audit, CancellationToken ct = default)
    {
        ReplenishmentRead(audit);
        return StockPackagesCoreAsync(actor, location, offset, limit, search, audit, Permissions.StockRead,
            "inventory.replenishment.packages", null, ct);
    }

    // Metadata is captured and fenced in the advisory owner's transaction. This callback supplies
    // neither inventory quantities nor procurement authority, and never infers package contents.
    internal async Task<T> WithReplenishmentDefinitionAsync<T>(IMongoDatabase caller, IClientSessionHandle tx,
        ActorContext actor, GrantScope location, long skuId, long expectedSkuRevision, long definitionId,
        AuthorizationReadMetadata audit, Func<StockDefinitionSnapshot, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        try {
            ReplenishmentRead(audit); StockChoiceLocation(location);
            if (tx is null || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) ||
                !ReferenceEquals(caller.Client, database.Client) || !caller.DatabaseNamespace.Equals(database.DatabaseNamespace) ||
                skuId <= 0 || expectedSkuRevision <= 0 || definitionId <= 0) throw CatalogRepository.Invalid();
            var context = await BeginAsync(actor, tx, ct, Permissions.StockRead);
            _ = StockChoiceQuery(context, Permissions.StockRead, [location]);
            var sku = await repository.GetAsync(tx, "skus", skuId, ct); var owner = RowOwner(sku);
            if (!ScopePolicy.ContainsScope(owner, location)) throw new AuthorizationDeniedException("scope_denied");
            var family = await repository.GetAsync(tx, "productFamilies", sku["familyId"].AsInt64, ct);
            var category = await repository.GetAsync(tx, "catalogCategories", family["categoryId"].AsInt64, ct);
            var definition = await repository.GetAsync(tx, "variantDefinitions", sku["currentDefinitionId"].AsInt64, ct);
            if (RowOwner(family) != owner || RowOwner(category) != owner || category["code"] != family["categoryCode"]) throw CatalogRepository.Invalid();
            RequirePair(sku, definition); RequireActive(sku); RequireActive(family); RequireActive(category);
            CatalogRepository.Expected(sku, expectedSkuRevision);
            if (definition["_id"] != definitionId) throw new PersistenceConflictException("catalog_revision_conflict", "Packaging changed; reload and review");
            context.Head("skus", sku); context.Head("productFamilies", family); context.Head("catalogCategories", category);
            var result = await read(new(sku, family, category, definition), ct);
            await AuditStockChoiceAsync(context, Permissions.StockRead, [location], (await ids.NextAsync("auditEvents", ct)).Value,
                "inventory.replenishment.definition", audit, new() { { "skuId", skuId }, { "definitionId", definitionId } }, 1, ct);
            return result;
        } catch { await AbortEvidenceCallerAsync(tx); throw; }
    }

    private static void ReplenishmentRead(AuthorizationReadMetadata audit)
    {
        if (audit is null || audit.Purpose != "inventory-replenishment" ||
            string.IsNullOrWhiteSpace(audit.CorrelationId) || audit.CorrelationId.Length > 128 ||
            audit.CorrelationId != audit.CorrelationId.Trim() || audit.CorrelationId.Any(char.IsControl))
            throw CatalogRepository.Invalid("invalid_replenishment_read");
    }
}
