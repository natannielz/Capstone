using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Mongo;

namespace UnitToko.Platform.Accounting;
internal sealed class AccountingPersistence(IMongoDatabase database, IClock clock)
{
    private readonly IdAllocator ids = new(database);
    internal IMongoCollection<BsonDocument> Collection(string name) => database.GetCollection<BsonDocument>(name);
    internal static void RequireSession(IMongoDatabase database, IClientSessionHandle session)
    {
        if (session is null || !session.IsInTransaction || !ReferenceEquals(session.Client, database.Client))
            throw new InvalidOperationException("Accounting requires the owning database client and an active caller transaction");
    }

    internal static void ValidateActor(ActorContext actor, AccountingWriteMetadata metadata)
    {
        if (actor is null || metadata is null)
            throw AccountingValidation.Invalid("invalid_accounting_actor");
        actor.Validate();
        foreach (var(value, limit)in new[]
        {
            (metadata.Reason, 2000),
            (metadata.CorrelationId, 128)
        }

        )
            if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value != value.Trim() || value.Any(char.IsControl))
                throw AccountingValidation.Invalid("invalid_accounting_metadata");
    }

    internal static async Task AbortAsync(IClientSessionHandle session)
    {
        if (session is null || !session.IsInTransaction)
            return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await session.AbortTransactionAsync(cleanup.Token);
        }
        catch (MongoException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task<BsonDocument> BookAsync(IClientSessionHandle session, OperationalBookKey key, CancellationToken token)
    {
        key.Validate();
        var row = await Collection("accountingBooks").Find(session, new BsonDocument { { "canonicalKey", key.CanonicalKey }, { "active", true } }).SingleOrDefaultAsync(token);
        if (row is null || AccountingBson.ReadKey(row["key"]) != key)
            throw AccountingValidation.Invalid("accounting_book_unavailable");
        return row;
    }

    internal async Task TouchBookAsync(IClientSessionHandle session, BsonDocument book, CancellationToken token)
    {
        var changed = await Collection("accountingBooks").UpdateOneAsync(session, new BsonDocument { { "_id", book["_id"] }, { "version", book["version"] }, { "chartVersionId", book["chartVersionId"] }, { "fence", book["fence"] }, { "active", true } }, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: token);
        if (changed.ModifiedCount != 1)
            throw new PersistenceConflictException("accounting_book_changed", "Accounting book changed");
    }

    internal async Task<BsonDocument> PeriodAsync(IClientSessionHandle session, long bookId, string month, CancellationToken token)
    {
        AccountingCalendar.ValidateMonth(month);
        var period = await Collection("periods").Find(session, new BsonDocument { { "bookId", bookId }, { "month", month } }).SingleOrDefaultAsync(token);
        if (period is null)
            throw AccountingValidation.Invalid("accounting_period_unconfigured");
        return period;
    }

    internal static void RequireOpen(BsonDocument book, BsonDocument period, string month)
    {
        if (!book["closedThrough"].IsBsonNull && string.CompareOrdinal(month, book["closedThrough"].AsString) <= 0 || period["state"] == "closed")
            throw new PersistenceConflictException("accounting_period_closed", "Accounting period is closed");
    }

    internal async Task AuditAsync(IClientSessionHandle session, string kind, string eventKey, long bookId, long? journalId, long? periodId, string? fingerprint, ActorContext actor, AccountingWriteMetadata metadata, CancellationToken token)
    {
        var id = (await ids.NextAsync("accountingAuditEvents", token)).Value;
        await Collection("accountingAuditEvents").InsertOneAsync(session, new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "bookId", bookId }, { "kind", kind }, { "eventKey", eventKey }, { "journalId", AccountingBson.Optional(journalId) }, { "periodId", AccountingBson.Optional(periodId) }, { "fingerprint", fingerprint is null ? BsonNull.Value : new BsonString(fingerprint) }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "createdAt", clock.UtcNow.UtcDateTime }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId } }, cancellationToken: token);
        await new OutboxStore(database, clock).EnqueueAsync(session, eventKey, kind, new BsonDocument { { "bookId", bookId }, { "journalId", AccountingBson.Optional(journalId) }, { "periodId", AccountingBson.Optional(periodId) }, { "auditId", id } }, token);
    }

    internal async Task<long> HistoryAsync(IClientSessionHandle session, BsonDocument period, string stage, long revision, string? hash, ActorContext actor, AccountingWriteMetadata metadata, CancellationToken token)
    {
        var id = (await ids.NextAsync("accountingPeriodHistory", token)).Value;
        await Collection("accountingPeriodHistory").InsertOneAsync(session, new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "bookId", period["bookId"] }, { "periodId", period["_id"] }, { "month", period["month"] }, { "version", checked(period["version"].AsInt64 + 1) }, { "dataRevision", revision }, { "stage", stage }, { "previousHistoryId", period["lastHistoryId"] }, { "snapshotHash", hash is null ? BsonNull.Value : new BsonString(hash) }, { "actorId", actor.UserId }, { "actorGrantVersion", actor.GrantVersion }, { "createdAt", clock.UtcNow.UtcDateTime }, { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId } }, cancellationToken: token);
        return id;
    }

    internal async Task InvalidateAsync(IClientSessionHandle session, long bookId, string month, ActorContext actor, AccountingWriteMetadata metadata, CancellationToken token)
    {
        var periods = await Collection("periods").Find(session, new BsonDocument { { "bookId", bookId }, { "month", new BsonDocument("$gte", month) } }).Sort(new BsonDocument("month", 1)).ToListAsync(token);
        foreach (var period in periods)
        {
            if (period["state"] == "closed")
                throw new PersistenceConflictException("accounting_period_closed", "A later cumulative period is closed");
            var revision = checked(period["dataRevision"].AsInt64 + 1);
            var version = checked(period["version"].AsInt64 + 1);
            var history = await HistoryAsync(session, period, "invalidated", revision, null, actor, metadata, token);
            var result = await Collection("periods").UpdateOneAsync(session, new BsonDocument { { "_id", period["_id"] }, { "version", period["version"] } }, Builders<BsonDocument>.Update.Set("version", version).Set("dataRevision", revision).Set("state", "open").Set("approvedRevision", BsonNull.Value).Set("snapshotHash", BsonNull.Value).Set("lastHistoryId", history), cancellationToken: token);
            if (result.ModifiedCount != 1)
                throw new PersistenceConflictException("accounting_period_changed", "Period changed");
            await AuditAsync(session, "period.invalidated", $"accounting:period:{period["_id"].AsInt64}:{version}", bookId, null, period["_id"].AsInt64, null, actor, metadata, token);
        }
    }
}
