using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using UnitToko.Platform.Catalog;

namespace UnitToko.Platform.Integrity;

/// <summary>Keyed seal over an immutable owner record, its proof membership and its dependency seals.</summary>
public static class ProvenanceSeal
{
    /// <summary>Stored seal limits (the owner collection validators enforce the same bounds).</summary>
    public const int MaximumScopes = 128, MaximumMembers = 1000;

    /// <summary>True when the membership fits the stored seal limits. A proof that does not fit stays on the full proof
    /// instead of attempting an insert the validator would refuse on every later read.</summary>
    public static bool Sealable(BsonArray membership) => membership.Count <= MaximumScopes &&
        membership.All(scope => !scope.AsBsonDocument.Contains("members") || scope["members"].AsBsonArray.Count <= MaximumMembers);

    /// <summary>Canonical content hash; the freshness fence carries no business content.</summary>
    public static string RowHash(BsonDocument row)
    {
        var copy = row.DeepClone().AsBsonDocument; copy.Remove("fence");
        return CatalogRepository.Digest(copy);
    }

    private static BsonDocument Payload(BsonDocument seal) => new() {
        { "kind", seal["kind"] }, { "ownerId", seal["ownerId"] }, { "recordHash", seal["recordHash"] }, { "membership", seal["membership"] },
        { "dependencies", seal["dependencies"] }, { "keyId", seal["keyId"] }, { "sealedAt", seal["sealedAt"] } };

    // The MAC covers the exact BSON bytes, field order included: MongoDB matches embedded-document filters by
    // field order, so a field-sorted digest would let a keyless writer reorder a sealed filter undetected.
    private static string Mac(byte[] key, BsonDocument seal) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Payload(seal).ToBson())).ToLowerInvariant();

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
