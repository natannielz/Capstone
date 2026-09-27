using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Identity;

public sealed partial class IdentityOnboardingService(IMongoDatabase database, AuthorizationStore store, IdAllocator ids,
    SessionPayloadProtector protector, SessionPolicy policy, IClock clock)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private static readonly ResourceScope Administrative = new(IsAdministrative: true);
    private IMongoCollection<BsonDocument> Collection(string name) => database.GetCollection<BsonDocument>(name);
    private static BsonValue Number(long? value) => value is { } id ? new BsonInt64(id) : BsonNull.Value;
    private static BsonValue Date(DateTimeOffset? value) => value is { } at ? new BsonDateTime(at.UtcDateTime) : BsonNull.Value;
    private static DateTimeOffset At(BsonDocument value, string field) => new(value[field].ToUniversalTime(), TimeSpan.Zero);
    private static DateTimeOffset? OptionalAt(BsonDocument value, string field) => value.TryGetValue(field, out var at) && !at.IsBsonNull ? new(at.ToUniversalTime(), TimeSpan.Zero) : null;
    private static long? OptionalLong(BsonDocument value, string field) => value.TryGetValue(field, out var id) && id.IsInt64 ? id.AsInt64 : null;
    private static string SecretHash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
    private static long SecurityVersion(BsonDocument identity) => identity.TryGetValue("securityVersion", out var value) && value.IsInt64 && value.AsInt64 > 0 && value.AsInt64 < long.MaxValue
        ? value.AsInt64 : throw new AuthorizationDeniedException("identity_security_version_unavailable");
    private async Task<BsonDocument> FindAsync(string collection, long id, IClientSessionHandle session, CancellationToken token) =>
        await Collection(collection).Find(session, F.Eq("_id", id)).FirstOrDefaultAsync(token) ?? throw new AuthorizationDeniedException("resource_unavailable");
    private AuthorizationDecision AdministrativeDecision(ActorContext actor, AuthorizationSnapshot snapshot, string permission) =>
        ScopePolicy.Authorize(actor, snapshot, permission, Administrative, clock.UtcNow);
    private static AuthorizationDecision Combine(params AuthorizationDecision[] decisions) => decisions.FirstOrDefault(decision => !decision.Allowed) ??
        new(true, "allowed", decisions.SelectMany(decision => decision.GrantIds).Distinct().ToImmutableArray());
    private static void Revision(long id, long version)
    {
        if (id <= 0 || version <= 0 || version == long.MaxValue) throw new DomainValidationException("invalid_version", "A supported positive reference and version are required");
    }
    private static void Pending(BsonDocument invitation, long expectedVersion, string status)
    {
        if (invitation["version"].AsInt64 != expectedVersion || invitation["status"].AsString != status)
            throw new PersistenceConflictException("version_conflict", "Invitation changed; refresh its current state");
    }
    private static BsonValue RoleDocument(InvitationRoleSnapshot? role) => role is null ? BsonNull.Value : new BsonDocument
    {
        { "templateId", role.TemplateId }, { "templateVersion", role.TemplateVersion },
        { "permissions", new BsonArray(role.Permissions) }, { "delegablePermissions", new BsonArray(role.DelegablePermissions) }
    };
    private static InvitationRoleSnapshot? Role(BsonDocument invitation) => invitation["role"].IsBsonNull ? null : new(
        invitation["role"]["templateId"].AsString, invitation["role"]["templateVersion"].AsInt32,
        invitation["role"]["permissions"].AsBsonArray.Select(value => value.AsString).ToImmutableArray(),
        invitation["role"]["delegablePermissions"].AsBsonArray.Select(value => value.AsString).ToImmutableArray());
    private static BsonDocument Result(BsonDocument invitation) => new()
    {
        { "id", invitation["_id"] }, { "userId", invitation["userId"] }, { "version", invitation["version"] }, { "status", invitation["status"] }
    };
    private static InvitationResult ParseResult(BsonDocument result) => new(result["id"].AsInt64, result["userId"].AsInt64, result["version"].AsInt64, result["status"].AsString);
    private static readonly HashSet<string> AuditFields = new(StringComparer.Ordinal)
    {
        "_id", "schemaVersion", "userId", "membershipId", "grantId", "scope", "role", "requestedBy", "requesterGrantVersion", "requesterSecurityVersion",
        "reason", "createdAt", "expiresAt", "accessExpiresAt", "status", "version", "approvedBy", "approvedAt", "approvedFactorAt",
        "approverGrantVersion", "approverSecurityVersion", "authorityGrantIds", "providerIntentId", "resolvedBy", "resolvedAt", "resolutionReason", "redeemedAt",
        "providerIssuer", "providerConnectionId"
    };
    private static BsonDocument AuditState(BsonDocument value) => new(value.Elements.Where(element => AuditFields.Contains(element.Name)));
    private Task AuditAsync(long id, long actorId, long subjectId, string operation, AuthorizationCommandMetadata metadata,
        GrantScope scope, BsonDocument before, BsonDocument after, IClientSessionHandle session, CancellationToken token) =>
        Collection("authorizationHistory").InsertOneAsync(session, new BsonDocument
        {
            { "_id", id }, { "schemaVersion", 1 }, { "actorId", actorId }, { "subjectId", subjectId }, { "operation", operation },
            { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId }, { "createdAt", clock.UtcNow.UtcDateTime },
            { "scope", AuthorizationBson.Scope(scope) }, { "before", AuditState(before) }, { "after", AuditState(after) }
        }, cancellationToken: token);
}
