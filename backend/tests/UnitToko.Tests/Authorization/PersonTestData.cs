using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;

namespace UnitToko.Tests.Authorization;

/// <summary>Synthetic reviewed authority only; no production source or reviewer is asserted.</summary>
internal static class PersonTestData
{
    internal const long SourceId = 810000;
    internal const long RequesterId = 890001;
    internal const long ReviewerId = 890002;
    internal static long PersonId(long userId) => 1000000 + userId;
    internal static long EvidenceId(long userId) => 2000000 + userId;
    internal static long GroupId(long userId) => 3000000 + userId;
    internal static async Task SeedDistinctPeopleAsync(IMongoDatabase database, IClock clock, params long[] userIds)
    {
        var identities = database.GetCollection<BsonDocument>("identities");
        foreach (var account in userIds.Concat([RequesterId, ReviewerId]).Distinct())
            if (!await identities.Find(new BsonDocument("_id", account)).AnyAsync())
                await identities.InsertOneAsync(new BsonDocument { { "_id", account }, { "schemaVersion", 1 }, { "issuer", "https://synthetic-person.example/" }, { "subject", "person-fixture-" + account }, { "status", "active" }, { "grantVersion", 1L }, { "fence", 0L } });
        var sources = database.GetCollection<BsonDocument>("personEvidenceSources");
        if (!await sources.Find(new BsonDocument("_id", SourceId)).AnyAsync()) await sources.InsertOneAsync(Source(clock.UtcNow));
        foreach (var userId in userIds)
        {
            await database.GetCollection<BsonDocument>("personRecords").InsertOneAsync(Person(userId));
            await database.GetCollection<BsonDocument>("personEvidenceVersions").InsertOneAsync(Evidence(userId, clock.UtcNow));
            await database.GetCollection<BsonDocument>("personAccountBindings").InsertOneAsync(Binding(userId, clock.UtcNow));
            await database.GetCollection<BsonDocument>("personEquivalenceGroups").InsertOneAsync(Group(userId, clock.UtcNow));
        }
    }
    internal static BsonDocument Source(DateTimeOffset now)
    {
        var row = Head(SourceId);
        row.AddRange(new BsonDocument { { "key", "synthetic-source" }, { "status", "approved" }, { "adapterId", "synthetic-adapter-v1" }, { "policyReference", "synthetic-reviewed-policy" } });
        Provenance(row, now); Lifetime(row, now); return row;
    }
    internal static BsonDocument Person(long userId)
    {
        var row = Head(PersonId(userId));
        row.AddRange(new BsonDocument { { "sourceId", SourceId }, { "sourceSubjectKey", "private-person-" + userId },
            { "currentEvidenceId", EvidenceId(userId) }, { "currentEquivalenceId", GroupId(userId) }, { "status", "active" } });
        return row;
    }
    internal static BsonDocument Binding(long userId, DateTimeOffset now)
    {
        var row = Head(userId);
        row.AddRange(new BsonDocument { { "personId", PersonId(userId) }, { "evidenceId", EvidenceId(userId) }, { "verification", "verified" }, { "status", "active" }, { "reason", "Synthetic independent review" } });
        Provenance(row, now); Lifetime(row, now); return row;
    }
    internal static BsonDocument Group(long userId, DateTimeOffset now)
    {
        var row = Head(GroupId(userId));
        row.AddRange(new BsonDocument { { "canonicalPersonId", PersonId(userId) },
            { "memberPersonIds", new BsonArray { PersonId(userId) } }, { "memberUserIds", new BsonArray { userId } }, { "status", "active" } });
        Provenance(row, now); return row;
    }
    internal static BsonDocument Evidence(long userId, DateTimeOffset now)
    {
        var row = new BsonDocument { { "_id", EvidenceId(userId) }, { "schemaVersion", 1 }, { "personId", PersonId(userId) },
            { "evidenceVersion", 1L }, { "sourceId", SourceId }, { "sourceVersion", 1L }, { "sourceSubjectKey", "private-person-" + userId },
            { "sourceReference", "private-source-file-" + userId }, { "sourceDigest", new string('a', 64) }, { "adapterId", "synthetic-adapter-v1" },
            { "createdAt", now.UtcDateTime }, { "reason", "Synthetic evidence review" } };
        Provenance(row, now); Lifetime(row, now); return row;
    }
    private static BsonDocument Head(long id) => new() { { "_id", id }, { "schemaVersion", 1 }, { "version", 1L }, { "fence", 0L } };
    private static void Provenance(BsonDocument row, DateTimeOffset now) => row.AddRange(new BsonDocument
        { { "requestedBy", RequesterId }, { "approvedBy", ReviewerId }, { "approvalId", 890003L }, { "approvalVersion", 1L }, { "reviewedAt", now.UtcDateTime } });
    private static void Lifetime(BsonDocument row, DateTimeOffset now) => row.AddRange(new BsonDocument
        { { "effectiveAt", now.AddDays(-1).UtcDateTime }, { "expiresAt", now.AddDays(30).UtcDateTime } });
}
