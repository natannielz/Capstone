using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Identity;

public sealed partial class MongoIdentitySessionStore
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private static readonly UpdateDefinitionBuilder<BsonDocument> U = Builders<BsonDocument>.Update;
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly IClock clock;
    private readonly SessionPolicy policy;
    private readonly SessionPayloadProtector protector;
    private IMongoCollection<BsonDocument> Sessions => database.GetCollection<BsonDocument>("identitySessions");
    private sealed record Envelope(long UserId, long GrantVersion, long SecurityVersion, string FamilyId,
        DateTimeOffset CreatedAt, DateTimeOffset AbsoluteExpiresAt, ProviderIdentityProof Proof, string ProviderPayload);

    public MongoIdentitySessionStore(IMongoDatabase database, MongoUnitOfWork unit, IClock clock,
        SessionPolicy policy, SessionPayloadProtector protector)
    {
        this.database = database; this.unit = unit; this.clock = clock;
        this.policy = policy.Validate(); this.protector = protector;
    }

    public async Task<SessionIssue> CreateAsync(IClientSessionHandle tx, IdentityAdmission admission,
        ProviderIdentityProof proof, string providerPayload, CancellationToken cancellationToken = default)
    {
        RequireTransaction(tx); ValidateNewProof(proof, providerPayload);
        if (admission.UserId <= 0 || admission.GrantVersion <= 0 || admission.SecurityVersion <= 0 ||
            admission.Issuer != proof.Issuer || admission.Subject != proof.Subject) throw Denied();
        await FenceIdentityAsync(tx, admission, cancellationToken);
        var token = SessionPersistence.NewToken(); var id = SessionPersistence.TokenHash(token)!;
        var now = Milliseconds(clock.UtcNow);
        var envelope = new Envelope(admission.UserId, admission.GrantVersion, admission.SecurityVersion, id,
            now, now + SessionPolicy.AbsoluteLifetime, Normalize(proof), providerPayload);
        var row = Document(id, envelope, now);
        await Sessions.InsertOneAsync(tx, row, cancellationToken: cancellationToken);
        return new(token, Snapshot(row, envelope));
    }

    public async Task<IdentitySession?> ReadAsync(string opaqueToken, CancellationToken cancellationToken = default)
    {
        var id = SessionPersistence.TokenHash(opaqueToken);
        if (id is null) return null;
        try
        {
            return await unit.ExecuteAsync<IdentitySession?>(async (tx, ct) =>
            {
                var row = await Sessions.Find(tx, F.Eq("_id", id) & F.Eq("status", "active") &
                    F.Gt("idleExpiresAt", clock.UtcNow.UtcDateTime) & F.Gt("absoluteExpiresAt", clock.UtcNow.UtcDateTime)).FirstOrDefaultAsync(ct);
                if (row is null) return null;
                var envelope = Decode(row);
                await FenceIdentityAsync(tx, Admission(envelope), ct);
                var now = Milliseconds(clock.UtcNow);
                var idle = Earlier(now + SessionPolicy.IdleTimeout, envelope.AbsoluteExpiresAt);
                var updated = await Sessions.FindOneAndUpdateAsync(tx, Live(row, now),
                    U.Max("lastUsedAt", now.UtcDateTime).Max("idleExpiresAt", idle.UtcDateTime).Inc("fence", 1L),
                    new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct);
                return updated is null ? null : Snapshot(updated, envelope);
            }, cancellationToken);
        }
        catch (AuthorizationDeniedException) { return null; }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
    }

    public async Task RevokeAsync(string opaqueToken, string reason, CancellationToken cancellationToken = default)
    {
        ValidateReason(reason);
        var id = SessionPersistence.TokenHash(opaqueToken);
        if (id is null) return;
        await unit.ExecuteAsync(async (tx, ct) =>
        {
            var row = await Sessions.Find(tx, F.Eq("_id", id)).FirstOrDefaultAsync(ct);
            if (row is null) return false;
            await Sessions.UpdateManyAsync(tx, F.Eq("familyId", row["familyId"]) & F.Ne("status", "revoked"),
                Revocation(reason), cancellationToken: ct);
            return true;
        }, cancellationToken);
    }

    /// <summary>The caller increments identity.securityVersion in this same transaction first.</summary>
    public Task RevokeUserSessionsAsync(IClientSessionHandle tx, long userId, string reason,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction(tx); ValidateReason(reason);
        if (userId <= 0) throw Denied();
        return Sessions.UpdateManyAsync(tx, F.Eq("userId", userId) & F.Ne("status", "revoked"), Revocation(reason), cancellationToken: cancellationToken);
    }

    public async Task<SessionIssue> RotateForStepUpAsync(IClientSessionHandle tx, SessionReference reference,
        ProviderIdentityProof proof, string providerPayload, string challengeBinding, DateTimeOffset challengeStartedAt,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction(tx); ValidateNewProof(proof, providerPayload);
        var now = Milliseconds(clock.UtcNow);
        if (string.IsNullOrWhiteSpace(challengeBinding) || challengeBinding.Length > 128 || challengeBinding.Any(char.IsControl) ||
            proof.ChallengeBinding != challengeBinding || challengeStartedAt > now + SessionPolicy.ProviderClockSkew ||
            challengeStartedAt < now - SessionPolicy.StepUpLifetime ||
            proof.FactorCompletedAt < challengeStartedAt - SessionPolicy.ProviderClockSkew) throw Denied();
        var row = await Sessions.Find(tx, F.Eq("_id", reference.Id) & F.Eq("version", reference.Version) &
            F.Eq("securityVersion", reference.SecurityVersion) & F.Eq("status", "active") &
            F.Gt("idleExpiresAt", now.UtcDateTime) & F.Gt("absoluteExpiresAt", now.UtcDateTime)).FirstOrDefaultAsync(cancellationToken) ?? throw Denied();
        var old = Decode(row);
        if (proof.Issuer != old.Proof.Issuer || proof.Subject != old.Proof.Subject ||
            challengeStartedAt < old.CreatedAt - SessionPolicy.ProviderClockSkew) throw Denied();
        await FenceIdentityAsync(tx, Admission(old), cancellationToken);
        var changed = await Sessions.UpdateOneAsync(tx, Live(row, now),
            U.Set("status", "rotated").Set("revokedAt", now.UtcDateTime).Set("revocationReason", "step_up_rotation")
                .Inc("version", 1L).Inc("fence", 1L), cancellationToken: cancellationToken);
        if (changed.ModifiedCount != 1) throw Denied();
        var token = SessionPersistence.NewToken(); var id = SessionPersistence.TokenHash(token)!;
        var envelope = old with { Proof = Normalize(proof), ProviderPayload = providerPayload };
        var replacement = Document(id, envelope, now);
        await Sessions.InsertOneAsync(tx, replacement, cancellationToken: cancellationToken);
        return new(token, Snapshot(replacement, envelope));
    }

    private async Task FenceIdentityAsync(IClientSessionHandle tx, IdentityAdmission admission, CancellationToken ct)
    {
        var changed = await database.GetCollection<BsonDocument>("identities").UpdateOneAsync(tx,
            F.Eq("_id", admission.UserId) & F.Eq("issuer", admission.Issuer) & F.Eq("subject", admission.Subject) &
            F.Eq("status", "active") & F.Eq("grantVersion", admission.GrantVersion) & F.Eq("securityVersion", admission.SecurityVersion),
            U.Inc("fence", 1L), cancellationToken: ct);
        if (changed.ModifiedCount != 1) throw Denied();
    }
}
