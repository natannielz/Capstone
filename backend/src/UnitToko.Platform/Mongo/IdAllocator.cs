using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;

namespace UnitToko.Platform.Mongo;

public sealed class IdAllocator(IMongoDatabase database)
{
    private readonly IMongoCollection<BsonDocument> counters = database.GetCollection<BsonDocument>("idCounters").WithWriteConcern(WriteConcern.WMajority);

    // Allocation is outside the business transaction: aborted work leaves gaps, never reused IDs.
    public async Task<LongId> NextAsync(string entity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entity) || entity.Length > 80 ||
            !System.Text.RegularExpressions.Regex.IsMatch(entity, @"^[A-Za-z][A-Za-z0-9._-]*$"))
            throw new DomainValidationException("invalid_entity", "Entity name is required and bounded");
        var filter = Builders<BsonDocument>.Filter.Eq("_id", entity) &
            Builders<BsonDocument>.Filter.Lt("value", long.MaxValue);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var result = await counters.FindOneAndUpdateAsync(filter,
                    Builders<BsonDocument>.Update.Inc("value", 1L),
                    new FindOneAndUpdateOptions<BsonDocument>
                    { IsUpsert = true, ReturnDocument = ReturnDocument.After }, cancellationToken);
                if (!result["value"].IsInt64 || result["value"].AsInt64 <= 0)
                    throw new PersistenceConflictException("id_exhausted", "ID sequence exhausted");
                return new LongId(result["value"].AsInt64);
            }
            catch (MongoCommandException ex) when (ex.Code == 11000)
            {
                var current = await counters.Find(Builders<BsonDocument>.Filter.Eq("_id", entity))
                    .FirstOrDefaultAsync(cancellationToken);
                if (current is not null && current["value"].IsInt64 && current["value"].AsInt64 == long.MaxValue)
                    throw new PersistenceConflictException("id_exhausted", "ID sequence exhausted");
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                var current = await counters.Find(Builders<BsonDocument>.Filter.Eq("_id", entity))
                    .FirstOrDefaultAsync(cancellationToken);
                if (current is not null && current["value"].IsInt64 && current["value"].AsInt64 == long.MaxValue)
                    throw new PersistenceConflictException("id_exhausted", "ID sequence exhausted");
            }
        }
        throw new PersistenceConflictException("id_allocation_conflict", "ID sequence is busy; retry the request");
    }
}
