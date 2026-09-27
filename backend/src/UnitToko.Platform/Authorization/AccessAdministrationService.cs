using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Authorization;

public sealed partial class AccessAdministrationService(IMongoDatabase database, AuthorizationStore store, IdAllocator ids, IClock clock)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private static readonly ResourceScope Administrative = new(IsAdministrative: true);
    private IMongoCollection<BsonDocument> Collection(string name) => database.GetCollection<BsonDocument>(name);
    private AuthorizationDecision DemandAdministrative(ActorContext actor, AuthorizationSnapshot snapshot, string permission)
    {
        var decision = ScopePolicy.Authorize(actor, snapshot, permission, Administrative, clock.UtcNow);
        if (!decision.Allowed) throw new AuthorizationDeniedException(decision.Code);
        return decision;
    }
    private CommandAuthorization AuthorizeScope(ActorContext actor, AuthorizationSnapshot snapshot, string permission, GrantScope scope)
    {
        var query = ScopePolicy.Compile(actor, snapshot, permission, clock.UtcNow); AuthorizationStore.Require(query);
        var matches = query.Clauses.Where(clause => ScopePolicy.ContainsScope(clause.Scope, scope)).Select(clause => clause.GrantId).ToImmutableArray();
        if (matches.IsEmpty) throw new AuthorizationDeniedException("scope_denied");
        // This helper is used only for removing access. The caller's active authority is
        // fenced by ExecuteCommandAsync; inactive target scopes must remain revocable.
        return new(new(true, "allowed", matches), []);
    }
    private static void ValidateVersion(long id, long version)
    {
        if (id <= 0 || version <= 0 || version == long.MaxValue) throw new DomainValidationException("invalid_version", "A positive ID and supported expected version are required");
    }
    private void ValidateEffective(long userId, DateTimeOffset effectiveAt, DateTimeOffset? expiresAt)
    {
        if (userId <= 0 || effectiveAt == default || expiresAt is { } end && (end <= effectiveAt || end <= clock.UtcNow))
            throw new DomainValidationException("invalid_effective_period", "Access requires a valid effective period");
    }
    private BsonDocument Proposal(long id, string kind, long userId, GrantScope scope, long requestedBy, string reason, BsonDocument request) => new()
    {
        { "_id", id }, { "schemaVersion", AuthorizationBson.SchemaVersion }, { "kind", kind }, { "subjectId", userId },
        { "scope", AuthorizationBson.Scope(scope) }, { "requestedBy", requestedBy }, { "reason", reason }, { "request", request },
        { "status", "pending" }, { "version", 1L }, { "createdAt", clock.UtcNow.UtcDateTime }
    };
    private async Task<BsonDocument> FindAsync(string collection, long id, IClientSessionHandle session, CancellationToken token) =>
        await Collection(collection).Find(session, F.Eq("_id", id)).FirstOrDefaultAsync(token) ?? throw new AuthorizationDeniedException("resource_unavailable");
    private async Task<BsonDocument> FindProposalAsync(long id, string kind, IClientSessionHandle session, CancellationToken token)
    {
        var proposal = await FindAsync("authorizationProposals", id, session, token);
        if (proposal["kind"].AsString != kind) throw new AuthorizationDeniedException("resource_unavailable");
        return proposal;
    }
    private static void EnsurePending(BsonDocument proposal, long version)
    {
        if (proposal["version"].AsInt64 != version || proposal["status"].AsString != "pending")
            throw new PersistenceConflictException("version_conflict", "Proposal changed; refresh and try again");
    }
    private Task ApproveProposalAsync(BsonDocument proposal, long version, long actorId, long resultId, IClientSessionHandle session, CancellationToken token) =>
        MongoUnitOfWork.RequireVersionAsync(Collection("authorizationProposals"), session, F.Eq("_id", proposal["_id"]) & F.Eq("status", "pending"), version,
            Builders<BsonDocument>.Update.Set("status", "approved").Set("approvedBy", actorId).Set("approvedAt", clock.UtcNow.UtcDateTime).Set("resultId", resultId), token);
    private async Task RequireActiveIdentityAsync(long userId, IClientSessionHandle session, CancellationToken token)
    {
        if (await Collection("identities").Find(session, F.Eq("_id", userId) & F.Eq("status", "active")).FirstOrDefaultAsync(token) is null)
            throw new AuthorizationDeniedException("beneficiary_inactive");
    }
    private async Task<long> BumpIdentityAsync(long userId, IClientSessionHandle session, CancellationToken token, bool requireActive = true)
    {
        var filter = F.Eq("_id", userId) & (requireActive ? F.Eq("status", "active") : F.Empty);
        var identity = await Collection("identities").FindOneAndUpdateAsync(session, filter,
            Builders<BsonDocument>.Update.Inc("grantVersion", 1L).Inc("fence", 1L), new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, token);
        return identity is null ? throw new AuthorizationDeniedException("beneficiary_inactive") : identity["grantVersion"].AsInt64;
    }
    private Task AuditAsync(long id, ActorContext actor, long subjectId, string operation, AuthorizationCommandMetadata metadata,
        BsonDocument before, BsonDocument after, IClientSessionHandle session, CancellationToken token) =>
        Collection("authorizationHistory").InsertOneAsync(session, new BsonDocument
        {
            { "_id", id }, { "schemaVersion", AuthorizationBson.SchemaVersion }, { "actorId", actor.UserId }, { "subjectId", subjectId },
            { "operation", operation }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "createdAt", clock.UtcNow.UtcDateTime },
            { "actorGrantVersion", actor.GrantVersion }, { "before", before }, { "after", after }
        }, cancellationToken: token);
    private static BsonDocument ProposalResult(BsonDocument value) => new()
        { { "id", value["_id"] }, { "kind", value["kind"] }, { "subjectId", value["subjectId"] }, { "status", value["status"] }, { "version", value["version"] } };
    private static AccessProposalResult ParseProposalResult(BsonDocument value) => new(value["id"].AsInt64, value["kind"].AsString, value["subjectId"].AsInt64, value["status"].AsString, value["version"].AsInt64);
    private static BsonDocument ChangeResult(long id, long userId, long version, long grantVersion, string status) => new()
        { { "id", id }, { "subjectId", userId }, { "version", version }, { "grantVersion", grantVersion }, { "status", status } };
    private static AccessChangeResult ParseChangeResult(BsonDocument value) => new(value["id"].AsInt64, value["subjectId"].AsInt64, value["version"].AsInt64, value["grantVersion"].AsInt64, value["status"].AsString);
}
