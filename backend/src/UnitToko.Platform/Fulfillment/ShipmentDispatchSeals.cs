using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Integrity;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Fulfillment;

/// <summary>Fulfillment-owned seal over a committed shipment's value history: the shipment row plus each consumed
/// lot's value transitions up to its own. Written once after a full consumed-lot ancestry proof inside an owning
/// unit-of-work operation; later reads verify it instead of replaying that ancestry.</summary>
internal static class ShipmentDispatchSeals
{
    private const string Kind = ProvenanceSealSchemas.ShipmentDispatchKind;
    private const int MaximumMembers = 1000;
    private static IMongoCollection<BsonDocument> Seals(IMongoDatabase database) =>
        database.GetCollection<BsonDocument>(ProvenanceSealSchemas.ShipmentDispatchCollection);
    // Transition ids are allocated monotonically and a balance's transitions serialize on its revision.
    private static (string Collection, BsonDocument Filter)[] Scopes(BsonDocument row) =>
        [.. row["valueEffects"].AsBsonArray.Select(effect => (LotValueStore.Transitions, new BsonDocument {
            { "$or", new BsonArray { new BsonDocument("availableId", effect["balanceId"]), new BsonDocument("destinationId", effect["balanceId"]) } },
            { "_id", new BsonDocument("$lte", effect["transitionId"]) } }))];
    private static string[] Shape(IEnumerable<(string Collection, BsonDocument Filter)> scopes) =>
        [.. scopes.Select(scope => scope.Collection + "\n" + scope.Filter.ToJson()).Order(StringComparer.Ordinal)];

    /// <summary>False when no key ring or no seal exists, so the caller runs the full proof. A present seal that
    /// fails (MAC, live row hash, membership or scope shape) refuses and is never re-proven around.</summary>
    internal static async Task<bool> VerifyAsync(IMongoDatabase database, IClientSessionHandle tx, BsonDocument row, CancellationToken ct)
    {
        var ring = ProvenanceSealRing.Current;
        if (ring is null) return false;
        var seal = await Seals(database).Find(tx, new BsonDocument("_id", row["_id"])).SingleOrDefaultAsync(ct);
        if (seal is null) return false;
        try { await new SealVerifier(database, ring).VerifyAsync(tx, seal, Kind, row["_id"].AsInt64, row, ct); }
        catch (SealUnavailableException) { throw ShipmentDispatchRecords.Invalid(); }
        var sealedScopes = seal["membership"].AsBsonArray.Select(scope => scope.AsBsonDocument).ToArray();
        if (sealedScopes.Any(scope => !scope.Contains("members")) || seal["dependencies"].AsBsonArray.Count != 0 ||
            !Shape(sealedScopes.Select(scope => (scope["collection"].AsString, scope["filter"].AsBsonDocument))).SequenceEqual(Shape(Scopes(row))))
            throw ShipmentDispatchRecords.Invalid();
        return true;
    }

    /// <summary>Seals after the caller's full proof succeeded. Only an owning operation seals; caller-supplied sessions never write.</summary>
    internal static async Task SealAsync(IMongoDatabase database, IClock clock, IClientSessionHandle tx, BsonDocument row, CancellationToken ct)
    {
        var ring = ProvenanceSealRing.Current;
        if (ring is null || !MongoUnitOfWork.OwnsOperation(tx) || await Seals(database).Find(tx, new BsonDocument("_id", row["_id"])).AnyAsync(ct)) return;
        var scopes = new List<(string Collection, BsonDocument Filter, IReadOnlyList<BsonDocument> Rows)>();
        foreach (var (collection, filter) in Scopes(row)) {
            var members = await database.GetCollection<BsonDocument>(collection).Find(tx, filter).Sort(new BsonDocument("_id", 1)).Limit(MaximumMembers + 1).ToListAsync(ct);
            if (members.Count > MaximumMembers) return; // Too long to seal: this shipment keeps the full proof.
            scopes.Add((collection, filter, members));
        }
        await Seals(database).InsertOneAsync(tx, ProvenanceSeal.Create(ring, Kind, row["_id"].AsInt64, ProvenanceSeal.RowHash(row),
            SealVerifier.Membership(scopes), [], clock.UtcNow), cancellationToken: ct);
    }
}
