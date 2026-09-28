using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class PersonGovernanceMigrationTests(MongoFixture fixture)
{
    private readonly IClock clock = new SystemClock();
    private static readonly DateTime Date = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Fresh_v9_is_empty_strict_repeatable_and_preserves_v1_through_v8()
    {
        await InDatabase(async db =>
        {
            await ApplyV8(db);
            var before = await Definitions(db); var history = await History(db);
            var indexes = new Dictionary<string, List<BsonDocument>>();
            foreach (var row in before) indexes[row["name"].AsString] = await Indexes(db, row["name"].AsString);
            await ApplyV9(db);
            Assert.Equal(history, (await History(db)).Take(8));
            foreach (var row in before)
            {
                var name = row["name"].AsString;
                Assert.Equal(row, await Definition(db, name)); Assert.Equal(indexes[name], await Indexes(db, name));
            }
            foreach (var name in PersonGovernanceSchemas.Collections)
            {
                Assert.Equal(0, await db.GetCollection<BsonDocument>(name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
                var options = (await Definition(db, name))["options"];
                Assert.Equal("strict", options["validationLevel"]); Assert.Equal("error", options["validationAction"]);
                Assert.Equal(PersonGovernanceSchemas.Validator(name), options["validator"]);
                Assert.DoesNotContain(await Indexes(db, name), index => index.Contains("expireAfterSeconds"));
            }
            Assert.Equal(0, await db.GetCollection<BsonDocument>("idCounters").CountDocumentsAsync(
                new BsonDocument("_id", new BsonDocument("$in", new BsonArray(PersonGovernanceSchemas.Collections)))));
            var applied = await History(db); await ApplyV9(db); Assert.Equal(applied, await History(db));
        });
    }

    [Fact]
    public async Task Nested_schema_types_unknown_fields_chronology_and_transitions_fail_closed()
    {
        await InDatabase(async db =>
        {
            await ApplyV8(db); await ApplyV9(db);
            foreach (var pair in new[] { ("personGovernancePolicies", Policy()), ("personReviewProposals", Proposal()), ("personReviewHistory", HistoryRow(Proposal())) })
            {
                var (name, valid) = pair;
                Assert.True(PersonGovernanceSchemas.IsValid(name, valid));
                await db.GetCollection<BsonDocument>(name).InsertOneAsync(valid);
                await db.GetCollection<BsonDocument>(name).DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
                var duplicate = new BsonDocument { AllowDuplicateNames = true }; duplicate.AddRange(valid.Elements); duplicate.Add("_id", valid["_id"]);
                Assert.False(PersonGovernanceSchemas.IsValid(name, duplicate));
                foreach (var field in valid.Names.Where(field => field != "expiresAt"))
                {
                    var missing = Clone(valid); missing.Remove(field); await Invalid(db, name, missing);
                    var wrong = Clone(valid); wrong[field] = field == "_id" ? new BsonInt64(0) : new BsonArray { valid[field] };
                    await Invalid(db, name, wrong);
                }
                var extra = Clone(valid); extra["unreviewed"] = true; await Invalid(db, name, extra);
            }
            var proposalMutations = new List<Action<BsonDocument>>
            {
                row => row["kind"] = "merge",
                row => row["resolution"] = BsonNull.Value,
                row => row["version"] = 2L,
                row => row["target"]["canonicalPersonId"] = 999L,
                row => row["target"]["userIds"] = new BsonArray { 10L, 10L },
                row => row["target"]["userIds"] = new BsonArray { 20L, 10L },
                row => row["target"]["personIds"] = new BsonArray { 1 },
                row => row["requesterPerson"]["userId"] = 99L,
                row => row["target"]["heads"][0]["revision"] = 1,
                row => row["target"]["heads"][0]["collection"] = "personGovernancePolicies",
                row => row["target"]["heads"][0]["digest"] = new string('A', 64),
                row => row["target"]["heads"] = new BsonArray { row["target"]["heads"][0], row["target"]["heads"][0] },
                row => { var one = Clone(row["target"]["heads"][0].AsBsonDocument); var two = Clone(one); two["revision"] = 2L; row["target"]["heads"] = new BsonArray { one, two }; },
                row => row["target"]["heads"] = new BsonArray(row["target"]["heads"].AsBsonArray.Reverse()),
                row => row["createdAt"] = new BsonDateTime(long.MaxValue),
                row => row["policy"]["unexpected"] = true,
                row => row["reason"] = "bad\nreason",
                row => row["correlationId"] = new string('c', 129),
                row => row["requestDigest"] = new string('0', 63)
            };
            foreach (var mutation in proposalMutations) { var row = Proposal(); mutation(row); await Invalid(db, "personReviewProposals", row); }
            var approvedMutations = new List<Action<BsonDocument>>
            {
                row => row.Remove("resolution"),
                row => { row["resolution"]["actorId"] = 20L; row["resolution"]["person"]["userId"] = 20L; },
                row => { row["resolution"]["actorId"] = 10L; row["resolution"]["person"]["userId"] = 10L; },
                row => row["resolution"]["person"]["userId"] = 99L,
                row => row["resolution"]["person"]["canonicalPersonId"] = row["requesterPerson"]["canonicalPersonId"],
                row => row["resolution"]["person"]["canonicalPersonId"] = row["target"]["canonicalPersonId"],
                row => row["target"]["personIds"] = new BsonArray { PersonTestData.PersonId(10), PersonTestData.PersonId(30) },
                row => row["resolution"]["decidedAt"] = Date.AddSeconds(-1),
                row => row["resolution"]["resultGroupVersion"] = 1L,
                row => row["resolution"]["affectedRevisions"] = new BsonArray(),
                row => row["resolution"]["affectedRevisions"][0]["userId"] = 99L,
                row => row["resolution"]["affectedRevisions"][0]["grantVersion"] = 3L,
                row => row["resolution"]["affectedRevisions"][0]["previousSecurityVersion"] = long.MaxValue,
                row => row["target"]["groupVersion"] = long.MaxValue,
                row => { row["target"]["groupVersion"] = long.MaxValue; row["resolution"]["resultGroupVersion"] = long.MaxValue; },
                row => { row["resolution"]["affectedRevisions"][0]["previousGrantVersion"] = long.MaxValue; row["resolution"]["affectedRevisions"][0]["grantVersion"] = long.MaxValue; },
                row => { row["resolution"]["affectedRevisions"][0]["previousSecurityVersion"] = long.MaxValue; row["resolution"]["affectedRevisions"][0]["securityVersion"] = long.MaxValue; }
            };
            foreach (var mutation in approvedMutations) { var row = Proposal("approved"); mutation(row); await Invalid(db, "personReviewProposals", row); }
            foreach (var status in new[] { "approved", "rejected", "withdrawn" })
            {
                var row = Proposal(status); Assert.True(PersonGovernanceSchemas.IsValid("personReviewProposals", row));
                await db.GetCollection<BsonDocument>("personReviewProposals").InsertOneAsync(row);
                await db.GetCollection<BsonDocument>("personReviewProposals").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
                var history = HistoryRow(row); Assert.True(PersonGovernanceSchemas.IsValid("personReviewHistory", history));
                await db.GetCollection<BsonDocument>("personReviewHistory").InsertOneAsync(history);
                await db.GetCollection<BsonDocument>("personReviewHistory").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
                var wrongHistory = Clone(history); wrongHistory["actorGrantVersion"] = 2L; await Invalid(db, "personReviewHistory", wrongHistory);
                wrongHistory = Clone(history); wrongHistory["reason"] = "Different"; await Invalid(db, "personReviewHistory", wrongHistory);
                wrongHistory = Clone(history); wrongHistory["event"] = "proposed"; await Invalid(db, "personReviewHistory", wrongHistory);
            }
            var withdrawal = Proposal("withdrawn"); withdrawal["resolution"]["actorId"] = 30L; withdrawal["resolution"]["person"]["userId"] = 30L;
            await Invalid(db, "personReviewProposals", withdrawal);
            var rejected = Proposal("rejected"); rejected["resolution"]["affectedRevisions"] = Proposal("approved")["resolution"]["affectedRevisions"];
            await Invalid(db, "personReviewProposals", rejected);
            var policy = Policy(); policy["expiresAt"] = Date; await Invalid(db, "personGovernancePolicies", policy);
            policy = Policy(); policy["effectiveAt"] = new BsonDateTime(long.MinValue); await Invalid(db, "personGovernancePolicies", policy);
        });
    }

    [Theory]
    [InlineData("record")]
    [InlineData("policy")]
    [InlineData("resolution-policy")]
    [InlineData("future-policy")]
    [InlineData("missing-history")]
    [InlineData("missing-terminal")]
    [InlineData("orphan-history")]
    [InlineData("duplicate-transition")]
    [InlineData("latest-snapshot")]
    [InlineData("rewritten-request")]
    [InlineData("missing-head")]
    [InlineData("wrong-head-id-type")]
    [InlineData("counter")]
    [InlineData("ttl")]
    [InlineData("unique")]
    [InlineData("sparse")]
    [InlineData("partial")]
    [InlineData("hidden")]
    [InlineData("collation")]
    [InlineData("name")]
    public async Task Every_new_collection_and_relationship_is_checked_before_ddl_or_counter_writes(string kind)
    {
        await InDatabase(async db =>
        {
            await ApplyV8(db); await Seed(db);
            var proposals = db.GetCollection<BsonDocument>("personReviewProposals"); var histories = db.GetCollection<BsonDocument>("personReviewHistory");
            if (kind == "record") await histories.UpdateOneAsync(new BsonDocument("_id", 302L), Builders<BsonDocument>.Update.Set("actorId", 30));
            if (kind == "policy") await db.GetCollection<BsonDocument>("personGovernancePolicies").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
            if (kind == "resolution-policy")
            {
                await proposals.UpdateOneAsync(new BsonDocument("_id", 200L), Builders<BsonDocument>.Update.Set("resolution.policy.id", 999L));
                await histories.UpdateOneAsync(new BsonDocument("_id", 302L), Builders<BsonDocument>.Update.Set("proposal.resolution.policy.id", 999L));
            }
            if (kind == "future-policy")
            {
                await proposals.UpdateOneAsync(new BsonDocument("_id", 200L), Builders<BsonDocument>.Update.Set("policy.version", 2L));
                await histories.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Set("proposal.policy.version", 2L));
            }
            if (kind == "missing-history") await histories.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
            if (kind == "missing-terminal") await histories.DeleteOneAsync(new BsonDocument("_id", 302L));
            if (kind == "orphan-history") await proposals.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
            if (kind == "duplicate-transition") { var duplicate = HistoryRow(Proposal()); duplicate["_id"] = 303L; await histories.InsertOneAsync(duplicate); }
            if (kind == "latest-snapshot") await proposals.UpdateOneAsync(new BsonDocument("_id", 200L), Builders<BsonDocument>.Update.Set("resolution.reason", "Changed reason"));
            if (kind == "rewritten-request")
            {
                await proposals.UpdateOneAsync(new BsonDocument("_id", 200L), Builders<BsonDocument>.Update.Set("reason", "Rewritten original"));
                await histories.UpdateOneAsync(new BsonDocument("_id", 302L), Builders<BsonDocument>.Update.Set("proposal.reason", "Rewritten original"));
            }
            if (kind == "missing-head") await db.GetCollection<BsonDocument>("personEvidenceVersions").DeleteOneAsync(new BsonDocument("_id", PersonTestData.EvidenceId(10)));
            if (kind == "wrong-head-id-type")
            {
                var row = await db.GetCollection<BsonDocument>("identities").Find(new BsonDocument("_id", 10L)).SingleAsync();
                await db.GetCollection<BsonDocument>("identities").DeleteOneAsync(new BsonDocument("_id", 10L));
                row["_id"] = 10; await db.GetCollection<BsonDocument>("identities").InsertOneAsync(row, new InsertOneOptions { BypassDocumentValidation = true });
            }
            await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "personReviewHistory" }, { "value", kind == "counter" ? -1L : 2L } });
            if (kind is "ttl" or "unique" or "sparse" or "partial" or "hidden" or "collation" or "name")
            {
                var options = new CreateIndexOptions<BsonDocument> { Name = "ux_proposalId_transitionVersion", Unique = kind != "unique" };
                if (kind == "ttl") options.ExpireAfter = TimeSpan.FromDays(1000);
                if (kind == "sparse") options.Sparse = true;
                if (kind == "partial") options.PartialFilterExpression = new BsonDocument("proposalId", 200L);
                if (kind == "hidden") options.Hidden = true;
                if (kind == "collation") options.Collation = new Collation("en");
                var key = kind is "ttl" or "name" ? new BsonDocument("createdAt", 1) : new BsonDocument { { "proposalId", 1 }, { "transitionVersion", 1 } };
                await histories.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key, options));
            }
            var before = await Definitions(db); var counters = await db.GetCollection<BsonDocument>("idCounters").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            var indexes = new Dictionary<string, List<BsonDocument>>(); foreach (var name in PersonGovernanceSchemas.Collections) indexes[name] = await Indexes(db, name);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyV9(db)); Assert.Contains("reconcile", error.Message);
            Assert.Equal(before, await Definitions(db)); Assert.Equal(counters, await db.GetCollection<BsonDocument>("idCounters").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());
            foreach (var name in PersonGovernanceSchemas.Collections) Assert.Equal(indexes[name], await Indexes(db, name));
        });
    }

    [Theory]
    [InlineData("capped")]
    [InlineData("view")]
    [InlineData("timeseries")]
    [InlineData("collation")]
    [InlineData("retention")]
    public async Task Unsafe_final_collection_options_do_not_create_earlier_collections(string kind)
    {
        await InDatabase(async db =>
        {
            await ApplyV8(db); const string name = "personReviewHistory";
            if (kind == "capped") await db.CreateCollectionAsync(name, new CreateCollectionOptions { Capped = true, MaxSize = 4096 });
            if (kind == "collation") await db.CreateCollectionAsync(name, new CreateCollectionOptions { Collation = new Collation("en") });
            if (kind == "view") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name }, { "viewOn", "fixtureSource" }, { "pipeline", new BsonArray() } });
            if (kind == "timeseries") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name }, { "timeseries", new BsonDocument("timeField", "createdAt") } });
            if (kind == "retention") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name }, { "clusteredIndex", new BsonDocument { { "key", new BsonDocument("_id", 1) }, { "unique", true } } }, { "expireAfterSeconds", 86400L } });
            var before = await Definitions(db); await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyV9(db)); Assert.Equal(before, await Definitions(db));
        });
    }

    [Fact]
    public async Task Majority_resume_preserves_stronger_rules_historical_pins_semantic_snapshots_and_highwater()
    {
        await InDatabase(async db =>
        {
            await ApplyV8(db); await Seed(db);
            var prior = new BsonDocument("manifestReference", new BsonDocument("$ne", "forbidden"));
            await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "personGovernancePolicies" }, { "validator", prior } });
            await db.GetCollection<BsonDocument>("personReviewHistory").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "proposalId", 1 }, { "transitionVersion", 1 } }, new CreateIndexOptions { Name = "reviewed_transition", Unique = true }));
            await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "personReviewProposals" }, { "value", 500L } });
            // Historical pins do not require today's status, version or document insertion order.
            await db.GetCollection<BsonDocument>("personGovernancePolicies").UpdateOneAsync(new BsonDocument("_id", 100L),
                Builders<BsonDocument>.Update.Set("version", 8L).Set("status", "revoked").Set("manifestDigest", new string('b', 64)));
            await db.GetCollection<BsonDocument>("personEquivalenceGroups").UpdateOneAsync(new BsonDocument("_id", PersonTestData.GroupId(10)),
                Builders<BsonDocument>.Update.Set("version", 9L).Set("status", "suspended"));
            var proposal = await db.GetCollection<BsonDocument>("personReviewProposals").Find(new BsonDocument("_id", 200L)).SingleAsync();
            await db.GetCollection<BsonDocument>("personReviewProposals").ReplaceOneAsync(new BsonDocument("_id", 200L), new BsonDocument(proposal.Elements.Reverse()));
            using var stop = new CancellationTokenSource(); var commands = new ConcurrentBag<BsonDocument>();
            var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0&w=1");
            settings.ClusterConfigurator = builder =>
            {
                builder.Subscribe<CommandStartedEvent>(command => commands.Add(Clone(command.Command)));
                builder.Subscribe<CommandSucceededEvent>(command => { if (command.CommandName == "collMod") stop.Cancel(); });
            };
            var observed = new MongoClient(settings).GetDatabase(db.DatabaseNamespace.DatabaseName);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApplyV9(observed, stop.Token));
            var combined = (await Definition(db, "personGovernancePolicies"))["options"]["validator"];
            Assert.Contains(combined["$and"].AsBsonArray, rule => rule == prior);
            await ApplyV9(db); Assert.Equal(combined, (await Definition(db, "personGovernancePolicies"))["options"]["validator"]);
            Assert.Contains(await Indexes(db, "personReviewHistory"), index => index["name"] == "reviewed_transition");
            Assert.DoesNotContain(await Indexes(db, "personReviewHistory"), index => index["name"] == "ux_proposalId_transitionVersion");
            foreach (var pair in new[] { ("personGovernancePolicies", 101L), ("personReviewProposals", 501L), ("personReviewHistory", 303L) })
                Assert.Equal(pair.Item2, (await new IdAllocator(db).NextAsync(pair.Item1)).Value);
            var ddl = commands.Where(command => command.Contains("collMod") || command.Contains("create") || command.Contains("createIndexes")).ToArray();
            Assert.NotEmpty(ddl); Assert.All(ddl, command => Assert.Equal("majority", command["writeConcern"]["w"]));
            var forbidden = Policy(); forbidden["_id"] = 101L; forbidden["manifestReference"] = "forbidden";
            Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>("personGovernancePolicies").InsertOneAsync(forbidden))).WriteError.Code);
        });
    }

    private static BsonDocument Policy() => new()
    {
        { "_id", 100L }, { "schemaVersion", 1 }, { "status", "active" }, { "version", 1L }, { "fence", 0L },
        { "manifestReference", "synthetic-reviewed-manifest" }, { "manifestDigest", Digest }, { "effectiveAt", Date }, { "expiresAt", BsonNull.Value }
    };
    private static BsonDocument Pin() => new() { { "id", 100L }, { "version", 1L }, { "digest", Digest } };
    private static BsonDocument Head(string collection, long id) => new() { { "collection", collection }, { "id", id }, { "revision", 1L }, { "digest", Digest } };
    private static BsonArray Heads(long userId) => new(new[]
    {
        Head("identities", userId), Head("personAccountBindings", userId), Head("personEquivalenceGroups", PersonTestData.GroupId(userId)),
        Head("personEvidenceSources", PersonTestData.SourceId), Head("personEvidenceVersions", PersonTestData.EvidenceId(userId)), Head("personRecords", PersonTestData.PersonId(userId))
    });
    private static BsonDocument Person(long userId) => new() { { "userId", userId }, { "canonicalPersonId", PersonTestData.PersonId(userId) }, { "heads", Heads(userId) } };
    private static BsonDocument Proposal(string status = "pending")
    {
        var row = new BsonDocument
        {
            { "_id", 200L }, { "schemaVersion", 1 }, { "kind", "equivalence-group-suspension" }, { "version", status == "pending" ? 1L : 2L }, { "status", status },
            { "requestedBy", 20L }, { "requesterGrantVersion", 1L }, { "requesterSecurityVersion", 1L }, { "createdAt", Date },
            { "reason", "Reviewed synthetic suspension request" }, { "correlationId", "fixture-propose" },
            { "target", new BsonDocument { { "groupId", PersonTestData.GroupId(10) }, { "groupVersion", 1L }, { "groupStatus", "active" },
                { "canonicalPersonId", PersonTestData.PersonId(10) }, { "personIds", new BsonArray { PersonTestData.PersonId(10) } },
                { "userIds", new BsonArray { 10L } }, { "heads", Heads(10) } } },
            { "requesterPerson", Person(20) }, { "policy", Pin() }, { "requestDigest", Digest }
        };
        if (status == "pending") return row;
        var actor = status == "withdrawn" ? 20L : 30L;
        row.Add("resolution", new BsonDocument
        {
            { "actorId", actor }, { "actorGrantVersion", 1L }, { "actorSecurityVersion", 1L }, { "decidedAt", Date.AddMinutes(1) },
            { "reason", "Independent synthetic decision" }, { "correlationId", "fixture-decide" }, { "person", Person(actor) }, { "policy", Pin() },
            { "resultGroupVersion", status == "approved" ? 2L : 1L },
            { "affectedRevisions", status == "approved" ? new BsonArray { new BsonDocument
                { { "userId", 10L }, { "previousGrantVersion", 1L }, { "grantVersion", 2L }, { "previousSecurityVersion", 1L }, { "securityVersion", 2L } } } : new BsonArray() }
        });
        return row;
    }
    private static BsonDocument HistoryRow(BsonDocument proposal)
    {
        var pending = proposal["status"] == "pending";
        var metadata = pending ? proposal : proposal["resolution"].AsBsonDocument;
        return new()
        {
            { "_id", pending ? 301L : 302L }, { "schemaVersion", 1 }, { "proposalId", proposal["_id"] },
            { "transitionVersion", proposal["version"] }, { "event", pending ? "proposed" : proposal["status"] },
            { "actorId", metadata[pending ? "requestedBy" : "actorId"] }, { "actorGrantVersion", metadata[pending ? "requesterGrantVersion" : "actorGrantVersion"] },
            { "actorSecurityVersion", metadata[pending ? "requesterSecurityVersion" : "actorSecurityVersion"] },
            { "createdAt", metadata[pending ? "createdAt" : "decidedAt"] }, { "reason", metadata["reason"] },
            { "correlationId", metadata["correlationId"] }, { "proposal", Clone(proposal) }
        };
    }
    private static async Task Seed(IMongoDatabase db)
    {
        await PersonTestData.SeedDistinctPeopleAsync(db, new MutableAuthorizationClock(new DateTimeOffset(Date)), 10, 20, 30);
        await db.GetCollection<BsonDocument>("personGovernancePolicies").InsertOneAsync(Policy());
        await db.GetCollection<BsonDocument>("personReviewProposals").InsertOneAsync(Proposal("approved"));
        await db.GetCollection<BsonDocument>("personReviewHistory").InsertManyAsync([HistoryRow(Proposal()), HistoryRow(Proposal("approved"))]);
    }
    private async Task InDatabase(Func<IMongoDatabase, Task> run)
    {
        var name = "ut_person_governance_migration_" + Guid.NewGuid().ToString("N");
        try { await run(fixture.Client.GetDatabase(name)); } finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private async Task ApplyV8(IMongoDatabase db)
    {
        IMigrationStep[] steps = [new FoundationMigration(db), new AuthorizationMigration(db), new AuthorizationLifecycleMigration(db), new IdentityMigration(db),
            new ProviderLifecycleMigration(db), new CatalogVisibilityMigration(db), new HistoricalReadMigration(db), new PersonAuthorityMigration(db)];
        var gate = new MigrationGate(db, clock, 9);
        foreach (var step in steps) { var lease = await gate.AcquireAsync(step.Version, CancellationToken.None); await step.ApplyAsync(lease!, CancellationToken.None); await lease!.CompleteAsync(CancellationToken.None); }
    }
    private async Task ApplyV9(IMongoDatabase db, CancellationToken token = default)
    {
        var lease = await new MigrationGate(db, clock, 9).AcquireAsync(9, token); if (lease is null) return;
        try { await new PersonGovernanceMigration(db).ApplyAsync(lease, token); await lease.CompleteAsync(token); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    private static BsonDocument Clone(BsonDocument value) => value.DeepClone().AsBsonDocument;
    private static async Task Invalid(IMongoDatabase db, string name, BsonDocument row)
    {
        Assert.False(PersonGovernanceSchemas.IsValid(name, row));
        Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>(name).InsertOneAsync(row))).WriteError.Code);
    }
    private static Task<List<BsonDocument>> History(IMongoDatabase db) => db.GetCollection<BsonDocument>("schemaMigrations").Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync();
    private static async Task<List<BsonDocument>> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonDocument> Definition(IMongoDatabase db, string name) => await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
    private static async Task<List<BsonDocument>> Indexes(IMongoDatabase db, string name) => await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync();
}
