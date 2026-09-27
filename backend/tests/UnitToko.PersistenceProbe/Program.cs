using System.Globalization;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Mongo;

// This is a disposable integration-test process, never a deployable worker.
// It has no configurable destination and can only write to this local test namespace.
if (args.Length != 3 ||
    !Regex.IsMatch(args[0], "\\Aunittoko_test_[a-f0-9]{32}_outboxprocess\\z", RegexOptions.CultureInvariant) ||
    !DateTimeOffset.TryParseExact(args[1], "O", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var now) ||
    args[2] is not ("crash-window" or "complete"))
{
    Console.Error.WriteLine("Expected a disposable test database, fixed clock, and bounded probe mode.");
    return 2;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var client = new MongoClient("mongodb://127.0.0.1:27028/?replicaSet=rs0&serverSelectionTimeoutMS=5000&connectTimeoutMS=5000&appName=UnitToko.PersistenceProbe");
var database = client.GetDatabase(args[0]);
var outbox = new OutboxStore(database, new ProbeClock(now));
var claim = await outbox.ClaimAsync("probe-" + Environment.ProcessId, TimeSpan.FromSeconds(30), timeout.Token);
if (claim is null) throw new InvalidOperationException("The expected test event was not claimable.");

// A durable, simulated downstream service: sourceKey is its idempotency key.
// Its one atomic upsert records an effect once while counting every delivery.
// This deliberately models at-least-once delivery, not exactly-once transport.
var receipts = database.GetCollection<BsonDocument>("probeDownstreamReceipts")
    .WithWriteConcern(WriteConcern.WMajority);
var receipt = await receipts.FindOneAndUpdateAsync(
    new BsonDocument("_id", claim["sourceKey"]),
    Builders<BsonDocument>.Update
        .SetOnInsert("effectCount", 1)
        .SetOnInsert("payload", claim["payload"])
        .Inc("deliveryAttempts", 1),
    new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
    timeout.Token);
if (!receipt["payload"].Equals(claim["payload"]))
    throw new InvalidOperationException("A source key was reused for different payloads.");

Console.WriteLine("DOWNSTREAM_ACKNOWLEDGED");
await Console.Out.FlushAsync(timeout.Token);
if (args[2] == "crash-window")
    await Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token);

if (!await outbox.CompleteAsync(claim, timeout.Token))
    throw new InvalidOperationException("The current lease could not complete.");
Console.WriteLine("OUTBOX_COMPLETED");
return 0;

internal sealed class ProbeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}
