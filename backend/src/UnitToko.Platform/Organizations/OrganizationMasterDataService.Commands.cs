using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Organizations;

public sealed partial class OrganizationMasterDataService
{
    public async Task<OrganizationProposalResult> ProposeAsync(ActorContext actor, OrganizationChangeRequest request,
        AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        request = OrganizationChangePolicy.Normalize(request); AuthorizationStore.ValidateMetadata(metadata);
        var proposalId = (await ids.NextAsync("organizationProposals", cancellationToken)).Value;
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        var result = await store.ExecuteCommandAsync(actor, "organization.propose", metadata, request,
            (snapshot, _, _) => Task.FromResult(new CommandAuthorization(Decision(actor, snapshot, Permissions.TechnicalConfigure), [])),
            async (session, token) =>
            {
                var snapshot = await store.LoadAsync(actor, session, token);
                var validated = await ValidateChangeAsync(request, snapshot, session, token);
                var impact = await ImpactAsync(request, snapshot.Directory, session, token);
                await store.FenceAsync(actor, validated.Parents.Concat(impact.Nodes.Where(node => node.Status == AuthorizationStatus.Active)).Distinct(), session, token);
                var proposal = new BsonDocument
                {
                    { "_id", proposalId }, { "schemaVersion", 1 }, { "changeKind", AuthorizationBson.Name(request.ChangeKind) },
                    { "scopeId", Number(request.ScopeId) }, { "kind", AuthorizationBson.Name(request.Kind) }, { "parentId", Number(request.ParentId) },
                    { "code", request.Code }, { "normalizedCode", request.Code }, { "name", request.Name },
                    { "expectedScopeVersion", request.ExpectedScopeVersion }, { "expectedProfileVersion", request.ExpectedProfileVersion },
                    { "requestedBy", actor.UserId }, { "reason", metadata.Reason }, { "status", "pending" }, { "version", 1L },
                    { "createdAt", clock.UtcNow.UtcDateTime }, { "before", Before(validated.Node, validated.Profile) }, { "impact", ImpactDocument(impact.Summary) }
                };
                await Collection("organizationProposals").InsertOneAsync(session, proposal, cancellationToken: token);
                await AuditAsync(historyId, actor, request.ScopeId ?? proposalId, "organization.proposed", metadata, new(), proposal, session, token);
                return Result(proposalId, request.ScopeId, "pending", 1);
            }, cancellationToken);
        return new(result["id"].AsInt64, result["status"].AsString, result["version"].AsInt64);
    }

    public async Task<OrganizationChangeResult> ApproveAsync(ActorContext actor, long proposalId, long expectedVersion,
        AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ValidateVersion(proposalId, expectedVersion); AuthorizationStore.ValidateMetadata(metadata);
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        var allocatedScopeId = (await ids.NextAsync("authorizationScopes", cancellationToken)).Value;
        var result = await store.ExecuteCommandAsync(actor, "organization.approve", metadata, new { proposalId, expectedVersion },
            async (snapshot, session, token) =>
            {
                var proposal = await FindAsync("organizationProposals", proposalId, session, token);
                return new(OrganizationChangePolicy.CanDecide(actor, snapshot, proposal["requestedBy"].AsInt64, clock.UtcNow), []);
            }, async (session, token) =>
            {
                var proposal = await FindAsync("organizationProposals", proposalId, session, token); EnsurePending(proposal, expectedVersion);
                var request = Request(proposal);
                var snapshot = await store.LoadAsync(actor, session, token);
                var validated = await ValidateChangeAsync(request, snapshot, session, token);
                var impact = await ImpactAsync(request, snapshot.Directory, session, token);
                if (proposal["impact"]["revision"].AsString != impact.Summary.Revision)
                    throw new PersistenceConflictException("organization_impact_changed", "Affected access changed; withdraw this proposal and review a fresh proposal");
                if (impact.Identities.Any(identity => identity["_id"].AsInt64 == actor.UserId))
                    throw new AuthorizationDeniedException("beneficiary_cannot_approve");
                await store.FenceAsync(actor, validated.Parents.Concat(impact.Nodes.Where(node => node.Status == AuthorizationStatus.Active)).Distinct(), session, token);
                var scopeId = request.ScopeId ?? allocatedScopeId;
                var before = new BsonDocument { { "scope", validated.Node is null ? BsonNull.Value : await FindAsync("authorizationScopes", scopeId, session, token) },
                    { "profile", validated.Profile ?? (BsonValue)BsonNull.Value }, { "proposal", proposal.DeepClone() } };
                if (request.ChangeKind == OrganizationChangeKind.Create)
                    await Collection("authorizationScopes").InsertOneAsync(session, AuthorizationBson.Node(new ScopeNode(scopeId, request.Kind, request.ParentId, AuthorizationStatus.Active, 1)), cancellationToken: token);
                else
                {
                    var update = Builders<BsonDocument>.Update.Inc("fence", 1L);
                    if (request.ChangeKind == OrganizationChangeKind.Reactivate) update = update.Set("status", "active");
                    await MongoUnitOfWork.RequireVersionAsync(Collection("authorizationScopes"), session,
                        F.Eq("_id", scopeId) & F.Eq("kind", AuthorizationBson.Name(request.Kind)) & F.Eq("status", AuthorizationBson.Name(validated.Node!.Status)),
                        request.ExpectedScopeVersion, update, token);
                }
                await WriteProfileAsync(scopeId, proposalId, request, validated.Profile, actor.UserId, session, token);
                if (request.ChangeKind == OrganizationChangeKind.Reactivate)
                    await InvalidateAffectedAccountsAsync(impact, session, token);
                await MongoUnitOfWork.RequireVersionAsync(Collection("organizationProposals"), session,
                    F.Eq("_id", proposalId) & F.Eq("status", "pending"), expectedVersion,
                    Builders<BsonDocument>.Update.Set("status", "approved").Set("approvedBy", actor.UserId)
                        .Set("approvedAt", clock.UtcNow.UtcDateTime).Set("resultScopeId", scopeId), token);
                var after = new BsonDocument { { "scope", await FindAsync("authorizationScopes", scopeId, session, token) },
                    { "profile", await FindAsync("organizationProfiles", scopeId, session, token) }, { "proposal", await FindAsync("organizationProposals", proposalId, session, token) } };
                await AuditAsync(historyId, actor, scopeId, "organization.approved", metadata, before, after, session, token);
                return Result(proposalId, scopeId, "approved", expectedVersion + 1, request.ExpectedScopeVersion + 1,
                    request.ExpectedProfileVersion + 1, impact.Summary.AffectedAccounts);
            }, cancellationToken);
        return ParseResult(result);
    }

