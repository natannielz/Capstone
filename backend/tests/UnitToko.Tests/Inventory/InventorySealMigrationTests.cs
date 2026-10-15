using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Integrity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
public sealed class InventorySealMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Migration_116_creates_strict_empty_conversion_and_release_seal_stores_and_reapply_preserves_seals() {
        var database = fixture.Client.GetDatabase("inventory_seal_migration_" + Guid.NewGuid().ToString("N"));
        try {
            var migrations = new CollectionMigrations(database, new SystemClock());
            await migrations.ApplyAsync();
            Assert.True(await migrations.IsCurrentAsync());
            Assert.True(CollectionMigrations.CurrentVersion >= 116);
            await InventorySealSchemas.RequireStorageAsync(database, default);
            var ring = SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = new byte[32] });
            foreach (var (collection, kind) in new[] { (InventorySealSchemas.Conversions, InventorySealSchemas.ConversionKind),
                (InventorySealSchemas.Releases, InventorySealSchemas.ReleaseKind) }) {
                var rows = database.GetCollection<BsonDocument>(collection);
                Assert.Equal(0, await rows.CountDocumentsAsync(new BsonDocument()));
                var seal = ProvenanceSeal.Create(ring, kind, 7, new string('a', 64), SealVerifier.Membership([
                    ("packConversions", new BsonDocument("_id", 7L), [new BsonDocument { { "_id", 7L }, { "v", 1 } }])]), [], DateTimeOffset.UtcNow);
                await rows.InsertOneAsync(seal);
                var unsealed = seal.DeepClone().AsBsonDocument; unsealed["_id"] = 8L; unsealed["ownerId"] = 8L; unsealed.Remove("mac");
                await Assert.ThrowsAsync<MongoWriteException>(() => rows.InsertOneAsync(unsealed));
                var wrongKind = seal.DeepClone().AsBsonDocument; wrongKind["_id"] = 9L; wrongKind["ownerId"] = 9L; wrongKind["kind"] = "inventory.receiptOrigin";
                await Assert.ThrowsAsync<MongoWriteException>(() => rows.InsertOneAsync(wrongKind));
            }
            await migrations.ApplyAsync();
            Assert.Equal(1, await database.GetCollection<BsonDocument>(InventorySealSchemas.Conversions).CountDocumentsAsync(new BsonDocument()));
            Assert.Equal(1, await database.GetCollection<BsonDocument>(InventorySealSchemas.Releases).CountDocumentsAsync(new BsonDocument()));
        } finally { await fixture.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName); }
    }
}
