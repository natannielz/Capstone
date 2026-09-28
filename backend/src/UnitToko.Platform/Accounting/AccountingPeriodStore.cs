using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Accounting;
// Trusted internal storage boundary only. No public route or approval-writing method exists here.
// T12 must supply reconciled, person-independent approval and current authorization before invoking close.
internal sealed class AccountingPeriodStore(IMongoDatabase database, IClock clock)
{
    private readonly AccountingPersistence persistence = new(database, clock);
    internal async Task<AccountingStorageSnapshot> CaptureAsync(IClientSessionHandle session, OperationalBookKey key, string month, CancellationToken token = default)
    {
        AccountingPersistence.RequireSession(database, session);
        var book = await persistence.BookAsync(session, key, token);
        var period = await persistence.PeriodAsync(session, book["_id"].AsInt64, month, token);
        return await SnapshotAsync(session, book, period, token);
    }

    private async Task<AccountingStorageSnapshot> SnapshotAsync(IClientSessionHandle session, BsonDocument book, BsonDocument period, CancellationToken token)
    {
        using var snapshot = new AccountingSnapshotBuilder(book["_id"].AsInt64, period);
        using var cursor = await persistence.Collection("journalEntries").Find(session, new BsonDocument { { "bookId", book["_id"] }, { "businessDate", new BsonDocument("$lte", period["month"].AsString + "-31") } }, new FindOptions { BatchSize = 64 }).Sort(new BsonDocument { { "businessDate", 1 }, { "_id", 1 } }).ToCursorAsync(token);
        while (await cursor.MoveNextAsync(token))
            foreach (var entry in cursor.Current)
                snapshot.Add(entry);
        return snapshot.Finish();
    }

    internal async Task CloseApprovedAsync(IClientSessionHandle session, ActorContext actor, OperationalBookKey key, string month, AccountingWriteMetadata metadata, CancellationToken token = default)
    {
        try
        {
            AccountingPersistence.RequireSession(database, session);
            AccountingPersistence.ValidateActor(actor, metadata);
            var book = await persistence.BookAsync(session, key, token);
            var period = await persistence.PeriodAsync(session, book["_id"].AsInt64, month, token);
            AccountingPersistence.RequireOpen(book, period, month);
            if (period["state"] != "approved" || period["approvedRevision"] != period["dataRevision"] || !period["lastHistoryId"].IsInt64)
                throw Conflict();
            await persistence.TouchBookAsync(session, book, token);
            var approval = await persistence.Collection("accountingPeriodHistory").Find(session, new BsonDocument("_id", period["lastHistoryId"])).SingleOrDefaultAsync(token);
            if (approval is null || !approval["previousHistoryId"].IsInt64)
                throw Conflict();
            var submission = await persistence.Collection("accountingPeriodHistory").Find(session, new BsonDocument("_id", approval["previousHistoryId"])).SingleOrDefaultAsync(token);
            if (submission is null || !ApprovedHistoryMatches(period, approval, submission) || actor.UserId == approval["actorId"].AsInt64 || actor.UserId == submission["actorId"].AsInt64)
                throw Conflict();
            var snapshot = await SnapshotAsync(session, book, period, token);
            if (snapshot.Hash != period["snapshotHash"].AsString)
                throw Conflict();
            var version = checked(period["version"].AsInt64 + 1);
            var history = await persistence.HistoryAsync(session, period, "closed", snapshot.DataRevision, snapshot.Hash, actor, metadata, token);
            var update = await persistence.Collection("periods").UpdateOneAsync(session, new BsonDocument { { "_id", period["_id"] }, { "version", period["version"] }, { "state", "approved" }, { "dataRevision", snapshot.DataRevision }, { "snapshotHash", snapshot.Hash } }, Builders<BsonDocument>.Update.Set("version", version).Set("state", "closed").Set("lastHistoryId", history), cancellationToken: token);
            if (update.ModifiedCount != 1)
                throw Conflict();
            await persistence.Collection("accountingBooks").UpdateOneAsync(session, new BsonDocument("_id", book["_id"]), Builders<BsonDocument>.Update.Set("closedThrough", month), cancellationToken: token);
            await persistence.AuditAsync(session, "period.closed", $"accounting:period:{period["_id"].AsInt64}:{version}", book["_id"].AsInt64, null, period["_id"].AsInt64, snapshot.Hash, actor, metadata, token);
        }
        catch
        {
            await AccountingPersistence.AbortAsync(session);
            throw;
        }
    }

    internal static bool ApprovedHistoryMatches(BsonDocument period, BsonDocument approval, BsonDocument submission) => approval["stage"] == "approved" && submission["stage"] == "submitted" && approval["periodId"] == period["_id"] && submission["periodId"] == period["_id"] && approval["bookId"] == period["bookId"] && submission["bookId"] == period["bookId"] && approval["month"] == period["month"] && submission["month"] == period["month"] && approval["version"] == period["version"] && submission["version"].AsInt64 + 1 == approval["version"].AsInt64 && approval["dataRevision"] == period["dataRevision"] && submission["dataRevision"] == period["dataRevision"] && approval["snapshotHash"] == period["snapshotHash"] && submission["snapshotHash"] == period["snapshotHash"] && approval["actorId"] != submission["actorId"] && submission["createdAt"].ToUniversalTime() <= approval["createdAt"].ToUniversalTime();
    private static PersistenceConflictException Conflict() => new("accounting_approval_stale", "A matching approved storage snapshot and stage history are required");
}
