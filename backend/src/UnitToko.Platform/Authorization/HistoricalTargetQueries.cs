using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.Authorization;

public sealed record HistoricalTargetSummary(long Id, ScopeNodeKind Kind, long? ParentId, string? Name,
    string? Code, string Status, long Version, GrantScope Scope);
public sealed record HistoricalTargetPage(long Total, ImmutableArray<HistoricalTargetSummary> Items);

public sealed partial class AccessAdministrationService
{
    public Task<HistoricalTargetPage> ListHistoricalTargetsAsync(ActorContext actor, long userId, long membershipId,
        int offset, int limit, CancellationToken cancellationToken = default, AuthorizationReadMetadata? audit = null)
    {
        AuthorizationStore.ValidatePage(offset, limit);
        if (userId <= 0 || membershipId <= 0)
            throw new DomainValidationException("invalid_historical_target_request", "An account and approved parent membership are required");
        return AuditedReadAsync(actor, "access.subject.read", audit,
            new() { { "subjectId", userId }, { "offset", offset }, { "limit", limit } }, async (snapshot, session, token) =>
            {
                var authority = DemandAdministrative(actor, snapshot, Permissions.AccessPropose);
                await RequireActiveIdentityAsync(userId, session, token);
                var member = AuthorizationBson.Membership(await FindAsync("memberships", membershipId, session, token));
                void ValidateMember()
                {
                    if (member.UserId != userId || member.Version <= 0 || member.ApprovedBy <= 0 || member.ApprovedBy == userId ||
                        member.Scope.Kind != GrantScopeKind.BuyingOrganization ||
                        !ScopePolicy.IsEffective(member.Status, member.EffectiveAt, member.ExpiresAt, member.RevokedAt, clock.UtcNow) ||
                        !ScopePolicy.IsScopeActive(member.Scope, snapshot.Directory))
                        throw new AuthorizationDeniedException("approved_membership_required");
                }
                ValidateMember();
                GrantScope Target(ScopeNode node) => node.Kind == ScopeNodeKind.Branch
                    ? new(GrantScopeKind.BuyingBranch, member.Scope.OrganizationId, node.Id)
                    : new(GrantScopeKind.BuyingDivision, member.Scope.OrganizationId, node.ParentId, node.Id);
                var nodes = snapshot.Directory.Nodes.Where(node => node.Kind is ScopeNodeKind.Branch or ScopeNodeKind.Division)
                    .Where(node => HistoricalReadPolicy.IsTargetEligible(Target(node), snapshot.Directory))
                    .OrderBy(node => node.Id).ToArray();
                var page = nodes.Skip(offset).Take(limit).ToArray();
                var profiles = await Collection("organizationProfiles").Find(session, F.In("_id", page.Select(node => node.Id)))
                    .Project<BsonDocument>(new BsonDocument { { "_id", 1 }, { "name", 1 }, { "code", 1 } }).ToListAsync(token);
                static string? Label(BsonDocument? profile, string field) => profile is not null && profile.TryGetValue(field, out var value) &&
                    value.IsString && value.AsString.Length is > 0 and <= 256 && !value.AsString.Any(char.IsControl) ? value.AsString : null;
                var items = page.Select(node =>
                {
                    var profile = profiles.SingleOrDefault(value => value["_id"].IsInt64 && value["_id"].AsInt64 == node.Id);
                    return new HistoricalTargetSummary(node.Id, node.Kind, node.ParentId, Label(profile, "name"), Label(profile, "code"),
                        AuthorizationBson.Name(node.Status), node.Version, Target(node));
                }).ToImmutableArray();
                var grants = snapshot.Grants.Where(grant => authority.GrantIds.Contains(grant.Id)).ToArray();
                var members = snapshot.Memberships.Where(value => grants.Any(grant => grant.MembershipId == value.Id))
                    .Append(member).DistinctBy(value => value.Id).ToArray();
                var activeNodes = grants.SelectMany(grant => ScopePolicy.RequiredNodes(grant.Scope, snapshot.Directory))
                    .Concat(members.SelectMany(value => ScopePolicy.RequiredNodes(value.Scope, snapshot.Directory))).Distinct();
                var fence = new HistoricalAuthorizationFence(database, store);
                await fence.FenceAsync(actor, activeNodes, nodes, grants, members, session, token);
                await fence.FenceBeneficiaryAsync(userId, session, token);
                DemandAdministrative(actor, snapshot, Permissions.AccessPropose);
                ValidateMember();
                return new AuditedValue<HistoricalTargetPage>(new(nodes.LongLength, items), items.Length, [new(GrantScopeKind.System)]);
            }, cancellationToken);
    }
}
