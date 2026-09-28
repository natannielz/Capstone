using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Identity;

/// <summary>
/// Loads complete current structural membership for an independently governed withdrawal.
/// Status, elapsed validity and historical source revisions are not interpreted as positive authority.
/// Every supported membership writer must advance the common group head to serialize insertions.
/// </summary>
internal sealed class PersonWithdrawalTargetLoader(IMongoDatabase database)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private const int MaximumMembers = 256;
    private const int MaximumHeads = 2048;

    internal async Task<PersonWithdrawalTarget> LoadAsync(IClientSessionHandle tx, long groupId,
        CancellationToken cancellationToken = default)
    {
        if (!tx.IsInTransaction || groupId <= 0) throw Denied();
        var captured = new Dictionary<(string Collection, long Id), BsonDocument>();
        var provenance = new HashSet<long>();
        void Remember(string collection, BsonDocument row)
        {
            if (!PersonAuthoritySchemas.IsValid(collection, row) ||
                collection != "personEvidenceVersions" && row["fence"].AsInt64 == long.MaxValue) throw Denied();
            var key = (collection, row["_id"].AsInt64);
            if (captured.TryGetValue(key, out var previous) && !previous.Equals(row)) throw Denied();
            captured[key] = row;
            if (captured.Count > MaximumHeads) throw Denied();
            if (row.Contains("requestedBy")) { provenance.Add(row["requestedBy"].AsInt64); provenance.Add(row["approvedBy"].AsInt64); }
        }
        async Task<BsonDocument> One(string collection, long id)
        {
            if (captured.TryGetValue((collection, id), out var existing)) return existing;
            var rows = await database.GetCollection<BsonDocument>(collection).Find(tx, F.Eq("_id", id)).Limit(2).ToListAsync(cancellationToken);
            if (rows.Count != 1) throw Denied();
            Remember(collection, rows[0]);
            return rows[0];
        }
        async Task<List<BsonDocument>> Many(string collection, FilterDefinition<BsonDocument> filter)
        {
            var rows = await database.GetCollection<BsonDocument>(collection).Find(tx, filter).Limit(MaximumMembers + 1).ToListAsync(cancellationToken);
            if (rows.Count > MaximumMembers) throw Denied();
            foreach (var row in rows) Remember(collection, row);
            return rows;
        }

        var group = await One("personEquivalenceGroups", groupId);
        if (group["status"].AsString is not ("active" or "disputed") || group["version"].AsInt64 == long.MaxValue) throw Denied();
        var people = Ids(group, "memberPersonIds"); var users = Ids(group, "memberUserIds");
        var personRows = await Many("personRecords", F.In("_id", people) | F.Eq("currentEquivalenceId", groupId));
        if (!personRows.Select(row => row["_id"].AsInt64).Order().SequenceEqual(people) ||
            personRows.Any(row => row["currentEquivalenceId"].AsInt64 != groupId)) throw Denied();
        var bindings = await Many("personAccountBindings", F.In("personId", people) | F.In("_id", users));
        if (!bindings.Select(row => row["_id"].AsInt64).Order().SequenceEqual(users) ||
            bindings.Any(row => !people.Contains(row["personId"].AsInt64))) throw Denied();
        var overlapping = await Many("personEquivalenceGroups", F.Ne("_id", groupId) &
            (F.In("memberPersonIds", people) | F.In("memberUserIds", users)));
        if (overlapping.Any(row => row["status"] != "superseded")) throw Denied();

        foreach (var person in personRows)
        {
            var personId = person["_id"].AsInt64;
            var source = await One("personEvidenceSources", person["sourceId"].AsInt64);
            var evidence = await One("personEvidenceVersions", person["currentEvidenceId"].AsInt64);
            if (evidence["personId"].AsInt64 != personId || evidence["sourceId"] != source["_id"] ||
                evidence["sourceSubjectKey"] != person["sourceSubjectKey"] ||
                evidence["sourceVersion"].AsInt64 > source["version"].AsInt64 ||
                evidence["sourceVersion"] == source["version"] && evidence["adapterId"] != source["adapterId"]) throw Denied();
            var sameSubject = await database.GetCollection<BsonDocument>("personRecords").Find(tx,
                F.Eq("sourceId", source["_id"]) & F.Eq("sourceSubjectKey", person["sourceSubjectKey"]))
                .Limit(2).ToListAsync(cancellationToken);
            if (sameSubject.Count != 1 || !sameSubject[0].Equals(person)) throw Denied();
            var aliases = bindings.Where(row => row["personId"].AsInt64 == personId).ToArray();
            if (aliases.Length == 0 || aliases.Any(row => row["evidenceId"] != evidence["_id"])) throw Denied();
        }

        var identities = await database.GetCollection<BsonDocument>("identities")
            .Find(tx, F.In("_id", users)).Limit(MaximumMembers + 1).ToListAsync(cancellationToken);
        if (identities.Count != users.Length) throw Denied();
        var identityRows = identities.Select(IdentityProjection).OrderBy(row => row["_id"].AsInt64).ToArray();
        if (!identityRows.Select(row => row["_id"].AsInt64).SequenceEqual(users)) throw Denied();
        foreach (var identity in identityRows) captured.Add(("identities", identity["_id"].AsInt64), identity);
        if (captured.Count > MaximumHeads) throw Denied();

        // Original reviewers need retained typed identity references, not current admission,
        // positive staff proof or membership in the affected account set.
        if (provenance.Count != 0)
        {
            var references = await database.GetCollection<BsonDocument>("identities").Find(tx, F.In("_id", provenance))
                .Project(new BsonDocument("_id", 1)).Limit(provenance.Count + 1).ToListAsync(cancellationToken);
            if (references.Count != provenance.Count ||
                references.Any(row => !row.TryGetValue("_id", out var id) || !id.IsInt64 || id.AsInt64 <= 0 || !provenance.Contains(id.AsInt64))) throw Denied();
        }
        return new(database, tx, group, captured.Select(row => (row.Key.Collection, row.Value)));
    }

    private static BsonDocument IdentityProjection(BsonDocument row)
    {
        if (!PositiveLong(row, "_id") || !PositiveLong(row, "grantVersion") ||
            row.TryGetValue("securityVersion", out var security) && (!security.IsInt64 || security.AsInt64 <= 0) ||
            !row.TryGetValue("schemaVersion", out var schema) || !schema.IsInt32 || schema.AsInt32 != 1 ||
            !row.TryGetValue("fence", out var fence) || !fence.IsInt64 || fence.AsInt64 < 0 || fence.AsInt64 == long.MaxValue ||
            !Text(row, "issuer", 2048) || !Text(row, "subject", 512) ||
            !row.TryGetValue("status", out var status) || !status.IsString || status.AsString is not ("pending" or "active" or "suspended" or "revoked")) throw Denied();
        var result = new BsonDocument();
        foreach (var field in new[] { "_id", "schemaVersion", "issuer", "subject", "status", "grantVersion", "securityVersion", "fence" })
            if (row.TryGetValue(field, out var value)) result.Add(field, value);
        return result;
    }

    private static bool PositiveLong(BsonDocument row, string field) =>
        row.TryGetValue(field, out var value) && value.IsInt64 && value.AsInt64 > 0;
    private static bool Text(BsonDocument row, string field, int maximum) => row.TryGetValue(field, out var value) &&
        value.IsString && !string.IsNullOrWhiteSpace(value.AsString) && value.AsString.Length <= maximum && !value.AsString.Any(char.IsControl);
    private static long[] Ids(BsonDocument row, string field) => row[field].AsBsonArray.Select(id => id.AsInt64).ToArray();
    private static AuthorizationDeniedException Denied() => new("person_withdrawal_target_unavailable");
}
