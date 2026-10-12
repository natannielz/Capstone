using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using UnitToko.Platform.Catalog;

namespace UnitToko.Platform.Integrity;

/// <summary>Keyed seal over an immutable owner record, its proof membership and its dependency seals.</summary>
public static class ProvenanceSeal
{
    /// <summary>Canonical content hash; the freshness fence carries no business content.</summary>
    public static string RowHash(BsonDocument row)
    {
        var copy = row.DeepClone().AsBsonDocument; copy.Remove("fence");
        return CatalogRepository.Digest(copy);
    }

    private static BsonDocument Payload(BsonDocument seal) => new() {
        { "kind", seal["kind"] }, { "ownerId", seal["ownerId"] }, { "recordHash", seal["recordHash"] }, { "membership", seal["membership"] },
        { "dependencies", seal["dependencies"] }, { "keyId", seal["keyId"] }, { "sealedAt", seal["sealedAt"] } };

    private static string Mac(byte[] key, BsonDocument seal) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(CatalogRepository.Digest(Payload(seal))))).ToLowerInvariant();

    public static BsonDocument Create(SealKeyRing ring, string kind, long ownerId, string recordHash, BsonArray membership, BsonArray dependencies, DateTimeOffset at)
    {
        var seal = new BsonDocument {
            { "_id", ownerId }, { "schemaVersion", 1 }, { "kind", kind }, { "ownerId", ownerId }, { "recordHash", recordHash },
            { "membership", membership.DeepClone() }, { "dependencies", dependencies.DeepClone() }, { "keyId", ring.CurrentKeyId },
            { "sealedAt", DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()).UtcDateTime } };
        seal["mac"] = Mac(ring.Key(ring.CurrentKeyId), seal);
        return seal;
    }

    public static bool MacValid(SealKeyRing ring, BsonDocument seal)
    {
        foreach (var field in new[] { "kind", "ownerId", "recordHash", "membership", "dependencies", "keyId", "sealedAt", "mac" })
            if (!seal.Contains(field)) return false;
        if (!seal["keyId"].IsString || !ring.Has(seal["keyId"].AsString) || !seal["mac"].IsString) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(seal["mac"].AsString),
            Encoding.ASCII.GetBytes(Mac(ring.Key(seal["keyId"].AsString), seal)));
    }
}
