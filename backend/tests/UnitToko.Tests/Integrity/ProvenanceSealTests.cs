using System.Security.Cryptography;
using MongoDB.Bson;
using UnitToko.Platform.Integrity;

namespace UnitToko.Tests.Integrity;

public sealed class ProvenanceSealTests
{
    private static readonly byte[] KeyA = RandomNumberGenerator.GetBytes(32), KeyB = RandomNumberGenerator.GetBytes(32);
    private static BsonArray Members() => new() { new BsonDocument { { "collection", "c" }, { "filter", new BsonDocument("orderId", 1L) },
        { "members", new BsonArray { new BsonDocument { { "id", 1L }, { "hash", new string('a', 64) } } } } } };

    [Fact]
    public void Seal_verifies_and_any_payload_change_breaks_the_mac() {
        var ring = SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = KeyA });
        var seal = ProvenanceSeal.Create(ring, "inventory.receiptOrigin", 7, new string('b', 64), Members(), [], DateTimeOffset.UnixEpoch);
        Assert.True(ProvenanceSeal.MacValid(ring, seal));
        foreach (var edit in new Action<BsonDocument>[] { s => s["recordHash"] = new string('c', 64), s => s["ownerId"] = 8L,
            s => s["membership"][0]["members"][0]["hash"] = new string('d', 64), s => s["keyId"] = "k2" }) {
            var copy = seal.DeepClone().AsBsonDocument; edit(copy);
            Assert.False(ProvenanceSeal.MacValid(ring, copy));
        }
    }

    [Fact]
    public void Retained_key_verifies_old_seals_and_unknown_key_refuses() {
        var old = SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = KeyA });
        var seal = ProvenanceSeal.Create(old, "inventory.receiptOrigin", 7, new string('b', 64), Members(), [], DateTimeOffset.UnixEpoch);
        var rotated = SealKeyRing.ForKeys("k2", new Dictionary<string, byte[]> { ["k1"] = KeyA, ["k2"] = KeyB });
        Assert.True(ProvenanceSeal.MacValid(rotated, seal));
        Assert.False(ProvenanceSeal.MacValid(SealKeyRing.ForKeys("k2", new Dictionary<string, byte[]> { ["k2"] = KeyB }), seal));
    }

    [Fact]
    public void Reordering_fields_inside_a_sealed_filter_breaks_the_mac_while_a_bson_round_trip_verifies() {
        // MongoDB matches embedded-document equality by field order, so the MAC must bind that order.
        var ring = SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = KeyA });
        var membership = new BsonArray { new BsonDocument { { "collection", "c" },
            { "filter", new BsonDocument("source", new BsonDocument { { "namespace", "n" }, { "documentId", 1L } }) }, { "count", 0L } } };
        var seal = ProvenanceSeal.Create(ring, "inventory.receiptOrigin", 7, new string('b', 64), membership, [], DateTimeOffset.UnixEpoch);
        Assert.True(ProvenanceSeal.MacValid(ring, MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonDocument>(seal.ToBson())));
        var reordered = seal.DeepClone().AsBsonDocument;
        reordered["membership"][0]["filter"]["source"] = new BsonDocument { { "documentId", 1L }, { "namespace", "n" } };
        Assert.False(ProvenanceSeal.MacValid(ring, reordered));
    }

    [Fact]
    public void Sealed_facts_are_covered_by_the_mac_and_seals_without_facts_are_unchanged() {
        var ring = SealKeyRing.ForKeys("k1", new Dictionary<string, byte[]> { ["k1"] = KeyA });
        var facts = new BsonDocument { { "factsVersion", 1 }, { "revision", 4L }, { "spendingState", "Approved" } };
        var withFacts = ProvenanceSeal.Create(ring, "ordering.orderProgress", 7, new string('b', 64), Members(), [], DateTimeOffset.UnixEpoch, facts);
        Assert.True(ProvenanceSeal.MacValid(ring, withFacts));
        Assert.True(ProvenanceSeal.MacValid(ring, MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonDocument>(withFacts.ToBson())));
        Assert.Equal(facts, withFacts["facts"]);
        var edited = withFacts.DeepClone().AsBsonDocument; edited["facts"]["revision"] = 5L;
        Assert.False(ProvenanceSeal.MacValid(ring, edited));
        var stripped = withFacts.DeepClone().AsBsonDocument; stripped.Remove("facts");
        Assert.False(ProvenanceSeal.MacValid(ring, stripped));
        // Existing kinds never carry facts; their seal bytes are unchanged by the optional field.
        var plain = ProvenanceSeal.Create(ring, "inventory.receiptOrigin", 7, new string('b', 64), Members(), [], DateTimeOffset.UnixEpoch);
        Assert.False(plain.Contains("facts"));
        Assert.True(ProvenanceSeal.MacValid(ring, plain));
    }

    [Fact]
    public void Sealable_enforces_the_stored_scope_and_member_limits() {
        static BsonArray Scopes(int scopes, int members) => new(Enumerable.Range(0, scopes).Select(scope => new BsonDocument {
            { "collection", "c" }, { "filter", new BsonDocument("s", scope) },
            { "members", new BsonArray(Enumerable.Range(0, members).Select(id => new BsonDocument { { "id", (long)id }, { "hash", new string('a', 64) } })) } }));
        Assert.True(ProvenanceSeal.Sealable(Scopes(ProvenanceSeal.MaximumScopes, ProvenanceSeal.MaximumMembers)));
        Assert.False(ProvenanceSeal.Sealable(Scopes(1, ProvenanceSeal.MaximumMembers + 1)));
        Assert.False(ProvenanceSeal.Sealable(Scopes(ProvenanceSeal.MaximumScopes + 1, 1)));
        Assert.Equal((128, 1000), (ProvenanceSeal.MaximumScopes, ProvenanceSeal.MaximumMembers));
    }

    [Fact]
    public void Row_hash_ignores_only_the_freshness_fence() {
        var row = new BsonDocument { { "_id", 1L }, { "fence", 0L }, { "value", "x" } };
        var advanced = row.DeepClone().AsBsonDocument; advanced["fence"] = 5L;
        var changed = row.DeepClone().AsBsonDocument; changed["value"] = "y";
        Assert.Equal(ProvenanceSeal.RowHash(row), ProvenanceSeal.RowHash(advanced));
        Assert.NotEqual(ProvenanceSeal.RowHash(row), ProvenanceSeal.RowHash(changed));
    }

    [Fact]
    public void Outbox_members_bind_the_event_but_not_its_later_processing_state() {
        var row = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "sourceKey", "s:1" }, { "kind", "k" },
            { "payload", new BsonDocument("id", 1L) }, { "status", "pending" }, { "attempts", 0 }, { "dueAt", DateTime.UnixEpoch }, { "createdAt", DateTime.UnixEpoch } };
        var processed = row.DeepClone().AsBsonDocument;
        processed["status"] = "completed"; processed["attempts"] = 1; processed["completedAt"] = DateTime.UtcNow; processed["processingFence"] = 2L;
        processed["dueAt"] = DateTime.UtcNow; processed["leaseOwner"] = "w"; processed["processingReceipt"] = new BsonDocument("r", 1);
        Assert.Equal(ProvenanceSeal.MemberHash("outboxEvents", row), ProvenanceSeal.MemberHash("outboxEvents", processed));
        foreach (var field in new[] { "sourceKey", "kind", "createdAt", "payload" }) {
            var changed = row.DeepClone().AsBsonDocument; changed[field] = field == "createdAt" ? DateTime.UtcNow : "x";
            Assert.NotEqual(ProvenanceSeal.MemberHash("outboxEvents", row), ProvenanceSeal.MemberHash("outboxEvents", changed));
        }
        // Every other collection keeps the whole-row hash (only the fence is ignored).
        Assert.NotEqual(ProvenanceSeal.MemberHash("auditEvents", row), ProvenanceSeal.MemberHash("auditEvents", processed));
        Assert.Equal(ProvenanceSeal.RowHash(row), ProvenanceSeal.MemberHash("auditEvents", row));
    }
}
