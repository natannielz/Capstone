using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Catalog;

public sealed partial class CatalogAuthoringService
{
    public async Task<ImmutableArray<StockWarehouseOption>> StockWarehouseOptionsAsync(ActorContext actor,
        StockCountChoicePurpose purpose, AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        var permission = purpose switch
        {
            StockCountChoicePurpose.Count => Permissions.StockCount,
            StockCountChoicePurpose.History => Permissions.StockRead,
            _ => throw new DomainValidationException("invalid_stock_choice_purpose", "Choose counting or history")
        };
        ValidateStockChoiceRead(audit, purpose);
        return await StockWarehouseOptionsCoreAsync(actor, permission, audit, "inventory.stock-count.warehouses", cancellationToken);
    }

    private async Task<ImmutableArray<StockWarehouseOption>> StockWarehouseOptionsCoreAsync(ActorContext actor,
        string permission, AuthorizationReadMetadata audit, string auditKind, CancellationToken cancellationToken)
    {
        var auditId = await ids.NextAsync("auditEvents", cancellationToken);
        return await unit.ExecuteAsync(async (tx, ct) =>
        {
            var context = await BeginAsync(actor, tx, ct, permission);
            var query = StockChoiceQuery(context, permission, []);
            var candidates = new HashSet<GrantScope>();
            foreach (var clause in query.Clauses)
            {
                if (clause.Scope.Kind == GrantScopeKind.FulfillmentWarehouse) candidates.Add(clause.Scope);
                if (clause.Scope.Kind == GrantScopeKind.FulfillmentShop)
                    foreach (var node in context.Snapshot.Directory.Nodes.Where(node =>
                        node.Kind == ScopeNodeKind.Warehouse && node.ParentId == clause.Scope.ShopId && node.Status == AuthorizationStatus.Active))
                        candidates.Add(clause.Scope with { Kind = GrantScopeKind.FulfillmentWarehouse, WarehouseId = node.Id });
            }
            var locations = candidates.Where(location => query.Matches(CatalogRepository.Resource(location)))
                .OrderBy(location => location.OrganizationId).ThenBy(location => location.ShopId)
                .ThenBy(location => location.ChannelId).ThenBy(location => location.WarehouseId).ToArray();
            if (locations.Length > 256) throw CatalogRepository.Invalid("stock_choice_authority_limit");
            foreach (var location in locations) StockChoiceLocation(location);
            var nodes = locations.SelectMany(location => ScopePolicy.RequiredNodes(location, context.Snapshot.Directory)).Distinct().ToArray();
            var profiles = await repository.Rows("organizationProfiles").Find(tx, F.In("_id", nodes.Select(node => node.Id))).ToListAsync(ct);
            string Name(long id, ScopeNodeKind kind)
            {
                var node = context.Snapshot.Directory.Find(id, kind) ?? throw CatalogRepository.Invalid("stock_choice_profile_unavailable");
                var matches = profiles.Where(row => row.TryGetValue("_id", out var value) && value == new BsonInt64(id)).ToArray();
                if (matches.Length != 1) throw CatalogRepository.Invalid("stock_choice_profile_unavailable");
                return StockChoiceProfileName(matches[0], node);
            }
            var result = locations.Select(location => new StockWarehouseOption(location,
                Name(location.OrganizationId!.Value, ScopeNodeKind.FulfillmentOrganization),
                Name(location.ShopId!.Value, ScopeNodeKind.Shop), Name(location.WarehouseId!.Value, ScopeNodeKind.Warehouse),
                Name(location.ChannelId!.Value, ScopeNodeKind.Channel))).ToImmutableArray();
            await AuditStockChoiceAsync(context, permission, locations, auditId.Value, auditKind,
                audit, new BsonDocument("purpose", audit.Purpose), result.Length, ct);
            return result;
        }, cancellationToken);
    }

    public async Task<CatalogPage<StockCountPackageOption>> CountablePackagesAsync(ActorContext actor, GrantScope location,
        int offset, int limit, string? search, AuthorizationReadMetadata audit, CancellationToken cancellationToken = default)
    {
        ValidateStockChoiceRead(audit, StockCountChoicePurpose.Count);
        return await StockPackagesCoreAsync(actor, location, offset, limit, search, audit, Permissions.StockCount,
            "inventory.stock-count.packages", null, cancellationToken);
    }

