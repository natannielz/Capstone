using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Catalog.CatalogPriceBson;

namespace UnitToko.Platform.Catalog;

public sealed partial class CatalogPriceService
{
    public async Task<IReadOnlyList<CatalogPriceChoice>> SelectSkusAsync(ActorContext actor, CatalogPriceAccess access,
        string? search = null, long? afterId = null, int limit = 25, CancellationToken ct = default)
    {
        if (afterId is <= 0 || limit is < 1 or > 25) throw B.Invalid("invalid_catalog_price_request");
        var permission = Permission(access);
        var searchFilter = CatalogAuthoringService.SearchFilter("displayName", search) |
            CatalogAuthoringService.SearchFilter("sku", search);
        await StorageAsync(ct); var auditId = await ids.NextAsync("auditEvents", ct);
        return await authorization.ReadAsync<IReadOnlyList<CatalogPriceChoice>>(actor, async (snapshot, tx, token) => {
            AuthorizationQuery Query() {
                var value = ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow);
                AuthorizationStore.Require(value); return value;
            }
            var query = Query(); var candidates = new HashSet<GrantScope>();
            foreach (var clause in query.Clauses) {
                if (clause.Scope.Kind is GrantScopeKind.FulfillmentShop or GrantScopeKind.FulfillmentWarehouse)
                    candidates.Add(clause.Scope);
                if (clause.Scope.Kind == GrantScopeKind.FulfillmentShop)
                    foreach (var node in snapshot.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Warehouse &&
                        node.ParentId == clause.Scope.ShopId && node.Status == AuthorizationStatus.Active))
                        candidates.Add(clause.Scope with { Kind = GrantScopeKind.FulfillmentWarehouse, WarehouseId = node.Id });
            }
            var owners = candidates.Where(owner => ScopePolicy.IsResourceActive(CatalogRepository.Resource(owner), snapshot.Directory) &&
                query.Matches(CatalogRepository.Resource(owner))).ToArray();
            if (owners.Length is 0 or > 256) throw B.Invalid("catalog_price_authority_limit");
            var filter = F.Or(owners.Select(owner => CatalogRepository.OwnerFilter("authoringOwner", owner))) & searchFilter;
            if (afterId.HasValue) filter &= F.Gt("_id", afterId.Value);
            var rows = await Rows("skus").Find(tx, filter).Sort(new BsonDocument("_id", 1)).Limit(limit).ToListAsync(token);
            var result = new List<CatalogPriceChoice>();
            foreach (var row in rows) {
                var context = await ContextAsync(actor, snapshot, tx, row["_id"].AsInt64, permission, token);
                result.Add(new(CatalogRepository.Sku(context.Sku), context.Family["name"].AsString,
                    CatalogRepository.Definition(context.Definition)));
                await CompleteAsync(context, token);
            }
            await Rows("auditEvents").InsertOneAsync(tx, new BsonDocument {
                { "_id", auditId.Value }, { "schemaVersion", 1 }, { "kind", "catalog.price.read" }, { "operation", B.Prefix + "selection" },
                { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "permission", permission },
                { "owners", new BsonArray(owners.Select(CatalogRepository.Owner)) },
                { "selection", new BsonDocument { { "search", (BsonValue?)search ?? BsonNull.Value },
                    { "afterId", afterId.HasValue ? new BsonInt64(afterId.Value) : BsonNull.Value }, { "limit", limit } } },
                { "resultCount", result.Count }, { "correlationId", Guid.NewGuid().ToString("N") }, { "createdAt", Now().UtcDateTime }
            }, cancellationToken: token);
            // Fence even empty pages; expiry/revocation cannot turn search into a data oracle.
            var clauses = query.Clauses;
            await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
                clauses.SelectMany(clause => clause.RequiredNodes).Concat(owners.SelectMany(owner => ScopePolicy.RequiredNodes(owner, snapshot.Directory))).Distinct(),
                [], snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)),
                snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, token);
            await RequireLiveSessionAsync(actor, tx, token);
            var final = Query();
            if (owners.Any(owner => !final.Matches(CatalogRepository.Resource(owner)))) throw B.Invalid("scope_denied");
            return result;
        }, ct);
    }

    public Task<IReadOnlyList<BsonDocument>> ListProposalsAsync(ActorContext actor, long skuId, CatalogPriceAccess access,
        long? beforeId = null, int limit = 25, CancellationToken ct = default)
    {
        if (beforeId is <= 0 || limit is < 1 or > 25) throw B.Invalid("invalid_catalog_price_request");
        return ReadAsync<IReadOnlyList<BsonDocument>>(actor, skuId, access, B.Prefix + "proposals", new() {
            { "skuId", skuId }, { "beforeId", beforeId.HasValue ? new BsonInt64(beforeId.Value) : BsonNull.Value }, { "limit", limit } },
            async (context, token) => {
                var filter = F.Eq("skuId", skuId); if (beforeId.HasValue) filter &= F.Lt("_id", beforeId.Value);
                var rows = await Rows(B.Proposals).Find(context.Tx, filter).Sort(new BsonDocument("_id", -1)).Limit(limit).ToListAsync(token);
                var result = new List<BsonDocument>();
                foreach (var row in rows) result.Add(await ProposalViewAsync(context, row["_id"].AsInt64, token));
                return result;
            }, ct);
    }
}