    public Task<OrganizationChangeResult> RejectAsync(ActorContext actor, long proposalId, long expectedVersion,
        AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default) =>
        CloseAsync(actor, proposalId, expectedVersion, metadata, false, cancellationToken);
    public Task<OrganizationChangeResult> WithdrawAsync(ActorContext actor, long proposalId, long expectedVersion,
        AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default) =>
        CloseAsync(actor, proposalId, expectedVersion, metadata, true, cancellationToken);
    private async Task<OrganizationChangeResult> CloseAsync(ActorContext actor, long proposalId, long expectedVersion,
        AuthorizationCommandMetadata metadata, bool withdraw, CancellationToken cancellationToken)
    {
        ValidateVersion(proposalId, expectedVersion); AuthorizationStore.ValidateMetadata(metadata);
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        var result = await store.ExecuteCommandAsync(actor, withdraw ? "organization.withdraw" : "organization.reject", metadata, new { proposalId, expectedVersion },
            async (snapshot, session, token) =>
            {
                var proposal = await FindAsync("organizationProposals", proposalId, session, token);
                return new(OrganizationChangePolicy.CanDecide(actor, snapshot, proposal["requestedBy"].AsInt64, clock.UtcNow, withdraw), []);
            }, async (session, token) =>
            {
                var proposal = await FindAsync("organizationProposals", proposalId, session, token); EnsurePending(proposal, expectedVersion);
                var status = withdraw ? "withdrawn" : "rejected";
                await MongoUnitOfWork.RequireVersionAsync(Collection("organizationProposals"), session,
                    F.Eq("_id", proposalId) & F.Eq("status", "pending"), expectedVersion,
                    Builders<BsonDocument>.Update.Set("status", status).Set("decisionBy", actor.UserId)
                        .Set("decisionAt", clock.UtcNow.UtcDateTime).Set("decisionReason", metadata.Reason), token);
                var after = await FindAsync("organizationProposals", proposalId, session, token);
                await AuditAsync(historyId, actor, NullableLong(proposal, "scopeId") ?? proposalId,
                    "organization." + status, metadata, proposal, after, session, token);
                return Result(proposalId, NullableLong(proposal, "scopeId"), status, expectedVersion + 1);
            }, cancellationToken);
        return ParseResult(result);
    }
    private Task WriteProfileAsync(long scopeId, long proposalId, OrganizationChangeRequest request, BsonDocument? existing,
        long actorId, IClientSessionHandle session, CancellationToken token)
    {
        if (existing is null) return Collection("organizationProfiles").InsertOneAsync(session, new BsonDocument
        {
            { "_id", scopeId }, { "schemaVersion", 1 }, { "kind", AuthorizationBson.Name(request.Kind) }, { "parentId", Number(request.ParentId) },
            { "code", request.Code }, { "normalizedCode", request.Code }, { "name", request.Name }, { "version", 1L },
            { "createdBy", actorId }, { "updatedBy", actorId }, { "createdAt", clock.UtcNow.UtcDateTime },
            { "updatedAt", clock.UtcNow.UtcDateTime }, { "approvedProposalId", proposalId }
        }, cancellationToken: token);
        return MongoUnitOfWork.RequireVersionAsync(Collection("organizationProfiles"), session, F.Eq("_id", scopeId), request.ExpectedProfileVersion,
            Builders<BsonDocument>.Update.Set("code", request.Code).Set("normalizedCode", request.Code).Set("name", request.Name)
                .Set("updatedBy", actorId).Set("updatedAt", clock.UtcNow.UtcDateTime).Set("approvedProposalId", proposalId), token);
    }
}
