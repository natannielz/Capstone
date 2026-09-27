using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Authorization;

public sealed partial class AccessAdministrationService
{
    public Task<AccessProposalResult> RejectProposalAsync(ActorContext actor, long proposalId, long expectedVersion,
        AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default) =>
        ResolveProposalAsync(actor, proposalId, expectedVersion, metadata, false, cancellationToken);
    public Task<AccessProposalResult> WithdrawProposalAsync(ActorContext actor, long proposalId, long expectedVersion,
        AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default) =>
        ResolveProposalAsync(actor, proposalId, expectedVersion, metadata, true, cancellationToken);

    private async Task<AccessProposalResult> ResolveProposalAsync(ActorContext actor, long proposalId, long expectedVersion,
        AuthorizationCommandMetadata metadata, bool withdraw, CancellationToken cancellationToken)
    {
        ValidateVersion(proposalId, expectedVersion);
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        var resolution = withdraw ? "withdrawn" : "rejected";
        BsonDocument? proposal = null;
        var result = await store.ExecuteCommandAsync(actor, withdraw ? "access.proposal.withdraw" : "access.proposal.reject", metadata,
            new { proposalId, expectedVersion }, async (snapshot, session, token) =>
            {
                proposal = await FindAsync("authorizationProposals", proposalId, session, token);
                return ProposalResolutionAuthority(actor, snapshot, proposal, withdraw);
            }, async (session, token) =>
            {
                EnsurePending(proposal!, expectedVersion);
                await MongoUnitOfWork.RequireVersionAsync(Collection("authorizationProposals"), session,
                    F.Eq("_id", proposalId) & F.Eq("status", "pending"), expectedVersion,
                    Builders<BsonDocument>.Update.Set("status", "rejected").Set("resolutionKind", resolution)
                        .Set("resolvedBy", actor.UserId).Set("resolvedAt", clock.UtcNow.UtcDateTime).Set("resolutionReason", metadata.Reason), token);
                var after = await FindAsync("authorizationProposals", proposalId, session, token);
                await AuditAsync(historyId, actor, proposal!["subjectId"].AsInt64, "access.proposal." + resolution, metadata, proposal, after, session, token);
                return ProposalResult(after);
            }, cancellationToken);
        return ParseProposalResult(result);
    }

    private CommandAuthorization ProposalResolutionAuthority(ActorContext actor, AuthorizationSnapshot snapshot, BsonDocument proposal, bool withdraw)
    {
        var kind = proposal["kind"].AsString;
        if (kind is not ("grant" or "membership")) throw new AuthorizationDeniedException("resource_unavailable");
        if (withdraw)
        {
            if (proposal["requestedBy"].AsInt64 != actor.UserId) throw new AuthorizationDeniedException("resource_unavailable");
            return new(DemandAdministrative(actor, snapshot, kind == "grant" ? Permissions.AccessPropose : Permissions.MembershipPropose), []);
        }
        var permission = kind == "grant" ? Permissions.AccessApprove : Permissions.MembershipApprove;
        var independence = ApprovalPolicy.Evaluate(actor.UserId, permission,
            new(proposal["version"].AsInt64, proposal["version"].AsInt64, ProposedBy: proposal["requestedBy"].AsInt64, BeneficiaryId: proposal["subjectId"].AsInt64));
        if (!independence.Allowed) throw new AuthorizationDeniedException(independence.Code);
        // Rejection removes a pending request; it requires scoped approval authority, not permission to grant its requested powers.
        return AuthorizeScope(actor, snapshot, permission, AuthorizationBson.Scope(proposal["scope"].AsBsonDocument));
    }
    private bool MayResolve(ActorContext actor, AuthorizationSnapshot snapshot, BsonDocument proposal, bool withdraw)
    {
        try { return ProposalResolutionAuthority(actor, snapshot, proposal, withdraw).Decision.Allowed; }
        catch (AuthorizationDeniedException) { return false; }
    }
    private static string ProposalStatus(BsonDocument proposal) => proposal["status"].AsString == "rejected" &&
        proposal.TryGetValue("resolutionKind", out var disposition) && disposition == "withdrawn" ? "withdrawn" : proposal["status"].AsString;
}
