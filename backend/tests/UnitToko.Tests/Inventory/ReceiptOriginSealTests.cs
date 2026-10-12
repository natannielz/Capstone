using System.Runtime.Versioning;
using System.Security.Cryptography;
using MongoDB.Bson;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Procurement;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Integrity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ReceiptOriginSealTests(MongoFixture fixture)
{
    private static Task<HeldReceiptOriginProof> Capture(ProcurementStockValuationTestData data, IClientSessionHandle tx) =>
        HeldReceiptOriginProof.CaptureAsync(data.Database, data.Unit, data.Clock, data.Inspection.Receiving.Procurement.Catalog,
            data.Inspection.Receiving.Orders.Service, data.Sources, tx, data.ReceiptId, default);

    [WindowsStorageFact]
    public async Task Sealed_origin_full_recheck_runs_once_per_owning_operation() {
        await using var data = await ProcurementStockValuationTestData.CreateAsync(fixture, mixedQuality: true);
        ProvenanceSealRing.Configure(SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = RandomNumberGenerator.GetBytes(32) }));
        try {
            await data.Unit.ExecuteAsync(async (tx, ct) => (await Capture(data, tx)).Receipt);
            var before = HeldReceiptOriginProof.FullRechecks;
            await data.Unit.ExecuteAsync(async (tx, ct) => {
                var first = await Capture(data, tx); var second = await Capture(data, tx);
                await first.RequireAsync(data.Database, tx, ct); await second.RequireAsync(data.Database, tx, ct); await first.RequireAsync(data.Database, tx, ct);
                return true; });
            Assert.Equal(before + 1, HeldReceiptOriginProof.FullRechecks);
            await data.Unit.ExecuteAsync(async (tx, ct) => { await (await Capture(data, tx)).RequireAsync(data.Database, tx, ct); return true; });
            Assert.Equal(before + 2, HeldReceiptOriginProof.FullRechecks);
            using var raw = await fixture.Client.StartSessionAsync(); raw.StartTransaction();
            var rawProof = await Capture(data, raw);
            await rawProof.RequireAsync(data.Database, raw, default); await rawProof.RequireAsync(data.Database, raw, default);
            Assert.Equal(before + 4, HeldReceiptOriginProof.FullRechecks);
            await raw.AbortTransactionAsync();
        } finally { ProvenanceSealRing.Configure(null); }
    }

    [WindowsStorageFact]
    public async Task Receipt_origin_is_sealed_once_then_verified_without_reconstruction_and_refuses_tampering() {
        await using var data = await ProcurementStockValuationTestData.CreateAsync(fixture, mixedQuality: true);
        var seals = data.Rows(ProvenanceSealSchemas.Collection);
        ProvenanceSealRing.Configure(SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = RandomNumberGenerator.GetBytes(32) }));
        try {
            using (var raw = await fixture.Client.StartSessionAsync()) { raw.StartTransaction(); await Capture(data, raw); await raw.AbortTransactionAsync(); }
            Assert.Equal(0, await seals.CountDocumentsAsync(new BsonDocument()));
            var full = await data.Unit.ExecuteAsync(async (tx, ct) => (await Capture(data, tx)).Receipt);
            Assert.Equal(1, await seals.CountDocumentsAsync(new BsonDocument()));
            var before = ProcurementStockReceiptRecords.OriginReconstructions;
            var sealedReceipt = await data.Unit.ExecuteAsync(async (tx, ct) => { var proof = await Capture(data, tx); await proof.RequireAsync(data.Database, tx, ct); return proof.Receipt; });
            Assert.Equal(before, ProcurementStockReceiptRecords.OriginReconstructions);
            Assert.Equal(full, sealedReceipt);
            var seal = await seals.Find(new BsonDocument()).SingleAsync();
            await seals.UpdateOneAsync(new BsonDocument("_id", seal["_id"]), new BsonDocument("$set", new BsonDocument("mac", new string('0', 64))));
            await Assert.ThrowsAnyAsync<Exception>(() => data.Unit.ExecuteAsync(async (tx, ct) => (await Capture(data, tx)).Receipt));
            await seals.ReplaceOneAsync(new BsonDocument("_id", seal["_id"]), seal);
            var receipts = data.Rows(ProcurementStockReceiptBson.Receipts);
            var row = await receipts.Find(new BsonDocument("_id", data.ReceiptId)).SingleAsync();
            var edited = row.DeepClone().AsBsonDocument; edited["reason"] = "Edited after sealing"; edited["input"]["reason"] = "Edited after sealing"; edited["snapshotHash"] = ProcurementStockReceiptBson.Hash(edited);
            await receipts.ReplaceOneAsync(new BsonDocument("_id", row["_id"]), edited);
            await Assert.ThrowsAnyAsync<Exception>(() => data.Unit.ExecuteAsync(async (tx, ct) => (await Capture(data, tx)).Receipt));
            await receipts.ReplaceOneAsync(new BsonDocument("_id", row["_id"]), row);
            await seals.DeleteManyAsync(new BsonDocument());
            var resealedBefore = ProcurementStockReceiptRecords.OriginReconstructions;
            await data.Unit.ExecuteAsync(async (tx, ct) => (await Capture(data, tx)).Receipt);
            Assert.True(ProcurementStockReceiptRecords.OriginReconstructions > resealedBefore);
            Assert.Equal(1, await seals.CountDocumentsAsync(new BsonDocument()));
            await seals.DeleteManyAsync(new BsonDocument());
            await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => data.Unit.ExecuteAsync(async (tx, ct) => (await Capture(data, tx)).Receipt)));
            Assert.Equal(1, await seals.CountDocumentsAsync(new BsonDocument()));
        } finally { ProvenanceSealRing.Configure(null); }
    }

    [Fact]
    public async Task Migration_115_creates_strict_empty_seal_storage_and_reapply_preserves_seals() {
        var database = fixture.Client.GetDatabase("receipt_seal_migration_" + Guid.NewGuid().ToString("N"));
        try {
            var migrations = new CollectionMigrations(database, new SystemClock());
            await migrations.ApplyAsync();
            Assert.True(await migrations.IsCurrentAsync());
            await ProvenanceSealSchemas.RequireStorageAsync(database, default);
            var seals = database.GetCollection<BsonDocument>(ProvenanceSealSchemas.Collection);
            var ring = SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = new byte[32] });
            var seal = ProvenanceSeal.Create(ring, ProvenanceSealSchemas.ReceiptOriginKind, 7, new string('a', 64), SealVerifier.Membership([
                ("procurementStockReceipts", new BsonDocument("_id", 7L), [new BsonDocument { { "_id", 7L }, { "v", 1 } }])]), [], DateTimeOffset.UtcNow);
            await seals.InsertOneAsync(seal);
            var unsealed = seal.DeepClone().AsBsonDocument; unsealed["_id"] = 8L; unsealed["ownerId"] = 8L; unsealed.Remove("mac");
            await Assert.ThrowsAsync<MongoWriteException>(() => seals.InsertOneAsync(unsealed));
            var duplicate = seal.DeepClone().AsBsonDocument;
            await Assert.ThrowsAsync<MongoWriteException>(() => seals.InsertOneAsync(duplicate));
            await migrations.ApplyAsync();
            Assert.Equal(seal, await seals.Find(new BsonDocument("_id", 7L)).SingleAsync());
        } finally { await fixture.Client.DropDatabaseAsync(database.DatabaseNamespace.DatabaseName); }
    }
}
