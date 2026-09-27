using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Identity;

public sealed partial class MongoIdentitySessionStore
{
    private BsonDocument Document(string id, Envelope envelope, DateTimeOffset now)
    {
        var proof = envelope.Proof;
        var row = new BsonDocument
        {
            { "_id", id }, { "schemaVersion", 1 }, { "familyId", envelope.FamilyId }, { "userId", envelope.UserId },
            { "issuer", proof.Issuer }, { "subject", proof.Subject }, { "connectionId", proof.ConnectionId },
            { "grantVersion", envelope.GrantVersion }, { "securityVersion", envelope.SecurityVersion },
            { "status", "active" }, { "version", 1L }, { "fence", 0L }, { "createdAt", envelope.CreatedAt.UtcDateTime },
            { "lastUsedAt", now.UtcDateTime }, { "idleExpiresAt", Earlier(now + SessionPolicy.IdleTimeout, envelope.AbsoluteExpiresAt).UtcDateTime },
            { "absoluteExpiresAt", envelope.AbsoluteExpiresAt.UtcDateTime }, { "providerAuthenticatedAt", proof.ProviderAuthenticatedAt.UtcDateTime },
            { "factorCompletedAt", proof.FactorCompletedAt.UtcDateTime }, { "factorType", proof.FactorType },
            { "assurancePolicyVersion", proof.AssurancePolicyVersion },
            { "protectedPayload", protector.Protect("identity-session." + id, JsonSerializer.Serialize(envelope)) }
        };
        if (proof.ProviderSessionId is not null) row.Add("providerSid", proof.ProviderSessionId);
        return row;
    }

    private Envelope Decode(BsonDocument row)
    {
        Envelope value;
        try
        {
            value = JsonSerializer.Deserialize<Envelope>(protector.Unprotect("identity-session." + Text(row, "_id"), Text(row, "protectedPayload"))) ?? throw Denied();
        }
        catch (CryptographicException) { throw Denied(); }
        catch (JsonException) { throw Denied(); }
        if (value.Proof is null || !policy.AcceptsStoredEvidence(value.Proof, clock.UtcNow) ||
            value.UserId != Number(row, "userId") || value.GrantVersion != Number(row, "grantVersion") ||
            value.SecurityVersion != Number(row, "securityVersion") || value.FamilyId != Text(row, "familyId") ||
            value.CreatedAt != Date(row, "createdAt") || value.AbsoluteExpiresAt != Date(row, "absoluteExpiresAt") ||
            value.AbsoluteExpiresAt != value.CreatedAt + SessionPolicy.AbsoluteLifetime ||
            value.Proof.Issuer != Text(row, "issuer") || value.Proof.Subject != Text(row, "subject") ||
            value.Proof.ConnectionId != Text(row, "connectionId") || value.Proof.ProviderAuthenticatedAt != Date(row, "providerAuthenticatedAt") ||
            value.Proof.FactorCompletedAt != Date(row, "factorCompletedAt") || value.Proof.FactorType != Text(row, "factorType") ||
            value.Proof.AssurancePolicyVersion != Text(row, "assurancePolicyVersion") ||
            value.Proof.ProviderSessionId != (row.Contains("providerSid") ? Text(row, "providerSid") : null) ||
            Date(row, "lastUsedAt") < value.CreatedAt || Date(row, "lastUsedAt") >= value.AbsoluteExpiresAt ||
            Date(row, "idleExpiresAt") > value.AbsoluteExpiresAt || Date(row, "idleExpiresAt") <= Date(row, "lastUsedAt")) throw Denied();
        return value;
    }

    private static IdentitySession Snapshot(BsonDocument row, Envelope value) => new(
        new(Text(row, "_id"), Number(row, "version"), value.SecurityVersion), value.UserId, value.GrantVersion,
        value.Proof, value.CreatedAt, Date(row, "lastUsedAt"), Date(row, "idleExpiresAt"), value.AbsoluteExpiresAt);
    private static IdentityAdmission Admission(Envelope value) => new(value.UserId, value.SecurityVersion, value.GrantVersion, value.Proof.Issuer, value.Proof.Subject);
    private static FilterDefinition<BsonDocument> Live(BsonDocument row, DateTimeOffset now) => SessionPersistence.LiveFilter(
        new(Text(row, "_id"), Number(row, "version"), Number(row, "securityVersion")), Number(row, "userId"), Text(row, "issuer"), Text(row, "subject"), Number(row, "grantVersion"), now);
    private UpdateDefinition<BsonDocument> Revocation(string reason) => U.Set("status", "revoked").Set("revokedAt", clock.UtcNow.UtcDateTime)
        .Set("revocationReason", reason).Inc("version", 1L).Inc("fence", 1L);
    private void ValidateNewProof(ProviderIdentityProof proof, string payload)
    {
        if (!policy.Accepts(proof, clock.UtcNow) || payload is null || Encoding.UTF8.GetByteCount(payload) > SessionPolicy.MaximumProviderPayloadBytes)
            throw Denied();
    }
    private static void RequireTransaction(IClientSessionHandle tx)
    {
        if (!tx.IsInTransaction) throw new InvalidOperationException("Identity changes require an active transaction");
    }
    private static void ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 256 || reason.Any(char.IsControl))
            throw new ArgumentException("A bounded session revocation reason is required");
    }
    private static long Number(BsonDocument row, string name) => row.TryGetValue(name, out var value) && value.IsInt64 && value.AsInt64 > 0
        ? value.AsInt64 : throw Denied();
    private static string Text(BsonDocument row, string name) => row.TryGetValue(name, out var value) && value.IsString && !string.IsNullOrEmpty(value.AsString)
        ? value.AsString : throw Denied();
    private static DateTimeOffset Date(BsonDocument row, string name) => row.TryGetValue(name, out var value) && value.BsonType == BsonType.DateTime && value.IsValidDateTime
        ? new(value.ToUniversalTime(), TimeSpan.Zero) : throw Denied();
    private static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;
    private static DateTimeOffset Milliseconds(DateTimeOffset value) => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());
    private static ProviderIdentityProof Normalize(ProviderIdentityProof proof) => proof with
    { ProviderAuthenticatedAt = Milliseconds(proof.ProviderAuthenticatedAt), FactorCompletedAt = Milliseconds(proof.FactorCompletedAt) };
    private static AuthorizationDeniedException Denied() => new("session_unavailable");
}
