using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo.Migrations;

// v10 retains private review previews. No rows, appointments or retention policy are seeded.
internal sealed class PersonReviewPreviewMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 10;
    private sealed record RequiredIndex(string Name, BsonDocument Key, bool Unique = false);
    private sealed record Plan(string Name, bool Exists, BsonDocument Validator, List<RequiredIndex> Indexes, long Maximum);
    private static RequiredIndex[] Indexes(string collection) => collection == "personReviewPreviews"
        ? [new("ux_reference", new("reference", 1), true)]
        : throw new ArgumentOutOfRangeException(nameof(collection));

    public async Task ApplyAsync(MigrationLease lease, CancellationToken token)
    {
        var plans = new List<Plan>();
        var documents = new Dictionary<string, Dictionary<long, BsonDocument>>();
        foreach (var name in PersonReviewPreviewSchemas.Collections)
        {
            await lease.RenewAsync(token);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, token)).SingleOrDefaultAsync(token);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "collection") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.TryGetValue("collation", out var collation) && !SimpleCollation(collation))
                throw Incompatible(name, "collection options");
            var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var required = PersonReviewPreviewSchemas.Validator(name);
            var validator = previous.ElementCount == 0 ? required : Contains(previous, required) ? previous : new BsonDocument("$and", new BsonArray { previous, required });
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var records = await rows.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(token);
            if (records.Any(row => !PersonReviewPreviewSchemas.IsValid(name, row)) ||
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
        const string name = "personReviewPreviews";
        var references = new Dictionary<string, Dictionary<long, Dictionary<string, long>>>(StringComparer.Ordinal);
        var policies = new Dictionary<long, BsonDocument>();
        var proposals = new Dictionary<long, (BsonDocument Row, BsonDocument[] History)>();
        void Reference(string collection, BsonValue value, string? revisionField = null, long revision = 1)
        {
            if (!references.TryGetValue(collection, out var ids)) references[collection] = ids = [];
            if (!ids.TryGetValue(value.AsInt64, out var versions)) ids[value.AsInt64] = versions = [];
            if (revisionField is not null) versions[revisionField] = Math.Max(versions.GetValueOrDefault(revisionField), revision);
        }
        async Task<BsonDocument> ExactRow(string collection, long id)
        {
            var found = await database.GetCollection<BsonDocument>(collection).WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument("_id", id)).SingleOrDefaultAsync(token);
            if (found is null || !found.TryGetValue("_id", out var value) || !value.IsInt64 || value.AsInt64 != id)
                throw Incompatible(name, "exact typed " + collection + " cross-links");
            return found;
        }
        async Task<(BsonDocument Row, BsonDocument[] History)> Proposal(long id)
        {
            if (proposals.TryGetValue(id, out var cached)) return cached;
            var proposal = await ExactRow("personReviewProposals", id);
            if (!PersonGovernanceSchemas.IsValid("personReviewProposals", proposal)) throw Incompatible(name, "proposal cross-links");
            var events = (await database.GetCollection<BsonDocument>("personReviewHistory").WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument("proposalId", id)).Limit(3).ToListAsync(token)).ToArray();
            var terminal = proposal["status"] != "pending";
            if (events.Length != (terminal ? 2 : 1) || events.Any(row => !PersonGovernanceSchemas.IsValid("personReviewHistory", row)) ||
                events.Count(row => row["transitionVersion"] == 1L) != 1 || terminal && events.Count(row => row["transitionVersion"] == 2L) != 1)
                throw Incompatible(name, "proposal history cross-links");
            var original = proposal.DeepClone().AsBsonDocument;
            original["version"] = 1L; original["status"] = "pending"; original.Remove("resolution");
            if (!PersonGovernanceSchemas.SemanticEquals(events.Single(row => row["transitionVersion"] == 1L)["proposal"], original) ||
                terminal && !PersonGovernanceSchemas.SemanticEquals(events.Single(row => row["transitionVersion"] == 2L)["proposal"], proposal))
                throw Incompatible(name, "immutable original request cross-links");
            var result = (proposal, events); proposals[id] = result; return result;
        }
        foreach (var row in rows[name].Values)
        {
            Reference("identities", row["actorId"], "grantVersion", row["grantVersion"].AsInt64);
            Reference("identities", row["actorId"], "securityVersion", row["securityVersion"].AsInt64);
            var target = row["target"].AsBsonDocument;
            Reference("personEquivalenceGroups", target["groupId"], "version", target["groupVersion"].AsInt64);
            Reference("personRecords", target["canonicalPersonId"]);
            foreach (var id in target["personIds"].AsBsonArray) Reference("personRecords", id);
            foreach (var id in target["userIds"].AsBsonArray)
            {
                Reference("identities", id); Reference("personAccountBindings", id);
            }
            foreach (var head in target["heads"].AsBsonArray)
            {
                var collection = head["collection"].AsString;
                Reference(collection, head["id"], collection == "identities" ? "grantVersion" : collection == "personEvidenceVersions" ? "evidenceVersion" : "version",
                    head["revision"].AsInt64);
            }
            foreach (var profile in row["profiles"].AsBsonArray)
            {
                Reference("identities", profile["userId"]);
                Reference("accountProfiles", profile["userId"], "version", profile["version"].AsInt64);
            }
            var pin = row["policy"].AsBsonDocument; var policyId = pin["id"].AsInt64;
            if (!policies.TryGetValue(policyId, out var policy)) policies[policyId] = policy = await ExactRow("personGovernancePolicies", policyId);
            if (!PersonGovernanceSchemas.IsValid("personGovernancePolicies", policy) || policy["version"].AsInt64 < pin["version"].AsInt64)
                throw Incompatible(name, "policy cross-links");

            var approve = row["kind"] == "approve";
            if (approve)
            {
                var reviewed = (await Proposal(row["proposalId"].AsInt64)).Row;
                if (reviewed["requestedBy"] != row["approvalRequestedBy"] || reviewed["requestDigest"] != row["proposalDigest"] ||
                    !PersonGovernanceSchemas.SemanticEquals(reviewed["target"], target) || !PersonGovernanceSchemas.SemanticEquals(reviewed["policy"], pin))
                    throw Incompatible(name, "reviewed original request cross-links");
            }
            if (!row.TryGetValue("consumption", out var consumption)) continue;
            var result = await Proposal(consumption["resultProposalId"].AsInt64);
            var history = result.History.SingleOrDefault(item => item["_id"] == consumption["resultHistoryId"]);
            if (history is null || history["event"] != (approve ? "approved" : "proposed") || history["actorId"] != row["actorId"] ||
                history["actorGrantVersion"] != row["grantVersion"] || history["actorSecurityVersion"] != row["securityVersion"] ||
                !PersonGovernanceSchemas.SemanticEquals(history["proposal"]["target"], target) ||
                !PersonGovernanceSchemas.SemanticEquals(approve ? history["proposal"]["resolution"]["policy"] : history["proposal"]["policy"], pin) ||
                approve && history["proposal"]["requestDigest"] != row["proposalDigest"])
                throw Incompatible(name, "consumed result/history cross-links");
        }

        // Sessions legitimately disappear under their existing TTL. These historical pins
        // must never be used as current session authority; runtime admission revalidates them.
        // All durable references are exact typed IDs, with historical revisions allowed to advance.
        foreach (var reference in references.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var ids in reference.Value.Keys.Order().Chunk(256))
            {
                var found = await database.GetCollection<BsonDocument>(reference.Key).WithReadConcern(ReadConcern.Majority)
                    .Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(ids)))).ToListAsync(token);
                if (found.Any(row => !row.TryGetValue("_id", out var id) || !id.IsInt64) ||
                    !ids.SequenceEqual(found.Select(row => row["_id"].AsInt64).Order()))
                    throw Incompatible(name, "historical target/profile/account cross-links");
                foreach (var document in found)
                    foreach (var revision in reference.Value[document["_id"].AsInt64])
                    {
                        var value = document.GetValue(revision.Key,
                            reference.Key == "identities" && revision.Key == "securityVersion" ? new BsonInt64(1) : BsonNull.Value);
                        if (!value.IsInt64 || value.AsInt64 < revision.Value)
                            throw Incompatible(name, "historical revision cross-links");
                    }
            }
        }
    }

    private static bool IsIdentityIndex(BsonDocument index) => index["key"].AsBsonDocument.Equals(new BsonDocument("_id", 1)) && index["name"] == "_id_" && !UnsafeIndexOptions(index);
    private static bool UnsafeIndexOptions(BsonDocument index) => index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean || index.Contains("partialFilterExpression") || index.Contains("expireAfterSeconds") || index.TryGetValue("collation", out var collation) && !SimpleCollation(collation);
    private static bool SimpleCollation(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.GetValue("locale", "") == "simple";
    private static bool Contains(BsonDocument validator, BsonDocument required) => validator.Equals(required) || validator.TryGetValue("$and", out var value) && value.IsBsonArray && value.AsBsonArray.Any(item => item.IsBsonDocument && Contains(item.AsBsonDocument, required));
    private static InvalidOperationException Incompatible(string collection, string reason) => new($"Person-preview migration found incompatible {reason} in {collection}; reconcile them before retrying");
}
