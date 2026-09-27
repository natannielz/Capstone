using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Platform.Authorization;

public sealed partial class AccessAdministrationService
{
    public Task<AccessSubjectSummary> ReadSubjectAsync(ActorContext actor, long userId, CancellationToken cancellationToken = default, AuthorizationReadMetadata? audit = null) =>
        AuditedReadAsync(actor, "access.subject.read", audit, new("subjectId", userId), async (snapshot, session, token) =>
        {
            var all = AdministrativeRead(actor, snapshot);
            var authorities = AdministrationScopes(actor, snapshot);
            if (!all && authorities.Length == 0) throw new AuthorizationDeniedException("permission_denied");
            var memberships = (await Collection("memberships").Find(session, F.Eq("userId", userId)).ToListAsync(token))
                .Select(AuthorizationBson.Membership).Where(membership => all || authorities.Any(scope => ScopePolicy.ContainsScope(scope, membership.Scope))).ToArray();
            if (!all && memberships.Length == 0) throw new AuthorizationDeniedException("resource_unavailable");
            var identity = await FindAsync("identities", userId, session, token);
            var grants = (await Collection("authorizationGrants").Find(session, F.Eq("userId", userId)).ToListAsync(token))
                .Select(AuthorizationBson.Grant).Where(grant => all || authorities.Any(scope => ScopePolicy.ContainsScope(scope, grant.Scope))).ToArray();
            bool Revoke(GrantScope scope) => ScopePolicy.Compile(actor, snapshot, Permissions.AccessRevoke, clock.UtcNow)
                .Clauses.Any(clause => ScopePolicy.ContainsScope(clause.Scope, scope));
            var result = new AccessSubjectSummary(userId, identity["status"].AsString, identity["grantVersion"].AsInt64,
                memberships.Select(membership => new MembershipSummary(membership.Id, membership.Scope, AuthorizationBson.Name(membership.Status), membership.Version,
                    membership.Status == AuthorizationStatus.Active && Revoke(membership.Scope) ? ["suspend"] : [], membership.EffectiveAt, membership.ExpiresAt)).ToImmutableArray(),
                grants.Select(grant => new GrantSummary(grant.Id, grant.MembershipId, grant.TemplateId, grant.TemplateVersion, grant.Permissions,
                    grant.Scope, AuthorizationBson.Name(grant.Status), grant.Version, grant.Status != AuthorizationStatus.Revoked && Revoke(grant.Scope) ? ["revoke"] : [],
                    grant.DelegablePermissions.IsDefault ? [] : grant.DelegablePermissions, grant.EffectiveAt, grant.ExpiresAt)).ToImmutableArray());
            return new AuditedValue<AccessSubjectSummary>(result, 1, ReadAuthorityScopes(actor, snapshot));
        }, cancellationToken);

