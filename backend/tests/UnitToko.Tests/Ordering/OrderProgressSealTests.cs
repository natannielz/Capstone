using System.Runtime.Versioning;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Integrity;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OrderProgressSealTests(MongoFixture fixture)
{
    private static void Same(OrderApprovalProgress expected, OrderApprovalProgress actual) {
        Assert.Equal(expected.Revision, actual.Revision); Assert.Equal(expected.SpendingState, actual.SpendingState);
        Assert.Equal(expected.AcceptedSuborders.ToArray(), actual.AcceptedSuborders.ToArray()); Assert.Equal(expected.RejectedSuborders.ToArray(), actual.RejectedSuborders.ToArray());
        Assert.Equal(expected.Cancelled, actual.Cancelled); Assert.Equal(expected.DispatchSequence, actual.DispatchSequence);
        Assert.Equal(expected.HasRemainingReservation, actual.HasRemainingReservation);
    }

    [WindowsStorageFact]
    public async Task Order_progress_checkpoint_seals_replay_only_newer_history_and_refuse_a_tampered_prefix() {
        var prepared = await OrderSubstitutionTestData.CreateAsync(fixture, systemClock: false); await using var data = prepared.Data;
        var seals = data.Rows(OrderProgressSealSchemas.Collection);
        Task<SubmittedOrderView> Read() => data.Orders.ReadOwnAsync(data.Buyer, data.OrderId);
        var ring = SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = RandomNumberGenerator.GetBytes(32) });
        ProvenanceSealRing.Configure(ring);
        var alerts = new System.Collections.Concurrent.ConcurrentQueue<SealIntegrityAlert>();
        void Observe(SealIntegrityAlert alert) { if (alert.Kind == OrderProgressSealSchemas.Kind) alerts.Enqueue(alert); }
        SealIntegrityAlerts.Raised += Observe;
        try {
            var full = OrderProgressSeals.FullReplays;
            var first = await Read();
            Assert.Equal(full + 1, OrderProgressSeals.FullReplays);
            var seal = await seals.Find(new BsonDocument("_id", data.OrderId)).SingleAsync();
            Assert.Equal(first.Progress.Revision, seal["facts"]["revision"].AsInt64);
            Assert.Equal(OrderProgressSealSchemas.FactsVersion, seal["facts"]["factsVersion"].AsInt32);
            Same(first.Progress, (await Read()).Progress);
            Assert.Equal(full + 1, OrderProgressSeals.FullReplays);

            // New history after the checkpoint is replayed incrementally and the seal advances.
            var incremental = OrderProgressSeals.IncrementalReplays;
            data.Dispatcher = await data.IssueExistingSessionAsync(data.Dispatcher);
            await data.DispatchAsync(await data.PrepareDispatchAsync(8, "progress-seal-prepared"), "progress-seal-dispatched");
            var advanced = await Read();
            Assert.Equal(full + 1, OrderProgressSeals.FullReplays);
            Assert.True(OrderProgressSeals.IncrementalReplays > incremental);
            Assert.Equal(1L, advanced.Progress.DispatchSequence);
            var moved = await seals.Find(new BsonDocument("_id", data.OrderId)).SingleAsync();
            Assert.Equal(1L, moved["facts"]["dispatchSequence"].AsInt64);
            // The incremental result equals a fresh full replay.
            await seals.DeleteManyAsync(new BsonDocument());
            var fresh = await Read();
            Assert.Equal(full + 2, OrderProgressSeals.FullReplays);
            Same(fresh.Progress, advanced.Progress);
            var current = await seals.Find(new BsonDocument("_id", data.OrderId)).SingleAsync();

            // A tampered decision inside the sealed prefix refuses with a membership alert; it is never replayed around.
            var decisions = data.Rows("orderDecisions"); var decision = await decisions.Find(new BsonDocument("orderId", data.OrderId)).FirstAsync();
            // Schema-valid content change (the validator would refuse an unknown field before the seal is read).
            await decisions.UpdateOneAsync(new BsonDocument("_id", decision["_id"]), new BsonDocument("$set", new BsonDocument("reason", decision["reason"].AsString + " edited")));
            await Assert.ThrowsAnyAsync<Exception>(Read);
            Assert.StartsWith("membership:", alerts.Last().Check);
            Assert.Equal(full + 2, OrderProgressSeals.FullReplays);
            await decisions.UpdateOneAsync(new BsonDocument("_id", decision["_id"]), new BsonDocument("$set", new BsonDocument("reason", decision["reason"])));

            // Evidence a sealed row depends on (audit, receipt, movement, preparation, outbox) is bound too: the full replay
            // would refuse its loss, so the checkpoint must as well.
            foreach (var (collection, filter) in new[] {
                ("auditEvents", new BsonDocument("_id", decision["auditId"])),
                ("idempotencyRecords", new BsonDocument { { "actorId", decision["decidedBy"] }, { "key", decision["requestKey"] } }),
                ("movements", new BsonDocument("sourceEvent", new BsonDocument("$regex", "^fulfillment\\.shipment:"))),
                (UnitToko.Platform.Fulfillment.ShipmentPreparationBson.Versions, new BsonDocument()),
                ("outboxEvents", new BsonDocument("sourceKey", new BsonDocument("$regex", "^fulfillment\\.shipment\\.dispatched:"))) }) {
                var rows = data.Rows(collection); var evidence = await rows.Find(filter).FirstAsync();
                await rows.DeleteOneAsync(new BsonDocument("_id", evidence["_id"]));
                await Assert.ThrowsAnyAsync<Exception>(Read);
                Assert.Equal("membership:" + collection, alerts.Last().Check);
                await rows.InsertOneAsync(evidence);
            }
            // Outbox processing state changes after commit; it is not sealed content.
            await data.Rows("outboxEvents").UpdateManyAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument {
                { "status", "completed" }, { "attempts", 1 }, { "completedAt", DateTime.UtcNow }, { "processingFence", 1L } }));
            Same(fresh.Progress, (await Read()).Progress);
            Assert.Equal(full + 2, OrderProgressSeals.FullReplays);

            // A forged seal refuses with a seal alert.
            await seals.UpdateOneAsync(new BsonDocument("_id", data.OrderId), new BsonDocument("$set", new BsonDocument("mac", new string('0', 64))));
            await Assert.ThrowsAnyAsync<Exception>(Read);
            Assert.Equal("seal", alerts.Last().Check);
            Assert.Equal(full + 2, OrderProgressSeals.FullReplays);

            // A validly signed seal with stale facts semantics is treated as absent: full replay, then a current seal.
            var stale = current["facts"].DeepClone().AsBsonDocument; stale["factsVersion"] = OrderProgressSealSchemas.FactsVersion + 1;
            await seals.ReplaceOneAsync(new BsonDocument("_id", data.OrderId), ProvenanceSeal.Create(ring, OrderProgressSealSchemas.Kind, data.OrderId,
                current["recordHash"].AsString, current["membership"].AsBsonArray, [], DateTimeOffset.UtcNow, stale));
            Same(fresh.Progress, (await Read()).Progress);
            Assert.Equal(full + 3, OrderProgressSeals.FullReplays);
            Assert.Equal(OrderProgressSealSchemas.FactsVersion, (await seals.Find(new BsonDocument("_id", data.OrderId)).SingleAsync())["facts"]["factsVersion"].AsInt32);
        } finally { SealIntegrityAlerts.Raised -= Observe; ProvenanceSealRing.Configure(null); }
    }
}
