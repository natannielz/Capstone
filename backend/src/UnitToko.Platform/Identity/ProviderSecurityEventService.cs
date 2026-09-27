using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Identity;

/// <summary>Applies only verifier-produced provider changes. Every event has its own majority
/// transaction, so a failed batch can safely be replayed without acknowledging uncommitted work.</summary>
public sealed class ProviderSecurityEventService(IMongoDatabase database, MongoUnitOfWork unit,
    MongoIdentitySessionStore sessions, ProviderSecurityEventVerifier verifier, IClock clock)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private static readonly UpdateDefinitionBuilder<BsonDocument> U = Builders<BsonDocument>.Update;
    private IMongoCollection<BsonDocument> Events => database.GetCollection<BsonDocument>("identitySecurityEvents");
    private IMongoCollection<BsonDocument> Identities => database.GetCollection<BsonDocument>("identities");
    private IMongoCollection<BsonDocument> Holds => database.GetCollection<BsonDocument>("identityAdmissionHolds");

    public async Task<ProviderEventBatchResult> ProcessAsync(ProviderEventTransport transport, ReadOnlyMemory<byte> body,
        string authorization, CancellationToken cancellationToken = default)
    {
        // Verification precedes all storage access, including duplicate checks.
        var verified = verifier.Verify(transport, body, authorization);
        var applied = 0; var duplicates = 0; var unknown = 0;
        foreach (var change in verified.Events)
        {
            var outcome = await unit.ExecuteAsync((tx, ct) => ApplyAsync(tx, change, ct), cancellationToken);
            switch (outcome)
            {
                case "applied": applied++; break;
                case "duplicate": duplicates++; break;
                case "unknown": unknown++; break;
            }
        }
        return new(applied, duplicates, unknown, verified.IgnoredCount);
    }

    private async Task<string> ApplyAsync(IClientSessionHandle tx, VerifiedProviderSecurityEvent change, CancellationToken ct)
    {
        var eventHash = Hash(change.Issuer, change.EventId);
        var prior = await Events.Find(tx, F.Eq("_id", eventHash)).FirstOrDefaultAsync(ct);
        if (prior is not null)
        {
            if (prior["issuer"] != change.Issuer || prior["eventId"] != change.EventId ||
                prior["fingerprint"] != change.Fingerprint)
                throw new PersistenceConflictException("provider_event_conflict", "A provider event identifier was reused with different facts");
            return "duplicate";
        }

        await IdentityAdmissionHolds.RestrictOrTouchSubjectAsync(database, tx, change.Issuer, change.Subject,
            clock, change.Change is ProviderSecurityChange.AccountDisabled or ProviderSecurityChange.AccountDeleted or
                ProviderSecurityChange.FactorRemoved,
            change.Change is ProviderSecurityChange.PasswordChanged or ProviderSecurityChange.FactorChanged ? clock.UtcNow : null, ct);
        var identity = await Identities.Find(tx, F.Eq("issuer", change.Issuer) & F.Eq("subject", change.Subject)).FirstOrDefaultAsync(ct);
        long? userId = null; long? previousVersion = null; long? nextVersion = null;
        if (identity is not null)
        {
            userId = identity["_id"].AsInt64;
            var legacy = !identity.TryGetValue("securityVersion", out var current);
            previousVersion = legacy ? 1L : current is { IsInt64: true } && current.AsInt64 is > 0 and < long.MaxValue
                ? current.AsInt64 : throw new PersistenceConflictException("identity_security_version_unavailable", "Account security revision is unavailable");
            nextVersion = checked(previousVersion.Value + 1);
            var filter = F.Eq("_id", userId.Value) & F.Eq("issuer", change.Issuer) & F.Eq("subject", change.Subject) &
                (legacy ? F.Exists("securityVersion", false) : F.Eq("securityVersion", previousVersion.Value));
            var update = legacy ? U.Set("securityVersion", nextVersion.Value).Inc("fence", 1L) :
                U.Inc("securityVersion", 1L).Inc("fence", 1L);
            var changed = await Identities.UpdateOneAsync(tx, filter, update, cancellationToken: ct);
            if (changed.ModifiedCount != 1)
                throw new PersistenceConflictException("version_conflict", "Account security revision changed");
            await sessions.RevokeUserSessionsAsync(tx, userId.Value, "provider_security_event", ct);
            var holdReason = change.Change switch
            {
                ProviderSecurityChange.AccountDisabled => "providerBlocked",
                ProviderSecurityChange.AccountDeleted => "providerDeleted",
                ProviderSecurityChange.FactorRemoved => "factorRemoved",
                _ => null
            };
            if (holdReason is not null)
                await Holds.InsertOneAsync(tx, new BsonDocument
                {
                    { "_id", eventHash }, { "schemaVersion", 1 }, { "userId", userId.Value },
                    { "sourceEventId", eventHash }, { "reason", holdReason }, { "status", "active" },
                    { "createdAt", clock.UtcNow.UtcDateTime }, { "securityVersion", nextVersion.Value }, { "version", 1L }
                }, cancellationToken: ct);
        }
        await Events.InsertOneAsync(tx, new BsonDocument
        {
            { "_id", eventHash }, { "schemaVersion", 1 }, { "issuer", change.Issuer }, { "eventId", change.EventId },
            { "subjectHash", Hash(change.Issuer, change.Subject) }, { "fingerprint", change.Fingerprint },
            { "change", ChangeName(change.Change) }, { "occurredAt", change.OccurredAt.UtcDateTime },
            { "receivedAt", clock.UtcNow.UtcDateTime }, { "outcome", identity is null ? "unknown" : "applied" },
            { "userId", userId.HasValue ? new BsonInt64(userId.Value) : BsonNull.Value },
            { "previousSecurityVersion", previousVersion.HasValue ? new BsonInt64(previousVersion.Value) : BsonNull.Value },
            { "securityVersion", nextVersion.HasValue ? new BsonInt64(nextVersion.Value) : BsonNull.Value }
        }, cancellationToken: ct);
        return identity is null ? "unknown" : "applied";
    }

    private static string ChangeName(ProviderSecurityChange change) => change switch
    {
        ProviderSecurityChange.AccountDisabled => "accountDisabled",
        ProviderSecurityChange.AccountDeleted => "accountDeleted",
        ProviderSecurityChange.PasswordChanged => "passwordChanged",
        ProviderSecurityChange.FactorChanged => "factorChanged",
        ProviderSecurityChange.FactorRemoved => "factorRemoved",
        _ => throw new ArgumentOutOfRangeException(nameof(change))
    };
    private static string Hash(string issuer, string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issuer + "\0" + value))).ToLowerInvariant();
}
