using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Events;

namespace UnitToko.Platform.Mongo;

/// <summary>
/// Reuse of a repeated read inside one transaction attempt. A transaction reads a single snapshot, so the same read
/// returns the same answer until the transaction itself writes. A driver command listener counts every write per
/// (server session, transaction number); a remembered result is reused only while that count is unchanged, and only on
/// clients configured with <see cref="Track"/>. Commit or abort forgets the attempt. Checks stay as strict as before:
/// the first read in each window runs in full, a failed read is never remembered, and any write starts a new window.
/// </summary>
public static class TransactionReadMemo
{
    private sealed class Attempt
    {
        public long Writes, Window = -1;
        public DateTime Touched = DateTime.UtcNow;
        public readonly Dictionary<string, object?> Values = new(StringComparer.Ordinal);
    }

    private static readonly ConcurrentDictionary<ClusterId, byte> Tracked = new();
    private static readonly ConcurrentDictionary<(string Session, long Transaction), Attempt> Attempts = new();
    private static readonly HashSet<string> WriteCommands = new(StringComparer.Ordinal) {
        "insert", "update", "delete", "findAndModify", "bulkWrite", "create", "createIndexes", "drop", "dropIndexes", "collMod", "renameCollection",
    };

    /// <summary>Diagnostics for latency probes: the key, whether it was reused, and the write window it was asked in.</summary>
    internal static Action<string, bool, long>? Observed { get; set; }

    /// <summary>Counts writes for every transaction on this client so its reads may be reused.</summary>
    public static void Track(ClusterBuilder builder)
    {
        builder.Subscribe<ClusterOpeningEvent>(opening => Tracked.TryAdd(opening.ClusterId, 0));
        builder.Subscribe<CommandStartedEvent>(Observe);
    }

    /// <summary>The remembered result of <paramref name="read"/> for <paramref name="key"/> in this transaction attempt, or a fresh
    /// read. <paramref name="copy"/> keeps callers from changing a remembered value (required for mutable results).</summary>
    public static async Task<T> GetOrAddAsync<T>(IClientSessionHandle session, string key, Func<Task<T>> read, Func<T, T>? copy = null)
    {
        if (!session.IsInTransaction || !Tracked.ContainsKey(session.Client.Cluster.ClusterId)) return await read();
        var attempt = Attempts.GetOrAdd((Key(session.WrappedCoreSession.Id), session.WrappedCoreSession.CurrentTransaction.TransactionNumber), _ => new Attempt());
        long window;
        lock (attempt)
        {
            attempt.Touched = DateTime.UtcNow;
            if (attempt.Window != attempt.Writes) { attempt.Values.Clear(); attempt.Window = attempt.Writes; }
            if (attempt.Values.TryGetValue(key, out var found)) { Observed?.Invoke(key, true, attempt.Window); return copy is null ? (T)found! : copy((T)found!); }
            window = attempt.Window;
        }
        Observed?.Invoke(key, false, window);
        var value = await read();
        lock (attempt)
            if (attempt.Writes == window && attempt.Window == window) attempt.Values[key] = copy is null ? value : copy(value);
        Prune();
        return value;
    }

    /// <summary>A single-row read remembered by its exact filter; each caller receives its own copy.</summary>
    public static Task<BsonDocument?> OneAsync(IMongoCollection<BsonDocument> rows, IClientSessionHandle session, BsonDocument filter, CancellationToken ct) =>
        GetOrAddAsync<BsonDocument?>(session, "one:" + Name(rows) + ":" + filter.ToJson(),
            async () => (BsonDocument?)await rows.Find(session, filter).SingleOrDefaultAsync(ct), Copy);

    /// <summary>A single-row read remembered by <paramref name="shape"/>, which must name the filter exactly (for example a schema
    /// family and an id); the filter, often a large schema predicate, is only built when the read actually runs.</summary>
    public static Task<BsonDocument?> RowAsync(IMongoCollection<BsonDocument> rows, IClientSessionHandle session, string shape, Func<FilterDefinition<BsonDocument>> filter,
        CancellationToken ct) =>
        GetOrAddAsync<BsonDocument?>(session, "row:" + Name(rows) + ":" + shape,
            async () => (BsonDocument?)await rows.Find(session, filter()).SingleOrDefaultAsync(ct), Copy);

    /// <summary>An existence check remembered by <paramref name="shape"/>, which must name the filter exactly.</summary>
    public static Task<bool> AnyAsync(IMongoCollection<BsonDocument> rows, IClientSessionHandle session, string shape, Func<FilterDefinition<BsonDocument>> filter,
        CancellationToken ct) =>
        GetOrAddAsync(session, "any:" + Name(rows) + ":" + shape, () => rows.Find(session, filter()).AnyAsync(ct));

    private static string Name(IMongoCollection<BsonDocument> rows) => rows.Database.DatabaseNamespace.DatabaseName + "." + rows.CollectionNamespace.CollectionName;
    private static BsonDocument? Copy(BsonDocument? value) => value?.DeepClone().AsBsonDocument;

    private static void Observe(CommandStartedEvent started)
    {
        Tracked.TryAdd(started.ConnectionId.ServerId.ClusterId, 0);
        var command = started.Command;
        // Every command inside a transaction carries autocommit:false; retryable writes outside one do not.
        if (!command.Contains("autocommit") || !command.TryGetValue("lsid", out var lsid) || !command.TryGetValue("txnNumber", out var number)) return;
        var key = (Key(lsid.AsBsonDocument), number.ToInt64());
        if (started.CommandName is "commitTransaction" or "abortTransaction") { Attempts.TryRemove(key, out _); return; }
        if (!WriteCommands.Contains(started.CommandName)) return;
        var attempt = Attempts.GetOrAdd(key, _ => new Attempt());
        lock (attempt) { attempt.Writes++; attempt.Touched = DateTime.UtcNow; }
    }

    private static string Key(BsonDocument sessionId) => Convert.ToHexString(sessionId["id"].AsBsonBinaryData.Bytes);

    // An attempt whose session vanished without commit or abort is dropped once it has been idle for ten minutes.
    private static void Prune()
    {
        if (Attempts.Count < 4096) return;
        var stale = DateTime.UtcNow.AddMinutes(-10);
        foreach (var (key, attempt) in Attempts) if (attempt.Touched < stale) Attempts.TryRemove(key, out _);
    }
}
