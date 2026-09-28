using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo.Migrations;

// v9 stores private policy references and reviewed suspension history. It never appoints or seeds authority.
internal sealed class PersonGovernanceMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 9;
    private sealed record RequiredIndex(string Name, BsonDocument Key, bool Unique = false);
    private sealed record Plan(string Name, bool Exists, BsonDocument Validator, List<RequiredIndex> Indexes, long Maximum);
    private static RequiredIndex[] Indexes(string collection) => collection switch
    {
        "personGovernancePolicies" => [],
        "personReviewProposals" => [new("ix_status_id", new() { { "status", 1 }, { "_id", 1 } })],
        "personReviewHistory" => [new("ux_proposalId_transitionVersion", new() { { "proposalId", 1 }, { "transitionVersion", 1 } }, true)],
        _ => throw new ArgumentOutOfRangeException(nameof(collection))
    };

    public async Task ApplyAsync(MigrationLease lease, CancellationToken token)
    {
        var plans = new List<Plan>();
        var documents = new Dictionary<string, Dictionary<long, BsonDocument>>();
        foreach (var name in PersonGovernanceSchemas.Collections)
        {
            await lease.RenewAsync(token);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, token)).SingleOrDefaultAsync(token);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "collection") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.TryGetValue("collation", out var collation) && !SimpleCollation(collation))
                throw Incompatible(name, "collection options");
            var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var required = PersonGovernanceSchemas.Validator(name);
            var validator = previous.ElementCount == 0 ? required : Contains(previous, required) ? previous : new BsonDocument("$and", new BsonArray { previous, required });
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var records = await rows.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(token);
            if (records.Any(row => !PersonGovernanceSchemas.IsValid(name, row)) ||
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
            if (plan.Maximum > 0)
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
        var policies = rows["personGovernancePolicies"]; var proposals = rows["personReviewProposals"]; var history = rows["personReviewHistory"];
        var references = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        void Reference(string collection, BsonValue id)
        {
            if (!references.TryGetValue(collection, out var ids)) references[collection] = ids = [];
            ids.Add(id.AsInt64);
        }
        void Heads(BsonArray values)
        {
            foreach (var head in values) Reference(head["collection"].AsString, head["id"]);
        }
        void Person(BsonDocument person)
        {
            Reference("identities", person["userId"]); Reference("personRecords", person["canonicalPersonId"]);
            Heads(person["heads"].AsBsonArray);
        }
        void Policy(BsonDocument pin)
        {
            if (!policies.TryGetValue(pin["id"].AsInt64, out var policy) || pin["version"].AsInt64 > policy["version"].AsInt64)
                throw Incompatible("personReviewProposals", "policy cross-links");
            // Historical digests cover the complete pinned semantic policy, not just manifestDigest.
            // Old pins remain valid history when the present policy is suspended, revoked or revised.
        }
        foreach (var proposal in proposals.Values)
        {
            var target = proposal["target"].AsBsonDocument;
            Reference("personEquivalenceGroups", target["groupId"]);
            foreach (var person in target["personIds"].AsBsonArray) Reference("personRecords", person);
            foreach (var user in target["userIds"].AsBsonArray)
            {
                Reference("identities", user); Reference("personAccountBindings", user);
            }
            Heads(target["heads"].AsBsonArray);
            Person(proposal["requesterPerson"].AsBsonDocument);
            Policy(proposal["policy"].AsBsonDocument);
            if (proposal.TryGetValue("resolution", out var resolution))
            {
                Person(resolution["person"].AsBsonDocument); Policy(resolution["policy"].AsBsonDocument);
            }
        }
        var byProposal = history.Values.GroupBy(row => row["proposalId"].AsInt64).ToDictionary(group => group.Key, group => group.ToArray());
        if (byProposal.Keys.Any(id => !proposals.ContainsKey(id))) throw Incompatible("personReviewHistory", "orphan proposal history");
        foreach (var proposal in proposals.Values)
        {
            if (!byProposal.TryGetValue(proposal["_id"].AsInt64, out var events)) throw Incompatible("personReviewHistory", "missing proposal history");
            var terminal = proposal["status"] != "pending";
            if (events.Length != (terminal ? 2 : 1) || events.Count(row => row["transitionVersion"] == 1L) != 1 ||
                terminal && events.Count(row => row["transitionVersion"] == 2L) != 1)
                throw Incompatible("personReviewHistory", "incomplete or duplicate proposal history");
            var original = proposal.DeepClone().AsBsonDocument;
            original["version"] = 1L; original["status"] = "pending"; original.Remove("resolution");
            if (!PersonGovernanceSchemas.SemanticEquals(events.Single(row => row["transitionVersion"] == 1L)["proposal"], original) ||
                terminal && !PersonGovernanceSchemas.SemanticEquals(events.Single(row => row["transitionVersion"] == 2L)["proposal"], proposal))
                throw Incompatible("personReviewHistory", "immutable proposal snapshots");
        }
        // Historical references need retained exact-typed identities, not present positive authority.
        // Process IDs in bounded groups and require every referenced ID exactly once.
        foreach (var reference in references.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var ids in reference.Value.Order().Chunk(256))
            {
                var found = await database.GetCollection<BsonDocument>(reference.Key).WithReadConcern(ReadConcern.Majority)
                    .Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(ids))))
                    .Project(new BsonDocument("_id", 1)).ToListAsync(token);
                if (found.Any(row => !row.TryGetValue("_id", out var id) || !id.IsInt64) ||
                    !ids.SequenceEqual(found.Select(row => row["_id"].AsInt64).Order()))
                    throw Incompatible("personReviewProposals", "historical target/person/head cross-links");
            }
        }
    }

    private static bool IsIdentityIndex(BsonDocument index) => index["key"].AsBsonDocument.Equals(new BsonDocument("_id", 1)) && index["name"] == "_id_" && !UnsafeIndexOptions(index);
    private static bool UnsafeIndexOptions(BsonDocument index) => index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean || index.Contains("partialFilterExpression") || index.Contains("expireAfterSeconds") || index.TryGetValue("collation", out var collation) && !SimpleCollation(collation);
    private static bool SimpleCollation(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.GetValue("locale", "") == "simple";
    private static bool Contains(BsonDocument validator, BsonDocument required) => validator.Equals(required) || validator.TryGetValue("$and", out var value) && value.IsBsonArray && value.AsBsonArray.Any(item => item.IsBsonDocument && Contains(item.AsBsonDocument, required));
    private static InvalidOperationException Incompatible(string collection, string reason) => new($"Person-governance migration found incompatible {reason} in {collection}; reconcile them before retrying");
}
