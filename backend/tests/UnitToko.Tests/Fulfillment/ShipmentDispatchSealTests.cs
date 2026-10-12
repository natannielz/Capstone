using System.Runtime.Versioning;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Integrity;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Fulfillment;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ShipmentDispatchSealTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Shipment_value_history_is_sealed_after_a_full_proof_then_verified_without_ancestry_replay_and_refuses_a_forged_seal() {
        var prepared = await ApprovedShipmentDispatchAdmissionReaderTests.PreparedAsync(fixture, converted: true);
        await using var data = prepared.Data;
        var shipped = await data.DispatchAsync(await data.PrepareDispatchAsync(8, "shipment-seal-prepared"), "shipment-seal-dispatched");
        var id = shipped["shipmentId"].AsInt64;
        var seals = data.Rows(ProvenanceSealSchemas.ShipmentDispatchCollection);
        Task<BsonDocument> Read() => data.Unit.ExecuteAsync((tx, ct) => new ShipmentDispatchRecords(data.Database, data.Clock).ReadAsync(tx, id, ct));
        ProvenanceSealRing.Configure(SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = RandomNumberGenerator.GetBytes(32) }));
        try {
            // A caller-supplied session runs the full proof and never writes a seal.
            using (var raw = await fixture.Client.StartSessionAsync()) {
                raw.StartTransaction(); await new ShipmentDispatchRecords(data.Database, data.Clock).ReadAsync(raw, id, default); await raw.AbortTransactionAsync();
            }
            Assert.Equal(0, await seals.CountDocumentsAsync(new BsonDocument()));
            var before = ShipmentDispatchValueRecords.AncestryProofs;
            var full = await Read();
            var effects = full["valueEffects"].AsBsonArray.Count; Assert.True(effects > 0);
            Assert.Equal(before + effects, ShipmentDispatchValueRecords.AncestryProofs);
            Assert.Equal(1, await seals.CountDocumentsAsync(new BsonDocument()));
            // Sealed: the same verified row, no ancestry replay.
            Assert.Equal(full, await Read());
            Assert.Equal(before + effects, ShipmentDispatchValueRecords.AncestryProofs);
            // A present but forged seal refuses and is never re-proven around.
            var seal = await seals.Find(new BsonDocument()).SingleAsync();
            await seals.UpdateOneAsync(new BsonDocument("_id", seal["_id"]), new BsonDocument("$set", new BsonDocument("mac", new string('0', 64))));
            await Assert.ThrowsAnyAsync<Exception>(Read);
            Assert.Equal(before + effects, ShipmentDispatchValueRecords.AncestryProofs);
            // A sealed membership row that changes refuses as well.
            await seals.ReplaceOneAsync(new BsonDocument("_id", seal["_id"]), seal);
            var member = seal["membership"].AsBsonArray.Select(scope => scope.AsBsonDocument).First(scope => scope.Contains("members") && scope["members"].AsBsonArray.Count > 0);
            var rows = data.Rows(member["collection"].AsString); var original = await rows.Find(member["filter"].AsBsonDocument).FirstAsync();
            await rows.UpdateOneAsync(new BsonDocument("_id", original["_id"]), new BsonDocument("$set", new BsonDocument("sealProbe", 1)),
                new UpdateOptions { BypassDocumentValidation = true });
            await Assert.ThrowsAnyAsync<Exception>(Read);
            await rows.ReplaceOneAsync(new BsonDocument("_id", original["_id"]), original, new ReplaceOptions { BypassDocumentValidation = true });
            Assert.Equal(full, await Read());
            // An absent seal falls back to the full proof, which seals again.
            await seals.DeleteManyAsync(new BsonDocument());
            Assert.Equal(full, await Read());
            Assert.Equal(before + 2 * effects, ShipmentDispatchValueRecords.AncestryProofs);
            Assert.Equal(1, await seals.CountDocumentsAsync(new BsonDocument()));
        } finally { ProvenanceSealRing.Configure(null); }
    }
}
