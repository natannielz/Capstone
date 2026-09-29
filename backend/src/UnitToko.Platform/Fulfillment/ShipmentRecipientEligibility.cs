using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;

namespace UnitToko.Platform.Fulfillment;

internal sealed record ShipmentRecipientTarget(long UserId, long GrantVersion, long ProfileVersion, string Name,
    ImmutableArray<PermissionGrant> Grants, ImmutableArray<Membership> Memberships, ImmutableArray<ScopeNode> Nodes);

/// <summary>Candidate eligibility uses approved application records only, not a pretend recipient
/// session or courier-supplied contact. Actual consent separately requires authenticated assurance.</summary>
internal sealed class ShipmentRecipientEligibility(IMongoDatabase database, AuthorizationStore authorization, IClock clock)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async Task<ImmutableArray<ShipmentRecipientTarget>> ListAsync(IClientSessionHandle tx, AuthorizationSnapshot snapshot, ResourceScope resource, long? beforeId, CancellationToken ct) {
        if (beforeId is <= 0) throw Invalid();
        var filter = F.Eq("scope.organizationId", resource.BuyingOrganizationId) & F.Eq("status", "active") & F.AnyEq("permissions", Permissions.ReceiptAccept);
        if (beforeId.HasValue) filter &= F.Lt("userId", beforeId.Value);
        using var cursor = await Rows("authorizationGrants").Aggregate(tx, new AggregateOptions { BatchSize = 25 }).Match(filter)
            .Group(new BsonDocument("_id", "$userId")).Sort(new BsonDocument("_id", -1)).ToCursorAsync(ct);
        var result = ImmutableArray.CreateBuilder<ShipmentRecipientTarget>();
        while (result.Count < 26 && await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current) {
            try { result.Add(await ReadAsync(tx, snapshot, resource, row["_id"].AsInt64, ct)); }
            catch (AuthorizationDeniedException error) when (error.Code == "shipment_recipient_unavailable") { }
            if (result.Count == 26) break;
        }
        return result.ToImmutable();
    }
    internal async Task<ShipmentRecipientTarget> ReadAsync(IClientSessionHandle tx, AuthorizationSnapshot snapshot, ResourceScope resource, long userId, CancellationToken ct) {
        if (userId <= 0 || resource.AssignedUserIds.Contains(userId)) throw Invalid();
        var identity = await Rows("identities").Find(tx, F.Eq("_id", userId) & F.Eq("status", "active")).SingleOrDefaultAsync(ct);
        if (identity is null || !identity.TryGetValue("grantVersion", out var grantVersion) || !grantVersion.IsInt64 || grantVersion.AsInt64 < 0) throw Invalid();
        var profile = await Rows("accountProfiles").Find(tx, F.Eq("_id", userId)).SingleOrDefaultAsync(ct);
        if (profile is null || !profile.TryGetValue("version", out var version) || !version.IsInt64 || version.AsInt64 <= 0 ||
            !profile.TryGetValue("displayName", out var name) || !name.IsString || string.IsNullOrWhiteSpace(name.AsString) || name.AsString.Length > 256 ||
            name.AsString != name.AsString.Trim() || name.AsString.Any(char.IsControl)) throw Invalid();
        var grants = (await Rows("authorizationGrants").Find(tx, F.Eq("userId", userId)).ToListAsync(ct)).Select(AuthorizationBson.Grant).ToImmutableArray();
        var members = (await Rows("memberships").Find(tx, F.Eq("userId", userId)).ToListAsync(ct)).Select(AuthorizationBson.Membership).ToImmutableArray();
        var matches = Eligible(userId, grants, members, snapshot.Directory, resource, clock.UtcNow);
        var matchingMembers = members.Where(member => matches.Any(grant => grant.MembershipId == member.Id)).ToImmutableArray();
        return new(userId, grantVersion.AsInt64, version.AsInt64, name.AsString, matches, matchingMembers,
            matches.SelectMany(grant => ScopePolicy.RequiredNodes(grant.Scope, snapshot.Directory)).Concat(matchingMembers.SelectMany(member => ScopePolicy.RequiredNodes(member.Scope, snapshot.Directory))).Distinct().ToImmutableArray());
    }
    internal async Task FenceAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx, ResourceScope resource, ShipmentRecipientTarget target, CancellationToken ct) {
        RequireCurrent(snapshot, resource, target);
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, target.Nodes, [], target.Grants, target.Memberships, tx, ct);
        if ((await Rows("identities").UpdateOneAsync(tx, F.Eq("_id", target.UserId) & F.Eq("status", "active") & F.Eq("grantVersion", target.GrantVersion) &
            F.Lt("fence", long.MaxValue - 1), Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw Invalid();
        if (!await Rows("accountProfiles").Find(tx, F.Eq("_id", target.UserId) & F.Eq("version", target.ProfileVersion) & F.Eq("displayName", target.Name)).AnyAsync(ct)) throw Invalid();
        RequireCurrent(snapshot, resource, target);
    }
    internal void RequireCurrent(AuthorizationSnapshot snapshot, ResourceScope resource, ShipmentRecipientTarget target) =>
        _ = Eligible(target.UserId, target.Grants, target.Memberships, snapshot.Directory, resource, clock.UtcNow);
    private static ImmutableArray<PermissionGrant> Eligible(long userId, ImmutableArray<PermissionGrant> grants, ImmutableArray<Membership> members,
        ScopeDirectory directory, ResourceScope resource, DateTimeOffset now) {
        if (resource.AssignedUserIds.Contains(userId) || !ScopePolicy.IsResourceActive(resource, directory) ||
            grants.Select(grant => grant.Id).Distinct().Count() != grants.Length || members.Select(member => member.Id).Distinct().Count() != members.Length) throw Invalid();
        var allowed = Permissions.Find(Permissions.ReceiptAccept)!.AllowedScopes;
        var matches = grants.Where(grant => allowed.Contains(grant.Scope.Kind) && ScopePolicy.IsGrantActive(grant, userId, now) && ScopePolicy.IsScopeActive(grant.Scope, directory) &&
            grant.Permissions.Contains(Permissions.ReceiptAccept) &&
            (grant.Scope.Kind is not (GrantScopeKind.BuyingBranch or GrantScopeKind.BuyingDivision) || grant.Permissions.Contains(Permissions.BranchPic)) &&
            (grant.Scope.Kind != GrantScopeKind.BuyingOrganization || grant.Permissions.Contains(Permissions.OrganizationWide)) &&
            new ScopeClause(grant.Id, grant.MembershipId, userId, grant.Scope, []).Matches(resource) && members.Any(member => member.Id == grant.MembershipId && member.UserId == userId &&
                member.Id > 0 && member.Version > 0 && member.ApprovedBy > 0 && member.ApprovedBy != userId &&
                ScopePolicy.IsEffective(member.Status, member.EffectiveAt, member.ExpiresAt, member.RevokedAt, now) && ScopePolicy.IsScopeActive(member.Scope, directory) &&
                ScopePolicy.ContainsScope(member.Scope, grant.Scope))).ToImmutableArray();
        if (matches.Length == 0) throw Invalid(); return matches;
    }
    internal async Task<PersonAuthorityProof> IndependenceAsync(IClientSessionHandle tx, long courierId, long recipientId, CancellationToken ct) {
        if (courierId == recipientId) throw Invalid();
        var proof = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [courierId, recipientId], ct);
        if (!PersonIndependencePolicy.AreIndependent(proof.Evidence.Single(row => row.UserId == recipientId), proof.Evidence.Single(row => row.UserId == courierId)))
            throw new AuthorizationDeniedException("independent_recipient_required");
        proof.RequireCurrent(tx, clock.UtcNow); return proof;
    }
    internal static AuthorizationDeniedException Invalid() => new("shipment_recipient_unavailable");
}
