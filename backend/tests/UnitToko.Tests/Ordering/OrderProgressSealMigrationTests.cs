using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Integrity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderProgressSealMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Migration_117_creates_strict_empty_order_progress_seal_storage_that_requires_facts() {
        var database = fixture.Client.GetDatabase("order_progress_seal_migration_" + Guid.NewGuid().ToString("N"));
        try {
            var migrations = new CollectionMigrations(database, new SystemClock());
            await migrations.ApplyAsync();
            Assert.True(await migrations.IsCurrentAsync());
            Assert.True(CollectionMigrations.CurrentVersion >= 117);
            await OrderProgressSealSchemas.RequireStorageAsync(database, default);
            var rows = database.GetCollection<BsonDocument>(OrderProgressSealSchemas.Collection);
            Assert.Equal(0, await rows.CountDocumentsAsync(new BsonDocument()));
            var ring = SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = new byte[32] });
            var membership = SealVerifier.Membership([("orderDecisions", new BsonDocument("orderId", 7L), [new BsonDocument { { "_id", 70L }, { "v", 1 } }])]);
            var facts = new BsonDocument { { "factsVersion", OrderProgressSealSchemas.FactsVersion }, { "revision", 2L } };
            await rows.InsertOneAsync(ProvenanceSeal.Create(ring, OrderProgressSealSchemas.Kind, 7, new string('a', 64), membership, [], DateTimeOffset.UtcNow, facts));
            var withoutFacts = ProvenanceSeal.Create(ring, OrderProgressSealSchemas.Kind, 8, new string('a', 64), membership, [], DateTimeOffset.UtcNow);
            await Assert.ThrowsAsync<MongoWriteException>(() => rows.InsertOneAsync(withoutFacts));
            var wrongKind = ProvenanceSeal.Create(ring, "inventory.receiptOrigin", 9, new string('a', 64), membership, [], DateTimeOffset.UtcNow, facts);
            await Assert.ThrowsAsync<MongoWriteException>(() => rows.InsertOneAsync(wrongKind));
            await migrations.ApplyAsync();
            Assert.Equal(1, await rows.CountDocumentsAsync(new BsonDocument()));
        } finally { await fixture.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName); }
    }
}