    private async Task<CatalogPage<StockCountPackageOption>> StockPackagesCoreAsync(ActorContext actor, GrantScope location,
        int offset, int limit, string? search, AuthorizationReadMetadata audit, string permission, string auditKind,
        string? packageKind, CancellationToken cancellationToken)
    {
        StockChoiceLocation(location);
        AuthorizationStore.ValidatePage(offset, limit);
        var searchFilter = SearchFilter("displayName", search);
        var selection = new BsonDocument { { "location", CatalogRepository.Owner(location) },
            { "offset", offset }, { "limit", limit }, { "search", SearchValue(search) } };
        var auditId = await ids.NextAsync("auditEvents", cancellationToken);
        return await unit.ExecuteAsync(async (tx, ct) =>
        {
            var context = await BeginAsync(actor, tx, ct, permission);
            _ = StockChoiceQuery(context, permission, [location]);
            var shop = location with { Kind = GrantScopeKind.FulfillmentShop, WarehouseId = null };
            var filter = F.Or(CatalogRepository.OwnerFilter("authoringOwner", location),
                CatalogRepository.OwnerFilter("authoringOwner", shop)) & F.Eq("active", true) & searchFilter;
            var pipeline = StockChoicePipeline(filter);
            if (packageKind is not null) pipeline.Add(new("$match", new BsonDocument("definition.packageKind", packageKind)));
            pipeline.Add(new("$facet", new BsonDocument
            {
                { "count", new BsonArray { new BsonDocument("$count", "total") } },
                { "page", new BsonArray { new BsonDocument("$sort", new BsonDocument("_id", 1)),
                    new BsonDocument("$skip", offset), new BsonDocument("$limit", limit),
                    new BsonDocument("$project", new BsonDocument("_id", 1)) } }
            }));
            var page = await repository.Rows("skus").Aggregate<BsonDocument>(tx, pipeline).SingleAsync(ct);
            var total = page["count"].AsBsonArray.Count == 0 ? 0L : page["count"][0]["total"].ToInt64();
            var selectedIds = page["page"].AsBsonArray.Select(row => row["_id"].AsInt64).ToArray();
            var result = ImmutableArray.CreateBuilder<StockCountPackageOption>(selectedIds.Length);
            foreach (var skuId in selectedIds)
            {
                var sku = await repository.GetAsync(tx, "skus", skuId, ct);
                var owner = RowOwner(sku);
                if (owner != location && owner != shop) throw CatalogRepository.Invalid("stock_count_scope_denied");
                var family = await repository.GetAsync(tx, "productFamilies", sku["familyId"].AsInt64, ct);
                var category = await repository.GetAsync(tx, "catalogCategories", family["categoryId"].AsInt64, ct);
                var definition = await repository.GetAsync(tx, "variantDefinitions", sku["currentDefinitionId"].AsInt64, ct);
                if (RowOwner(family) != owner || RowOwner(category) != owner || family["categoryCode"] != category["code"])
                    throw CatalogRepository.Invalid();
                RequirePair(sku, definition); RequireActive(sku); RequireActive(family); RequireActive(category);
                context.Head("skus", sku); context.Head("productFamilies", family); context.Head("catalogCategories", category);
                context.Owners.Add(owner);
                result.Add(new(new(skuId), sku["revision"].AsInt64, new(definition["_id"].AsInt64),
                    definition["version"].AsInt64, sku["sku"].AsString, family["name"].AsString,
                    definition["packageLabel"].AsString, definition["packageKind"].AsString,
                    definition["baseUnitCode"].AsString, definition["baseUnitsPerPackage"].AsInt32));
            }
            await AuditStockChoiceAsync(context, permission, [location], auditId.Value,
                auditKind, audit, selection, result.Count, ct);
            return new CatalogPage<StockCountPackageOption>(total, result.ToImmutable());
        }, cancellationToken);
    }

    private static void ValidateStockChoiceRead(AuthorizationReadMetadata audit, StockCountChoicePurpose purpose)
    {
        var expected = purpose == StockCountChoicePurpose.Count ? "inventory-stock-count" : "inventory-stock-history";
        if (audit is null || audit.Purpose != expected || CatalogAuthoringPolicy.Text(audit.CorrelationId, 128) != audit.CorrelationId)
            throw new DomainValidationException("invalid_stock_choice_purpose", "Use the required bounded read purpose");
    }

    private static void StockChoiceLocation(GrantScope location)
    {
        if (location is null || location.Kind != GrantScopeKind.FulfillmentWarehouse || location.OrganizationId is not > 0 ||
            location.ShopId is not > 0 || location.WarehouseId is not > 0 || location.ChannelId is not > 0 ||
            location.BranchId is not null || location.DivisionId is not null)
            throw new DomainValidationException("invalid_stock_choice_location", "Choose an exact warehouse and channel");
    }

