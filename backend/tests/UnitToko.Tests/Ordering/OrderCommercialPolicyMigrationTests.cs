using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using B = UnitToko.Platform.Ordering.OrderCommercialPolicyBson;

namespace UnitToko.Tests.Ordering;

/// <summary>Fresh actual registered105→106 fixtures, including genuine prior catalog
/// producers. No future owner/history is removed; this is not pinned old-runtime acceptance.</summary>
[Collection("Mongo")]
public sealed class OrderCommercialPolicyMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Actual105_records_and_stronger_partial_guard_survive106_with_UUID_options_indexes_counters_history_and_replay()
    {
        await using var data = await Fresh105CatalogAsync("COMMERCIAL-MIGRATION-12");
        await ProspectiveAsync(data.Database, B.Proposals);
        var original = await AccountContactStorage.DefinitionAsync(data.Database, B.Proposals, default); Assert.NotNull(original);
        var stronger = new BsonDocument("$and", new BsonArray { original["options"]["validator"].DeepClone(), new BsonDocument("_id", new BsonDocument("$gte", 1L)) });
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", B.Proposals }, { "validator", stronger } });
        await data.Rows(B.Proposals).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("reason", 1),
            new CreateIndexOptions { Name = "operator_commercial_reason_lookup" }));
        await data.Rows("idCounters").InsertOneAsync(new BsonDocument { { "_id", B.Proposals }, { "value", 0L } });
        var before = await SnapshotAsync(data.Database); var history = await HistoryAsync(data.Database);
        Assert.NotEmpty(before["skus"]["rows"].AsBsonArray); Assert.NotEmpty(before["auditEvents"]["rows"].AsBsonArray);
        Assert.NotEmpty(before["idempotencyRecords"]["rows"].AsBsonArray); Assert.Equal(105, history.Count);
        await StoreTestData.ApplyRegisteredThroughAsync(data.Database, data.Clock, 106);
        var after = await SnapshotAsync(data.Database);
        foreach (var name in before.Names) {
            if (name == B.Proposals) {
                Assert.Equal(before[name]["definition"], after[name]["definition"]); Assert.Equal(before[name]["rows"], after[name]["rows"]);
                foreach (var index in before[name]["indexes"].AsBsonArray) Assert.Contains(index, after[name]["indexes"].AsBsonArray);
            } else Assert.Equal(before[name], after[name]);
        }
        foreach (var name in OrderCommercialPolicySchemas.Collections) {
            Assert.Empty(after[name]["rows"].AsBsonArray);
            foreach (var key in OrderCommercialPolicySchemas.Keys(name)) Assert.Contains(after[name]["indexes"].AsBsonArray,
                index => index["key"] == key && index.AsBsonDocument.GetValue("unique", false).AsBoolean);
        }
        Assert.Equal(stronger, after[B.Proposals]["definition"]["options"]["validator"]);
        var applied = await HistoryAsync(data.Database); Assert.Equal(106, applied.Count);
        Assert.Equal(history, new BsonArray(applied.Take(105))); Assert.Equal("applied", applied[105]["state"]);
        await StoreTestData.ApplyRegisteredThroughAsync(data.Database, data.Clock, 106);
        Assert.Equal(after, await SnapshotAsync(data.Database)); Assert.Equal(applied, await HistoryAsync(data.Database));
        await NoActivationEffectsAsync(data.Database);
    }

    [Theory]
    [InlineData("counter")] [InlineData("effect")] [InlineData("owner-row")]
    [InlineData("ttl")] [InlineData("partial")] [InlineData("unknown-unique")]
    public async Task Prospective_owner_counter_effect_or_unsafe_guard_refuses_before_any106_DDL_and_preserves_the_fault(string fault)
    {
        await using var data = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 105);
        if (fault == "counter") await data.Database.GetCollection<BsonDocument>("idCounters").InsertOneAsync(new BsonDocument { { "_id", B.Decisions }, { "value", 1L } });
        else if (fault == "effect") await data.Database.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument {
            { "_id", ObjectId.GenerateNewId() }, { "kind", B.Prefix + "approve" }
        }, new InsertOneOptions { BypassDocumentValidation = true });
        else {
            await ProspectiveAsync(data.Database, B.Proposals);
            var rows = data.Database.GetCollection<BsonDocument>(B.Proposals);
            if (fault == "owner-row") await rows.InsertOneAsync(new BsonDocument("_id", 1L), new InsertOneOptions { BypassDocumentValidation = true });
            else await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("reason", 1), new CreateIndexOptions<BsonDocument> {
                Name = "unsafe_commercial_operator_guard", Unique = fault == "unknown-unique",
                ExpireAfter = fault == "ttl" ? TimeSpan.FromDays(1) : null,
                PartialFilterExpression = fault == "partial" ? new BsonDocument("reason", new BsonDocument("$exists", true)) : null
            }));
        }
        var before = await SnapshotAsync(data.Database); var history = await HistoryAsync(data.Database); var ddl = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "Commercial106Preflight-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command => {
            if (command.DatabaseNamespace.DatabaseName == data.Database.DatabaseNamespace.DatabaseName && (command.CommandName is "create" or "collMod" or "createIndexes") &&
                OrderCommercialPolicySchemas.Collections.Contains(command.Command.GetValue(command.CommandName, "").AsString)) Interlocked.Increment(ref ddl);
        });
        using var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoreTestData.ApplyRegisteredThroughAsync(database, data.Clock, 106));
        Assert.Equal(0, ddl); Assert.Equal(before, await SnapshotAsync(data.Database));
        var failed = await HistoryAsync(data.Database); Assert.Equal(106, failed.Count);
        Assert.Equal(history, new BsonArray(failed.Take(105))); Assert.Equal("applying", failed[105]["state"]);
        foreach (var name in OrderCommercialPolicySchemas.Collections.Where(name => name != B.Proposals || fault is "counter" or "effect"))
            Assert.Null(await AccountContactStorage.DefinitionAsync(data.Database, name, default));
    }

    [Fact]
    public async Task Interrupted_first_actual106_create_resumes_without_reseeding_or_replacing_its_UUID_or_history()
    {
        await using var data = await Fresh105CatalogAsync("COMMERCIAL-INTERRUPTED-12");
        var before = await SnapshotAsync(data.Database); var history = await HistoryAsync(data.Database);
        using var cancellation = new CancellationTokenSource(); var request = 0; var observed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "Commercial106Interrupted-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (request == 0 && command.DatabaseNamespace.DatabaseName == data.Database.DatabaseNamespace.DatabaseName && command.CommandName == "create" &&
                    command.Command.GetValue("create", BsonNull.Value) == B.Heads) request = command.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (request != 0 && command.RequestId == request && Interlocked.CompareExchange(ref observed, 1, 0) == 0) cancellation.Cancel();
            });
        };
        using var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StoreTestData.ApplyRegisteredThroughAsync(database, data.Clock, 106, cancellation.Token));
        Assert.Equal(1, observed); var partial = await AccountContactStorage.DefinitionAsync(data.Database, B.Heads, default); Assert.NotNull(partial);
        var interruptedHistory = await HistoryAsync(data.Database); Assert.Equal(history, new BsonArray(interruptedHistory.Take(105))); Assert.Equal("applying", interruptedHistory[105]["state"]);
        var interrupted = await SnapshotAsync(data.Database); foreach (var name in before.Names) Assert.Equal(before[name], interrupted[name]);
        await StoreTestData.ApplyRegisteredThroughAsync(data.Database, data.Clock, 106);
        var after = await SnapshotAsync(data.Database); foreach (var name in before.Names) Assert.Equal(before[name], after[name]);
        Assert.Equal(partial, await AccountContactStorage.DefinitionAsync(data.Database, B.Heads, default));
        foreach (var name in OrderCommercialPolicySchemas.Collections) Assert.Empty(after[name]["rows"].AsBsonArray);
        var applied = await HistoryAsync(data.Database); Assert.Equal(history, new BsonArray(applied.Take(105))); Assert.Equal("applied", applied[105]["state"]);
        await StoreTestData.ApplyRegisteredThroughAsync(data.Database, data.Clock, 106);
        Assert.Equal(after, await SnapshotAsync(data.Database)); Assert.Equal(applied, await HistoryAsync(data.Database));
        await NoActivationEffectsAsync(data.Database);
    }

    private async Task<CatalogAuthoringTestData> Fresh105CatalogAsync(string sku)
    {
        var source = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 105);
        source.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        try {
            var data = await CatalogAuthoringTestData.AttachAsync(source, fixture.Client); await data.SkuAsync(12, sku, "COM-MIGRATION");
            var history = await HistoryAsync(data.Database); Assert.Equal(105, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"]));
            foreach (var name in OrderCommercialPolicySchemas.Collections) {
                Assert.Null(await AccountContactStorage.DefinitionAsync(data.Database, name, default));
                Assert.Empty(await data.Rows("idCounters").Find(new BsonDocument("_id", name)).ToListAsync());
            }
            return data;
        } catch { await source.DisposeAsync(); throw; }
    }
    private static Task ProspectiveAsync(IMongoDatabase database, string name) => database.CreateCollectionAsync(name, new CreateCollectionOptions<BsonDocument> {
        Validator = OrderCommercialPolicySchemas.Validator(name), ValidationAction = DocumentValidationAction.Error, ValidationLevel = DocumentValidationLevel.Strict });
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase database) => new(await database.GetCollection<BsonDocument>("schemaMigrations")
        .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase database)
    {
        var result = new BsonDocument(); var definitions = await (await database.ListCollectionsAsync()).ToListAsync();
        foreach (var definition in definitions.Where(row => row.GetValue("type", "") == "collection" && row["name"] != "schemaMigrations").OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; var rows = database.GetCollection<BsonDocument>(name);
            result[name] = new BsonDocument { { "definition", definition }, { "indexes", new BsonArray(await (await rows.Indexes.ListAsync()).ToListAsync()) },
                { "rows", new BsonArray(await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } };
        }
        return result;
    }
    private static async Task NoActivationEffectsAsync(IMongoDatabase database)
    {
        foreach (var name in new[] { "orders", "invoices", "stockBalances", "movements", "journalEntries", "creditVersions" })
            Assert.False(await database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
    }
}
