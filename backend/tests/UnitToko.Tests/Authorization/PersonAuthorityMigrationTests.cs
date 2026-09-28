using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class PersonAuthorityMigrationTests(MongoFixture fixture)
{
    private readonly IClock clock = new SystemClock();
    private static readonly DateTime Date = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Fresh_v8_is_empty_strict_repeatable_and_preserves_every_prior_definition_and_history()
    {
        await InDatabase(async db =>
        {
            await ApplyV7(db);
            var before = await Definitions(db); var history = await History(db);
            var indexes = new Dictionary<string, List<BsonDocument>>();
            foreach (var definition in before) indexes[definition["name"].AsString] = await Indexes(db, definition["name"].AsString);
            await ApplyV8(db);
            Assert.Equal(history, (await History(db)).Take(7));
            foreach (var definition in before)
            {
                var name = definition["name"].AsString;
                Assert.Equal(definition, await Definition(db, name)); Assert.Equal(indexes[name], await Indexes(db, name));
            }
            foreach (var name in PersonAuthoritySchemas.Collections)
            {
                Assert.Equal(0, await db.GetCollection<BsonDocument>(name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
                var options = (await Definition(db, name))["options"];
                Assert.Equal("strict", options["validationLevel"]); Assert.Equal("error", options["validationAction"]);
                Assert.Equal(PersonAuthoritySchemas.Validator(name), options["validator"]);
                Assert.DoesNotContain(await Indexes(db, name), index => index.Contains("expireAfterSeconds"));
            }
            Assert.DoesNotContain(await Definitions(db), definition => definition["name"] == "personReviewProposals" || definition["name"] == "personReviewHistory");
            Assert.Equal(0, await db.GetCollection<BsonDocument>("idCounters").CountDocumentsAsync(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(PersonAuthoritySchemas.Collections)))));
            var completed = await History(db); await ApplyV8(db); Assert.Equal(completed, await History(db));
        });
    }

    [Fact]
    public async Task Every_schema_rejects_missing_unknown_wrong_type_and_relationally_invalid_values()
    {
        await InDatabase(async db =>
        {
            await ApplyV7(db); await ApplyV8(db);
            foreach (var pair in Graph())
            {
                var name = pair.Key; var valid = pair.Value;
                var duplicateField = new BsonDocument { AllowDuplicateNames = true };
                duplicateField.AddRange(valid.Elements); duplicateField.Add("_id", valid["_id"]);
                Assert.False(PersonAuthoritySchemas.IsValid(name, duplicateField));
                Assert.True(PersonAuthoritySchemas.IsValid(name, valid)); await db.GetCollection<BsonDocument>(name).InsertOneAsync(valid);
                await db.GetCollection<BsonDocument>(name).DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
                foreach (var field in valid.Names.Where(field => field != "expiresAt"))
                {
                    var missing = valid.DeepClone().AsBsonDocument; missing.Remove(field); await Invalid(db, name, missing);
                    var wrong = valid.DeepClone().AsBsonDocument; wrong[field] = field == "_id" ? new BsonInt64(0) : new BsonArray { valid[field] }; await Invalid(db, name, wrong);
                }
                var extra = valid.DeepClone().AsBsonDocument; extra["unexpected"] = true; await Invalid(db, name, extra);
                foreach (var field in valid.Where(field => field.Value.IsInt64))
                {
                    var wrong = valid.DeepClone().AsBsonDocument; wrong[field.Name] = (int)field.Value.AsInt64; await Invalid(db, name, wrong);
                }
                if (valid.Contains("expiresAt"))
                {
                    var absent = valid.DeepClone().AsBsonDocument; absent.Remove("expiresAt"); await db.GetCollection<BsonDocument>(name).InsertOneAsync(absent);
                    await db.GetCollection<BsonDocument>(name).DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
                    var oldEffective = valid.DeepClone().AsBsonDocument; oldEffective["effectiveAt"] = Date.AddYears(-20); await db.GetCollection<BsonDocument>(name).InsertOneAsync(oldEffective);
                    await db.GetCollection<BsonDocument>(name).DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
                    var expired = valid.DeepClone().AsBsonDocument; expired["expiresAt"] = Date; await Invalid(db, name, expired);
                }
                if (valid.Contains("approvedBy")) { var self = valid.DeepClone().AsBsonDocument; self["approvedBy"] = self["requestedBy"]; await Invalid(db, name, self); }
                if (valid.Contains("reviewedAt")) { var invalidDate = valid.DeepClone().AsBsonDocument; invalidDate["reviewedAt"] = new BsonDateTime(long.MaxValue); await Invalid(db, name, invalidDate); }
            }
            var group = Graph()["personEquivalenceGroups"]; group["memberPersonIds"] = new BsonArray { 20L, 20L }; await Invalid(db, "personEquivalenceGroups", group);
            group = Graph()["personEquivalenceGroups"]; group["canonicalPersonId"] = 21L; await Invalid(db, "personEquivalenceGroups", group);
            var binding = Graph()["personAccountBindings"]; binding["approvedBy"] = binding["_id"]; await Invalid(db, "personAccountBindings", binding);
            var evidence = Graph()["personEvidenceVersions"]; evidence["sourceDigest"] = new string('A', 64); await Invalid(db, "personEvidenceVersions", evidence);
            evidence = Graph()["personEvidenceVersions"]; evidence["createdAt"] = Date.AddSeconds(-1); await Invalid(db, "personEvidenceVersions", evidence);
        });
    }

    [Theory]
    [InlineData("record")]
    [InlineData("cross-link")]
    [InlineData("membership")]
    [InlineData("identity")]
    [InlineData("group-reviewer")]
    [InlineData("evidence-reviewer")]
    [InlineData("binding-reviewer")]
    [InlineData("counter")]
    [InlineData("duplicate")]
    [InlineData("ttl")]
    [InlineData("unique")]
    [InlineData("sparse")]
    [InlineData("partial")]
    [InlineData("hidden")]
    [InlineData("collation")]
    [InlineData("name")]
    public async Task All_five_collections_are_preflighted_before_any_schema_index_or_counter_mutation(string kind)
    {
        await InDatabase(async db =>
        {
            await ApplyV7(db); var graph = Graph();
            if (kind == "record") graph["personEvidenceVersions"]["sourceVersion"] = 1;
            if (kind == "cross-link") graph["personEvidenceVersions"]["sourceSubjectKey"] = "wrong";
            if (kind == "membership") graph["personEquivalenceGroups"]["memberUserIds"] = new BsonArray { 50L, 51L };
            var memberReviewer = kind is "group-reviewer" or "evidence-reviewer" or "binding-reviewer";
            if (memberReviewer)
            {
                graph["personEquivalenceGroups"]["memberUserIds"] = new BsonArray { 50L, 51L };
                graph[kind == "group-reviewer" ? "personEquivalenceGroups" : kind == "evidence-reviewer" ? "personEvidenceVersions" : "personAccountBindings"]["approvedBy"] = 51L;
            }
            await Seed(db, graph);
            if (memberReviewer)
            {
                var secondBinding = Graph()["personAccountBindings"]; secondBinding["_id"] = 51L;
                await db.GetCollection<BsonDocument>("personAccountBindings").InsertOneAsync(secondBinding);
                await db.GetCollection<BsonDocument>("identities").InsertOneAsync(new BsonDocument
                    { { "_id", 51L }, { "schemaVersion", 1 }, { "issuer", "https://fixture.example/" }, { "subject", "subject-51" }, { "status", "active" }, { "grantVersion", 1L }, { "fence", 0L } });
            }
            if (kind == "identity") await db.GetCollection<BsonDocument>("identities").DeleteOneAsync(new BsonDocument("_id", 80L));
            await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "personEvidenceVersions" }, { "value", kind == "counter" ? -1L : 2L } });
            if (kind == "duplicate") { var second = graph["personEvidenceVersions"].DeepClone().AsBsonDocument; second["_id"] = 31L; await db.GetCollection<BsonDocument>("personEvidenceVersions").InsertOneAsync(second); }
            if (kind is "ttl" or "unique" or "sparse" or "partial" or "hidden" or "collation" or "name")
            {
                var options = new CreateIndexOptions<BsonDocument> { Name = "ix_sourceId_createdAt" };
                if (kind == "ttl") options.ExpireAfter = TimeSpan.FromDays(1000);
                if (kind == "unique") options.Unique = true;
                if (kind == "sparse") options.Sparse = true;
                if (kind == "partial") options.PartialFilterExpression = new BsonDocument("sourceId", 10L);
                if (kind == "hidden") options.Hidden = true;
                if (kind == "collation") options.Collation = new Collation("en");
                var key = kind is "ttl" or "name" ? new BsonDocument("createdAt", 1) : new BsonDocument { { "sourceId", 1 }, { "createdAt", 1 } };
                await db.GetCollection<BsonDocument>("personEvidenceVersions").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key, options));
            }
            var definitions = await Definitions(db); var counters = await db.GetCollection<BsonDocument>("idCounters").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            var indexes = new Dictionary<string, List<BsonDocument>>(); foreach (var name in PersonAuthoritySchemas.Collections) indexes[name] = await Indexes(db, name);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyV8(db)); Assert.Contains("reconcile", error.Message);
            Assert.Equal(definitions, await Definitions(db)); Assert.Equal(counters, await db.GetCollection<BsonDocument>("idCounters").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync());
            foreach (var name in PersonAuthoritySchemas.Collections) Assert.Equal(indexes[name], await Indexes(db, name));
        });
    }

    [Theory]
    [InlineData("capped")]
    [InlineData("view")]
    [InlineData("timeseries")]
    [InlineData("collation")]
    [InlineData("retention")]
    public async Task Final_collection_options_are_rejected_before_earlier_collection_creation(string kind)
    {
        await InDatabase(async db =>
        {
            await ApplyV7(db); const string name = "personEvidenceVersions";
            if (kind == "capped") await db.CreateCollectionAsync(name, new CreateCollectionOptions { Capped = true, MaxSize = 4096 });
            if (kind == "collation") await db.CreateCollectionAsync(name, new CreateCollectionOptions { Collation = new Collation("en") });
            if (kind == "view") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name }, { "viewOn", "fixtureSource" }, { "pipeline", new BsonArray() } });
            if (kind == "timeseries") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name }, { "timeseries", new BsonDocument("timeField", "createdAt") } });
            if (kind == "retention") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", name }, { "clusteredIndex", new BsonDocument { { "key", new BsonDocument("_id", 1) }, { "unique", true } } }, { "expireAfterSeconds", 86400L } });
            var before = await Definitions(db); await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyV8(db)); Assert.Equal(before, await Definitions(db));
        });
    }

    [Fact]
    public async Task Interrupted_majority_ddl_resumes_preserving_stronger_rules_alternate_indexes_and_highwater()
    {
        await InDatabase(async db =>
        {
            await ApplyV7(db); await Seed(db, Graph());
            var prior = new BsonDocument("key", new BsonDocument("$ne", "forbidden"));
            await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "personEvidenceSources" }, { "validator", prior } });
            await db.GetCollection<BsonDocument>("personEvidenceSources").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("key", 1), new CreateIndexOptions { Name = "reviewed_source_key", Unique = true }));
            await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", "personRecords" }, { "value", 100L } });
            using var stop = new CancellationTokenSource(); var commands = new ConcurrentBag<BsonDocument>();
            var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0&w=1");
            settings.ClusterConfigurator = builder =>
            {
                builder.Subscribe<CommandStartedEvent>(command => commands.Add(command.Command.DeepClone().AsBsonDocument));
                builder.Subscribe<CommandSucceededEvent>(command => { if (command.CommandName == "collMod") stop.Cancel(); });
            };
            var observed = new MongoClient(settings).GetDatabase(db.DatabaseNamespace.DatabaseName);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApplyV8(observed, stop.Token));
            var combined = (await Definition(db, "personEvidenceSources"))["options"]["validator"];
            Assert.Contains(combined["$and"].AsBsonArray, rule => rule == prior);
            await ApplyV8(db); Assert.Equal(combined, (await Definition(db, "personEvidenceSources"))["options"]["validator"]);
            Assert.Contains(await Indexes(db, "personEvidenceSources"), index => index["name"] == "reviewed_source_key");
            Assert.DoesNotContain(await Indexes(db, "personEvidenceSources"), index => index["name"] == "ux_key");
            foreach (var pair in new[] { ("personEvidenceSources", 11L), ("personRecords", 101L), ("personEquivalenceGroups", 41L), ("personEvidenceVersions", 31L) })
                Assert.Equal(pair.Item2, (await new IdAllocator(db).NextAsync(pair.Item1)).Value);
            Assert.False(await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", "personAccountBindings")).AnyAsync());
            var ddl = commands.Where(command => command.Contains("collMod") || command.Contains("create") || command.Contains("createIndexes")).ToArray();
            Assert.NotEmpty(ddl); Assert.All(ddl, command => Assert.Equal("majority", command["writeConcern"]["w"]));
            var forbidden = Graph()["personEvidenceSources"]; forbidden["_id"] = 11L; forbidden["key"] = "forbidden";
            Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>("personEvidenceSources").InsertOneAsync(forbidden))).WriteError.Code);
        });
    }

    private static Dictionary<string, BsonDocument> Graph()
    {
        BsonDocument Row(long id, bool mutable = true) { var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 } }; if (mutable) { row.Add("version", 1L); row.Add("fence", 0L); } return row; }
        void Approval(BsonDocument row) { row.Add("requestedBy", 70L); row.Add("approvedBy", 80L); row.Add("approvalId", 90L); row.Add("approvalVersion", 1L); row.Add("reviewedAt", Date); }
        void Lifetime(BsonDocument row) { row.Add("effectiveAt", Date); row.Add("expiresAt", BsonNull.Value); }
        var source = Row(10); source.Add("key", "reviewed-roster"); source.Add("status", "approved"); source.Add("adapterId", "fixture-v1"); source.Add("policyReference", "reviewed-fixture-policy"); Approval(source); Lifetime(source);
        var person = Row(20); person.Add("sourceId", 10L); person.Add("sourceSubjectKey", "opaque-subject"); person.Add("currentEvidenceId", 30L); person.Add("currentEquivalenceId", 40L); person.Add("status", "active");
        var binding = Row(50); binding.Add("personId", 20L); binding.Add("evidenceId", 30L); binding.Add("verification", "verified"); binding.Add("status", "active"); Approval(binding); Lifetime(binding); binding.Add("reason", "Independent reviewed fixture");
        var group = Row(40); group.Add("canonicalPersonId", 20L); group.Add("memberPersonIds", new BsonArray { 20L }); group.Add("memberUserIds", new BsonArray { 50L }); group.Add("status", "active"); Approval(group);
        var evidence = Row(30, false); evidence.Add("personId", 20L); evidence.Add("evidenceVersion", 1L); evidence.Add("sourceId", 10L); evidence.Add("sourceVersion", 1L); evidence.Add("sourceSubjectKey", "opaque-subject"); evidence.Add("sourceReference", "private-fixture-reference"); evidence.Add("sourceDigest", new string('a', 64)); evidence.Add("adapterId", "fixture-v1"); Approval(evidence); evidence.Add("createdAt", Date); evidence.Add("reason", "Independent reviewed fixture"); Lifetime(evidence);
        return new() { ["personEvidenceSources"] = source, ["personRecords"] = person, ["personAccountBindings"] = binding, ["personEquivalenceGroups"] = group, ["personEvidenceVersions"] = evidence };
    }
    private static async Task Seed(IMongoDatabase db, Dictionary<string, BsonDocument> graph)
    {
        foreach (var pair in graph) await db.GetCollection<BsonDocument>(pair.Key).InsertOneAsync(pair.Value);
        foreach (var id in new[] { 50L, 70L, 80L }) await db.GetCollection<BsonDocument>("identities").InsertOneAsync(new BsonDocument
            { { "_id", id }, { "schemaVersion", 1 }, { "issuer", "https://fixture.example/" }, { "subject", "subject-" + id }, { "status", "active" }, { "grantVersion", 1L }, { "fence", 0L } });
    }
    private async Task InDatabase(Func<IMongoDatabase, Task> run) { var name = "ut_person_migration_" + Guid.NewGuid().ToString("N"); try { await run(fixture.Client.GetDatabase(name)); } finally { await fixture.Client.DropDatabaseAsync(name); } }
    private async Task ApplyV7(IMongoDatabase db)
    {
        IMigrationStep[] steps = [new FoundationMigration(db), new AuthorizationMigration(db), new AuthorizationLifecycleMigration(db), new IdentityMigration(db), new ProviderLifecycleMigration(db), new CatalogVisibilityMigration(db), new HistoricalReadMigration(db)];
        var gate = new MigrationGate(db, clock, 8);
        foreach (var step in steps) { var lease = await gate.AcquireAsync(step.Version, CancellationToken.None); await step.ApplyAsync(lease!, CancellationToken.None); await lease!.CompleteAsync(CancellationToken.None); }
    }
    private async Task ApplyV8(IMongoDatabase db, CancellationToken token = default)
    {
        var lease = await new MigrationGate(db, clock, 8).AcquireAsync(8, token); if (lease is null) return;
        try { await new PersonAuthorityMigration(db).ApplyAsync(lease, token); await lease.CompleteAsync(token); }
        catch { await lease.ReleaseAsync(); throw; }
    }
    private static async Task Invalid(IMongoDatabase db, string name, BsonDocument row) { Assert.False(PersonAuthoritySchemas.IsValid(name, row)); Assert.Equal(121, (await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>(name).InsertOneAsync(row))).WriteError.Code); }
    private static Task<List<BsonDocument>> History(IMongoDatabase db) => db.GetCollection<BsonDocument>("schemaMigrations").Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync();
    private static async Task<List<BsonDocument>> Definitions(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonDocument> Definition(IMongoDatabase db, string name) => await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
    private static async Task<List<BsonDocument>> Indexes(IMongoDatabase db, string name) => await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync();
}
