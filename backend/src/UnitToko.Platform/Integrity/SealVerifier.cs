using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Integrity;

/// <summary>Verifies a provenance seal against live rows: MAC, record hash and exact proof membership.
/// A passing verification is memoized only inside the owning unit-of-work operation's transaction attempt.</summary>
public sealed class SealVerifier(IMongoDatabase database, SealKeyRing ring)
{
    private const int MaximumMembers = ProvenanceSeal.MaximumMembers;
    private static readonly ConditionalWeakTable<IClientSessionHandle, Dictionary<string, IReadOnlyList<BsonDocument>>> verified = new();

    public static BsonArray Membership(IEnumerable<(string Collection, BsonDocument Filter, IReadOnlyList<BsonDocument> Rows)> scopes,
        IEnumerable<(string Collection, BsonDocument Filter, long Value)>? counts = null) =>
        new(scopes.OrderBy(scope => scope.Collection, StringComparer.Ordinal).ThenBy(scope => scope.Filter.ToJson(), StringComparer.Ordinal)
            .Select(scope => new BsonDocument {
                { "collection", scope.Collection }, { "filter", scope.Filter.DeepClone() },
                { "members", new BsonArray(scope.Rows.OrderBy(row => row["_id"]).Select(row => new BsonDocument {
                    { "id", row["_id"] }, { "hash", ProvenanceSeal.MemberHash(scope.Collection, row) } })) } })
            .Concat((counts ?? []).OrderBy(count => count.Collection, StringComparer.Ordinal).ThenBy(count => count.Filter.ToJson(), StringComparer.Ordinal)
                .Select(count => new BsonDocument { { "collection", count.Collection }, { "filter", count.Filter.DeepClone() }, { "count", count.Value } })));

    /// <summary>Returns, per membership scope in seal order, the verified live rows as {collection, filter, rows}.</summary>
    public async Task<IReadOnlyList<BsonDocument>> VerifyAsync(IClientSessionHandle tx, BsonDocument seal, string kind, long ownerId,
        BsonDocument liveRecord, CancellationToken ct)
    {
        // Every refusal of a present seal raises an integrity alert naming the failing check (spec 3.3).
        SealUnavailableException Mismatch(string check) { SealIntegrityAlerts.Raise(kind, ownerId, check); return new("seal_mismatch"); }
        if (!tx.IsInTransaction || !ProvenanceSeal.MacValid(ring, seal) || seal["kind"] != kind || seal["ownerId"] != ownerId) throw Mismatch("seal");
        if (seal["recordHash"] != ProvenanceSeal.RowHash(liveRecord)) throw Mismatch("record");
        var memo = MongoUnitOfWork.OwnsOperation(tx) ? tx.WrappedCoreSession.CurrentTransaction.TransactionNumber + ":" + seal["mac"].AsString : null;
        if (memo is not null && verified.TryGetValue(tx, out var done)) lock (done) if (done.TryGetValue(memo, out var known)) return known;
        var result = new List<BsonDocument>();
        foreach (var scope in seal["membership"].AsBsonArray.Select(value => value.AsBsonDocument)) {
            if (scope.Contains("count")) {
                if (await database.GetCollection<BsonDocument>(scope["collection"].AsString).CountDocumentsAsync(tx, scope["filter"].AsBsonDocument, cancellationToken: ct) != scope["count"].AsInt64)
                    throw Mismatch("membership:" + scope["collection"].AsString);
                result.Add(new BsonDocument { { "collection", scope["collection"] }, { "filter", scope["filter"] }, { "count", scope["count"] } });
                continue;
            }
            var actual = await database.GetCollection<BsonDocument>(scope["collection"].AsString).Find(tx, scope["filter"].AsBsonDocument)
                .Sort(new BsonDocument("_id", 1)).Limit(MaximumMembers + 1).ToListAsync(ct);
            var expected = scope["members"].AsBsonArray;
            if (actual.Count != expected.Count ||
                actual.Where((row, index) => row["_id"] != expected[index]["id"] || ProvenanceSeal.MemberHash(scope["collection"].AsString, row) != expected[index]["hash"].AsString).Any())
                throw Mismatch("membership:" + scope["collection"].AsString);
            result.Add(new BsonDocument { { "collection", scope["collection"] }, { "filter", scope["filter"] }, { "rows", new BsonArray(actual) } });
        }
        if (memo is not null) { var table = verified.GetValue(tx, _ => []); lock (table) table[memo] = result; }
        return result;
    }
}
