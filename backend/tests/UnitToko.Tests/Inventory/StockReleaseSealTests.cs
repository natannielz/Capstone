using System.Runtime.Versioning;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Integrity;
using UnitToko.Platform.Inventory;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Inventory.StockReleaseBson;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class StockReleaseSealTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Release_source_is_sealed_after_a_full_proof_then_verified_without_procurement_replay_and_refuses_tampering() {
        await using var data = await StockReleaseServiceTests.Data.CreateAsync(fixture);
        var proposed = await data.ProposeAsync("release-seal-proposed");
        await data.DecideAsync(proposed["proposalId"].AsInt64, "release-seal-approved");
        var decision = await data.Rows(B.Decisions).Find(new BsonDocument("decision", "approve")).SingleAsync();
        var source = B.Source(decision["_id"].AsInt64, decision["before"]["physicalPoolId"].AsInt64); var hash = decision["causeHash"].AsString;
        var service = data.NewService();
        Task Authenticate() => data.Unit.ExecuteAsync(async (tx, ct) => { await service.AuthenticateSourceAsync(data.Database, tx, source, hash, ct); return true; });
        var seals = data.Rows(InventorySealSchemas.Releases);
        var ring = SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = RandomNumberGenerator.GetBytes(32) });
        ProvenanceSealRing.Configure(ring);
        var alerts = new System.Collections.Concurrent.ConcurrentQueue<SealIntegrityAlert>();
        void Observe(SealIntegrityAlert alert) { if (alert.Kind == InventorySealSchemas.ReleaseKind) alerts.Enqueue(alert); }
        SealIntegrityAlerts.Raised += Observe;
        try {
            // A caller-supplied session runs the full proof and never writes a seal.
            using (var raw = await fixture.Client.StartSessionAsync()) {
                raw.StartTransaction(); await service.AuthenticateSourceAsync(data.Database, raw, source, hash, default); await raw.AbortTransactionAsync();
            }
            Assert.Equal(0, await seals.CountDocumentsAsync(new BsonDocument()));
            var before = StockReleaseSeals.FullProofs;
            await Authenticate();
            Assert.Equal(before + 1, StockReleaseSeals.FullProofs);
            var seal = await seals.Find(new BsonDocument()).SingleAsync();
            Assert.Equal(proposed["proposalId"].AsInt64, seal["ownerId"].AsInt64);
            Assert.Equal(StockReleaseSeals.SealedCollections.Count, seal["membership"].AsBsonArray.Count);
            await Authenticate();
            Assert.Equal(before + 1, StockReleaseSeals.FullProofs);
            // A present but forged seal refuses, raises an integrity alert and is never re-proven around.
            await seals.UpdateOneAsync(new BsonDocument("_id", seal["_id"]), new BsonDocument("$set", new BsonDocument("mac", new string('0', 64))));
            await Assert.ThrowsAnyAsync<Exception>(Authenticate);
            Assert.Equal(before + 1, StockReleaseSeals.FullProofs);
            Assert.Equal(new SealIntegrityAlert(InventorySealSchemas.ReleaseKind, proposed["proposalId"].AsInt64, "seal"), Assert.Single(alerts));
            await seals.ReplaceOneAsync(new BsonDocument("_id", seal["_id"]), seal);
            await Authenticate();
            Assert.Equal(before + 1, StockReleaseSeals.FullProofs);
            // Legitimate later freshness fences (rows that carry one) on sealed procurement rows keep the seal verifying without a full proof or alert.
            foreach (var scope in seal["membership"].AsBsonArray.Select(value => value.AsBsonDocument))
                await data.Rows(scope["collection"].AsString).UpdateManyAsync(new BsonDocument("$and", new BsonArray { scope["filter"], new BsonDocument("fence", new BsonDocument("$exists", true)) }), new BsonDocument("$inc", new BsonDocument("fence", 1L)),
                    new UpdateOptions { BypassDocumentValidation = true });
            await Authenticate();
            Assert.Equal(before + 1, StockReleaseSeals.FullProofs);
            Assert.Single(alerts);
            // A changed sealed member row refuses with a membership alert.
            var member = seal["membership"].AsBsonArray.Select(value => value.AsBsonDocument).First(scope => scope["collection"] != "lots");
            var memberRows = data.Rows(member["collection"].AsString); var original = await memberRows.Find(member["filter"].AsBsonDocument).SingleAsync();
            await memberRows.UpdateOneAsync(new BsonDocument("_id", original["_id"]), new BsonDocument("$set", new BsonDocument("sealProbe", 1)),
                new UpdateOptions { BypassDocumentValidation = true });
            await Assert.ThrowsAnyAsync<Exception>(Authenticate);
            Assert.StartsWith("membership:", alerts.Last().Check);
            await memberRows.UpdateOneAsync(new BsonDocument("_id", original["_id"]), new BsonDocument("$unset", new BsonDocument("sealProbe", "")),
                new UpdateOptions { BypassDocumentValidation = true });
            // A validly signed seal that omits a sealed source row refuses with a shape alert.
            var partial = seal["membership"].AsBsonArray.Skip(1).ToArray();
            await seals.ReplaceOneAsync(new BsonDocument("_id", seal["_id"]), ProvenanceSeal.Create(ring, InventorySealSchemas.ReleaseKind,
                proposed["proposalId"].AsInt64, seal["recordHash"].AsString, new BsonArray(partial), [], DateTimeOffset.UtcNow));
            await Assert.ThrowsAnyAsync<Exception>(Authenticate);
            Assert.Equal("shape", alerts.Last().Check);
            Assert.Equal(before + 1, StockReleaseSeals.FullProofs);
            await seals.ReplaceOneAsync(new BsonDocument("_id", seal["_id"]), seal);
            // An absent seal falls back to the full proof, which seals again.
            await seals.DeleteManyAsync(new BsonDocument());
            await Authenticate();
            Assert.Equal(before + 2, StockReleaseSeals.FullProofs);
            Assert.Equal(1, await seals.CountDocumentsAsync(new BsonDocument()));
        } finally { SealIntegrityAlerts.Raised -= Observe; ProvenanceSealRing.Configure(null); }
    }
}
