using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Integrity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Inventory.PackConversionBson;

namespace UnitToko.Platform.Inventory;

/// <summary>Inventory-owned seal over a committed pack conversion: the conversion row plus its immutable evidence
/// (destination lot, own and previous value transitions, audit, receipt, outbox). Written once after the full
/// conversion proof inside an owning unit-of-work operation; later reads verify it instead of reconstructing the
/// source prefix. Mutable evidence (balances, pools, the source lot's growing movement membership) is never sealed.</summary>
internal static class PackConversionSeals
{
    private const string Kind = InventorySealSchemas.ConversionKind;
    internal static readonly IReadOnlySet<string> SealedCollections = new HashSet<string>(StringComparer.Ordinal) {
        B.Conversions, "lots", LotValueStore.Transitions, "auditEvents", "idempotencyRecords", "outboxEvents" };
    // Full conversion proofs run (tests assert that a verified seal replaces them).
    internal static long FullProofs;
    private static IMongoCollection<BsonDocument> Seals(IMongoDatabase database) => database.GetCollection<BsonDocument>(InventorySealSchemas.Conversions);

    /// <summary>Null when no key ring or no seal exists (the caller runs the full proof). Otherwise the verified
    /// immutable scopes. A present seal that fails, or that names anything but sealed collections, refuses.</summary>
    internal static async Task<IReadOnlyList<BsonDocument>?> VerifyAsync(IMongoDatabase database, IClientSessionHandle tx, BsonDocument row, CancellationToken ct)
    {
        var ring = ProvenanceSealRing.Current;
        if (ring is null) return null;
        var seal = await Seals(database).Find(tx, new BsonDocument("_id", row["_id"])).SingleOrDefaultAsync(ct);
        if (seal is null) return null;
        IReadOnlyList<BsonDocument> verified;
        try { verified = await new SealVerifier(database, ring).VerifyAsync(tx, seal, Kind, row["_id"].AsInt64, row, ct); }
        catch (SealUnavailableException) { throw B.Broken(); }
        // Shape: only sealed collections, each sealed collection exactly once as a non-empty row scope (counts may join).
        var rowScopes = verified.Where(scope => scope.Contains("rows")).ToArray();
        if (seal["dependencies"].AsBsonArray.Count != 0 || verified.Any(scope => !SealedCollections.Contains(scope["collection"].AsString)) ||
            rowScopes.Any(scope => scope["rows"].AsBsonArray.Count == 0) || rowScopes.Length != SealedCollections.Count ||
            rowScopes.Select(scope => scope["collection"].AsString).Distinct(StringComparer.Ordinal).Count() != SealedCollections.Count) {
            SealIntegrityAlerts.Raise(Kind, row["_id"].AsInt64, "shape"); throw B.Broken();
        }
        return verified;
    }

    /// <summary>Seals after the full proof succeeded. Only an owning operation seals; caller-supplied sessions never write.</summary>
    internal static async Task SealAsync(IMongoDatabase database, IClock clock, IClientSessionHandle tx, BsonDocument row,
        PurchaseOrderSourceReadCapture capture, CancellationToken ct)
    {
        var ring = ProvenanceSealRing.Current;
        if (ring is null || !MongoUnitOfWork.OwnsOperation(tx) || capture.SealedScopes() is not { } scopes ||
            await Seals(database).Find(tx, new BsonDocument("_id", row["_id"])).AnyAsync(ct)) return;
        var membership = SealVerifier.Membership(scopes.Where(scope => SealedCollections.Contains(scope.Collection)),
            capture.SealedCounts().Where(count => SealedCollections.Contains(count.Collection)));
        if (!ProvenanceSeal.Sealable(membership)) return; // Beyond the stored seal limits: this conversion keeps the full proof.
        await Seals(database).InsertOneAsync(tx, ProvenanceSeal.Create(ring, Kind, row["_id"].AsInt64, ProvenanceSeal.RowHash(row),
            membership, [], clock.UtcNow), cancellationToken: ct);
    }
}
