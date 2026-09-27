using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Organizations;

public sealed partial class OrganizationMasterDataService(IMongoDatabase database, AuthorizationStore store, IdAllocator ids, IClock clock)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Collection(string name) => database.GetCollection<BsonDocument>(name);
    private AuthorizationDecision Decision(ActorContext actor, AuthorizationSnapshot snapshot, string permission) =>
        ScopePolicy.Authorize(actor, snapshot, permission, new(IsAdministrative: true), clock.UtcNow);
    private AuthorizationDecision DemandRead(ActorContext actor, AuthorizationSnapshot snapshot)
    {
        var decision = Decision(actor, snapshot, Permissions.TechnicalConfigure);
        if (!decision.Allowed) decision = Decision(actor, snapshot, Permissions.OrganizationApprove);
        if (!decision.Allowed) throw new AuthorizationDeniedException(decision.Code);
        return decision;
    }
    private static void ValidateVersion(long id, long version)
    {
        if (id <= 0 || version <= 0 || version == long.MaxValue)
            throw new DomainValidationException("invalid_version", "A positive reference and supported expected version are required");
    }
    private async Task<BsonDocument> FindAsync(string collection, long id, IClientSessionHandle session, CancellationToken token) =>
        await Collection(collection).Find(session, F.Eq("_id", id)).FirstOrDefaultAsync(token)
        ?? throw new AuthorizationDeniedException("resource_unavailable");
    private static void EnsurePending(BsonDocument proposal, long version)
    {
        if (proposal["version"].AsInt64 != version || proposal["status"].AsString != "pending")
            throw new PersistenceConflictException("version_conflict", "Proposal changed; refresh before deciding");
    }
    private async Task<(ScopeNode? Node, BsonDocument? Profile, ImmutableArray<ScopeNode> Parents)> ValidateChangeAsync(
        OrganizationChangeRequest request, AuthorizationSnapshot snapshot, IClientSessionHandle session, CancellationToken token)
    {
        ScopeNode? node = request.ScopeId is { } id ? snapshot.Directory.Find(id, request.Kind) : null;
        var parents = OrganizationChangePolicy.ValidateHierarchy(request, snapshot.Directory, node);
        var profile = request.ScopeId is { } scopeId
            ? await Collection("organizationProfiles").Find(session, F.Eq("_id", scopeId)).FirstOrDefaultAsync(token) : null;
        if (request.ChangeKind != OrganizationChangeKind.Create &&
            (node!.Version != request.ExpectedScopeVersion || (profile?["version"].AsInt64 ?? 0) != request.ExpectedProfileVersion))
            throw new PersistenceConflictException("version_conflict", "Organization changed; refresh and submit a new proposal");
        if (profile is not null && (profile["kind"].AsString != AuthorizationBson.Name(request.Kind) || NullableLong(profile, "parentId") != request.ParentId))
            throw new AuthorizationDeniedException("invalid_organization_data");
        var duplicate = F.Eq("kind", AuthorizationBson.Name(request.Kind)) & F.Eq("parentId", Number(request.ParentId)) & F.Eq("normalizedCode", request.Code);
        if (request.ScopeId is { } currentId) duplicate &= F.Ne("_id", currentId);
        if (await Collection("organizationProfiles").Find(session, duplicate).AnyAsync(token))
            throw new PersistenceConflictException("organization_code_exists", "Organization reference code is already used under this parent");
        return (node, profile, parents);
    }
    private Task AuditAsync(long id, ActorContext actor, long subjectId, string operation, AuthorizationCommandMetadata metadata,
        BsonDocument before, BsonDocument after, IClientSessionHandle session, CancellationToken token) =>
        Collection("authorizationHistory").InsertOneAsync(session, new BsonDocument
        {
            { "_id", id }, { "schemaVersion", 1 }, { "actorId", actor.UserId }, { "subjectId", subjectId },
            { "operation", operation }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId },
            { "createdAt", clock.UtcNow.UtcDateTime }, { "actorGrantVersion", actor.GrantVersion },
            { "before", before }, { "after", after }, { "scope", AuthorizationBson.Scope(new GrantScope(GrantScopeKind.System)) }
        }, cancellationToken: token);
    private static BsonValue Number(long? value) => value is { } id ? new BsonInt64(id) : BsonNull.Value;
    private static long? NullableLong(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && !raw.IsBsonNull ? raw.AsInt64 : null;
    private static string? NullableText(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && !raw.IsBsonNull ? raw.AsString : null;
    private static DateTimeOffset? NullableDate(BsonDocument value, string field) => value.TryGetValue(field, out var raw) && !raw.IsBsonNull ? new(raw.ToUniversalTime(), TimeSpan.Zero) : null;
    private static BsonDocument Result(long proposalId, long? scopeId, string status, long version, long? scopeVersion = null,
        long? profileVersion = null, long affectedAccounts = 0) => new()
    {
        { "id", proposalId }, { "subjectId", scopeId ?? proposalId }, { "kind", "organization" }, { "version", version },
        { "status", status }, { "scopeId", Number(scopeId) }, { "scopeVersion", Number(scopeVersion) },
        { "profileVersion", Number(profileVersion) }, { "affectedAccounts", affectedAccounts }
    };
    private static OrganizationChangeResult ParseResult(BsonDocument value) => new(value["id"].AsInt64, NullableLong(value, "scopeId"),
        value["status"].AsString, value["version"].AsInt64, NullableLong(value, "scopeVersion"), NullableLong(value, "profileVersion"), value["affectedAccounts"].AsInt64);
    private static OrganizationChangeRequest Request(BsonDocument value) => OrganizationChangePolicy.Normalize(new(
        Enum.Parse<OrganizationChangeKind>(value["changeKind"].AsString, true), Enum.Parse<ScopeNodeKind>(value["kind"].AsString, true),
        NullableLong(value, "parentId"), value["code"].AsString, value["name"].AsString, NullableLong(value, "scopeId"),
        value["expectedScopeVersion"].AsInt64, value["expectedProfileVersion"].AsInt64));
    private static BsonDocument Before(ScopeNode? node, BsonDocument? profile) => new()
    {
        { "code", profile?.GetValue("code", BsonNull.Value) ?? BsonNull.Value }, { "name", profile?.GetValue("name", BsonNull.Value) ?? BsonNull.Value },
        { "status", node is null ? BsonNull.Value : new BsonString(AuthorizationBson.Name(node.Status)) },
        { "scopeVersion", node?.Version ?? 0L }, { "profileVersion", profile?["version"].AsInt64 ?? 0L }
    };
}
