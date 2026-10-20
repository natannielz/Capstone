using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Billing;

// This is only a transaction-bound integrity fence. Owning services must authorize the
// real caller and authenticate every row before retaining it here. No DTO contains BSON.
internal sealed partial class OrderTimelineHistory(IMongoDatabase database, IClientSessionHandle tx)
{
    private readonly Dictionary<(string Collection, BsonValue Id), BsonDocument> held = [];
    private sealed record Membership(string Collection, BsonDocument Filter, BsonValue[] Ids);
    private readonly List<Membership> memberships = [];
    internal static PersistenceConflictException Broken() => new("order_timeline_history_unavailable", "The recorded transaction history is incomplete or changed");
    internal static void Equal(BsonValue actual, BsonValue expected) { if (!InvoiceRecords.Equal(actual, expected)) throw Broken(); }
    internal void Hold(string collection, BsonDocument row) {
        if (!tx.IsInTransaction || !ReferenceEquals(database.Client, tx.Client) || !row.Contains("_id")) throw Broken();
        var key = (collection, row["_id"]);
        if (held.TryGetValue(key, out var old)) Equal(old, row);
        else held.Add(key, row.DeepClone().AsBsonDocument);
    }
    internal async Task<BsonDocument> HoldAsync(string collection, BsonDocument filter, CancellationToken ct) {
        var row = await database.GetCollection<BsonDocument>(collection).Find(tx, filter).SingleOrDefaultAsync(ct) ?? throw Broken();
        RecordMembership(collection, filter, [row]);
        Hold(collection, row); return row;
    }
    internal async Task<List<BsonDocument>> CaptureAsync(string collection, BsonDocument filter, CancellationToken ct) {
        var rows = await database.GetCollection<BsonDocument>(collection).Find(tx, filter).ToListAsync(ct);
        RecordMembership(collection, filter, rows); return rows;
    }
    private void RecordMembership(string collection, BsonDocument filter, IEnumerable<BsonDocument> rows) {
        var ids = rows.Select(row => row["_id"].DeepClone()).Order().ToArray();
        var prior = memberships.SingleOrDefault(value => value.Collection == collection && value.Filter == filter);
        if (prior is not null) { if (!prior.Ids.SequenceEqual(ids)) throw Broken(); }
        else memberships.Add(new(collection, filter.DeepClone().AsBsonDocument, ids));
    }
    // Owner readers have already checked the exact shapes, hashes, results and original
    // actor/person proof. Preserve those same committed effects through the final await.
    internal async Task EffectsAsync(BsonDocument row, string operation, string actorField, CancellationToken ct,
        string? sourceKey = null, bool outbox = true) {
        await HoldAsync("auditEvents", new("_id", row["auditId"]), ct);
        await HoldAsync("idempotencyRecords", new() { { "actorId", row[actorField] }, { "operation", operation }, { "key", row["requestKey"] } }, ct);
        if (outbox) await HoldAsync("outboxEvents", new("sourceKey", sourceKey ?? operation + ":" + row["_id"]), ct);
        if (row.TryGetValue("journalId", out var journal) && !journal.IsBsonNull)
            await HoldAsync("journalEntries", new("_id", journal), ct);
    }
    internal async Task RequireEffectIdsAsync(string operation, string association, BsonValue id,
        string resultId, IEnumerable<long> expectedIds, CancellationToken ct) {
        var expected = expectedIds.Order().ToArray();
        foreach (var (name, kind, prefix) in new[] { ("auditEvents", "kind", "result"), ("idempotencyRecords", "operation", "result"), ("outboxEvents", "kind", "payload") }) {
            var rows = await CaptureAsync(name, new BsonDocument { { kind, operation }, { prefix + "." + association, id } }, ct);
            if (!rows.Select(row => row[prefix][resultId].AsInt64).Order().SequenceEqual(expected)) throw Broken();
            foreach (var row in rows) Hold(name, row);
        }
    }
    internal async Task<long[]> RequireMembersAsync(string collection, BsonDocument filter, CancellationToken ct) {
        var rows = await CaptureAsync(collection, filter, ct);
        foreach (var row in rows) {
            if (!held.TryGetValue((collection, row["_id"]), out var authenticated)) throw Broken();
            Equal(row, authenticated);
        }
        return rows.Select(row => row["_id"].AsInt64).Order().ToArray();
    }
    internal async Task FenceAsync(CancellationToken ct) {
        // Replay both positive and empty predicates. The committed check detects a
        // warmed orphan insertion outside this transaction's snapshot; exact row
        // writes below retain authenticated positive dependencies. Ordinary owner
        // writes also serialize on the declaration/case/account heads fenced here.
        foreach (var membership in memberships) {
            var rows = database.GetCollection<BsonDocument>(membership.Collection);
            var snapshot = await rows.Find(tx, membership.Filter).Project(new BsonDocument("_id", 1)).ToListAsync(ct);
            var committed = await rows.WithReadConcern(ReadConcern.Majority).Find(membership.Filter).Project(new BsonDocument("_id", 1)).ToListAsync(ct);
            if (!snapshot.Select(row => row["_id"]).Order().SequenceEqual(membership.Ids) ||
                !committed.Select(row => row["_id"]).Order().SequenceEqual(membership.Ids)) throw Broken();
        }
        foreach (var ((collection, _), row) in held) {
            var rows = database.GetCollection<BsonDocument>(collection);
            // Exact immutable replacement creates a real write conflict even for rows
            // whose closed schema has no mutable fence. All business bytes stay identical.
            if ((await rows.DeleteOneAsync(tx, row, cancellationToken: ct)).DeletedCount != 1) throw Broken();
            await rows.InsertOneAsync(tx, row, cancellationToken: ct);
        }
    }
}
