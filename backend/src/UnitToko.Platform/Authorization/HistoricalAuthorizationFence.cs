using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.Authorization;

/// <summary>Serializes explicitly authorized historical access without weakening ordinary active-only fences.</summary>
internal sealed class HistoricalAuthorizationFence(IMongoDatabase database, AuthorizationStore authorization)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;

    internal async Task FenceAsync(ActorContext actor, IEnumerable<ScopeNode> activeNodes,
        IEnumerable<ScopeNode> targetNodes, IEnumerable<PermissionGrant> grants, IEnumerable<Membership> memberships,
        IClientSessionHandle session, CancellationToken token)
    {
        var active = activeNodes.ToArray();
        var targets = targetNodes.ToArray();
        if (active.Any(node => node.Status != AuthorizationStatus.Active) ||
            targets.Any(node => node.Kind is not (ScopeNodeKind.Branch or ScopeNodeKind.Division) ||
                node.Status is not (AuthorizationStatus.Active or AuthorizationStatus.Suspended) || node.ParentId is not > 0))
            throw new AuthorizationDeniedException("invalid_historical_fence");
        var nodes = active.Concat(targets).ToArray();
        if (nodes.Any(node => node.Id <= 0 || node.Version <= 0) ||
            nodes.GroupBy(node => node.Id).Any(group => group.Distinct().Count() != 1))
            throw new AuthorizationDeniedException("invalid_historical_fence");

        await authorization.FenceAsync(actor, [], session, token);
        foreach (var node in nodes.Distinct().OrderBy(node => node.Id))
        {
            var expected = AuthorizationBson.Node(node);
            if (!expected.Contains("parentId")) expected.Add("parentId", BsonNull.Value);
            await FenceDocumentAsync("authorizationScopes", expected, session, token);
        }
        foreach (var grant in grants.Distinct().OrderBy(value => value.Id))
            await FenceDocumentAsync("authorizationGrants", AuthorizationBson.Grant(grant), session, token);
        foreach (var membership in memberships.Distinct().OrderBy(value => value.Id))
            await FenceDocumentAsync("memberships", AuthorizationBson.Membership(membership), session, token);
        await authorization.FenceAsync(actor, [], session, token);
    }

    internal async Task FenceBeneficiaryAsync(long userId, IClientSessionHandle session, CancellationToken token)
    {
        if (userId <= 0) throw new AuthorizationDeniedException("beneficiary_inactive");
        var changed = await database.GetCollection<BsonDocument>("identities").UpdateOneAsync(session,
            F.Eq("_id", userId) & F.Eq("status", "active") &
            new BsonDocument("$jsonSchema", new BsonDocument
            {
                { "bsonType", "object" }, { "required", new BsonArray { "_id", "status", "grantVersion" } },
                { "properties", new BsonDocument
                    {
                        { "_id", new BsonDocument("bsonType", "long") },
                        { "status", new BsonDocument("bsonType", "string") },
                        { "grantVersion", new BsonDocument { { "bsonType", "long" }, { "minimum", 0L } } },
                        { "fence", Counter() }
                    }
                }
            }),
            Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: token);
        if (changed.ModifiedCount != 1) throw new AuthorizationDeniedException("beneficiary_inactive");
    }

    private async Task FenceDocumentAsync(string collection, BsonDocument expected, IClientSessionHandle session, CancellationToken token)
    {
        var properties = new BsonDocument("fence", Counter());
        var filters = new List<FilterDefinition<BsonDocument>>();
        foreach (var field in expected.Where(field => field.Name != "fence"))
        {
            if (field.Name == "scope")
            {
                filters.Add(CanonicalScope("scope", field.Value.AsBsonDocument));
                continue;
            }
            filters.Add(F.Eq(field.Name, field.Value));
            properties.Add(field.Name, Type(field.Value));
        }
        if (collection is "authorizationGrants" or "memberships")
            foreach (var field in new[] { "expiresAt", "revokedAt" })
                if (!expected.Contains(field))
                {
                    filters.Add(F.Eq(field, BsonNull.Value));
                    properties.Add(field, new BsonDocument("bsonType", new BsonArray { "date", "null" }));
                }
        filters.Add(new BsonDocument("$jsonSchema", new BsonDocument
            { { "bsonType", "object" }, { "properties", properties } }));
        var changed = await database.GetCollection<BsonDocument>(collection).UpdateOneAsync(session, F.And(filters),
            Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: token);
        if (changed.ModifiedCount != 1) throw new AuthorizationDeniedException("historical_authority_changed");
    }

    private static FilterDefinition<BsonDocument> CanonicalScope(string field, BsonDocument expected)
    {
        var properties = new BsonDocument("kind", new BsonDocument("bsonType", "string"));
        var filters = new List<FilterDefinition<BsonDocument>> { F.Eq(field + ".kind", expected["kind"]) };
        foreach (var name in new[] { "organizationId", "branchId", "divisionId", "shopId", "warehouseId", "channelId" })
        {
            var value = expected.GetValue(name, BsonNull.Value);
            properties.Add(name, Type(value));
            filters.Add(F.Eq(field + "." + name, value));
        }
        filters.Add(new BsonDocument("$jsonSchema", new BsonDocument
        {
            { "bsonType", "object" }, { "required", new BsonArray { field } },
            { "properties", new BsonDocument(field, new BsonDocument
                {
                    { "bsonType", "object" }, { "required", new BsonArray { "kind" } },
                    { "additionalProperties", false }, { "properties", properties }
                }) }
        }));
        return F.And(filters);
    }

    private static BsonDocument Counter() => new() { { "bsonType", "long" }, { "minimum", 0L } };
    private static BsonDocument Type(BsonValue value) => value.BsonType switch
    {
        BsonType.Int64 => new("bsonType", "long"),
        BsonType.Int32 => new("bsonType", "int"),
        BsonType.String => new("bsonType", "string"),
        BsonType.DateTime => new("bsonType", "date"),
        BsonType.Null => new("bsonType", "null"),
        BsonType.Array => new() { { "bsonType", "array" }, { "items", new BsonDocument("bsonType", "string") } },
        _ => throw new AuthorizationDeniedException("invalid_historical_fence")
    };
}