    private static string StockChoiceProfileName(BsonDocument profile, ScopeNode node)
    {
        bool PositiveLong(string name) => profile.TryGetValue(name, out var value) && value.IsInt64 && value.AsInt64 > 0;
        if (profile.Names.Distinct(StringComparer.Ordinal).Count() != profile.ElementCount ||
            !PositiveLong("_id") || profile["_id"].AsInt64 != node.Id || !PositiveLong("version") ||
            !profile.TryGetValue("schemaVersion", out var schema) || !schema.IsInt32 || schema.AsInt32 != 1 ||
            !profile.TryGetValue("kind", out var kind) || !kind.IsString || kind.AsString != AuthorizationBson.Name(node.Kind) ||
            !profile.TryGetValue("parentId", out var parent) ||
            (node.ParentId is { } id ? !parent.IsInt64 || parent.AsInt64 != id : !parent.IsBsonNull) ||
            !profile.TryGetValue("code", out var code) || !code.IsString ||
            !profile.TryGetValue("normalizedCode", out var normalizedCode) || !normalizedCode.IsString ||
            !profile.TryGetValue("name", out var name) || !name.IsString)
            throw CatalogRepository.Invalid("stock_choice_profile_unavailable");
        try
        {
            var canonical = OrganizationChangePolicy.Normalize(new(OrganizationChangeKind.Create,
                node.Kind, node.ParentId, code.AsString, name.AsString));
            if (canonical.Code != code.AsString || canonical.Code != normalizedCode.AsString || canonical.Name != name.AsString)
                throw CatalogRepository.Invalid("stock_choice_profile_unavailable");
            return canonical.Name;
        }
        catch (DomainValidationException) { throw CatalogRepository.Invalid("stock_choice_profile_unavailable"); }
    }

    private AuthorizationQuery StockChoiceQuery(Context context, string permission, GrantScope[] locations)
    {
        var query = ScopePolicy.Compile(context.Actor, context.Snapshot, permission, clock.UtcNow);
        AuthorizationStore.Require(query);
        if (locations.Any(location => !query.Matches(CatalogRepository.Resource(location))))
            throw CatalogRepository.Invalid("stock_count_scope_denied");
        return query;
    }

    private async Task AuditStockChoiceAsync(Context context, string permission, GrantScope[] locations, long auditId,
        string operation, AuthorizationReadMetadata audit, BsonDocument selection, int count, CancellationToken ct)
    {
        await repository.Rows("auditEvents").InsertOneAsync(context.Tx, new BsonDocument
        {
            { "_id", auditId }, { "schemaVersion", 1 }, { "kind", "catalog.read" }, { "actorId", context.Actor.UserId },
            { "actorGrantVersion", context.Actor.GrantVersion }, { "operation", operation }, { "purpose", audit.Purpose },
            { "correlationId", audit.CorrelationId }, { "createdAt", clock.UtcNow.UtcDateTime },
            { "owners", OwnerArray(locations) }, { "selectionDigest", CatalogRepository.Digest(selection) }, { "resultCount", count }
        }, cancellationToken: ct);
        var query = StockChoiceQuery(context, permission, locations);
        var clauses = query.Clauses.Where(clause => locations.Length == 0 ||
            locations.Any(location => clause.Matches(CatalogRepository.Resource(location)))).ToArray();
        foreach (var head in context.Heads.OrderBy(pair => pair.Key.Collection, StringComparer.Ordinal).ThenBy(pair => pair.Key.Id))
            await repository.TouchAsync(context.Tx, head.Key.Collection, head.Value, ct);
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(context.Actor,
            clauses.SelectMany(clause => clause.RequiredNodes)
                .Concat(locations.Concat(context.Owners).SelectMany(location => ScopePolicy.RequiredNodes(location, context.Snapshot.Directory))).Distinct(),
            [], context.Snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
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
        _ = StockChoiceQuery(context, permission, locations);
        ct.ThrowIfCancellationRequested();
    }

    private static List<BsonDocument> StockChoicePipeline(FilterDefinition<BsonDocument> filter)
    {
        var result = new List<BsonDocument> { new("$match", (filter & CatalogSchemas.Validator("skus"))
            .Render(new(BsonDocumentSerializer.Instance, BsonSerializer.SerializerRegistry))) };
        void Join(string collection, string local, string name)
        {
            result.Add(new("$lookup", new BsonDocument { { "from", collection }, { "localField", local }, { "foreignField", "_id" },
                { "pipeline", new BsonArray { new BsonDocument("$match", CatalogSchemas.Validator(collection)) } }, { "as", name } }));
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
        result.Add(new("$match", new BsonDocument { { "family.active", true }, { "category.active", true },
            { "$expr", new BsonDocument("$and", predicates) } }));
        return result;
    }
}
