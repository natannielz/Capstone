using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using static UnitToko.Platform.Identity.PersonAuthorityBson;

namespace UnitToko.Platform.Identity;

/// <summary>Resolves only explicitly reviewed current personnel authority; never infers account equivalence.</summary>
public sealed class PersonRegistryService(IMongoDatabase database, IClock clock)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private const int MaximumMembers = 256;

    public async Task<ImmutableArray<PersonEvidence>> ResolveAsync(IClientSessionHandle tx, ImmutableArray<long> userIds,
        CancellationToken cancellationToken = default) => (await ResolveProofsAsync(tx, userIds, cancellationToken)).Evidence;

    internal async Task<PersonAuthorityProof> ResolveProofsAsync(IClientSessionHandle tx, ImmutableArray<long> userIds,
        CancellationToken cancellationToken = default)
    {
        if (!tx.IsInTransaction || userIds.IsDefaultOrEmpty || userIds.Length > MaximumMembers ||
            userIds.Any(id => id <= 0) || userIds.Distinct().Count() != userIds.Length) throw Denied();
        var now = clock.UtcNow;
        var validFrom = DateTimeOffset.MinValue;
        DateTimeOffset? validUntil = null;
        var records = new Dictionary<(string Collection, long Id), BsonDocument>();
        var groups = new Dictionary<long, long>();
        var result = ImmutableArray.CreateBuilder<PersonEvidence>();
        var provenanceAccounts = new HashSet<long>();

        void Remember(string collection, BsonDocument row, bool includeLifetime = true)
        {
            Require(collection, row);
            var key = (collection, Id(row));
            if (records.TryGetValue(key, out var existing) && !existing.Equals(row)) throw Denied();
            records[key] = row;
            if (row.Contains("requestedBy")) { provenanceAccounts.Add(Id(row, "requestedBy")); provenanceAccounts.Add(Id(row, "approvedBy")); }
            if (collection == "personAccountBindings") provenanceAccounts.Add(Id(row));
            if (collection is "personAccountBindings" or "personRecords" && records.Keys.Count(key => key.Collection == collection) > MaximumMembers) throw Denied();
            if (row.Contains("reviewedAt") && At(row, "reviewedAt") > now || row.Contains("createdAt") && At(row, "createdAt") > now) throw Denied();
            foreach (var timestamp in new[] { "reviewedAt", "createdAt" })
                if (row.Contains(timestamp) && At(row, timestamp) > validFrom) validFrom = At(row, timestamp);
            if (!includeLifetime || !row.Contains("effectiveAt")) return;
            var start = At(row, "effectiveAt");
            if (start > now) throw Denied();
            if (start > validFrom) validFrom = start;
            if (row.TryGetValue("expiresAt", out var expiry) && !expiry.IsBsonNull)
            {
                var end = At(row, "expiresAt");
                if (end <= now) throw Denied();
                if (validUntil is null || end < validUntil) validUntil = end;
            }
        }

        async Task<BsonDocument?> One(string collection, long id, bool optional = false)
        {
            if (records.TryGetValue((collection, id), out var cached)) { Remember(collection, cached); return cached; }
            var rows = await database.GetCollection<BsonDocument>(collection).Find(tx, F.Eq("_id", id)).Limit(2).ToListAsync(cancellationToken);
            if (rows.Count == 0 && optional) return null;
            if (rows.Count != 1) throw Denied();
            Remember(collection, rows[0]);
            return rows[0];
        }

        async Task<List<BsonDocument>> Many(string collection, FilterDefinition<BsonDocument> filter)
        {
            var rows = await database.GetCollection<BsonDocument>(collection).Find(tx, filter).Limit(MaximumMembers + 1).ToListAsync(cancellationToken);
            if (rows.Count > MaximumMembers) throw Denied();
            foreach (var row in rows) Remember(collection, row, collection != "personAccountBindings");
            return rows;
        }

        foreach (var userId in userIds)
        {
            var binding = await One("personAccountBindings", userId, true);
            if (binding is null) { result.Add(new(userId, null, PersonVerification.Unknown, 0)); continue; }
            if (binding["status"] != "active" || binding["verification"] != "verified") throw Denied();
            var person = (await One("personRecords", Id(binding, "personId")))!;
            var groupId = Id(person, "currentEquivalenceId");
            if (!groups.TryGetValue(groupId, out var canonical))
            {
                var group = (await One("personEquivalenceGroups", groupId))!;
                if (group["status"] != "active") throw Denied();
                var people = Ids(group, "memberPersonIds");
                var users = Ids(group, "memberUserIds");
                if (!people.Contains(Id(person)) || !users.Contains(userId) || users.Contains(Id(group, "approvedBy"))) throw Denied();
                canonical = Id(group, "canonicalPersonId");
                var allPeople = await Many("personRecords", F.In("_id", people) | F.Eq("currentEquivalenceId", groupId));
                if (!allPeople.Select(row => Id(row)).Order().SequenceEqual(people)) throw Denied();
                // Enumerate historical aliases as well. Their exact rows are fenced, but only requested
                // participants require an active, unexpired binding; account activity never proves person identity.
                var allBindings = await Many("personAccountBindings", F.In("personId", people) | F.In("_id", users));
                if (!allBindings.Select(row => Id(row)).Order().SequenceEqual(users)) throw Denied();
                // An active competing group is ambiguous even when its pointers were only partly updated.
                var competing = await database.GetCollection<BsonDocument>("personEquivalenceGroups").Find(tx,
                    F.Ne("_id", groupId) & (F.In("memberPersonIds", people) | F.In("memberUserIds", users)))
                    .Limit(MaximumMembers + 1).ToListAsync(cancellationToken);
                if (competing.Count > MaximumMembers) throw Denied();
                foreach (var oldGroup in competing)
                    if (Require("personEquivalenceGroups", oldGroup)["status"] != "superseded") throw Denied();
                foreach (var member in allPeople)
                {
                    if (member["status"] != "active" || Id(member, "currentEquivalenceId") != groupId) throw Denied();
                    var source = (await One("personEvidenceSources", Id(member, "sourceId")))!;
                    if (source["status"] != "approved") throw Denied();
                    var evidence = (await One("personEvidenceVersions", Id(member, "currentEvidenceId")))!;
                    if (Id(evidence, "personId") != Id(member) || Id(evidence, "sourceId") != Id(source) ||
                        Id(evidence, "sourceVersion") != Id(source, "version") || evidence["adapterId"] != source["adapterId"] ||
                        evidence["sourceSubjectKey"] != member["sourceSubjectKey"] || users.Contains(Id(evidence, "approvedBy"))) throw Denied();
                    var sameSubject = await database.GetCollection<BsonDocument>("personRecords").Find(tx,
                        F.Eq("sourceId", Id(source)) & F.Eq("sourceSubjectKey", member["sourceSubjectKey"])).Limit(2).ToListAsync(cancellationToken);
                    if (sameSubject.Count != 1 || !sameSubject[0].Equals(member)) throw Denied();
                    var memberBindings = allBindings.Where(row => Id(row, "personId") == Id(member)).ToArray();
                    if (memberBindings.Length == 0 || memberBindings.Any(row => Id(row, "evidenceId") != Id(evidence) ||
                        users.Contains(Id(row, "approvedBy")))) throw Denied();
                }
                if (allBindings.Any(row => !people.Contains(Id(row, "personId")) || row["verification"] != "verified")) throw Denied();
                groups.Add(groupId, canonical);
            }
            if (!records[("personEquivalenceGroups", groupId)]["memberUserIds"].AsBsonArray.Contains(new BsonInt64(userId)) ||
                Id(binding, "evidenceId") != Id(person, "currentEvidenceId")) throw Denied();
            result.Add(new(userId, canonical, PersonVerification.Verified, Id(binding, "version")));
        }
        if (provenanceAccounts.Count != 0)
        {
            var identities = await database.GetCollection<BsonDocument>("identities").Find(tx, F.In("_id", provenanceAccounts)).Limit(provenanceAccounts.Count + 1).ToListAsync(cancellationToken);
            if (identities.Count != provenanceAccounts.Count || identities.Any(row => !row["_id"].IsInt64 || !provenanceAccounts.Contains(row["_id"].AsInt64))) throw Denied();
        }
        var proof = new PersonAuthorityProof(tx, result.ToImmutable(), records.Select(row => (row.Key.Collection, row.Value)), validFrom, validUntil);
        // Unknown results are useful to callers; only complete verified bundles may be fenced.
        if (clock.UtcNow < validFrom || validUntil is { } endTime && clock.UtcNow >= endTime) throw Denied();
        return proof;
    }
}
