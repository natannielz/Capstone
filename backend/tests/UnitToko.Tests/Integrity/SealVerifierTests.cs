using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Integrity;

namespace UnitToko.Tests.Integrity;

[Collection("Mongo")]
public sealed class SealVerifierTests(MongoFixture fixture)
{
    [Fact]
    public async Task Membership_and_record_changes_refuse_while_fence_advance_passes() {
        var database = fixture.Client.GetDatabase("seal_verifier_" + Guid.NewGuid().ToString("N"));
        try {
            var rows = database.GetCollection<BsonDocument>("members");
            await rows.InsertManyAsync([new() { { "_id", 1L }, { "orderId", 9L }, { "fence", 0L }, { "v", "a" } },
                new() { { "_id", 2L }, { "orderId", 9L }, { "fence", 0L }, { "v", "b" } }]);
            var owner = new BsonDocument { { "_id", 50L }, { "v", "receipt" } };
            var ring = SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = new byte[32] });
            var filter = new BsonDocument("orderId", 9L);
            var seal = ProvenanceSeal.Create(ring, "test.kind", 50, ProvenanceSeal.RowHash(owner),
                SealVerifier.Membership([("members", filter, await rows.Find(filter).Sort(new BsonDocument("_id", 1)).ToListAsync())]), [], DateTimeOffset.UtcNow);
            var verifier = new SealVerifier(database, ring);
            async Task<IReadOnlyList<BsonDocument>> Verify(long ownerId = 50) {
                using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction();
                try { return await verifier.VerifyAsync(tx, seal, "test.kind", ownerId, owner, default); } finally { await tx.AbortTransactionAsync(); }
            }
            var alerts = new System.Collections.Concurrent.ConcurrentQueue<SealIntegrityAlert>();
            void Observe(SealIntegrityAlert alert) { if (alert.Kind == "test.kind") alerts.Enqueue(alert); }
            SealIntegrityAlerts.Raised += Observe;
            try {
                await Assert.ThrowsAsync<SealUnavailableException>(() => Verify(51));
                Assert.Equal(new SealIntegrityAlert("test.kind", 51, "seal"), Assert.Single(alerts));
                await rows.UpdateOneAsync(new BsonDocument("_id", 2L), new BsonDocument("$set", new BsonDocument("v", "tampered")));
                await Assert.ThrowsAsync<SealUnavailableException>(() => Verify());
                Assert.Equal(new SealIntegrityAlert("test.kind", 50, "membership:members"), alerts.Last());
                await rows.UpdateOneAsync(new BsonDocument("_id", 2L), new BsonDocument("$set", new BsonDocument("v", "b")));
            } finally { SealIntegrityAlerts.Raised -= Observe; }
            var scopes = await Verify();
            Assert.Equal(2, Assert.Single(scopes)["rows"].AsBsonArray.Count);
            await rows.UpdateOneAsync(new BsonDocument("_id", 1L), new BsonDocument("$set", new BsonDocument("fence", 4L)));
            await Verify();
            Assert.Equal("seal_mismatch", (await Assert.ThrowsAsync<SealUnavailableException>(() => Verify(51))).Code);
            await rows.UpdateOneAsync(new BsonDocument("_id", 1L), new BsonDocument("$set", new BsonDocument("v", "z")));
            Assert.Equal("seal_mismatch", (await Assert.ThrowsAsync<SealUnavailableException>(() => Verify())).Code);
            await rows.UpdateOneAsync(new BsonDocument("_id", 1L), new BsonDocument("$set", new BsonDocument("v", "a")));
            await rows.InsertOneAsync(new() { { "_id", 3L }, { "orderId", 9L }, { "fence", 0L }, { "v", "c" } });
            Assert.Equal("seal_mismatch", (await Assert.ThrowsAsync<SealUnavailableException>(() => Verify())).Code);
            await rows.DeleteManyAsync(new BsonDocument("_id", new BsonDocument("$in", new BsonArray { 2L, 3L })));
            Assert.Equal("seal_mismatch", (await Assert.ThrowsAsync<SealUnavailableException>(() => Verify())).Code);
        } finally { await fixture.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName); }
    }
}