    public Task<IReadOnlyList<AccessProposalSummary>> ListProposalsAsync(ActorContext actor, int offset, int limit, CancellationToken cancellationToken = default, AuthorizationReadMetadata? audit = null)
    {
        AuthorizationStore.ValidatePage(offset, limit);
        return AuditedReadAsync<IReadOnlyList<AccessProposalSummary>>(actor, "access.proposals.read", audit, new() { { "offset", offset }, { "limit", limit } }, async (snapshot, session, token) =>
        {
            var all = AdministrativeRead(actor, snapshot);
            var scopes = AdministrationScopes(actor, snapshot);
            if (!all && scopes.Length == 0) throw new AuthorizationDeniedException("permission_denied");
            var filter = all ? F.Empty : F.Or(scopes.Select(scope => ScopeContainmentFilter(scope, "scope")));
            var proposals = await Collection("authorizationProposals").Find(session, filter).Sort(new BsonDocument("_id", -1)).Skip(offset).Limit(limit).ToListAsync(token);
            var result = new List<AccessProposalSummary>();
            foreach (var proposal in proposals)
            {
                var kind = proposal["kind"].AsString;
                var candidate = kind == "grant" ? AuthorizationBson.Grant(proposal["request"].AsBsonDocument, true) : null;
                var allowed = false;
                if (proposal["status"].AsString == "pending")
                {
                    try
                    {
                        if (candidate is not null)
                        {
                            if (candidate.TemplateId == HistoricalReadPolicy.TemplateId)
                            {
                                ValidateHistoricalProposal(proposal, candidate);
                                allowed = (await AuthorizeHistoricalGrantApprovalAsync(actor, snapshot, candidate, session, token)).Decision.Allowed;
                            }
                            else
                            {
                                await ValidateBeneficiaryAsync(candidate, snapshot.Directory, session, token);
                                allowed = AccessGrantPolicy.CanApprove(actor, snapshot, candidate, clock.UtcNow).Allowed;
                            }
                        }
                        else if (kind == "membership")
                        {
                            var membership = DraftMembership(proposal, 1, actor.UserId);
                            await RequireActiveIdentityAsync(membership.UserId, session, token);
                            ValidateEffective(membership.UserId, membership.EffectiveAt, membership.ExpiresAt);
                            allowed = AccessGrantPolicy.CanApproveMembership(actor, snapshot, membership, proposal["requestedBy"].AsInt64, clock.UtcNow).Allowed;
                        }
                    }
                    catch (AuthorizationDeniedException) { }
                    catch (UnitToko.Modules.Contracts.DomainValidationException) { }
                }
                var actions = ImmutableArray.CreateBuilder<string>();
                if (proposal["status"].AsString == "pending")
                {
                    if (allowed) actions.Add("approve");
                    if (MayResolve(actor, snapshot, proposal, false)) actions.Add("reject");
                    if (MayResolve(actor, snapshot, proposal, true)) actions.Add("withdraw");
                }
                result.Add(new(proposal["_id"].AsInt64, kind, proposal["subjectId"].AsInt64, AuthorizationBson.Scope(proposal["scope"].AsBsonDocument),
                    proposal["requestedBy"].AsInt64, proposal["reason"].AsString, ProposalStatus(proposal), proposal["version"].AsInt64,
                    new(proposal["createdAt"].ToUniversalTime(), TimeSpan.Zero), candidate?.TemplateId, candidate?.Permissions ?? [], actions.ToImmutable(),
                    candidate?.DelegablePermissions ?? [], new(proposal["request"]["effectiveAt"].ToUniversalTime(), TimeSpan.Zero),
                    proposal["request"].AsBsonDocument.TryGetValue("expiresAt", out var expiry) ? new DateTimeOffset(expiry.ToUniversalTime(), TimeSpan.Zero) : null,
                    candidate?.MembershipId, candidate?.TemplateVersion));
            }
            return new(result, result.Count, ReadAuthorityScopes(actor, snapshot));
        }, cancellationToken);
    }
    private bool AdministrativeRead(ActorContext actor, AuthorizationSnapshot snapshot) =>
        ScopePolicy.Compile(actor, snapshot, Permissions.AccessAuditRead, clock.UtcNow).Matches(Administrative) ||
        ScopePolicy.Compile(actor, snapshot, Permissions.AccessPropose, clock.UtcNow).Matches(Administrative) ||
        ScopePolicy.Compile(actor, snapshot, Permissions.MembershipPropose, clock.UtcNow).Matches(Administrative);
    private ImmutableArray<GrantScope> AdministrationScopes(ActorContext actor, AuthorizationSnapshot snapshot) =>
        new[] { Permissions.AccessApprove, Permissions.MembershipApprove, Permissions.AccessRevoke }.SelectMany(permission =>
            ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow).Clauses.Select(clause => clause.Scope)).Distinct().ToImmutableArray();

    internal static FilterDefinition<BsonDocument> ScopeContainmentFilter(GrantScope scope, string prefix)
    {
        var sameOrganization = F.Eq(prefix + ".organizationId", scope.OrganizationId);
        var kind = prefix + ".kind";
        if (scope.Kind == GrantScopeKind.System) return F.Eq(kind, "system");
        var buyingKinds = new[] { "buyerOwned", "buyingDivision", "buyingBranch", "buyingOrganization" };
        return scope.Kind switch
        {
            GrantScopeKind.BuyingOrganization => sameOrganization & F.In(kind, buyingKinds),
            GrantScopeKind.BuyingBranch => sameOrganization & F.Eq(prefix + ".branchId", scope.BranchId) & F.In(kind, buyingKinds[..3]),
            GrantScopeKind.BuyingDivision => sameOrganization & F.Eq(prefix + ".branchId", scope.BranchId) & F.Eq(prefix + ".divisionId", scope.DivisionId) & F.In(kind, buyingKinds[..2]),
            GrantScopeKind.BuyerOwned => sameOrganization & F.Eq(kind, "buyerOwned") & F.Eq(prefix + ".branchId", scope.BranchId) & F.Eq(prefix + ".divisionId", scope.DivisionId),
            _ => sameOrganization & F.Eq(prefix + ".shopId", scope.ShopId) & F.Eq(prefix + ".channelId", scope.ChannelId) &
                (scope.Kind == GrantScopeKind.FulfillmentShop ? F.In(kind, new[] { "fulfillmentShop", "fulfillmentWarehouse", "courierAssignment" }) :
                    scope.Kind == GrantScopeKind.FulfillmentWarehouse ? F.Eq(prefix + ".warehouseId", scope.WarehouseId) & F.In(kind, new[] { "fulfillmentWarehouse", "courierAssignment" }) :
                        F.Eq(kind, "courierAssignment") & (scope.WarehouseId is null ? F.Empty : F.Eq(prefix + ".warehouseId", scope.WarehouseId)))
        };
    }
}
