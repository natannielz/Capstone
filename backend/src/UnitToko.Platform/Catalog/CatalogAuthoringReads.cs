using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Catalog;

public sealed partial class CatalogAuthoringService
{
    private static GrantScope[] Options(Context context)
    {
        var candidates = new HashSet<GrantScope>();
        foreach (var scope in context.Snapshot.Grants.Select(grant => grant.Scope).Where(scope => scope.Kind is GrantScopeKind.FulfillmentShop or GrantScopeKind.FulfillmentWarehouse))
        {
            candidates.Add(scope);
            if (scope.Kind == GrantScopeKind.FulfillmentShop)
                foreach (var node in context.Snapshot.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Warehouse && node.ParentId == scope.ShopId && node.Status == AuthorizationStatus.Active))
                    candidates.Add(scope with { Kind = GrantScopeKind.FulfillmentWarehouse, WarehouseId = node.Id });
        }
        return candidates.OrderBy(owner => owner.OrganizationId).ThenBy(owner => owner.ShopId).ThenBy(owner => owner.ChannelId).ThenBy(owner => owner.WarehouseId).ToArray();
    }
    private static string OwnerLabel(GrantScope owner) => $"Organisasi {owner.OrganizationId} / Toko {owner.ShopId}" +
        (owner.WarehouseId is { } warehouse ? $" / Gudang {warehouse}" : "") + $" / Kanal {owner.ChannelId}";
    private GrantScope[] Manageable(Context context)
    {
        var query = ScopePolicy.Compile(context.Actor, context.Snapshot, Permissions.CatalogManage, clock.UtcNow);
        AuthorizationStore.Require(query);
        var owners = Options(context).Where(owner => query.Matches(CatalogRepository.Resource(owner))).ToArray();
        if (owners.Length is 0 or > 256) throw CatalogRepository.Invalid("catalog_authority_limit");
        return owners;
    }
    public async Task<ImmutableArray<CatalogOwnerOption>> OwnerOptionsAsync(ActorContext actor, AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        ValidateRead(audit); var id = await ids.NextAsync("auditEvents", cancellationToken);
        return await unit.ExecuteAsync(async (tx, ct) =>
        {
            var context = await BeginAsync(actor, tx, ct); var owners = Manageable(context);
            foreach (var owner in owners) RequireOwner(context, owner);
            var result = owners.Select(owner => new CatalogOwnerOption(owner, OwnerLabel(owner))).ToImmutableArray();
            await AuditReadAsync(context, id.Value, "catalog.owners", audit, new BsonDocument(), result.Length, ct); return result;
        }, cancellationToken);
    }
    public async Task<CatalogPage<CatalogCategory>> CategoriesAsync(ActorContext actor, GrantScope owner, int offset, int limit, AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        ValidateRead(audit); AuthorizationStore.ValidatePage(offset, limit); CatalogAuthoringPolicy.Owner(owner);
        var id = await ids.NextAsync("auditEvents", cancellationToken);
        return await unit.ExecuteAsync(async (tx, ct) =>
        {
            var context = await BeginAsync(actor, tx, ct); RequireOwner(context, owner);
            var filter = CatalogRepository.OwnerFilter("authoringOwner", owner) & CatalogSchemas.Validator("catalogCategories");
            var total = await repository.Rows("catalogCategories").CountDocumentsAsync(tx, filter, cancellationToken: ct);
            var rows = await repository.Rows("catalogCategories").Find(tx, filter).Sort(new BsonDocument("_id", 1)).Skip(offset).Limit(limit).ToListAsync(ct);
            var result = rows.Select(row => { var item = CatalogRepository.Category(row); context.Head("catalogCategories", row); return item; }).ToImmutableArray();
            await AuditReadAsync(context, id.Value, "catalog.categories", audit, Page(owner, offset, limit), result.Length, ct); return new CatalogPage<CatalogCategory>(total, result);
        }, cancellationToken);
    }
    public async Task<CatalogPage<ProductFamily>> FamiliesAsync(ActorContext actor, GrantScope owner, string? search, int offset, int limit, AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        ValidateRead(audit); AuthorizationStore.ValidatePage(offset, limit); CatalogAuthoringPolicy.Owner(owner);
        var selection = Page(owner, offset, limit); selection.Add("search", SearchValue(search));
        var id = await ids.NextAsync("auditEvents", cancellationToken);
        return await unit.ExecuteAsync(async (tx, ct) =>
        {
            var context = await BeginAsync(actor, tx, ct); RequireOwner(context, owner);
            var filter = CatalogRepository.OwnerFilter("authoringOwner", owner) & CatalogSchemas.Validator("productFamilies") & SearchFilter("name", search);
            var total = await repository.Rows("productFamilies").CountDocumentsAsync(tx, filter, cancellationToken: ct);
            var rows = await repository.Rows("productFamilies").Find(tx, filter).Sort(new BsonDocument("_id", 1)).Skip(offset).Limit(limit).ToListAsync(ct);
            var result = ImmutableArray.CreateBuilder<ProductFamily>();
            foreach (var row in rows)
            {
                CatalogRepository.Require("productFamilies", row);
                var graph = await FamilyGraphAsync(context, row["_id"].AsInt64, ct); result.Add(CatalogRepository.Family(graph.Family));
            }
            await AuditReadAsync(context, id.Value, "catalog.families", audit, selection, result.Count, ct); return new CatalogPage<ProductFamily>(total, result.ToImmutable());
        }, cancellationToken);
    }
    public async Task<ProductFamily> FamilyAsync(ActorContext actor, long familyId, AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        ValidateRead(audit); var id = await ids.NextAsync("auditEvents", cancellationToken);
        return await unit.ExecuteAsync(async (tx, ct) =>
        {
            var context = await BeginAsync(actor, tx, ct); var graph = await FamilyGraphAsync(context, familyId, ct);
            var result = CatalogRepository.Family(graph.Family);
            await AuditReadAsync(context, id.Value, "catalog.family", audit, new BsonDocument("id", familyId), 1, ct); return result;
        }, cancellationToken);
    }
    public async Task<CatalogPage<CatalogSkuView>> SkusAsync(ActorContext actor, long familyId, int offset, int limit, AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        ValidateRead(audit); AuthorizationStore.ValidatePage(offset, limit); var id = await ids.NextAsync("auditEvents", cancellationToken);
        return await unit.ExecuteAsync(async (tx, ct) =>
        {
            var context = await BeginAsync(actor, tx, ct); var family = await FamilyGraphAsync(context, familyId, ct);
            var filter = F.Eq("familyId", familyId) & CatalogRepository.OwnerFilter("authoringOwner", RowOwner(family.Family)) & CatalogSchemas.Validator("skus");
            var total = await repository.Rows("skus").CountDocumentsAsync(tx, filter, cancellationToken: ct);
            var rows = await repository.Rows("skus").Find(tx, filter).Sort(new BsonDocument("_id", 1)).Skip(offset).Limit(limit).ToListAsync(ct);
            var result = ImmutableArray.CreateBuilder<CatalogSkuView>();
            foreach (var row in rows) result.Add(await SkuViewAsync(context, row["_id"].AsInt64, ct));
            await AuditReadAsync(context, id.Value, "catalog.skus", audit, new BsonDocument { { "familyId", familyId }, { "offset", offset }, { "limit", limit } }, result.Count, ct);
            return new CatalogPage<CatalogSkuView>(total, result.ToImmutable());
        }, cancellationToken);
    }
    public async Task<CatalogSkuView> SkuAsync(ActorContext actor, long skuId, AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        ValidateRead(audit); var id = await ids.NextAsync("auditEvents", cancellationToken);
        return await unit.ExecuteAsync(async (tx, ct) =>
        {
            var context = await BeginAsync(actor, tx, ct); var result = await SkuViewAsync(context, skuId, ct);
            await AuditReadAsync(context, id.Value, "catalog.sku", audit, new BsonDocument("id", skuId), 1, ct); return result;
        }, cancellationToken);
    }
    private async Task<CatalogSkuView> SkuViewAsync(Context context, long skuId, CancellationToken ct)
    {
        var graph = await SkuGraphAsync(context, skuId, ct);
        var assortments = await LoadListingsAsync(context, graph.Sku, [], ct);
        return new(CatalogRepository.Sku(graph.Sku), RowOwner(graph.Sku), CatalogRepository.Definition(graph.Definition),
            assortments.Select(row => new CatalogPublication(new(row["_id"].AsInt64), CatalogRepository.Owner(row["ownerScope"].AsBsonDocument), row["version"].AsInt64, row["status"].AsString)).ToImmutableArray());
    }
    public async Task<CatalogPage<CatalogAssortmentOption>> AssortmentsAsync(ActorContext actor, GrantScope owner, int offset, int limit, AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        ValidateRead(audit); AuthorizationStore.ValidatePage(offset, limit); CatalogAuthoringPolicy.Owner(owner); var id = await ids.NextAsync("auditEvents", cancellationToken);
        return await unit.ExecuteAsync(async (tx, ct) =>
        {
            var context = await BeginAsync(actor, tx, ct); RequireOwner(context, owner);
            var owners = Manageable(context).Where(item => item.OrganizationId == owner.OrganizationId).ToArray();
            foreach (var option in owners) RequireOwner(context, option);
            var filter = F.Eq("status", "active") & CatalogVisibilitySchemas.Collections()["catalogAssortments"] & F.Or(owners.Select(item => CatalogRepository.OwnerFilter("ownerScope", item)));
            var total = await repository.Rows("catalogAssortments").CountDocumentsAsync(tx, filter, cancellationToken: ct);
            var rows = await repository.Rows("catalogAssortments").Find(tx, filter).Sort(new BsonDocument("_id", 1)).Skip(offset).Limit(limit).ToListAsync(ct);
            var result = rows.Select(row =>
            {
                CatalogRepository.Assortment(row); context.Head("catalogAssortments", row); var scope = CatalogRepository.Owner(row["ownerScope"].AsBsonDocument);
                return new CatalogAssortmentOption(new(row["_id"].AsInt64), scope, row["version"].AsInt64, row["status"].AsString, OwnerLabel(scope) + " / Pilihan " + row["_id"].AsInt64);
            }).ToImmutableArray();
            await AuditReadAsync(context, id.Value, "catalog.assortments", audit, Page(owner, offset, limit), result.Length, ct); return new CatalogPage<CatalogAssortmentOption>(total, result);
        }, cancellationToken);
    }
    private static BsonDocument Page(GrantScope owner, int offset, int limit) => new() { { "owner", CatalogRepository.Owner(owner) }, { "offset", offset }, { "limit", limit } };
    private static BsonValue SearchValue(string? search)
    {
        if (search is null or "") return BsonNull.Value;
        return CatalogAuthoringPolicy.Text(search, 128);
    }
    internal static FilterDefinition<BsonDocument> SearchFilter(string field, string? search) => search is null or "" ? F.Empty :
        F.Regex(field, new BsonRegularExpression(Regex.Escape(SearchValue(search).AsString), "i"));
}
