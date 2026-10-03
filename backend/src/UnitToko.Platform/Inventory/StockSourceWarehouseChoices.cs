using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Inventory;

public sealed record StockSourceWarehouseOption(StockLocation Location, string OrganizationName, string ShopName,
    string WarehouseName, string ChannelName);

public sealed partial class StockSourceSetupService
{
    public async Task<ImmutableArray<StockSourceWarehouseOption>> WarehousesAsync(ActorContext actor, string purpose,
        string correlationId, CancellationToken ct = default)
    {
        var permission = purpose switch
        {
            "inventory-source-propose" => Permissions.StockSourcePropose,
            "inventory-source-review" => Permissions.StockSourceApprove,
            "inventory-source-history" => Permissions.StockSourceRead,
            "stock-source-reconciliation-review" => Permissions.StockSourceReconciliationRead,
            _ => throw new DomainValidationException("invalid_read_purpose", "Choose an explicit source-setup purpose")
        };
        if (!StockSourceSetupPolicy.Text(correlationId, 128)) throw StockSourceSetupBson.Invalid();
        await StorageAsync(ct);
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        return await authorization.ReadAsync(actor, async (snapshot, tx, token) =>
        {
            var query = ScopePolicy.Compile(actor, snapshot, permission, Now());
            AuthorizationStore.Require(query);
            var candidates = new HashSet<GrantScope>();
            foreach (var clause in query.Clauses)
            {
                if (clause.Scope.Kind == GrantScopeKind.FulfillmentWarehouse) candidates.Add(clause.Scope);
                if (clause.Scope.Kind == GrantScopeKind.FulfillmentShop)
                    foreach (var node in snapshot.Directory.Nodes.Where(node => node.Kind == ScopeNodeKind.Warehouse &&
                        node.ParentId == clause.Scope.ShopId && node.Status == AuthorizationStatus.Active))
                        candidates.Add(clause.Scope with { Kind = GrantScopeKind.FulfillmentWarehouse, WarehouseId = node.Id });
            }
            var locations = candidates.Select(scope => new StockLocation(new(scope.OrganizationId!.Value), new(scope.ShopId!.Value),
                    new(scope.WarehouseId!.Value), new(scope.ChannelId!.Value)))
                .Where(location => query.Matches(Resource(location)) && ScopePolicy.IsResourceActive(Resource(location), snapshot.Directory))
                .OrderBy(location => location.OrganizationId.Value).ThenBy(location => location.ShopId.Value)
                .ThenBy(location => location.ChannelId.Value).ThenBy(location => location.WarehouseId.Value).ToArray();
            if (locations.Length > 256) throw new PersistenceConflictException("stock_source_choice_limit", "Narrow the source-setup assignment");
            var nodes = locations.SelectMany(location => ScopePolicy.RequiredNodes(Resource(location), snapshot.Directory)).Distinct().ToArray();
            var profiles = await Rows("organizationProfiles").Find(tx, F.In("_id", nodes.Select(node => node.Id))).ToListAsync(token);
            string Name(long id, ScopeNodeKind kind)
            {
                var node = snapshot.Directory.Find(id, kind) ?? throw ProfileUnavailable();
                var profile = profiles.SingleOrDefault(row => row.GetValue("_id", BsonNull.Value) == new BsonInt64(id)) ?? throw ProfileUnavailable();
                return ProfileName(profile, node);
            }
            var result = locations.Select(location => new StockSourceWarehouseOption(location,
                Name(location.OrganizationId.Value, ScopeNodeKind.FulfillmentOrganization), Name(location.ShopId.Value, ScopeNodeKind.Shop),
                Name(location.WarehouseId.Value, ScopeNodeKind.Warehouse), Name(location.ChannelId.Value, ScopeNodeKind.Channel))).ToImmutableArray();
            // The organization writer advances the matching scope version atomically with every profile change.
            // CompleteAuthorityAsync fences those exact nodes, so a concurrent reviewed rename conflicts with this read.
            await Rows("auditEvents").InsertOneAsync(tx, new BsonDocument
            {
                { "_id", auditId }, { "schemaVersion", 1 }, { "kind", "inventory.source.read" },
                { "operation", "inventory.source.warehouses" }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion },
                { "purpose", purpose }, { "correlationId", correlationId }, { "createdAt", Now().UtcDateTime }, { "resultCount", result.Length }
            }, cancellationToken: token);
            await CompleteAuthorityAsync(actor, snapshot, permission, locations.Select(Resource).ToArray(), tx, token);
            return result;
        }, ct);
    }

    private static string ProfileName(BsonDocument row, ScopeNode node)
    {
        string[] required = ["_id", "schemaVersion", "kind", "parentId", "code", "normalizedCode", "name", "version",
            "createdBy", "updatedBy", "createdAt", "updatedAt", "approvedProposalId"];
        try
        {
            if (required.Any(field => !row.Contains(field)) || row.Names.Any(field => !required.Contains(field)) ||
                row.ElementCount != row.Names.Distinct(StringComparer.Ordinal).Count() ||
                !row["_id"].IsInt64 || row["_id"].AsInt64 != node.Id || !row["schemaVersion"].IsInt32 || row["schemaVersion"].AsInt32 != 1 ||
                row["kind"] != AuthorizationBson.Name(node.Kind) ||
                (node.ParentId is { } parent ? !row["parentId"].IsInt64 || row["parentId"].AsInt64 != parent : !row["parentId"].IsBsonNull) ||
                new[] { "version", "createdBy", "updatedBy", "approvedProposalId" }.Any(field => !row[field].IsInt64 || row[field].AsInt64 <= 0) ||
                StockSourceSetupBson.Time(row["createdAt"]) > StockSourceSetupBson.Time(row["updatedAt"]))
                throw ProfileUnavailable();
            var normalized = OrganizationChangePolicy.Normalize(new(OrganizationChangeKind.Create, node.Kind, node.ParentId,
                row["code"].AsString, row["name"].AsString));
            if (normalized.Code != row["code"].AsString || normalized.Code != row["normalizedCode"].AsString ||
                normalized.Name != row["name"].AsString) throw ProfileUnavailable();
            return normalized.Name;
        }
        catch (Exception error) when (error is DomainValidationException or InvalidCastException or KeyNotFoundException or ArgumentException)
        { throw ProfileUnavailable(); }
    }
    private static PersistenceConflictException ProfileUnavailable() =>
        new("stock_source_profile_unavailable", "Reviewed warehouse display information is unavailable");
}
