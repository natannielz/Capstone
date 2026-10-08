using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Ordering;

// Actual registered104→105 installation. The deliberately malformed future
// records below are negative evidence only and are never repaired or adopted.
[Collection("Mongo")]
public sealed class ShipmentDispatchValueMigrationTests(MongoFixture fixture)
{
    private static async Task<BsonDocument> SnapshotAsync(IMongoDatabase db) {
        var state = new BsonDocument();
        foreach (var definition in (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString)) {
            var name = definition["name"].AsString; if (name == "schemaMigrations") continue;
            var rows = db.GetCollection<BsonDocument>(name);
            state.Add(name, new BsonDocument { { "definition", definition },
                { "indexes", new BsonArray(await (await rows.Indexes.ListAsync()).ToListAsync()) },
                { "rows", new BsonArray(await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } });
        }
        return state;
    }
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations")
        .Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    [Fact]
    public async Task Actual104_bytes_indexes_and_operator_restrictions_survive105_and_no_op() {
        await using var data = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 104);
        var guard = new BsonDocument("orderSequence", new BsonDocument("$lte", 1000L));
        var installed = (await AccountContactStorage.DefinitionAsync(data.Database, "shipments", default))!["options"]["validator"].AsBsonDocument;
        Assert.True(InventorySchemas.Contains(installed, FoundationMigration.BusinessValidator()));
        var stronger = new BsonDocument("$and", new BsonArray { installed.DeepClone(), guard });
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "shipments" }, { "validator", stronger } });
        var before = await SnapshotAsync(data.Database); var history = await HistoryAsync(data.Database);
        await StoreTestData.ApplyRegisteredThroughAsync(data.Database, data.Clock, 105);
        var after = await SnapshotAsync(data.Database);
        foreach (var name in before.Names) {
            if (name == "shipments") {
                Assert.Equal(before[name]["rows"], after[name]["rows"]); Assert.Equal(before[name]["indexes"], after[name]["indexes"]);
                var expected = ShipmentDispatchValueMigration.ExtendValidator(stronger);
                Assert.Equal(expected, after[name]["definition"]["options"]["validator"]);
                var foundation = FoundationMigration.BusinessValidator();
                foundation["$and"][0]["$jsonSchema"]["properties"]["schemaVersion"]["enum"] = new BsonArray { 1, 2 };
                Assert.True(InventorySchemas.Contains(expected, foundation));
                Assert.False(InventorySchemas.Contains(expected, FoundationMigration.BusinessValidator()));
                var normalized = after[name].DeepClone(); normalized["definition"]["options"]["validator"] = stronger;
                Assert.Equal(before[name], normalized);
            } else Assert.Equal(before[name], after[name]);
        }
        Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(104)));
        await StoreTestData.ApplyRegisteredThroughAsync(data.Database, data.Clock, 105);
        Assert.Equal(after, await SnapshotAsync(data.Database));
    }
    [Theory]
    [InlineData("field")] [InlineData("schema")] [InlineData("expression")] [InlineData("modified-foundation")]
    [InlineData("mixed-conjunct")] [InlineData("getfield")]
    public async Task An_unknown_operator_version_one_restriction_refuses105_before_DDL_without_rewriting_its_guard(string kind) {
        await using var data = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 104);
        var installed = (await AccountContactStorage.DefinitionAsync(data.Database, "shipments", default))!["options"]["validator"].AsBsonDocument;
        BsonDocument guard;
        if (kind == "field") guard = new("schemaVersion", 1);
        else if (kind == "schema") guard = new("$jsonSchema", new BsonDocument("properties", new BsonDocument("schemaVersion", new BsonDocument("enum", new BsonArray { 1 }))));
        else if (kind == "expression") guard = new("$expr", new BsonDocument("$eq", new BsonArray { "$schemaVersion", 1 }));
        else if (kind == "mixed-conjunct") guard = new() { { "$and", new BsonArray { new BsonDocument("schemaVersion", 1) } },
            { "orderSequence", new BsonDocument("$lte", 1000L) } };
        else if (kind == "getfield") guard = new("$expr", new BsonDocument("$eq", new BsonArray { new BsonDocument("$getField", "schemaVersion"), 1 }));
        else {
            guard = FoundationMigration.BusinessValidator();
            guard["$and"][0]["$jsonSchema"]["properties"]["unitPrice"]["maximum"] = new BsonDecimal128(100m);
        }
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "shipments" },
            { "validator", new BsonDocument("$and", new BsonArray { installed.DeepClone(), guard }) } });
        var before = await SnapshotAsync(data.Database); var history = await HistoryAsync(data.Database); var ddl = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "Dispatch105OperatorVersion-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command => {
            if (command.DatabaseNamespace.DatabaseName == data.Database.DatabaseNamespace.DatabaseName && command.CommandName is "create" or "collMod" or "createIndexes")
                Interlocked.Increment(ref ddl);
        });
        using var client = new MongoClient(settings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoreTestData.ApplyRegisteredThroughAsync(client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName), data.Clock, 105));
        Assert.Equal(0, ddl); Assert.Equal(before, await SnapshotAsync(data.Database));
        Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(104)));
    }
    [Theory]
    [InlineData("value")] [InlineData("journal")] [InlineData("effect")]
    [InlineData("ttl")] [InlineData("partial")] [InlineData("unique")]
    public async Task Prospective_value_journal_effect_or_unsafe_index_refuses_before105_DDL_and_preserves_evidence(string fault) {
        await using var data = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 104);
        if (fault is "value" or "journal") {
            var table = fault == "value" ? LotValueStore.Transitions : "journalEntries";
            await data.Database.GetCollection<BsonDocument>(table).InsertOneAsync(new BsonDocument { { "_id", 9999L },
                { "source", new BsonDocument { { "namespace", "fulfillment.shipment" }, { "documentId", 9999L }, { "eventKind", fault == "value" ? "consumeLot" : "dispatchCogs" }, { "occurrence", 1L } } }
            }, new InsertOneOptions { BypassDocumentValidation = true });
        } else if (fault == "effect") await data.Database.GetCollection<BsonDocument>("outboxEvents").InsertOneAsync(new BsonDocument {
            { "_id", ObjectId.GenerateNewId() }, { "kind", "fulfillment.shipment.cogs" }, { "sourceKey", "fulfillment.shipment:9999:dispatchCogs:1" }
        }, new InsertOneOptions { BypassDocumentValidation = true });
        else await data.Database.GetCollection<BsonDocument>("shipments").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("reason", 1),
            new CreateIndexOptions<BsonDocument> { Name = "unsafe_dispatch_operator", Unique = fault == "unique", ExpireAfter = fault == "ttl" ? TimeSpan.FromDays(1) : null,
                PartialFilterExpression = fault == "partial" ? new BsonDocument("reason", new BsonDocument("$exists", true)) : null }));
        var before = await SnapshotAsync(data.Database); var history = await HistoryAsync(data.Database); var ddl = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "Dispatch105Preflight-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(command => {
            if (command.DatabaseNamespace.DatabaseName == data.Database.DatabaseNamespace.DatabaseName && command.CommandName is "create" or "collMod" or "createIndexes")
                Interlocked.Increment(ref ddl);
        });
        using var client = new MongoClient(settings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoreTestData.ApplyRegisteredThroughAsync(client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName), data.Clock, 105));
        Assert.Equal(0, ddl); Assert.Equal(before, await SnapshotAsync(data.Database));
        Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(104)));
    }
    [Fact]
    public async Task An_interrupted105_validator_change_resumes_without_rewriting104_data_or_dropping_guards() {
        await using var data = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 104);
        var before = await SnapshotAsync(data.Database); var history = await HistoryAsync(data.Database);
        using var cancellation = new CancellationTokenSource(); var requests = new HashSet<int>(); var changed = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "Dispatch105Interrupt-" + Guid.NewGuid().ToString("N");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (command.DatabaseNamespace.DatabaseName == data.Database.DatabaseNamespace.DatabaseName && command.CommandName == "collMod" && command.Command.GetValue("collMod", BsonNull.Value) == "shipments")
                    lock (requests) requests.Add(command.RequestId);
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                lock (requests) if (requests.Remove(command.RequestId) && Interlocked.CompareExchange(ref changed, 1, 0) == 0) cancellation.Cancel();
            });
        };
        using var client = new MongoClient(settings);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StoreTestData.ApplyRegisteredThroughAsync(client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName), data.Clock, 105, cancellation.Token));
        Assert.Equal(1, changed); var interrupted = await SnapshotAsync(data.Database);
        foreach (var name in before.Names) {
            if (name == "shipments") { Assert.Equal(before[name]["rows"], interrupted[name]["rows"]); Assert.Equal(before[name]["indexes"], interrupted[name]["indexes"]); }
            else Assert.Equal(before[name], interrupted[name]);
        }
        await StoreTestData.ApplyRegisteredThroughAsync(data.Database, data.Clock, 105);
        Assert.Equal(interrupted, await SnapshotAsync(data.Database)); Assert.Equal(history, new BsonArray((await HistoryAsync(data.Database)).Take(104)));
    }
}
