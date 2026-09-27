using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Mongo;

// A recoverable deployment lock. DDL itself is idempotent but MongoDB does not transact it.
internal sealed class MigrationGate(IMongoDatabase database, IClock clock)
{
    private readonly IMongoCollection<BsonDocument> history = database.GetCollection<BsonDocument>("schemaMigrations").WithWriteConcern(WriteConcern.WMajority);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    public async Task<bool> IsCurrentAsync(CancellationToken token)
    {
        if (await HasNewerAsync(token)) return false;
        var record = await history.Find(Builders<BsonDocument>.Filter.Eq("_id", CollectionMigrations.CurrentVersion))
            .FirstOrDefaultAsync(token);
        return record is not null && record.TryGetValue("state", out var state) && state == "applied";
    }

    public async Task<MigrationLease?> AcquireAsync(CancellationToken token)
    {
        if (await HasNewerAsync(token))
            throw new InvalidOperationException("Database has an unknown newer schema migration; refusing to alter it");
        var owner = Guid.NewGuid().ToString("N");
        var now = clock.UtcNow.UtcDateTime;
        var record = new BsonDocument
        {
            { "_id", CollectionMigrations.CurrentVersion }, { "state", "applying" },
            { "owner", owner }, { "leaseUntil", now.Add(LeaseDuration) }, { "startedAt", now }
        };
        try
        {
            await history.InsertOneAsync(record, cancellationToken: token);
            return new MigrationLease(history, clock, owner, LeaseDuration);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
        catch (MongoCommandException ex) when (ex.Code == 11000) { }

        var existing = await history.Find(Builders<BsonDocument>.Filter.Eq("_id", CollectionMigrations.CurrentVersion))
            .FirstOrDefaultAsync(token);
        if (existing is not null && existing.GetValue("state", BsonNull.Value) == "applied")
            return null;
        var filter = Builders<BsonDocument>.Filter.Eq("_id", CollectionMigrations.CurrentVersion) &
            Builders<BsonDocument>.Filter.Eq("state", "applying") &
            Builders<BsonDocument>.Filter.Lte("leaseUntil", now);
        var claimed = await history.FindOneAndUpdateAsync(filter,
            Builders<BsonDocument>.Update.Set("owner", owner).Set("leaseUntil", now.Add(LeaseDuration)),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, token);
        if (claimed is null)
            throw new PersistenceConflictException("migration_in_progress", "A schema migration is already running");
        return new MigrationLease(history, clock, owner, LeaseDuration);
    }

    private Task<bool> HasNewerAsync(CancellationToken token) =>
        history.Find(Builders<BsonDocument>.Filter.Gt("_id", CollectionMigrations.CurrentVersion))
            .AnyAsync(token);
}

internal sealed class MigrationLease(IMongoCollection<BsonDocument> history, IClock clock,
    string owner, TimeSpan duration)
{
    private FilterDefinition<BsonDocument> ActiveOwned =>
        Owned & Builders<BsonDocument>.Filter.Gt("leaseUntil", clock.UtcNow.UtcDateTime);

    private FilterDefinition<BsonDocument> Owned =>
        Builders<BsonDocument>.Filter.Eq("_id", CollectionMigrations.CurrentVersion) &
        Builders<BsonDocument>.Filter.Eq("state", "applying") &
        Builders<BsonDocument>.Filter.Eq("owner", owner);

    public async Task RenewAsync(CancellationToken token)
    {
        await AssertNoNewerAsync(token);
        var result = await history.UpdateOneAsync(ActiveOwned,
            Builders<BsonDocument>.Update.Set("leaseUntil", clock.UtcNow.UtcDateTime.Add(duration)),
            cancellationToken: token);
        if (result.MatchedCount != 1)
            throw new PersistenceConflictException("migration_lock_lost", "Schema migration lock was lost");
    }

    public async Task CompleteAsync(CancellationToken token)
    {
        await AssertNoNewerAsync(token);
        var result = await history.UpdateOneAsync(ActiveOwned,
            Builders<BsonDocument>.Update.Set("state", "applied")
                .Set("appliedAt", clock.UtcNow.UtcDateTime)
                .Unset("owner").Unset("leaseUntil"), cancellationToken: token);
        if (result.MatchedCount != 1)
            throw new PersistenceConflictException("migration_lock_lost", "Schema migration lock was lost");
    }

    private async Task AssertNoNewerAsync(CancellationToken token)
    {
        if (await history.Find(Builders<BsonDocument>.Filter.Gt("_id", CollectionMigrations.CurrentVersion)).AnyAsync(token))
            throw new InvalidOperationException("Database has an unknown newer schema migration; refusing to continue");
    }

    public async Task ReleaseAsync()
    {
        await history.UpdateOneAsync(Owned,
            Builders<BsonDocument>.Update.Set("leaseUntil", clock.UtcNow.UtcDateTime),
            cancellationToken: CancellationToken.None);
    }
}
