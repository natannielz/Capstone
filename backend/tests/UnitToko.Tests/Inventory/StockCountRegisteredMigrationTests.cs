using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Inventory;

// These integration tests are intentionally outside the accepted 12-file component manifest.
// They remain RED until the deployment registry contains the real v14 step after v13.
[Collection("Mongo")]
public sealed class StockCountRegisteredMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Registered_v14_follows_real_v13_and_preserves_ledger_and_evidence_constraints()
    {
        var database = NewDatabase();
        var clock = new RegistrationClock();
        try
        {
            await ApplyV13Async(database, clock);
            var earlierHistory = await HistoryAsync(database);
            Assert.Equal(13, earlierHistory.Count);
            Assert.All(earlierHistory, row => Assert.Equal("applied", row["state"].AsString));
            var sharedBefore = await SharedStateAsync(database);
            await AssertEvidenceConstraintsAsync(database);

            Assert.True(CollectionMigrations.CurrentVersion >= 14);
            var registered = new CollectionMigrations(database, clock);
            Assert.False(await registered.IsCurrentAsync());
            await registered.ApplyAsync();
            Assert.True(await registered.IsCurrentAsync());
            var completed = await HistoryAsync(database);
            Assert.Equal(Enumerable.Range(1, CollectionMigrations.CurrentVersion), completed.Select(row => row["_id"].AsInt32));
            Assert.Equal(earlierHistory, new BsonArray(completed.Take(13)));
            Assert.Equal("applied", completed[13]["state"].AsString);
            Assert.True(completed[13]["appliedAt"].IsBsonDateTime);
            Assert.False(completed[13].AsBsonDocument.Contains("owner"));
            await AssertSharedPreservedAsync(database, sharedBefore);
            await AssertEvidenceConstraintsAsync(database);
            await Assert.ThrowsAsync<MongoWriteException>(() => database.GetCollection<BsonDocument>(StockCountBson.Collection)
                .InsertOneAsync(new BsonDocument { { "_id", 1L }, { "schemaVersion", 1 } }));
            Assert.Equal(0, await database.GetCollection<BsonDocument>(StockCountBson.Collection).CountDocumentsAsync(new BsonDocument()));

            await registered.ApplyAsync();
            Assert.Equal(completed, await HistoryAsync(database));
        }
        finally
        {
            await fixture.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task Registered_v14_resumes_actual_partial_ddl_under_the_real_migration_gate()
    {
        var database = NewDatabase();
        var clock = new RegistrationClock();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await ApplyV13Async(database, clock);
            var earlierHistory = await HistoryAsync(database);
            var sharedBefore = await SharedStateAsync(database);
            var selected = 0;
            var interrupted = 0;
            var settings = fixture.Client.Settings.Clone();
            settings.ApplicationName = "StockCountRegisteredMigration-" + Guid.NewGuid().ToString("N");
            settings.ClusterConfigurator = builder =>
            {
                builder.Subscribe<CommandStartedEvent>(command =>
                {
                    if (command.CommandName == "create" && command.Command.GetValue("create", BsonNull.Value) == StockCountBson.Collection)
                        selected = command.RequestId;
                });
                builder.Subscribe<CommandSucceededEvent>(command =>
                {
                    if (command.CommandName == "create" && command.RequestId == selected &&
                        Interlocked.CompareExchange(ref interrupted, 1, 0) == 0)
                        cancellation.Cancel();
                });
            };
            var interruptedDatabase = new MongoClient(settings).GetDatabase(database.DatabaseNamespace.DatabaseName);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new CollectionMigrations(interruptedDatabase, clock).ApplyAsync(cancellation.Token));
            Assert.Equal(1, interrupted);
            var applying = await HistoryAsync(database);
            Assert.Equal(earlierHistory, new BsonArray(applying.Take(13)));
            Assert.Equal(14, applying.Count);
            Assert.Equal("applying", applying[13]["state"].AsString);
            Assert.False(applying[13].AsBsonDocument.Contains("appliedAt"));
            Assert.False(await new MigrationGate(database, clock, 14).IsCurrentAsync(default));
            var partialIndexes = await (await database.GetCollection<BsonDocument>(StockCountBson.Collection).Indexes.ListAsync()).ToListAsync();
            Assert.Contains(partialIndexes, row => row["name"] == "_id_");
            Assert.DoesNotContain(partialIndexes, row => row["name"] == "ux_stock_count_request");

            // Model elapsed time, without inserting/deleting/rewriting a successful history row.
            clock.UtcNow += TimeSpan.FromMinutes(11);
            var registered = new CollectionMigrations(database, clock);
            await registered.ApplyAsync();
            Assert.True(await registered.IsCurrentAsync());
            var completed = await HistoryAsync(database);
            Assert.Equal(earlierHistory, new BsonArray(completed.Take(13)));
            Assert.Equal(applying[13]["startedAt"], completed[13]["startedAt"]);
            Assert.Equal("applied", completed[13]["state"].AsString);
            var indexes = await (await database.GetCollection<BsonDocument>(StockCountBson.Collection).Indexes.ListAsync()).ToListAsync();
            Assert.Contains(indexes, row => row["name"] == "ux_stock_count_request" && row["unique"] == true);
            Assert.Contains(indexes, row => row["name"] == "ix_stock_count_location");
            await AssertSharedPreservedAsync(database, sharedBefore);
            await AssertEvidenceConstraintsAsync(database);
            await registered.ApplyAsync();
            Assert.Equal(completed, await HistoryAsync(database));
        }
        finally
        {
            await fixture.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    [Fact]
    public async Task Registered_v14_refuses_a_real_competing_lease_before_any_target_ddl()
    {
        var database = NewDatabase();
        var clock = new RegistrationClock();
        MigrationLease? lease = null;
        try
        {
            await ApplyV13Async(database, clock);
            lease = await new MigrationGate(database, clock, 14).AcquireAsync(14, default);
            Assert.NotNull(lease);
            var before = await HistoryAsync(database);
            var sharedBefore = await SharedStateAsync(database);
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() =>
                new CollectionMigrations(database, clock).ApplyAsync());
            Assert.Equal("migration_in_progress", error.Code);
            Assert.Equal(before, await HistoryAsync(database));
            Assert.Equal(sharedBefore, await SharedStateAsync(database));
            Assert.False(await (await database.ListCollectionNamesAsync(new ListCollectionNamesOptions
            {
                Filter = new BsonDocument("name", StockCountBson.Collection)
            })).AnyAsync());
        }
        finally
        {
            if (lease is not null)
                await lease.ReleaseAsync();
            await fixture.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName);
        }
    }

    private IMongoDatabase NewDatabase() => fixture.Client.GetDatabase("ut_count_registered_" + Guid.NewGuid().ToString("N"));

    private static async Task ApplyV13Async(IMongoDatabase database, IClock clock)
    {
        var durable = database.WithWriteConcern(WriteConcern.WMajority).WithReadPreference(ReadPreference.Primary);
        IMigrationStep[] steps =
        [
            new FoundationMigration(durable),
            new AuthorizationMigration(durable),
            new AuthorizationLifecycleMigration(durable),
            new IdentityMigration(durable),
            new ProviderLifecycleMigration(durable),
            new CatalogVisibilityMigration(durable),
            new HistoricalReadMigration(durable),
            new PersonAuthorityMigration(durable),
            new PersonGovernanceMigration(durable),
            new PersonReviewPreviewMigration(durable),
            new CatalogMigration(durable),
            new AccountingMigration(durable),
            new EvidenceMigration(durable)
        ];
        var gate = new MigrationGate(database, clock, 13);
        foreach (var step in steps)
        {
            var lease = await gate.AcquireAsync(step.Version, default);
            Assert.NotNull(lease);
            try
            {
                await step.ApplyAsync(lease, default);
                await lease.CompleteAsync(default);
            }
            catch
            {
                await lease.ReleaseAsync();
                throw;
            }
        }
        Assert.True(await gate.IsCurrentAsync(default));
    }

    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) =>
        new(await database.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());

    private static async Task<BsonDocument> SharedStateAsync(IMongoDatabase database)
    {
        var result = new BsonDocument();
        foreach (var name in new[] { "auditEvents", "idempotencyRecords" })
        {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions
            {
                Filter = new BsonDocument("name", name)
            })).SingleAsync();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync();
            result[name] = new BsonDocument
            {
                { "options", definition["options"] },
                { "indexes", new BsonArray(indexes.OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) }
            };
        }
        return result;
    }

    private static async Task AssertSharedPreservedAsync(IMongoDatabase database, BsonDocument before)
    {
        var after = await SharedStateAsync(database);
        foreach (var name in before.Names)
        {
            Assert.True(Contains(after[name]["options"]["validator"].AsBsonDocument, before[name]["options"]["validator"].AsBsonDocument));
            foreach (var index in before[name]["indexes"].AsBsonArray)
                Assert.Contains(after[name]["indexes"].AsBsonArray, candidate => CatalogSchemas.SemanticEquals(index, candidate));
            Assert.Equal(before[name]["options"]["validationLevel"], after[name]["options"]["validationLevel"]);
            Assert.Equal(before[name]["options"]["validationAction"], after[name]["options"]["validationAction"]);
        }
    }

    private static bool Contains(BsonDocument actual, BsonDocument expected) => CatalogSchemas.SemanticEquals(actual, expected) ||
        actual.TryGetValue("$and", out var conjunction) && conjunction.IsBsonArray &&
        conjunction.AsBsonArray.Any(value => value.IsBsonDocument && Contains(value.AsBsonDocument, expected));

    private static async Task AssertEvidenceConstraintsAsync(IMongoDatabase database)
    {
        var auditError = await Assert.ThrowsAsync<MongoWriteException>(() => database.GetCollection<BsonDocument>("auditEvents")
            .InsertOneAsync(new BsonDocument { { "_id", 700001L }, { "schemaVersion", 1 }, { "kind", "evidence.reserve" } }));
        Assert.Equal(121, auditError.WriteError.Code);
        var receiptError = await Assert.ThrowsAsync<MongoWriteException>(() => database.GetCollection<BsonDocument>("idempotencyRecords")
            .InsertOneAsync(new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "actorId", 700001L },
                { "operation", EvidenceSchemas.ReserveOperation }, { "key", "registered-migration" },
                { "evidenceAdmission", new BsonDocument() }
            }));
        Assert.Equal(121, receiptError.WriteError.Code);
    }

    private sealed class RegistrationClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }
}
