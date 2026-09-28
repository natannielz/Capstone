using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class StockCountMigrationGraph
{
    internal static async Task ValidateAsync(IMongoDatabase database, MigrationLease lease, CancellationToken token)
    {
        foreach (var name in StockCountSchemas.Collections)
        {
            using var cursor = await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority)
                .FindAsync(StockCountSchemas.Selection(name), new FindOptions<BsonDocument> { BatchSize = 128 }, token);
            while (await cursor.MoveNextAsync(token))
            {
                await lease.RenewAsync(token);
                foreach (var record in cursor.Current)
                {
                    var row = record;
                    if (name != StockCountBson.Collection)
                    {
                        var id = name == "auditEvents" ? StockCountBson.Long(record, "observationId") : StockCountBson.ReceiptObservationId(record);
                        row = await database.GetCollection<BsonDocument>(StockCountBson.Collection).WithReadConcern(ReadConcern.Majority)
                            .Find(new BsonDocument("_id", id)).SingleOrDefaultAsync(token);
                        if (row is null) throw StockCountMigration.Invalid("orphan audit or receipt");
                    }
                    await ValidateObservationAsync(database, row, token);
                    // A foreign duplicate link must not hide behind the observation's canonical record.
                    if (name == "auditEvents" && !StockCountBson.Same(record, StockCountBson.Audit(row)))
                        throw StockCountMigration.Invalid("audit linkage");
                    if (name == "idempotencyRecords" && (!record.TryGetValue("_id", out var receiptId) || !receiptId.IsObjectId ||
                        !StockCountBson.Same(record, StockCountBson.Receipt(row, receiptId.AsObjectId))))
                        throw StockCountMigration.Invalid("receipt linkage");
                }
            }
        }
    }
    private static async Task ValidateObservationAsync(IMongoDatabase database, BsonDocument row, CancellationToken token)
    {
        try
        {
            var observation = StockCountBson.Read(row);
            var audit = await database.GetCollection<BsonDocument>("auditEvents").WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument("_id", observation.AuditId)).SingleOrDefaultAsync(token);
            var receipt = await database.GetCollection<BsonDocument>("idempotencyRecords").WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument { { "actorId", observation.CountedBy }, { "operation", StockCountBson.Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(token);
            StockCountBson.Graph(row, audit, receipt);
            await StockCountBson.RequireHistoricalCatalogAsync(observation, async (collection, id, ct) =>
                await database.GetCollection<BsonDocument>(collection).WithReadConcern(ReadConcern.Majority)
                    .Find(new BsonDocument("_id", id)).SingleOrDefaultAsync(ct), token);
        }
        catch (Exception ex) when (ex is UnitToko.Platform.Authorization.AuthorizationDeniedException or KeyNotFoundException or InvalidCastException or ArgumentException)
        { throw StockCountMigration.Invalid("record graph"); }
    }
}
