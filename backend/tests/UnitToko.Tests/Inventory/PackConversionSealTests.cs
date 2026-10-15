using System.Runtime.Versioning;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Integrity;
using UnitToko.Platform.Inventory;
using UnitToko.Tests.Documents;
using B =UnitToko.Platform.Inventory.PackConversionBson;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PackConversionSealTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Conversion_is_sealed_after_a_full_proof_then_verified_without_reconstruction_and_refuses_tampering() {
        await using var prepared = await PackConversionServiceTests.PreparedConversion.CreateAsync(fixture);
        var data = prepared.Data; var service = prepared.Service();
        var row = await data.Rows(B.Conversions).Find(new BsonDocument()).SingleAsync();
        var source = B.Source(row["_id"].AsInt64, row["before"]["physicalPoolId"].AsInt64); var hash = row["causeHash"].AsString;
        Task Authenticate() => data.Unit.ExecuteAsync(async (tx, ct) => { await service.AuthenticateSourceAsync(data.Database, tx, source, hash, ct); return true; });
        var seals = data.Rows(InventorySealSchemas.Conversions);
        var ring = SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = RandomNumberGenerator.GetBytes(32) });
        ProvenanceSealRing.Configure(ring);
        var alerts = new System.Collections.Concurrent.ConcurrentQueue<SealIntegrityAlert>();
        void Observe(SealIntegrityAlert alert) { if (alert.Kind == InventorySealSchemas.ConversionKind) alerts.Enqueue(alert); }
        SealIntegrityAlerts.Raised += Observe;
        try {
            // A caller-supplied session runs the full proof and never writes a seal.
            using (var raw = await fixture.Client.StartSessionAsync()) {
                raw.StartTransaction(); await service.AuthenticateSourceAsync(data.Database, raw, source, hash, default); await raw.AbortTransactionAsync();
            }
            Assert.Equal(0, await seals.CountDocumentsAsync(new BsonDocument()));
            var before = PackConversionSeals.FullProofs;
            await Authenticate();
            Assert.Equal(before + 1, PackConversionSeals.FullProofs);
            var seal = await seals.Find(new BsonDocument()).SingleAsync();
            // Only immutable evidence is sealed: balances, pools and the growing source-lot movement membership stay fresh.
            Assert.All(seal["membership"].AsBsonArray, scope => Assert.Contains(scope["collection"].AsString, PackConversionSeals.SealedCollections));
            Assert.Contains(seal["membership"].AsBsonArray, scope => scope["collection"] == B.Conversions);
            await Authenticate();
            Assert.Equal(before + 1, PackConversionSeals.FullProofs);
            // A present but forged seal refuses, raises an integrity alert and is never re-proven around.
            await seals.UpdateOneAsync(new BsonDocument("_id", seal["_id"]), new BsonDocument("$set", new BsonDocument("mac", new string('0', 64))));
            await Assert.ThrowsAnyAsync<Exception>(Authenticate);
            Assert.Equal(before + 1, PackConversionSeals.FullProofs);
            Assert.Equal(new SealIntegrityAlert(InventorySealSchemas.ConversionKind, row["_id"].AsInt64, "seal"), Assert.Single(alerts));
            await seals.ReplaceOneAsync(new BsonDocument("_id", seal["_id"]), seal);
            await Authenticate();
            Assert.Equal(before + 1, PackConversionSeals.FullProofs);
            // Legitimate later freshness fences (rows that carry one) on sealed rows keep the seal verifying without a full proof or alert.
            foreach (var scope in seal["membership"].AsBsonArray.Select(value => value.AsBsonDocument).Where(value => value.Contains("members")))
                await data.Rows(scope["collection"].AsString).UpdateManyAsync(new BsonDocument("$and", new BsonArray { scope["filter"], new BsonDocument("fence", new BsonDocument("$exists", true)) }), new BsonDocument("$inc", new BsonDocument("fence", 1L)),
                    new UpdateOptions { BypassDocumentValidation = true });
            await Authenticate();
            Assert.Equal(before + 1, PackConversionSeals.FullProofs);
            Assert.Single(alerts);
            // A changed sealed member that the sealed path no longer re-proves (the audit evidence) refuses with a membership alert.
            var audit = seal["membership"].AsBsonArray.Select(value => value.AsBsonDocument).Single(scope => scope["collection"] == "auditEvents");
            var audits = data.Rows("auditEvents"); var auditRow = await audits.Find(audit["filter"].AsBsonDocument).FirstAsync();
            await audits.UpdateOneAsync(new BsonDocument("_id", auditRow["_id"]), new BsonDocument("$set", new BsonDocument("sealProbe", 1)),
                new UpdateOptions { BypassDocumentValidation = true });
            await Assert.ThrowsAnyAsync<Exception>(Authenticate);
            Assert.Equal("membership:auditEvents", alerts.Last().Check);
            await audits.UpdateOneAsync(new BsonDocument("_id", auditRow["_id"]), new BsonDocument("$unset", new BsonDocument("sealProbe", "")),
                new UpdateOptions { BypassDocumentValidation = true });
            // A validly signed seal that omits sealed evidence (here: everything but the conversion row) refuses with a shape alert.
            var conversionOnly = seal["membership"].AsBsonArray.Where(scope => scope["collection"] == B.Conversions).ToArray();
            await seals.ReplaceOneAsync(new BsonDocument("_id", seal["_id"]), ProvenanceSeal.Create(ring, InventorySealSchemas.ConversionKind,
                row["_id"].AsInt64, seal["recordHash"].AsString, new BsonArray(conversionOnly), [], DateTimeOffset.UtcNow));
            await Assert.ThrowsAnyAsync<Exception>(Authenticate);
            Assert.Equal("shape", alerts.Last().Check);
            Assert.Equal(before + 1, PackConversionSeals.FullProofs);
            await seals.ReplaceOneAsync(new BsonDocument("_id", seal["_id"]), seal);
            // An absent seal falls back to the full proof, which seals again.
            await seals.DeleteManyAsync(new BsonDocument());
            await Authenticate();
            Assert.Equal(before + 2, PackConversionSeals.FullProofs);
            Assert.Equal(1, await seals.CountDocumentsAsync(new BsonDocument()));
        } finally { SealIntegrityAlerts.Raised -= Observe; ProvenanceSealRing.Configure(null); }
    }
}
