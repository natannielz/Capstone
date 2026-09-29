using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Fulfillment;

internal sealed record ShipmentCourierTarget(long UserId, long GrantVersion, long ProfileVersion, string Name,
    ImmutableArray<PermissionGrant> Grants, ImmutableArray<Membership> Memberships, ImmutableArray<ScopeNode> Nodes);

/// <summary>Assignment checks the target's approved application grants and membership. It never
/// invents a logged-in courier actor or borrows the assigning staff member's factor assurance.</summary>
internal sealed class ShipmentCourierEligibility(IMongoDatabase database, AuthorizationStore authorization, IClock clock)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async Task<ImmutableArray<ShipmentCourierTarget>> ListAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx,
        ResourceScope resource, long? beforeId, CancellationToken ct) {
        RequireAssign(actor, snapshot, resource); if (beforeId is <= 0) throw Invalid();
        var filter = F.Eq("scope.kind", "courierAssignment") & F.Eq("scope.organizationId", resource.FulfillmentOrganizationId) &
            F.Eq("scope.shopId", resource.ShopId) & F.Eq("scope.channelId", resource.ChannelId) &
            (F.Eq("scope.warehouseId", BsonNull.Value) | F.Eq("scope.warehouseId", resource.WarehouseId)) &
            F.Eq("status", "active") & F.AnyEq("permissions", Permissions.ShipmentRead);
        if (beforeId.HasValue) filter &= F.Lt("userId", beforeId.Value);
        var candidates = Rows("authorizationGrants").Aggregate(tx, new AggregateOptions { BatchSize = 25 }).Match(filter)
            .Group(new BsonDocument("_id", "$userId")).Sort(new BsonDocument("_id", -1));
        using var cursor = await candidates.ToCursorAsync(ct); var result = ImmutableArray.CreateBuilder<ShipmentCourierTarget>();
        while (result.Count < 26 && await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current) {
            try { result.Add(await ReadAsync(actor, snapshot, tx, resource, row["_id"].AsInt64, ct)); }
            catch (AuthorizationDeniedException error) when (error.Code == "shipment_courier_unavailable") { }
            if (result.Count == 26) break;
        }
        foreach (var target in result) await FenceAsync(actor, snapshot, tx, resource, target, ct);
        foreach (var target in result) RequireCurrent(actor, snapshot, resource, target);
        return result.ToImmutable();
    }
    internal async Task<ShipmentCourierTarget> ReadAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx,
        ResourceScope resource, long userId, CancellationToken ct) {
        RequireAssign(actor, snapshot, resource);
        var identity = await Rows("identities").Find(tx, F.Eq("_id", userId) & F.Eq("status", "active")).SingleOrDefaultAsync(ct);
        if (identity is null || !identity.TryGetValue("grantVersion", out var grantVersion) || !grantVersion.IsInt64 || grantVersion.AsInt64 < 0) throw Invalid();
        var profile = await Rows("accountProfiles").Find(tx, F.Eq("_id", userId)).SingleOrDefaultAsync(ct);
        if (profile is null || !profile.TryGetValue("version", out var profileVersion) || !profileVersion.IsInt64 || profileVersion.AsInt64 <= 0 ||
            !profile.TryGetValue("displayName", out var name) || !name.IsString || string.IsNullOrWhiteSpace(name.AsString) || name.AsString.Length > 256 ||
            name.AsString != name.AsString.Trim() || name.AsString.Any(char.IsControl)) throw Invalid();
        var grants = (await Rows("authorizationGrants").Find(tx, F.Eq("userId", userId)).ToListAsync(ct)).Select(AuthorizationBson.Grant).ToImmutableArray();
        var members = (await Rows("memberships").Find(tx, F.Eq("userId", userId)).ToListAsync(ct)).Select(AuthorizationBson.Membership).ToImmutableArray();
        var matches = Eligible(userId, grants, members, snapshot.Directory, resource, clock.UtcNow);
        var matchingMembers = members.Where(member => matches.Any(grant => grant.MembershipId == member.Id)).ToImmutableArray();
        var nodes = matches.SelectMany(grant => ScopePolicy.RequiredNodes(grant.Scope, snapshot.Directory))
            .Concat(matchingMembers.SelectMany(member => ScopePolicy.RequiredNodes(member.Scope, snapshot.Directory))).Distinct().ToImmutableArray();
        return new(userId, grantVersion.AsInt64, profileVersion.AsInt64, name.AsString, matches, matchingMembers, nodes);
    }
    internal async Task FenceAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx,
        ResourceScope resource, ShipmentCourierTarget target, CancellationToken ct) {
        RequireAssign(actor, snapshot, resource);
        _ = Eligible(target.UserId, target.Grants, target.Memberships, snapshot.Directory, resource, clock.UtcNow);
        var assign = ScopePolicy.Compile(actor, snapshot, Permissions.ShipmentAssign, clock.UtcNow);
        var clauses = assign.Clauses.Where(clause => clause.Matches(resource)).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor,
            target.Nodes.Concat(clauses.SelectMany(clause => clause.RequiredNodes)).Distinct(), [],
            target.Grants.Concat(snapshot.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id))).Distinct(),
            target.Memberships.Concat(snapshot.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id))).Distinct(), tx, ct);
        if ((await Rows("identities").UpdateOneAsync(tx, F.Eq("_id", target.UserId) & F.Eq("status", "active") &
            F.Eq("grantVersion", target.GrantVersion) & F.Lt("fence", long.MaxValue - 1), Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw Invalid();
        // Profile changes must not silently alter the person name confirmed in dispatch review.
        if (!await Rows("accountProfiles").Find(tx, F.Eq("_id", target.UserId) & F.Eq("version", target.ProfileVersion) & F.Eq("displayName", target.Name)).AnyAsync(ct)) throw Invalid();
        RequireAssign(actor, snapshot, resource);
        _ = Eligible(target.UserId, target.Grants, target.Memberships, snapshot.Directory, resource, clock.UtcNow);
    }
    private void RequireAssign(ActorContext actor, AuthorizationSnapshot snapshot, ResourceScope resource) {
        if (!ScopePolicy.Authorize(actor, snapshot, Permissions.ShipmentAssign, resource, clock.UtcNow).Allowed) throw Invalid();
    }
    internal void RequireCurrent(ActorContext actor, AuthorizationSnapshot snapshot, ResourceScope resource, ShipmentCourierTarget target) {
        RequireAssign(actor, snapshot, resource);
        _ = Eligible(target.UserId, target.Grants, target.Memberships, snapshot.Directory, resource, clock.UtcNow);
    }
    private static ImmutableArray<PermissionGrant> Eligible(long userId, ImmutableArray<PermissionGrant> grants, ImmutableArray<Membership> members,
        ScopeDirectory directory, ResourceScope resource, DateTimeOffset now) {
        if (grants.Select(grant => grant.Id).Distinct().Count() != grants.Length || members.Select(member => member.Id).Distinct().Count() != members.Length ||
            !ScopePolicy.IsResourceActive(resource, directory)) throw Invalid();
        var assigned = resource with { AssignedUserIds = [userId] };
        var matches = grants.Where(grant => grant.Scope.Kind == GrantScopeKind.CourierAssignment && ScopePolicy.IsGrantActive(grant, userId, now) &&
            ScopePolicy.IsScopeActive(grant.Scope, directory) && (grant.Permissions.Contains(Permissions.ShipmentRead) || grant.Permissions.Contains(Permissions.DeliveryAttempt)) &&
            new ScopeClause(grant.Id, grant.MembershipId, userId, grant.Scope, []).Matches(assigned) && members.Any(member => member.Id == grant.MembershipId &&
                member.UserId == userId && member.Id > 0 && member.Version > 0 && member.ApprovedBy > 0 && member.ApprovedBy != userId &&
                ScopePolicy.IsEffective(member.Status, member.EffectiveAt, member.ExpiresAt, member.RevokedAt, now) &&
                ScopePolicy.IsScopeActive(member.Scope, directory) && ScopePolicy.ContainsScope(member.Scope, grant.Scope))).ToImmutableArray();
        if (!matches.Any(grant => grant.Permissions.Contains(Permissions.ShipmentRead)) || !matches.Any(grant => grant.Permissions.Contains(Permissions.DeliveryAttempt))) throw Invalid();
        return matches;
    }
    private static AuthorizationDeniedException Invalid() => new("shipment_courier_unavailable");
}
