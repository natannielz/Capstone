using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Integrity;
using UnitToko.Platform.Procurement;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class SealedCaptureTests(MongoFixture fixture)
{
    private static readonly SealKeyRing Ring = SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = new byte[32] });
    private static readonly BsonDocument Owner = new() { { "_id", 1L }, { "v", "owner" } };

    [Fact]
    public async Task Sealed_scopes_round_trip_into_a_capture_that_still_refuses_later_changes() {
        var database = fixture.Client.GetDatabase("sealed_capture_" + Guid.NewGuid().ToString("N"));
        try {
            var rows = database.GetCollection<BsonDocument>("procurementTestRows");
            await rows.InsertManyAsync([new() { { "_id", 1L }, { "fence", 0L }, { "v", "a" } }, new() { { "_id", 2L }, { "fence", 0L }, { "v", "b" } }]);
            BsonDocument seal;
            using (var tx = await fixture.Client.StartSessionAsync()) {
                tx.StartTransaction(); var capture = new PurchaseOrderSourceReadCapture();
                Assert.NotNull(await capture.OneAsync(database, tx, "procurementTestRows", new BsonDocument("_id", 1L), default));
                capture.Seal();
                var scopes = capture.SealedScopes();
                Assert.NotNull(scopes);
                seal = ProvenanceSeal.Create(Ring, "test.kind", 1, ProvenanceSeal.RowHash(Owner), SealVerifier.Membership(scopes!), [], DateTimeOffset.UtcNow);
                await tx.AbortTransactionAsync();
            }
            using (var tx = await fixture.Client.StartSessionAsync()) {
                tx.StartTransaction();
                var verified = await new SealVerifier(database, Ring).VerifyAsync(tx, seal, "test.kind", 1, Owner, default);
                var rebuilt = PurchaseOrderSourceReadCapture.FromSealed(database, tx, verified, null);
                await rebuilt.RequireAsync(database, tx, default);
                await rows.UpdateOneAsync(tx, new BsonDocument("_id", 1L), new BsonDocument("$set", new BsonDocument("v", "changed")));
                await Assert.ThrowsAnyAsync<Exception>(() => rebuilt.RequireAsync(database, tx, default));
                await tx.AbortTransactionAsync();
            }
        } finally { await fixture.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName); }
    }

    [Fact]
    public async Task Recorded_counts_round_trip_and_a_changed_count_refuses() {
        var database = fixture.Client.GetDatabase("sealed_capture_" + Guid.NewGuid().ToString("N"));
        try {
            await database.GetCollection<BsonDocument>("procurementTestRows").InsertOneAsync(new() { { "_id", 1L }, { "v", "a" } });
            using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction();
            var capture = new PurchaseOrderSourceReadCapture();
            await capture.OneAsync(database, tx, "procurementTestRows", new BsonDocument("_id", 1L), default);
            capture.RecordCount("procurementTestRows", new BsonDocument(), 1);
            capture.Seal();
            var seal = ProvenanceSeal.Create(Ring, "test.kind", 1, ProvenanceSeal.RowHash(Owner), SealVerifier.Membership(capture.SealedScopes()!, capture.SealedCounts()), [], DateTimeOffset.UtcNow);
            await tx.AbortTransactionAsync();
            using var check = await fixture.Client.StartSessionAsync(); check.StartTransaction();
            var verified = await new SealVerifier(database, Ring).VerifyAsync(check, seal, "test.kind", 1, Owner, default);
            var rebuilt = PurchaseOrderSourceReadCapture.FromSealed(database, check, verified, null);
            await rebuilt.RequireAsync(database, check, default);
            await database.GetCollection<BsonDocument>("procurementTestRows").InsertOneAsync(check, new() { { "_id", 2L }, { "v", "b" } });
            await Assert.ThrowsAnyAsync<Exception>(() => rebuilt.RequireAsync(database, check, default));
            await Assert.ThrowsAsync<SealUnavailableException>(() => new SealVerifier(database, Ring).VerifyAsync(check, seal, "test.kind", 1, Owner, default));
            await check.AbortTransactionAsync();
        } finally { await fixture.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName); }
    }
}
