using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo;

// Recoverable, ordered deployment leases. DDL is idempotent but MongoDB does not transact it.
internal sealed class MigrationGate(IMongoDatabase database, IClock clock, int currentVersion)
{
    private readonly IMongoCollection<BsonDocument> history = database
        .GetCollection<BsonDocument>("schemaMigrations")
        .WithWriteConcern(WriteConcern.WMajority)
        .WithReadPreference(ReadPreference.Primary)
        .WithReadConcern(ReadConcern.Majority);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    public async Task<bool> IsCurrentAsync(CancellationToken token)
    {
        var records = await ReadHistoryAsync(token);
        return MigrationHistory.IsValid(records, currentVersion) && records.Count == currentVersion &&
            records.All(record => record.GetValue("state", BsonNull.Value) == "applied");
    }

    public async Task<MigrationLease?> AcquireAsync(int version, CancellationToken token)
    {
        if (version < 1 || version > currentVersion)
            throw new ArgumentOutOfRangeException(nameof(version));
        var records = await ReadHistoryAsync(token);
        if (!MigrationHistory.IsValid(records, currentVersion))
            throw new InvalidOperationException("Database has unknown, newer or inconsistent schema migration history; refusing to alter it");
        var existing = records.SingleOrDefault(record => record["_id"].ToInt64() == version);
        if (existing is not null && existing.GetValue("state", BsonNull.Value) == "applied")
            return null;
        if (records.Count(record => record["_id"].ToInt64() < version &&
                record.GetValue("state", BsonNull.Value) == "applied") != version - 1)
            throw new InvalidOperationException("Earlier schema migrations must be applied first");

        var owner = Guid.NewGuid().ToString("N");
        var now = clock.UtcNow.UtcDateTime;
        var record = new BsonDocument
        {
            { "_id", version }, { "state", "applying" },
            { "owner", owner }, { "leaseUntil", now.Add(LeaseDuration) }, { "startedAt", now }
        };
        try
        {
            await history.InsertOneAsync(record, cancellationToken: token);
            return NewLease(version, owner);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
        catch (MongoCommandException ex) when (ex.Code == 11000) { }

        // Another deployment may have completed between the history read and insert.
        existing = await history.Find(Builders<BsonDocument>.Filter.Eq("_id", version))
            .FirstOrDefaultAsync(token);
        if (existing is not null && existing.GetValue("state", BsonNull.Value) == "applied")
            return null;
        var filter = Builders<BsonDocument>.Filter.Eq("_id", version) &
            Builders<BsonDocument>.Filter.Eq("state", "applying") &
            Builders<BsonDocument>.Filter.Lte("leaseUntil", now);
        var claimed = await history.FindOneAndUpdateAsync(filter,
            Builders<BsonDocument>.Update.Set("owner", owner).Set("leaseUntil", now.Add(LeaseDuration)),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, token);
        if (claimed is null)
            throw new PersistenceConflictException("migration_in_progress", "A schema migration is already running");
        return NewLease(version, owner);
    }

    private MigrationLease NewLease(int version, string owner) =>
        new(history, clock, version, currentVersion, owner, LeaseDuration);

    private async Task<List<BsonDocument>> ReadHistoryAsync(CancellationToken token) =>
        await history.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("_id", 1))
            .Limit(currentVersion + 1).ToListAsync(token);

}

internal static class MigrationHistory
{
    public static bool IsValid(IReadOnlyList<BsonDocument> records, int currentVersion)
    {
        if (records.Count > currentVersion) return false;
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (!record.TryGetValue("_id", out var id) || !(id.IsInt32 || id.IsInt64) ||
                id.ToInt64() != i + 1 || id.ToInt64() > currentVersion)
                return false;
            var state = record.GetValue("state", BsonNull.Value);
            if (state != "applied" && state != "applying") return false;
            if (state == "applied" && (!record.TryGetValue("appliedAt", out var appliedAt) || !appliedAt.IsBsonDateTime))
                return false;
            if (state == "applying" && (!record.TryGetValue("owner", out var owner) || !owner.IsString ||
                string.IsNullOrWhiteSpace(owner.AsString) || !record.TryGetValue("leaseUntil", out var leaseUntil) ||
                !leaseUntil.IsBsonDateTime)) return false;
            // An applying record can only be the final known step. No later DDL is allowed.
            if (state == "applying" && i != records.Count - 1) return false;
        }
        return true;
    }
}

internal sealed class MigrationLease(IMongoCollection<BsonDocument> history, IClock clock,
    int version, int currentVersion, string owner, TimeSpan duration)
{
    private FilterDefinition<BsonDocument> ActiveOwned =>
        Owned & Builders<BsonDocument>.Filter.Gt("leaseUntil", clock.UtcNow.UtcDateTime);

    private FilterDefinition<BsonDocument> Owned =>
        Builders<BsonDocument>.Filter.Eq("_id", version) &
        Builders<BsonDocument>.Filter.Eq("state", "applying") &
        Builders<BsonDocument>.Filter.Eq("owner", owner);

    public async Task RenewAsync(CancellationToken token)
    {
        await AssertHistoryAsync(token);
        var result = await history.UpdateOneAsync(ActiveOwned,
            Builders<BsonDocument>.Update.Set("leaseUntil", clock.UtcNow.UtcDateTime.Add(duration)),
            cancellationToken: token);
        if (result.MatchedCount != 1)
            throw new PersistenceConflictException("migration_lock_lost", "Schema migration lock was lost");
    }

    public async Task CompleteAsync(CancellationToken token)
    {
        await AssertHistoryAsync(token);
        var result = await history.UpdateOneAsync(ActiveOwned,
            Builders<BsonDocument>.Update.Set("state", "applied")
                .Set("appliedAt", clock.UtcNow.UtcDateTime)
                .Unset("owner").Unset("leaseUntil"), cancellationToken: token);
        if (result.MatchedCount != 1)
            throw new PersistenceConflictException("migration_lock_lost", "Schema migration lock was lost");
    }

    private async Task AssertHistoryAsync(CancellationToken token)
    {
        var records = await history.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(new BsonDocument("_id", 1)).Limit(currentVersion + 1).ToListAsync(token);
        if (!MigrationHistory.IsValid(records, currentVersion))
            throw new InvalidOperationException("Database has unknown, newer or inconsistent schema migration history; refusing to continue");
    }

    public async Task ReleaseAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await history.UpdateOneAsync(Owned,
            Builders<BsonDocument>.Update.Set("leaseUntil", clock.UtcNow.UtcDateTime),
            cancellationToken: timeout.Token);
    }
}
