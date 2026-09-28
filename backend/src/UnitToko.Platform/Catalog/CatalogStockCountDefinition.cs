using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Catalog;

/// <summary>Callback-local catalog facts for a stock-count observation; not a stock balance or authority token.</summary>
internal sealed class StockDefinitionSnapshot
{
    internal GrantScope Owner { get; }
    internal long SkuId { get; }
    internal long SkuRevision { get; }
    internal long FamilyId { get; }
    internal long FamilyRevision { get; }
    internal long CategoryId { get; }
    internal long CategoryRevision { get; }
    internal long DefinitionId { get; }
    internal long DefinitionVersion { get; }
    internal string SkuCode { get; }
    internal string PhysicalItemKey { get; }
    internal string PackageLabel { get; }
    internal string PackageKind { get; }
    internal string BaseUnitCode { get; }
    internal int BaseUnitsPerPackage { get; }

    internal StockDefinitionSnapshot(BsonDocument sku, BsonDocument family, BsonDocument category, BsonDocument definition)
    {
        Owner = CatalogRepository.Owner(sku["authoringOwner"].AsBsonDocument);
        SkuId = sku["_id"].AsInt64; SkuRevision = sku["revision"].AsInt64;
        FamilyId = family["_id"].AsInt64; FamilyRevision = family["revision"].AsInt64;
        CategoryId = category["_id"].AsInt64; CategoryRevision = category["revision"].AsInt64;
        DefinitionId = definition["_id"].AsInt64; DefinitionVersion = definition["version"].AsInt64;
        SkuCode = sku["sku"].AsString; PhysicalItemKey = sku["physicalItemKey"].AsString;
        PackageLabel = definition["packageLabel"].AsString; PackageKind = definition["packageKind"].AsString;
        BaseUnitCode = definition["baseUnitCode"].AsString; BaseUnitsPerPackage = definition["baseUnitsPerPackage"].AsInt32;
    }
}

public sealed partial class CatalogAuthoringService
{
    /// <summary>
    /// Joins an Inventory-owned transaction for observation metadata. The callback must neither commit
    /// nor perform external I/O. A current stock.count grant on this exact warehouse suffices; catalog
    /// management or buyer publication cannot substitute for that grant. Failures abort caller effects.
    /// </summary>
    internal async Task<T> WithStockCountDefinitionAsync<T>(IMongoDatabase commandDatabase, IClientSessionHandle tx,
        ActorContext actor, GrantScope location, long skuId, long? expectedSkuRevision, long? expectedDefinitionId,
        Func<StockDefinitionSnapshot, CancellationToken, Task<T>> mutation, CancellationToken cancellationToken = default)
    {
        try
        {
            if (tx is null || !tx.IsInTransaction || commandDatabase is null ||
                !ReferenceEquals(tx.Client, database.Client) || !ReferenceEquals(commandDatabase.Client, database.Client) ||
                !commandDatabase.DatabaseNamespace.Equals(database.DatabaseNamespace))
                throw new InvalidOperationException("Stock counting requires the owning database and active caller transaction");
            ArgumentNullException.ThrowIfNull(mutation);
            if (location is null || location.Kind != GrantScopeKind.FulfillmentWarehouse ||
                location.OrganizationId is not > 0 || location.ShopId is not > 0 || location.WarehouseId is not > 0 ||
                location.ChannelId is not > 0 || location.BranchId is not null || location.DivisionId is not null)
                throw CatalogRepository.Invalid("stock_count_scope_denied");
            var context = await BeginAsync(actor, tx, cancellationToken, Permissions.StockCount);
            _ = StockCountClauses(context, location);
            var sku = await repository.GetAsync(tx, "skus", skuId, cancellationToken);
            var owner = RowOwner(sku);
            if (!ScopePolicy.ContainsScope(owner, location)) throw CatalogRepository.Invalid("stock_count_scope_denied");
            var family = await repository.GetAsync(tx, "productFamilies", sku["familyId"].AsInt64, cancellationToken);
            var category = await repository.GetAsync(tx, "catalogCategories", family["categoryId"].AsInt64, cancellationToken);
            if (RowOwner(family) != owner || RowOwner(category) != owner || category["code"] != family["categoryCode"])
                throw CatalogRepository.Invalid();
            var definition = await repository.GetAsync(tx, "variantDefinitions", sku["currentDefinitionId"].AsInt64, cancellationToken);
            RequirePair(sku, definition); RequireActive(sku); RequireActive(family); RequireActive(category);
            if (expectedSkuRevision is { } expected) CatalogRepository.Expected(sku, expected);
            if (expectedDefinitionId is <= 0)
                throw new DomainValidationException("invalid_catalog_request", "A positive definition reference is required");
            if (expectedDefinitionId is { } definitionId && definitionId != definition["_id"].AsInt64)
                throw new PersistenceConflictException("catalog_revision_conflict", "Catalog packaging changed; reload and review");
            context.Head("skus", sku); context.Head("productFamilies", family); context.Head("catalogCategories", category);
            var result = await mutation(new(sku, family, category, definition), cancellationToken);
            await CompleteStockCountAsync(context, location, owner, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            await AbortEvidenceCallerAsync(tx);
            throw;
        }
    }

    private ScopeClause[] StockCountClauses(Context context, GrantScope location)
    {
        var query = ScopePolicy.Compile(context.Actor, context.Snapshot, Permissions.StockCount, clock.UtcNow);
        AuthorizationStore.Require(query);
        var resource = CatalogRepository.Resource(location);
        if (!query.Matches(resource)) throw CatalogRepository.Invalid("stock_count_scope_denied");
        return query.Clauses.Where(clause => clause.Matches(resource)).ToArray();
    }

    private async Task CompleteStockCountAsync(Context context, GrantScope location, GrantScope owner, CancellationToken ct)
    {
        var clauses = StockCountClauses(context, location);
        foreach (var head in context.Heads.OrderBy(pair => pair.Key.Collection, StringComparer.Ordinal).ThenBy(pair => pair.Key.Id))
            await repository.TouchAsync(context.Tx, head.Key.Collection, head.Value, ct);
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            clauses.SelectMany(clause => clause.RequiredNodes).Concat(ScopePolicy.RequiredNodes(location, context.Snapshot.Directory))
                .Concat(ScopePolicy.RequiredNodes(owner, context.Snapshot.Directory)).Distinct(), [],
            context.Snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
            context.Snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), context.Tx, ct);
        if (context.Actor.Session is { } reference)
        {
            var live = await repository.Rows("identitySessions").Find(context.Tx,
                SessionPersistence.LiveFilter(reference, context.Actor.UserId, context.Actor.ProviderIssuer, context.Actor.ProviderSubject,
                    context.Actor.GrantVersion, clock.UtcNow))
                .Project(new BsonDocument { { "idleExpiresAt", 1 }, { "absoluteExpiresAt", 1 } }).FirstOrDefaultAsync(ct);
            var now = new BsonDateTime(clock.UtcNow.UtcDateTime).MillisecondsSinceEpoch;
            if (live is null || !live.TryGetValue("idleExpiresAt", out var idle) || !idle.IsBsonDateTime ||
                !live.TryGetValue("absoluteExpiresAt", out var absolute) || !absolute.IsBsonDateTime ||
                idle.AsBsonDateTime.MillisecondsSinceEpoch <= now || absolute.AsBsonDateTime.MillisecondsSinceEpoch <= now)
                throw CatalogRepository.Invalid("stale_or_inactive_session");
        }
        _ = StockCountClauses(context, location);
    }
}
