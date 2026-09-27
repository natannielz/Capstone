using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Identity;

/// <summary>Records an owner-controlled provider recovery request. No password/factor secret input,
/// provider dispatch, email sending or account reactivation is implemented by this service.</summary>
public sealed class AccountRecoveryService(IMongoDatabase database, AuthorizationStore store, IdAllocator ids,
    MongoIdentitySessionStore sessions, SessionPayloadProtector protector, IClock clock)
{
    public async Task<RecoveryResult> InitiateAsync(ActorContext actor, long userId, long expectedSecurityVersion,
        AccountRecoveryKind kind, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || expectedSecurityVersion <= 0 || expectedSecurityVersion == long.MaxValue || !Enum.IsDefined(kind))
            throw new DomainValidationException("invalid_recovery_request", "Recovery requires a current account security revision and supported recovery type");
        var intentId = (await ids.NextAsync("identityProviderIntents", cancellationToken)).Value;
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        var f = Builders<BsonDocument>.Filter;
        var result = await store.ExecuteCommandAsync(actor, "identity.recovery.request", metadata, new { userId, expectedSecurityVersion, kind },
            (snapshot, _, _) => Task.FromResult(new CommandAuthorization(actor.UserId == userId
                ? AssurancePolicy.Satisfies(actor, new(true, TimeSpan.FromMinutes(5)), clock.UtcNow)
                    ? new(true, "allowed", []) : AuthorizationDecision.Deny("fresh_factor_required")
                : ScopePolicy.Authorize(actor, snapshot, Permissions.RecoveryInitiate, new(IsAdministrative: true), clock.UtcNow), [])),
            async (session, token) =>
            {
                var identities = database.GetCollection<BsonDocument>("identities");
                var identity = await identities.Find(session, f.Eq("_id", userId)).FirstOrDefaultAsync(token)
                    ?? throw new AuthorizationDeniedException("resource_unavailable");
                if (!identity.TryGetValue("securityVersion", out var epoch) || !epoch.IsInt64 || epoch.AsInt64 != expectedSecurityVersion)
                    throw new PersistenceConflictException("version_conflict", "The account security revision changed");
                var changed = await identities.UpdateOneAsync(session, f.Eq("_id", userId) & f.Eq("securityVersion", expectedSecurityVersion),
                    Builders<BsonDocument>.Update.Inc("securityVersion", 1L).Inc("fence", 1L), cancellationToken: token);
                if (changed.ModifiedCount != 1) throw new PersistenceConflictException("version_conflict", "The account security revision changed");
                await sessions.RevokeUserSessionsAsync(session, userId, "Account recovery requested", token);
                var actorIdentity = actor.UserId == userId ? identity : await identities.Find(session, f.Eq("_id", actor.UserId)).FirstAsync(token);
                var payload = new BsonDocument
                {
                    { "issuer", identity["issuer"] }, { "subject", identity["subject"] }, { "kind", kind == AccountRecoveryKind.Password ? "password" : "factor" },
                    { "actorGrantVersion", actor.GrantVersion }, { "actorSecurityVersion", actorIdentity["securityVersion"] }
                };
                // The durable intent is written only AFTER local revocation in this transaction.
                // A future dispatcher must have explicit environment approval and reauthorize its actor.
                await database.GetCollection<BsonDocument>("identityProviderIntents").InsertOneAsync(session, new BsonDocument
                {
                    { "_id", intentId }, { "schemaVersion", 1 }, { "kind", kind == AccountRecoveryKind.Password ? "passwordRecovery" : "factorRecovery" },
                    { "userId", userId }, { "invitationId", BsonNull.Value }, { "requestedBy", actor.UserId }, { "approvedBy", BsonNull.Value },
                    { "securityVersion", expectedSecurityVersion + 1 }, { "status", "blocked" }, { "reason", metadata.Reason }, { "createdAt", clock.UtcNow.UtcDateTime },
                    { "protectedPayload", protector.Protect("account-recovery", payload.ToJson()) }, { "version", 1L }
                }, cancellationToken: token);
                await database.GetCollection<BsonDocument>("authorizationHistory").InsertOneAsync(session, new BsonDocument
                {
                    { "_id", historyId }, { "schemaVersion", 1 }, { "actorId", actor.UserId }, { "subjectId", userId },
                    { "operation", "identity.recovery.requested" }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId },
                    { "createdAt", clock.UtcNow.UtcDateTime }, { "scope", AuthorizationBson.Scope(new GrantScope(GrantScopeKind.System)) },
                    { "before", new BsonDocument { { "_id", userId }, { "version", expectedSecurityVersion } } },
                    { "after", new BsonDocument { { "_id", userId }, { "version", expectedSecurityVersion + 1 }, { "status", identity["status"] } } }
                }, cancellationToken: token);
                return new BsonDocument { { "id", intentId }, { "userId", userId }, { "securityVersion", expectedSecurityVersion + 1 }, { "status", "blocked" } };
            }, cancellationToken);
        return new(result["id"].AsInt64, result["userId"].AsInt64, result["securityVersion"].AsInt64, result["status"].AsString);
    }
}
