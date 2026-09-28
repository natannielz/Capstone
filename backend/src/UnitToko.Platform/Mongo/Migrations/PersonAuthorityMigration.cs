using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo.Migrations;

// v8 establishes proof storage only. It never infers, approves, or seeds personnel authority.
internal sealed class PersonAuthorityMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 8;
    private sealed record RequiredIndex(string Name, BsonDocument Key, bool Unique = false);
    private sealed record Plan(string Name, bool Exists, BsonDocument Validator, List<RequiredIndex> Indexes, long Maximum);
    private static RequiredIndex[] Indexes(string collection) => collection switch
    {
        "personEvidenceSources" => [new("ux_key", new("key", 1), true)],
        "personRecords" => [new("ux_sourceId_sourceSubjectKey", new() { { "sourceId", 1 }, { "sourceSubjectKey", 1 } }, true), new("ix_currentEquivalenceId_id", new() { { "currentEquivalenceId", 1 }, { "_id", 1 } })],
        "personAccountBindings" => [new("ix_personId_id", new() { { "personId", 1 }, { "_id", 1 } })],
        "personEquivalenceGroups" => [new("ix_memberPersonIds", new("memberPersonIds", 1)), new("ix_memberUserIds", new("memberUserIds", 1))],
        "personEvidenceVersions" => [new("ux_personId_evidenceVersion", new() { { "personId", 1 }, { "evidenceVersion", 1 } }, true), new("ix_sourceId_createdAt", new() { { "sourceId", 1 }, { "createdAt", 1 } })],
        _ => throw new ArgumentOutOfRangeException(nameof(collection))
    };

    public async Task ApplyAsync(MigrationLease lease, CancellationToken token)
    {
        var plans = new List<Plan>();
        var documents = new Dictionary<string, Dictionary<long, BsonDocument>>();
        foreach (var name in PersonAuthoritySchemas.Collections)
        {
            await lease.RenewAsync(token);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, token)).SingleOrDefaultAsync(token);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "collection") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.TryGetValue("collation", out var collation) && !SimpleCollation(collation))
                throw Incompatible(name, "collection options");
            var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var required = PersonAuthoritySchemas.Validator(name);
            var validator = previous.ElementCount == 0 ? required : Contains(previous, required) ? previous : new BsonDocument("$and", new BsonArray { previous, required });
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var records = await rows.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(token);
            if (records.Any(row => !PersonAuthoritySchemas.IsValid(name, row)) ||
                await rows.Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(token)) throw Incompatible(name, "records");
            documents[name] = records.ToDictionary(row => row["_id"].AsInt64);
            var existing = definition is null ? [] : await (await rows.Indexes.ListAsync(token)).ToListAsync(token);
            if (existing.Any(index => index.Contains("expireAfterSeconds")) || definition is not null && !existing.Any(IsIdentityIndex)) throw Incompatible(name, "index");
            var create = new List<RequiredIndex>();
            foreach (var planned in Indexes(name))
            {
                var equivalent = false;
                foreach (var index in existing)
                {
                    var sameKey = index["key"].AsBsonDocument.Equals(planned.Key);
                    if (index["name"] != planned.Name && !sameKey) continue;
                    if (!sameKey || index.GetValue("unique", false).AsBoolean != planned.Unique || UnsafeIndexOptions(index)) throw Incompatible(name, "index");
                    equivalent = true;
                }
                if (planned.Unique && records.GroupBy(row => new BsonDocument(planned.Key.Names.Select(field => new BsonElement(field, row[field])))).Any(group => group.Count() > 1))
                    throw Incompatible(name, "duplicate unique keys");
                if (!equivalent) create.Add(planned);
            }
            var maximum = records.Count == 0 ? 0 : records.Max(row => row["_id"].AsInt64);
            if (name != "personAccountBindings")
            {
                var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(token);
                if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < 0 || value.AsInt64 == long.MaxValue) || maximum == long.MaxValue)
                    throw Incompatible(name, "idCounters high-water mark");
            }
            plans.Add(new(name, definition is not null, validator, create, maximum));
        }
        await ValidateGraph(documents, token);

        // Every collection, row, relationship, index and sequence has passed before the first DDL/counter write.
        foreach (var plan in plans)
        {
            await lease.RenewAsync(token);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { plan.Exists ? "collMod" : "create", plan.Name }, { "validator", plan.Validator }, { "validationLevel", "strict" },
                { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() }
            }, cancellationToken: token);
        }
        foreach (var plan in plans)
        {
            if (plan.Maximum > 0 && plan.Name != "personAccountBindings")
            {
                await lease.RenewAsync(token);
                await database.GetCollection<BsonDocument>("idCounters").WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(new BsonDocument("_id", plan.Name),
                    Builders<BsonDocument>.Update.Max("value", plan.Maximum), new UpdateOptions { IsUpsert = true }, token);
            }
            foreach (var index in plan.Indexes)
            {
                await lease.RenewAsync(token);
                await database.GetCollection<BsonDocument>(plan.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(index.Key, new CreateIndexOptions { Name = index.Name, Unique = index.Unique }), cancellationToken: token);
            }
        }
    }

    private async Task ValidateGraph(Dictionary<string, Dictionary<long, BsonDocument>> rows, CancellationToken token)
    {
        var sources = rows["personEvidenceSources"]; var people = rows["personRecords"]; var bindings = rows["personAccountBindings"];
        var groups = rows["personEquivalenceGroups"]; var evidence = rows["personEvidenceVersions"];
        BsonDocument Get(Dictionary<long, BsonDocument> collection, BsonValue id, string name) => collection.TryGetValue(id.AsInt64, out var row) ? row : throw Incompatible(name, "cross-links");
        foreach (var proof in evidence.Values)
        {
            var person = Get(people, proof["personId"], "personEvidenceVersions"); var source = Get(sources, proof["sourceId"], "personEvidenceVersions");
            if (person["sourceId"] != proof["sourceId"] || person["sourceSubjectKey"] != proof["sourceSubjectKey"] || proof["sourceVersion"].AsInt64 > source["version"].AsInt64)
                throw Incompatible("personEvidenceVersions", "cross-links");
        }
        foreach (var person in people.Values)
        {
            var source = Get(sources, person["sourceId"], "personRecords"); var proof = Get(evidence, person["currentEvidenceId"], "personRecords");
            var group = Get(groups, person["currentEquivalenceId"], "personRecords");
            if (proof["personId"] != person["_id"] || proof["sourceId"] != source["_id"] || proof["sourceSubjectKey"] != person["sourceSubjectKey"] ||
                group["status"] == "superseded" || !group["memberPersonIds"].AsBsonArray.Contains(person["_id"]) ||
                person["status"] == "active" && source["status"] == "approved" && (proof["sourceVersion"] != source["version"] || proof["adapterId"] != source["adapterId"]))
                throw Incompatible("personRecords", "cross-links");
        }
        foreach (var binding in bindings.Values)
        {
            var person = Get(people, binding["personId"], "personAccountBindings"); var proof = Get(evidence, binding["evidenceId"], "personAccountBindings");
            var group = Get(groups, person["currentEquivalenceId"], "personAccountBindings");
            if (proof["personId"] != person["_id"] || binding["evidenceId"] != person["currentEvidenceId"] || !group["memberUserIds"].AsBsonArray.Contains(binding["_id"]))
                throw Incompatible("personAccountBindings", "cross-links");
        }
        foreach (var group in groups.Values)
        {
            foreach (var id in group["memberPersonIds"].AsBsonArray) Get(people, id, "personEquivalenceGroups");
            foreach (var id in group["memberUserIds"].AsBsonArray) Get(bindings, id, "personEquivalenceGroups");
            if (group["status"] == "superseded") continue;
            var expectedPeople = people.Values.Where(person => person["currentEquivalenceId"] == group["_id"]).Select(person => person["_id"].AsInt64).Order().ToArray();
            var expectedUsers = bindings.Values.Where(binding => expectedPeople.Contains(binding["personId"].AsInt64)).Select(binding => binding["_id"].AsInt64).Order().ToArray();
            // Current equivalence also governs independent review. Historical proof versions remain unchanged.
            if (expectedUsers.Contains(group["approvedBy"].AsInt64) ||
                expectedPeople.Any(id => expectedUsers.Contains(evidence[people[id]["currentEvidenceId"].AsInt64]["approvedBy"].AsInt64)) ||
                expectedUsers.Any(id => expectedUsers.Contains(bindings[id]["approvedBy"].AsInt64)))
                throw Incompatible("personEquivalenceGroups", "independent reviewer cross-links");
            if (!expectedPeople.SequenceEqual(group["memberPersonIds"].AsBsonArray.Select(id => id.AsInt64)) || !expectedUsers.SequenceEqual(group["memberUserIds"].AsBsonArray.Select(id => id.AsInt64)))
                throw Incompatible("personEquivalenceGroups", "complete membership cross-links");
        }
        var accountIds = rows.Values.SelectMany(collection => collection.Values).Where(row => row.Contains("requestedBy"))
            .SelectMany(row => new[] { row["requestedBy"].AsInt64, row["approvedBy"].AsInt64 }).Concat(bindings.Keys).Distinct().ToArray();
        if (accountIds.Length > 0)
        {
            var identities = await database.GetCollection<BsonDocument>("identities").WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(accountIds)))).Project(new BsonDocument("_id", 1)).ToListAsync(token);
            if (identities.Any(row => !row["_id"].IsInt64) || !accountIds.Order().SequenceEqual(identities.Select(row => row["_id"].AsInt64).Order()))
                throw Incompatible("personAccountBindings", "identity/provenance cross-links");
        }
    }

    private static bool IsIdentityIndex(BsonDocument index) => index["key"].AsBsonDocument.Equals(new BsonDocument("_id", 1)) && index["name"] == "_id_" && !UnsafeIndexOptions(index);
    private static bool UnsafeIndexOptions(BsonDocument index) => index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean || index.Contains("partialFilterExpression") || index.Contains("expireAfterSeconds") || index.TryGetValue("collation", out var collation) && !SimpleCollation(collation);
    private static bool SimpleCollation(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.GetValue("locale", "") == "simple";
    private static bool Contains(BsonDocument validator, BsonDocument required) => validator.Equals(required) || validator.TryGetValue("$and", out var value) && value.IsBsonArray && value.AsBsonArray.Any(item => item.IsBsonDocument && Contains(item.AsBsonDocument, required));
    private static InvalidOperationException Incompatible(string collection, string reason) => new($"Person-authority migration found incompatible {reason} in {collection}; reconcile them before retrying");
}
