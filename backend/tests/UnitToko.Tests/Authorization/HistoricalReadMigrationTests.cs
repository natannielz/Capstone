using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Authorization;

[Collection("Mongo")]
public sealed class HistoricalReadMigrationTests(MongoFixture fixture)
{
    private const string Events = "businessReadEvents";
    private readonly IClock clock = new SystemClock();

    [Fact]
    public async Task Fresh_v7_and_repeat_produce_only_empty_strict_audit_collection_without_business_seeds()
    {
        await InDatabase(async db =>
        {
            await new CollectionMigrations(db, clock).ApplyAsync();
            Assert.True(CollectionMigrations.CurrentVersion >= 7);
            var before = await History(db);
            Assert.Equal(Enumerable.Range(1, CollectionMigrations.CurrentVersion), before.Select(row => row["_id"].AsInt32));
            var definition = await Definition(db);
            Assert.Equal("strict", definition["options"]["validationLevel"].AsString);
            Assert.Equal("error", definition["options"]["validationAction"].AsString);
            var indexes = await Indexes(db);
            Assert.Equal(3, indexes.Count);
            Assert.DoesNotContain(indexes, index => index.Contains("expireAfterSeconds"));
            foreach (var collection in new[] { Events, "authorizationGrants", "memberships", "orders", "invoices", "shipments" })
                Assert.Equal(0, await db.GetCollection<BsonDocument>(collection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            await new CollectionMigrations(db, clock).ApplyAsync();
            Assert.Equal(before, await History(db));
            Assert.Equal(definition, await Definition(db));
            Assert.Equal(indexes, await Indexes(db));
        });
    }

    [Fact]
    public async Task V6_upgrade_preserves_all_prior_definitions_indexes_history_data_and_counter_highwater()
    {
        await InDatabase(async db =>
        {
            await ApplyV6(db);
            var history = await History(db);
            var collections = await (await db.ListCollectionsAsync()).ToListAsync();
            var indexes = new Dictionary<string, List<BsonDocument>>();
            foreach (var collection in collections) indexes[collection["name"].AsString] = await (await db.GetCollection<BsonDocument>(collection["name"].AsString).Indexes.ListAsync()).ToListAsync();
            var legacy = new BsonDocument { { "_id", 19L }, { "schemaVersion", 1 }, { "legacyBusinessMarker", "untouched" } };
            await db.GetCollection<BsonDocument>("orders").InsertOneAsync(legacy);
            await db.GetCollection<BsonDocument>(Events).InsertOneAsync(Event(900));
            await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", Events }, { "value", 500L } });
            await new CollectionMigrations(db, clock).ApplyAsync();
            Assert.Equal(history, (await History(db)).Take(6));
            foreach (var collection in collections)
            {
                var name = collection["name"].AsString;
                Assert.Equal(collection, await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync());
                Assert.Equal(indexes[name], await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync());
            }
            Assert.Equal(legacy.ToBson(), (await db.GetCollection<BsonDocument>("orders").Find(new BsonDocument("_id", 19L)).SingleAsync()).ToBson());
            Assert.Equal(901, (await new IdAllocator(db).NextAsync(Events)).Value);
            Assert.True(await new CollectionMigrations(db, clock).IsCurrentAsync());
        });
    }

    [Fact]
    public async Task Strict_event_validator_accepts_supported_pairs_and_rejects_wrong_types_missing_fields_unbounded_metadata_and_payloads()
    {
        await InDatabase(async db =>
        {
            await new CollectionMigrations(db, clock).ApplyAsync();
            var zero = Event(1); zero["actorGrantVersion"] = 0L;
            await db.GetCollection<BsonDocument>(Events).InsertOneAsync(zero);
            var report = Event(2); report["permission"] = "report.read"; report["purpose"] = "reporting";
            await db.GetCollection<BsonDocument>(Events).InsertOneAsync(report);
            var division = Event(3); division["targetScope"]["kind"] = "buyingDivision"; division["targetScope"]["divisionId"] = 30L;
            await db.GetCollection<BsonDocument>(Events).InsertOneAsync(division);
            foreach (var field in Event(100).Names)
            {
                var row = Event(100); row.Remove(field); await Invalid(db, row);
            }
            foreach (var mutate in new Action<BsonDocument>[]
            {
                row => row["_id"] = 100, row => row["_id"] = 0L, row => row["schemaVersion"] = 1L,
                row => row["actorId"] = new BsonArray { 1L }, row => row["actorGrantVersion"] = -1L,
                row => row["source"] = "identities", row => row["permission"] = "order.write", row => row["purpose"] = "reporting",
                row => row["operation"] = "download", row => row["grantIds"] = new BsonArray(),
                row => row["grantIds"] = new BsonArray { 1L, 1L }, row => row["grantIds"] = new BsonArray { 1 },
                row => row["grantIds"] = new BsonArray(Enumerable.Range(1, 257).Select(value => new BsonInt64(value))),
                row => row["membershipIds"] = new BsonArray { 0L }, row => row["resultCount"] = -1L,
                row => row["resultCount"] = 1, row => row["correlationId"] = "", row => row["correlationId"] = new string('x', 129),
                row => row["correlationId"] = "bad\nvalue", row => row["correlationId"] = "bad\u0085value", row => row["createdAt"] = "now",
                row => row["selection"]["search"] = "private search", row => row["financialAmount"] = "secret",
                row => row["targetScope"]["branchId"] = 20, row => row["targetScope"]["organizationId"] = new BsonArray { 10L },
                row => row["targetScope"]["divisionId"] = 30L, row => row["targetScope"]["kind"] = "buyingOrganization",
                row => row["targetScope"]["shopId"] = 50L, row => row["targetScope"]["extra"] = true,
                row => row["selection"]["resourceId"] = 3L
            }) { var row = Event(100); mutate(row); await Invalid(db, row); }
            var query = Event(4); query["operation"] = "query"; query["selection"] = new BsonDocument { { "offset", 0 }, { "limit", 25 } }; query["resultCount"] = 2L;
            await db.GetCollection<BsonDocument>(Events).InsertOneAsync(query);
            query["_id"] = 100L; query["resultCount"] = 101L; await Invalid(db, query);
            var detail = Event(5); detail["operation"] = "detail"; detail["selection"] = new BsonDocument("resourceId", 12L); detail["resultCount"] = 1L;
            await db.GetCollection<BsonDocument>(Events).InsertOneAsync(detail);
            detail["_id"] = 100L; detail["resultCount"] = 2L; await Invalid(db, detail);
            var reference = Event(6); reference["operation"] = "reference"; reference["selection"] = new BsonDocument { { "resourceId", 12L }, { "referencePurpose", "evidence" } }; reference["resultCount"] = 1L;
            await db.GetCollection<BsonDocument>(Events).InsertOneAsync(reference);
            reference["_id"] = 100L; reference["selection"]["referencePurpose"] = "url"; await Invalid(db, reference);
        });
    }

    [Theory]
    [InlineData("record")]
    [InlineData("counter")]
    [InlineData("ttl")]
    [InlineData("name")]
    [InlineData("unique")]
    [InlineData("sparse")]
    [InlineData("partial")]
    [InlineData("hidden")]
    [InlineData("collation")]
    public async Task Preflight_failure_leaves_existing_audit_rules_indexes_counter_and_v6_history_unchanged(string kind)
    {
        await InDatabase(async db =>
        {
            await ApplyV6(db);
            var row = Event(1); if (kind == "record") row["actorId"] = 1;
            await db.GetCollection<BsonDocument>(Events).InsertOneAsync(row);
            var prior = new BsonDocument("actorId", new BsonDocument("$gte", 1L));
            await SetValidator(db, prior);
            await db.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", Events }, { "value", kind == "counter" ? -1L : 0L } });
            if (kind is not ("record" or "counter"))
            {
                var key = kind is "ttl" or "name" ? new BsonDocument("createdAt", 1) : ActorIndex();
                var options = new CreateIndexOptions<BsonDocument> { Name = kind == "ttl" ? "unsafe_retention" : "ix_actorId_createdAt" };
                if (kind == "ttl") options.ExpireAfter = TimeSpan.FromDays(30);
                if (kind == "unique") options.Unique = true;
                if (kind == "sparse") options.Sparse = true;
                if (kind == "partial") options.PartialFilterExpression = new BsonDocument("actorId", 1L);
                if (kind == "hidden") options.Hidden = true;
                if (kind == "collation") options.Collation = new Collation("en");
                await db.GetCollection<BsonDocument>(Events).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key, options));
            }
            var definition = await Definition(db); var indexes = await Indexes(db); var history = await History(db);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync());
            Assert.Contains("businessReadEvents", failure.Message); Assert.Contains("reconcile", failure.Message);
            Assert.Equal(definition, await Definition(db)); Assert.Equal(indexes, await Indexes(db));
            Assert.Equal(history, (await History(db)).Take(6));
            Assert.Equal(row.ToBson(), (await db.GetCollection<BsonDocument>(Events).Find(new BsonDocument("_id", 1L)).SingleAsync()).ToBson());
            Assert.Equal(kind == "counter" ? -1L : 0L, (await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", Events)).SingleAsync())["value"].AsInt64);
            Assert.False(await new CollectionMigrations(db, clock).IsCurrentAsync());
        });
    }

    [Theory]
    [InlineData("capped")]
    [InlineData("retention")]
    [InlineData("timeseries")]
    [InlineData("collation")]
    [InlineData("view")]
    public async Task Collection_retention_and_other_incompatible_options_fail_before_mutation(string kind)
    {
        await InDatabase(async db =>
        {
            await ApplyV6(db);
            if (kind == "capped") await db.CreateCollectionAsync(Events, new CreateCollectionOptions { Capped = true, MaxSize = 4096 });
            if (kind == "collation") await db.CreateCollectionAsync(Events, new CreateCollectionOptions { Collation = new Collation("en") });
            if (kind == "retention") await db.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "create", Events }, { "clusteredIndex", new BsonDocument { { "key", new BsonDocument("_id", 1) }, { "unique", true } } }, { "expireAfterSeconds", 86400L }
            });
            if (kind == "timeseries") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", Events }, { "timeseries", new BsonDocument("timeField", "createdAt") } });
            if (kind == "view") await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", Events }, { "viewOn", "fixtureSource" }, { "pipeline", new BsonArray() } });
            var definition = await Definition(db); var history = await History(db);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync());
            Assert.Contains("options", failure.Message);
            Assert.Equal(definition, await Definition(db)); Assert.Equal(history, (await History(db)).Take(6));
            Assert.False(await new CollectionMigrations(db, clock).IsCurrentAsync());
        });
    }

    [Fact]
    public async Task Interrupted_v7_preserves_stronger_validator_alternate_index_and_resumes_with_majority_ddl()
    {
        await InDatabase(async db =>
        {
            await ApplyV6(db);
            await db.GetCollection<BsonDocument>(Events).InsertOneAsync(Event(900));
            var prior = new BsonDocument("actorId", new BsonDocument("$lte", 5L));
            await SetValidator(db, prior);
            await db.GetCollection<BsonDocument>(Events).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(ActorIndex(), new CreateIndexOptions { Name = "reviewed_actor_lookup" }));
            var before = await History(db);
            using var stop = new CancellationTokenSource();
            var commands = new ConcurrentBag<BsonDocument>();
            var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:27028/?replicaSet=rs0&w=1");
            settings.ClusterConfigurator = builder =>
            {
                builder.Subscribe<CommandStartedEvent>(item => commands.Add(item.Command.DeepClone().AsBsonDocument));
                builder.Subscribe<CommandSucceededEvent>(item => { if (item.CommandName == "collMod") stop.Cancel(); });
            };
            var observed = new MongoClient(settings).GetDatabase(db.DatabaseNamespace.DatabaseName);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CollectionMigrations(observed, clock).ApplyAsync(stop.Token));
            Assert.Equal(before, (await History(db)).Take(6));
            var pending = await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument("_id", 7)).SingleAsync();
            Assert.Equal("applying", pending["state"].AsString);
            Assert.True(pending["leaseUntil"].ToUniversalTime() <= clock.UtcNow.UtcDateTime);
            var combined = (await Definition(db))["options"]["validator"];
            Assert.Contains(combined["$and"].AsBsonArray, item => item == prior);
            await new CollectionMigrations(observed, clock).ApplyAsync();
            Assert.Equal(combined, (await Definition(db))["options"]["validator"]);
            Assert.Equal(before, (await History(db)).Take(6));
            Assert.Equal(901, (await new IdAllocator(db).NextAsync(Events)).Value);
            var rejected = Event(902); rejected["actorId"] = 6L; await Invalid(db, rejected);
            Assert.Contains(await Indexes(db), index => index["name"] == "reviewed_actor_lookup");
            Assert.DoesNotContain(await Indexes(db), index => index["name"] == "ix_actorId_createdAt");
            var ddl = commands.Where(command => command.Contains("create") || command.Contains("collMod") || command.Contains("createIndexes")).ToArray();
            Assert.NotEmpty(ddl);
            Assert.All(ddl, command => { Assert.Equal("majority", command["writeConcern"]["w"].AsString); Assert.Contains(command.GetElement(0).Value.AsString, new[] { Events, "personEvidenceSources", "personRecords", "personAccountBindings", "personEquivalenceGroups", "personEvidenceVersions", "personGovernancePolicies", "personReviewProposals", "personReviewHistory" }); });
            Assert.True(await new CollectionMigrations(db, clock).IsCurrentAsync());
        });
    }

    private async Task InDatabase(Func<IMongoDatabase, Task> run)
    {
        var name = "ut_historical_migration_" + Guid.NewGuid().ToString("N");
        try { await run(fixture.Client.GetDatabase(name)); }
        finally { await fixture.Client.DropDatabaseAsync(name); }
    }
    private async Task ApplyV6(IMongoDatabase db)
    {
        var durable = db.WithWriteConcern(WriteConcern.WMajority);
        IMigrationStep[] steps = [new FoundationMigration(durable), new AuthorizationMigration(durable), new AuthorizationLifecycleMigration(durable),
            new IdentityMigration(durable), new ProviderLifecycleMigration(durable), new CatalogVisibilityMigration(durable)];
        var gate = new MigrationGate(db, clock, 6);
        foreach (var step in steps)
        {
            var lease = await gate.AcquireAsync(step.Version, CancellationToken.None);
            await step.ApplyAsync(lease!, CancellationToken.None); await lease!.CompleteAsync(CancellationToken.None);
        }
    }
    private static Task<List<BsonDocument>> History(IMongoDatabase db) => db.GetCollection<BsonDocument>("schemaMigrations").Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1)).ToListAsync();
    private static async Task<BsonDocument> Definition(IMongoDatabase db) => await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", Events) })).SingleAsync();
    private static async Task<List<BsonDocument>> Indexes(IMongoDatabase db) => await (await db.GetCollection<BsonDocument>(Events).Indexes.ListAsync()).ToListAsync();
    private static Task<BsonDocument> SetValidator(IMongoDatabase db, BsonDocument rules) => db.RunCommandAsync<BsonDocument>(new BsonDocument
        { { "collMod", Events }, { "validator", rules }, { "validationLevel", "strict" }, { "validationAction", "error" } });
    private static async Task Invalid(IMongoDatabase db, BsonDocument row) => Assert.Equal(121,
        (await Assert.ThrowsAsync<MongoWriteException>(() => db.GetCollection<BsonDocument>(Events).InsertOneAsync(row))).WriteError.Code);
    private static BsonDocument ActorIndex() => new() { { "actorId", 1 }, { "createdAt", 1 } };
    private BsonDocument Event(long id) => new()
    {
        { "_id", id }, { "schemaVersion", 1 }, { "actorId", 1L }, { "actorGrantVersion", 1L }, { "source", "orders" },
        { "permission", "history.read" }, { "purpose", "historical-review" }, { "operation", "count" },
        { "targetScope", new BsonDocument { { "kind", "buyingBranch" }, { "organizationId", 10L }, { "branchId", 20L } } },
        { "grantIds", new BsonArray { 1L } }, { "membershipIds", new BsonArray { 2L } }, { "selection", new BsonDocument() },
        { "resultCount", 5L }, { "correlationId", "migration-test" }, { "createdAt", clock.UtcNow.UtcDateTime }
    };
}
