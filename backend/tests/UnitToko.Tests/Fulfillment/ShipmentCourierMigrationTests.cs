using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Fulfillment;

[Collection("Mongo")]
public sealed class ShipmentCourierMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Actual117_prefix_refuses_prospective_orphans_and_malformed_counter_before_DDL_then_resumes_stronger_empty118() {
        await using var data = await StoreTestData.CreateWithMigrationTargetAsync(fixture, 119);
        var history = data.Database.GetCollection<BsonDocument>("schemaMigrations");
        var prefix = await history.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal(119, prefix.Count); Assert.True(MigrationHistory.IsValid(prefix, 119)); Assert.All(prefix, row => Assert.Equal("applied", row["state"]));
        var gate = new MigrationGate(data.Database, data.Clock, 120);
        async Task Refuse() {
            var before = await SnapshotAsync(data.Database);
            var lease = await gate.AcquireAsync(120, default); Assert.NotNull(lease);
            try { await Assert.ThrowsAsync<InvalidOperationException>(() => new ShipmentCourierMigration(data.Database, fixture.Client, data.Clock).ApplyAsync(lease, default)); }
            finally { await lease.ReleaseAsync(); }
            Assert.Equal(before, await SnapshotAsync(data.Database));
            foreach (var name in ShipmentCourierSchemas.Collections) Assert.Null(await AccountContactStorage.DefinitionAsync(data.Database, name, default));
            Assert.Equal(new BsonArray(prefix), new BsonArray(await history.Find(new BsonDocument("_id", new BsonDocument("$lte", 119))).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        }
        foreach (var (table, field, value) in new[] { ("auditEvents", "operation", ShipmentCourierRecords.Operation),
            ("auditEvents", "kind", ShipmentCourierRecords.BindingOperation("challenge")),
            ("idempotencyRecords", "operation", ShipmentCourierRecords.Operation),
            ("outboxEvents", "kind", ShipmentCourierRecords.Operation), ("outboxEvents", "sourceKey", ShipmentCourierRecords.Operation + ":41") }) {
            var rows = data.Database.GetCollection<BsonDocument>(table); var marker = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { field, value } };
            await rows.InsertOneAsync(marker, new InsertOneOptions { BypassDocumentValidation = true }); await Refuse();
            await rows.DeleteOneAsync(new BsonDocument("_id", marker["_id"]));
        }
        var counters = data.Database.GetCollection<BsonDocument>("idCounters");
        await counters.InsertOneAsync(new BsonDocument { { "_id", ShipmentCourierRecords.Events }, { "value", 0L }, { "unreviewed", true } }, new InsertOneOptions { BypassDocumentValidation = true });
        await Refuse(); await counters.DeleteOneAsync(new BsonDocument("_id", ShipmentCourierRecords.Events));
        var stronger = new BsonDocument("$and", new BsonArray { ShipmentCourierSchemas.Validator(ShipmentCourierRecords.Anchors), new BsonDocument("_id", new BsonDocument("$gt", 0L)) });
        await data.Database.CreateCollectionAsync(ShipmentCourierRecords.Anchors, new CreateCollectionOptions<BsonDocument> { Validator = stronger,
            ValidationLevel = DocumentValidationLevel.Strict, ValidationAction = DocumentValidationAction.Error });
        var definition = await AccountContactStorage.DefinitionAsync(data.Database, ShipmentCourierRecords.Anchors, default); Assert.NotNull(definition);
        var lease2 = await gate.AcquireAsync(120, default); Assert.NotNull(lease2);
        try { await new ShipmentCourierMigration(data.Database, fixture.Client, data.Clock).ApplyAsync(lease2, default); await lease2.CompleteAsync(default); }
        finally { await lease2.ReleaseAsync(); }
        var after = await AccountContactStorage.DefinitionAsync(data.Database, ShipmentCourierRecords.Anchors, default); Assert.NotNull(after);
        Assert.Equal(definition["info"]["uuid"], after["info"]["uuid"]); Assert.Equal(definition["options"], after["options"]);
        await ShipmentCourierRecords.RequireStorageAsync(data.Database, default);
        foreach (var name in ShipmentCourierSchemas.Collections) Assert.Empty(await data.Database.GetCollection<BsonDocument>(name).Find(new BsonDocument()).ToListAsync());
        Assert.Null(await gate.AcquireAsync(120, default));
        Assert.Equal(new BsonArray(prefix), new BsonArray(await history.Find(new BsonDocument("_id", new BsonDocument("$lte", 119))).Sort(new BsonDocument("_id", 1)).ToListAsync()));
    }
    internal static async Task<BsonDocument> SnapshotAsync(IMongoDatabase database) {
        var result = new BsonDocument();
        foreach (var definition in (await (await database.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)) {
            var name = definition["name"].AsString; if (name == "schemaMigrations") continue;
            var rows = database.GetCollection<BsonDocument>(name);
            var indexes = (await (await rows.Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal);
            result.Add(name, new BsonDocument { { "definition", definition }, { "indexes", new BsonArray(indexes) },
                { "rows", new BsonArray(await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) } });
        }
        return result;
    }
}
