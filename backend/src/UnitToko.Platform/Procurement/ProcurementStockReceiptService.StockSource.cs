using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementStockReceiptService
{
    private async Task CompleteHeldStockSourceAsync(Context context, BsonDocument actual, CancellationToken ct)
    {
        Current(context);
        // Context is rebuilt by authorize for every UoW attempt. Only a fresh businessWrite
        // has this genuine same-attempt held-writer handle. Existing-key replay leaves it null.
        if (context.SourceWrite is { } fresh) {
            await new ProcurementStockSourceProducer(database, ids, clock).RecordAsync(context.Tx, actual["_id"].AsInt64, fresh, ct);
            context.SourceWrite = null;
        }
        var pool = await Rows("inventoryPools").Find(context.Tx, new BsonDocument("_id", actual["poolId"]))
            .SingleOrDefaultAsync(ct); InventoryGraph.RequirePool(pool);
        var expected = new StockSourceObligationExpectation(actual["poolId"].AsInt64,
            InventoryGraph.Location(pool!["location"].AsBsonDocument), pool["revision"].AsInt64, pool["fence"].AsInt64);
        // Read only: do not repair missing history, append on replay, or introduce a second
        // reader/file owner. This proves the entire bound receipt membership before final fences.
        _ = await new ProcurementStockSourceRecords(database).ReadAsync(context.Tx,
            new ProcurementStockSourceEvents(database, ids, clock), expected, ct);
        Current(context); ct.ThrowIfCancellationRequested();
    }
}
