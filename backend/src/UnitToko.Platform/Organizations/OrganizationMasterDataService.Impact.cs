using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Organizations;

public sealed partial class OrganizationMasterDataService
{
    private sealed record ImpactState(OrganizationImpact Summary, ImmutableArray<ScopeNode> Nodes, ImmutableArray<BsonDocument> Identities);
    private static readonly OrganizationImpact NoImpact = new(0, 0, 0, 0,
        Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant());
    private async Task<ImpactState> ImpactAsync(OrganizationChangeRequest request, ScopeDirectory directory,
        IClientSessionHandle session, CancellationToken token)
    {
        if (request.ChangeKind != OrganizationChangeKind.Reactivate) return new(NoImpact, [], []);
        var affected = new HashSet<long> { request.ScopeId!.Value };
        bool added;
        do
        {
            added = false;
            foreach (var node in directory.Nodes)
                if (node.ParentId is { } parent && affected.Contains(parent)) added |= affected.Add(node.Id);
        } while (added);
        var nodes = directory.Nodes.Where(node => affected.Contains(node.Id)).OrderBy(node => node.Id).ToImmutableArray();
        var target = nodes.Single(node => node.Id == request.ScopeId);
        var related = new HashSet<long>(affected);
        var parentId = target.ParentId;
        while (parentId is { } parent)
        {
            if (!related.Add(parent)) throw new AuthorizationDeniedException("invalid_organization_hierarchy");
            parentId = directory.Nodes.SingleOrDefault(node => node.Id == parent)?.ParentId;
        }
        // Count all active-status records conservatively, including future/expired records. These are
        // potential access records, not a claim that each account can currently authenticate or act.
        var fields = new[] { "organizationId", "branchId", "divisionId", "shopId", "warehouseId", "channelId" };
        var filter = F.Or(fields.Select(field => F.In("scope." + field, related)));
        bool Affected(BsonDocument value) => OrganizationChangePolicy.IsAffectedScope(AuthorizationBson.Scope(value["scope"].AsBsonDocument), target, directory, affected);
        var memberships = (await Collection("memberships").Find(session, filter).Sort(new BsonDocument("_id", 1)).ToListAsync(token)).Where(Affected).ToList();
        var grants = (await Collection("authorizationGrants").Find(session, filter).Sort(new BsonDocument("_id", 1)).ToListAsync(token)).Where(Affected).ToList();
        var userIds = memberships.Concat(grants).Select(value => value["userId"].AsInt64).Distinct().Order().ToArray();
        var identities = userIds.Length == 0 ? [] : await Collection("identities").Find(session, F.In("_id", userIds)).Sort(new BsonDocument("_id", 1)).ToListAsync(token);
        if (identities.Count != userIds.Length || identities.Any(value => value["grantVersion"].AsInt64 == long.MaxValue))
            throw new AuthorizationDeniedException("invalid_organization_impact");
        var revisionDocument = new BsonDocument
        {
            { "nodes", new BsonArray(nodes.Select(AuthorizationBson.Node)) },
            { "memberships", new BsonArray(memberships) }, { "grants", new BsonArray(grants) },
            { "identities", new BsonArray(identities.Select(value => new BsonDocument
                { { "_id", value["_id"] }, { "status", value["status"] }, { "grantVersion", value["grantVersion"] } })) }
        };
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionDocument.ToJson()))).ToLowerInvariant();
        return new(new(nodes.Length, memberships.LongCount(value => value["status"] == "active"),
            grants.LongCount(value => value["status"] == "active"), identities.Count, revision), nodes, identities.ToImmutableArray());
    }
    private async Task InvalidateAffectedAccountsAsync(ImpactState impact, IClientSessionHandle session, CancellationToken token)
    {
        foreach (var identity in impact.Identities)
        {
            var filter = F.Eq("_id", identity["_id"]) & F.Eq("grantVersion", identity["grantVersion"]) & F.Eq("status", identity["status"]);
            var result = await Collection("identities").UpdateOneAsync(session, filter,
                Builders<BsonDocument>.Update.Inc("grantVersion", 1L).Inc("fence", 1L), cancellationToken: token);
            if (result.ModifiedCount != 1)
                throw new PersistenceConflictException("organization_impact_changed", "Affected accounts changed; review a fresh proposal");
        }
    }
    private static BsonDocument ImpactDocument(OrganizationImpact impact) => new()
    {
        { "affectedScopes", impact.AffectedScopes }, { "activeMemberships", impact.ActiveMemberships },
        { "activeGrants", impact.ActiveGrants }, { "affectedAccounts", impact.AffectedAccounts }, { "revision", impact.Revision }
    };
    private static OrganizationImpact ImpactSummary(BsonDocument value) => new(value["affectedScopes"].AsInt64,
        value["activeMemberships"].AsInt64, value["activeGrants"].AsInt64, value["affectedAccounts"].AsInt64, value["revision"].AsString);
}
