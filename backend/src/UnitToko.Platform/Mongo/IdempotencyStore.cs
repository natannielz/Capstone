using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Platform.Mongo;

public readonly record struct IdempotencyScope(string ActorId, string Operation, string Key, string BodyHash)
{
    public static IdempotencyScope ForActor(ActorContext actor, string operation, string key, string bodyHash) =>
        new(actor.Validate().UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), operation, key, bodyHash);

    public long ActorIdValue => DomainValues.ParseId(ActorId);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ActorId) || ActorId.Length > 20 ||
            string.IsNullOrWhiteSpace(Operation) || Operation.Length > 128 ||
            string.IsNullOrWhiteSpace(Key) || Key.Length > 200 ||
            BodyHash is null || BodyHash.Length != 64 || !BodyHash.All(Uri.IsHexDigit))
            throw new DomainValidationException("invalid_idempotency", "Invalid idempotency scope or SHA-256 body hash");
        var id = DomainValues.ParseId(ActorId);
        if (id.ToString(System.Globalization.CultureInfo.InvariantCulture) != ActorId)
            throw new DomainValidationException("invalid_idempotency", "Actor ID must be a canonical positive Int64 string");
    }
}

public sealed class IdempotencyStore(IMongoDatabase database, MongoUnitOfWork unitOfWork, IClock clock)
{
    private readonly IMongoCollection<BsonDocument> records = database.GetCollection<BsonDocument>("idempotencyRecords");

    public async Task<BsonDocument> ExecuteAsync(IdempotencyScope scope,
        Func<IClientSessionHandle, CancellationToken, Task<BsonDocument>> businessWrite,
        CancellationToken cancellationToken = default)
    {
        scope.Validate();
        var filter = Builders<BsonDocument>.Filter.Eq("actorId", scope.ActorIdValue) &
            Builders<BsonDocument>.Filter.Eq("operation", scope.Operation) &
            Builders<BsonDocument>.Filter.Eq("key", scope.Key);
        return await unitOfWork.ExecuteAsync(async (session, token) =>
        {
            var existing = await records.Find(session, filter).FirstOrDefaultAsync(token);
            if (existing is not null)
            {
                if (!string.Equals(existing["bodyHash"].AsString, scope.BodyHash, StringComparison.OrdinalIgnoreCase))
                    throw new PersistenceConflictException("idempotency_key_reused", "Idempotency key was used with a different request");
                return existing["result"].AsBsonDocument.DeepClone().AsBsonDocument;
            }
            var record = new BsonDocument
            {
                { "schemaVersion", CollectionMigrations.FoundationDocumentSchemaVersion }, { "actorId", new BsonInt64(scope.ActorIdValue) }, { "operation", scope.Operation }, { "key", scope.Key },
                { "bodyHash", scope.BodyHash.ToLowerInvariant() },
                { "createdAt", clock.UtcNow.UtcDateTime }, { "result", new BsonDocument() }
            };
            await records.InsertOneAsync(session, record, cancellationToken: token);
            var result = await businessWrite(session, token);
            await records.UpdateOneAsync(session, filter,
                Builders<BsonDocument>.Update.Set("result", result), cancellationToken: token);
            return result.DeepClone().AsBsonDocument;
        }, cancellationToken);
    }
}
