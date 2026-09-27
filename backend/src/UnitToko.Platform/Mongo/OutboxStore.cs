using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;

namespace UnitToko.Platform.Mongo;

public sealed class OutboxStore(IMongoDatabase database, IClock clock)
{
    private readonly IMongoCollection<BsonDocument> events = database.GetCollection<BsonDocument>("outboxEvents");
    private readonly IMongoCollection<BsonDocument> durableEvents =
        database.GetCollection<BsonDocument>("outboxEvents").WithWriteConcern(WriteConcern.WMajority);

    public async Task EnqueueAsync(IClientSessionHandle session, string sourceKey, string kind,
        BsonDocument payload, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || sourceKey.Length > 200 ||
            string.IsNullOrWhiteSpace(kind) || kind.Length > 100 || payload.ToBson().Length > 32_768)
            throw new DomainValidationException("invalid_outbox", "Outbox event is invalid or too large");
        await events.InsertOneAsync(session, new BsonDocument
        {
            { "schemaVersion", CollectionMigrations.CurrentVersion }, { "sourceKey", sourceKey }, { "kind", kind }, { "payload", payload },
            { "status", "pending" }, { "attempts", 0 },
            { "dueAt", clock.UtcNow.UtcDateTime }, { "createdAt", clock.UtcNow.UtcDateTime }
        }, cancellationToken: cancellationToken);
    }

    public async Task<BsonDocument?> ClaimAsync(string consumerId, TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(consumerId) || consumerId.Length > 128 ||
            lease < TimeSpan.FromSeconds(1) || lease > TimeSpan.FromHours(1))
            throw new DomainValidationException("invalid_lease", "Consumer and lease must be bounded");
        var now = clock.UtcNow.UtcDateTime;
        var filter = Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.In("status", new[] { "pending", "retry" }),
                Builders<BsonDocument>.Filter.Lte("dueAt", now)),
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("status", "leased"),
                Builders<BsonDocument>.Filter.Lte("leaseUntil", now)));
        return await durableEvents.FindOneAndUpdateAsync(filter,
            Builders<BsonDocument>.Update.Set("status", "leased")
                .Set("leaseOwner", consumerId)
                .Set("leaseToken", Guid.NewGuid().ToString("N"))
                .Set("leaseUntil", now.Add(lease))
                .Inc("attempts", 1),
            new FindOneAndUpdateOptions<BsonDocument>
            { Sort = Builders<BsonDocument>.Sort.Ascending("dueAt"), ReturnDocument = ReturnDocument.After },
            cancellationToken);
    }

    public async Task<bool> CompleteAsync(BsonDocument claim, CancellationToken cancellationToken = default) =>
        await TransitionAsync(claim, Builders<BsonDocument>.Update.Set("status", "completed")
            .Set("completedAt", clock.UtcNow.UtcDateTime).Unset("leaseOwner").Unset("leaseToken")
            .Unset("leaseUntil"), cancellationToken);

    public async Task<bool> RetryAsync(BsonDocument claim, TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromDays(1))
            throw new DomainValidationException("invalid_retry", "Retry delay must be bounded");
        return await TransitionAsync(claim, Builders<BsonDocument>.Update.Set("status", "retry")
            .Set("dueAt", clock.UtcNow.UtcDateTime.Add(delay)).Unset("leaseOwner")
            .Unset("leaseToken").Unset("leaseUntil"), cancellationToken);
    }

    private async Task<bool> TransitionAsync(BsonDocument claim, UpdateDefinition<BsonDocument> update,
        CancellationToken cancellationToken)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", claim["_id"]) &
            Builders<BsonDocument>.Filter.Eq("status", "leased") &
            Builders<BsonDocument>.Filter.Eq("leaseToken", claim["leaseToken"].AsString) &
            Builders<BsonDocument>.Filter.Gt("leaseUntil", clock.UtcNow.UtcDateTime);
        var result = await durableEvents.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);
        return result.ModifiedCount == 1;
    }
}
