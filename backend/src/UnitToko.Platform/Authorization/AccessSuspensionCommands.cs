using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Authorization;

public sealed partial class AccessAdministrationService
{
    public async Task<AccessChangeResult> SuspendIdentityAsync(ActorContext actor, long userId, long expectedVersion, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ValidateVersion(userId, expectedVersion);
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        var result = await store.ExecuteCommandAsync(actor, "access.identity.suspend", metadata, new { userId, expectedVersion },
            (snapshot, _, _) =>
            {
                var decision = DemandAdministrative(actor, snapshot, Permissions.AccessRevoke);
                if (actor.UserId == userId)
                    throw new DomainValidationException("identity_self_suspension_forbidden", "The account issuing this command cannot suspend itself");
                return Task.FromResult(new CommandAuthorization(decision, []));
            },
            async (session, token) =>
            {
                var before = await FindAsync("identities", userId, session, token);
                var update = await Collection("identities").FindOneAndUpdateAsync(session,
                    F.Eq("_id", userId) & F.Eq("grantVersion", expectedVersion) & F.Eq("status", "active"),
                    Builders<BsonDocument>.Update.Set("status", "suspended").Inc("grantVersion", 1L).Inc("fence", 1L),
                    new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, token);
                if (update is null) throw new PersistenceConflictException("version_conflict", "Identity changed; refresh and try again");
                await AuditAsync(historyId, actor, userId, "access.identity.suspended", metadata, SafeIdentity(before), SafeIdentity(update), session, token);
                return ChangeResult(userId, userId, update["grantVersion"].AsInt64, update["grantVersion"].AsInt64, "suspended");
            }, cancellationToken, complete: async (snapshot, session, token) =>
            {
                // Original commands and recorded-key replays retain current factor,
                // grant/membership expiry and live actor/session authority through the final write.
                DemandAdministrative(actor, snapshot, Permissions.AccessRevoke);
                await store.FenceAsync(actor, [], session, token);
                DemandAdministrative(actor, snapshot, Permissions.AccessRevoke);
            });
        return ParseChangeResult(result);
    }

    public async Task<AccessChangeResult> SuspendScopeAsync(ActorContext actor, long scopeId, long expectedVersion, AuthorizationCommandMetadata metadata, CancellationToken cancellationToken = default)
    {
        ValidateVersion(scopeId, expectedVersion);
        var historyId = (await ids.NextAsync("authorizationHistory", cancellationToken)).Value;
        var result = await store.ExecuteCommandAsync(actor, "access.scope.suspend", metadata, new { scopeId, expectedVersion },
            (snapshot, _, _) => Task.FromResult(new CommandAuthorization(DemandAdministrative(actor, snapshot, Permissions.AccessRevoke), [])),
            async (session, token) =>
            {
                var before = await FindAsync("authorizationScopes", scopeId, session, token);
                if (before["status"].AsString != "active") throw new PersistenceConflictException("scope_inactive", "Scope is not active");
                await MongoUnitOfWork.RequireVersionAsync(Collection("authorizationScopes"), session, F.Eq("_id", scopeId), expectedVersion,
                    Builders<BsonDocument>.Update.Set("status", "suspended").Inc("fence", 1L), token);
                var after = await FindAsync("authorizationScopes", scopeId, session, token);
                await AuditAsync(historyId, actor, scopeId, "access.scope.suspended", metadata, before, after, session, token);
                return ChangeResult(scopeId, scopeId, checked(expectedVersion + 1), actor.GrantVersion, "suspended");
            }, cancellationToken);
        return ParseChangeResult(result);
    }
    private static BsonDocument SafeIdentity(BsonDocument identity) => new()
        { { "_id", identity["_id"] }, { "status", identity["status"] }, { "grantVersion", identity["grantVersion"] } };
}
