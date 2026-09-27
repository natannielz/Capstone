using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Identity;

/// <summary>Reads only committed provider block/delete/factor-loss holds. Call after fencing the
/// identity in the same transaction so a racing provider event cannot leave a usable session.</summary>
internal static class IdentityAdmissionHolds
{
    internal static string SubjectId(string issuer, string subject) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issuer + "\0" + subject))).ToLowerInvariant();

    internal static Task RequireSubjectClearAsync(IMongoDatabase database, IClientSessionHandle tx,
        ProviderIdentityProof proof, IClock clock, string denialCode, CancellationToken cancellationToken) =>
        TouchSubjectAsync(database, tx, proof.Issuer, proof.Subject, clock, restrict: false, proofNotBefore: null,
            proof, denialCode, cancellationToken);

    internal static Task RestrictOrTouchSubjectAsync(IMongoDatabase database, IClientSessionHandle tx,
        string issuer, string subject, IClock clock, bool restrict, DateTimeOffset? proofNotBefore,
        CancellationToken cancellationToken) =>
        TouchSubjectAsync(database, tx, issuer, subject, clock, restrict, proofNotBefore, null,
            "provider_event_unavailable", cancellationToken);

    private static async Task TouchSubjectAsync(IMongoDatabase database, IClientSessionHandle tx,
        string issuer, string subject, IClock clock, bool restrict, DateTimeOffset? proofNotBefore,
        ProviderIdentityProof? proof,
        string denialCode, CancellationToken cancellationToken)
    {
        if (!tx.IsInTransaction || string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
            throw new AuthorizationDeniedException(denialCode);
        var subjects = database.GetCollection<BsonDocument>("identityProviderSubjects");
        var updates = Builders<BsonDocument>.Update;
        var update = updates.SetOnInsert("schemaVersion", 1).SetOnInsert("issuer", issuer)
            .Set("updatedAt", clock.UtcNow.UtcDateTime).Inc("fence", 1L);
        update = restrict ? update.Set("admissionRestricted", true) : update.SetOnInsert("admissionRestricted", false);
        if (proofNotBefore.HasValue) update = update.Max("proofNotBefore", proofNotBefore.Value.UtcDateTime);
        var row = await subjects.FindOneAndUpdateAsync(tx, Builders<BsonDocument>.Filter.Eq("_id", SubjectId(issuer, subject)),
            update, new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
            cancellationToken);
        if (row is null || row["issuer"] != issuer || proof is not null &&
            (row["admissionRestricted"].AsBoolean || row.TryGetValue("proofNotBefore", out var cutoff) && !cutoff.IsBsonNull &&
                (proof.ProviderAuthenticatedAt <= cutoff.ToUniversalTime() || proof.FactorCompletedAt <= cutoff.ToUniversalTime())))
            throw new AuthorizationDeniedException(denialCode);
    }

    internal static async Task RequireClearAsync(IMongoDatabase database, IClientSessionHandle tx,
        long userId, string denialCode, CancellationToken cancellationToken)
    {
        if (!tx.IsInTransaction || userId <= 0) throw new AuthorizationDeniedException(denialCode);
        var holds = database.GetCollection<BsonDocument>("identityAdmissionHolds");
        var filter = Builders<BsonDocument>.Filter.Eq("userId", userId) &
            Builders<BsonDocument>.Filter.Eq("status", "active");
        if (await holds.Find(tx, filter).AnyAsync(cancellationToken))
            throw new AuthorizationDeniedException(denialCode);
    }
}
