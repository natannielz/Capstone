using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Procurement;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementStockReceiptBson;

namespace UnitToko.Platform.Inventory;

/// <summary>The sole inactive-pool exception: one proven procurement receipt creates a new immutable root lot
/// and entirely held balance. It cannot alter an existing balance, value goods or make quantities available.</summary>
internal sealed class InventoryHeldReceiptWriter(IMongoDatabase database)
{
    internal static BsonDocument Lot(BsonDocument row) {
        var line = row["inspection"]["input"]["lines"][row["inspectionLineNumber"].AsInt32 - 1];
        return new() { { "_id", row["lotId"] }, { "schemaVersion", 1 }, { "kind", "inventory.lot" }, { "organizationId", row["location"]["organizationId"] },
            { "skuId", line["requestSkuId"] }, { "definitionId", line["definitionId"] }, { "rootLotId", row["lotId"] }, { "parentLotId", BsonNull.Value }, { "origin", "procurementReceipt" },
            { "sourceId", row["_id"] }, { "sourceLineId", 1L }, { "recordedAt", row["recordedAt"] }, { "expiresOn", row["input"]["expiresOn"] } };
    }
    internal static InventoryBalanceDocument Before(BsonDocument row) {
        var lot = Lot(row); var location = InventoryGraph.Location(row["poolSource"]["configuration"]["source"]["input"]["location"].AsBsonDocument);
        return new(row["balanceId"].AsInt64, row["poolId"].AsInt64, new(new(location, new(lot["skuId"].AsInt64), new(lot["definitionId"].AsInt64), new(lot["_id"].AsInt64)), 0, 0, 0, 0, 1));
    }
    internal static InventoryBalanceDocument After(BsonDocument row) { var before = Before(row); return before with { Balance = StockBalancePolicy.ReceiveHeld(before.Balance, 1, row["input"]["packages"].AsInt32) }; }
    internal static InventorySource Source(long id) => new(B.Operation, id, "receiveHeld", 1);
    internal static BsonDocument Movement(BsonDocument row, ActorContext actor) {
        var before = Before(row); var after = After(row); var change = new InventoryBalanceChange(before.Id, before.PhysicalPoolId, before.Balance.Key, 1, InventoryBalanceAction.ReceiveHeld, row["input"]["packages"].AsInt32);
        return InventoryMovementBson.Create(row["movementId"].AsInt64, actor, new(Source(row["_id"].AsInt64), 1, new(before.PhysicalPoolId, before.Balance.Key.Location, row["input"]["poolRevision"].AsInt64), [change]),
            [before], [after], new(row["reason"].AsString, row["correlationId"].AsString), new(row["recordedAt"].ToUniversalTime()));
    }
    internal async Task WriteAsync(IClientSessionHandle tx, ActorContext actor, ProcurementStockReceiptWriteProof proof, CancellationToken ct) {
        try {
            proof.Require(actor, tx); var row = proof.Row; var actual = await database.GetCollection<BsonDocument>(B.Receipts).Find(tx, new BsonDocument("_id", row["_id"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            ProcurementStockReceiptRecords.Equal(actual, row);
            if (!ReferenceEquals(tx.Client, database.Client) || row["actorId"] != actor.UserId || row["actorGrantVersion"] != actor.GrantVersion) throw B.Broken();
            await InventoryRepository.RequireStorageAsync(database, "lots", InventoryLotSchemas.Validator(), new BsonDocument("_id", 1), ct);
            await InventoryRepository.RequireStorageAsync(database, "stockBalances", InventorySchemas.BalanceValidator(), InventorySchemas.BalanceKey(), ct);
            await InventoryRepository.RequireStorageAsync(database, "movements", InventoryMovementSchemas.Validator(), InventoryMovementSchemas.Key(), ct);
            await proof.Pool.FenceAsync(ct); proof.Require(actor, tx);
            var before = Before(row); var lot = Lot(row); _ = InventoryGraph.ReadLot(lot);
            await database.GetCollection<BsonDocument>("lots").InsertOneAsync(tx, lot, cancellationToken: ct);
            var balances = database.GetCollection<BsonDocument>("stockBalances"); await balances.InsertOneAsync(tx, InventoryBson.Balance(before), cancellationToken: ct);
            if ((await balances.ReplaceOneAsync(tx, InventoryBson.Balance(before), InventoryBson.Balance(After(row)), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
            await database.GetCollection<BsonDocument>("movements").InsertOneAsync(tx, Movement(row, actor), cancellationToken: ct); proof.Require(actor, tx); ct.ThrowIfCancellationRequested();
        } catch { await InventoryRepository.AbortAsync(tx); throw; }
    }
    internal static async Task RequireHistoryAsync(IMongoDatabase database, IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        var lot = await database.GetCollection<BsonDocument>("lots").Find(tx, new BsonDocument("_id", row["lotId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(); ProcurementStockReceiptRecords.Equal(lot, Lot(row));
        // A later valuation/release owner must add genuine movement-chain proof before this initial held balance may change.
        var balance = await database.GetCollection<BsonDocument>("stockBalances").Find(tx, new BsonDocument("_id", row["balanceId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(); ProcurementStockReceiptRecords.Equal(balance, InventoryBson.Balance(After(row)));
        var movement = await database.GetCollection<BsonDocument>("movements").Find(tx, new BsonDocument("_id", row["movementId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var source = Source(row["_id"].AsInt64); var before = Before(row);
        var expected = new BsonDocument { { "_id", row["movementId"] }, { "schemaVersion", 1 }, { "kind", "inventory.movement" }, { "sourceEvent", source.EventKey },
            { "source", new BsonDocument { { "namespace", source.Namespace }, { "documentId", source.DocumentId }, { "eventKind", source.EventKind }, { "occurrence", source.Occurrence } } },
            { "sourceRevision", 1L }, { "physicalPoolId", row["poolId"] }, { "poolRevision", row["input"]["poolRevision"] }, { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] },
            { "recordedAt", row["recordedAt"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] },
            { "legs", new BsonArray { new BsonDocument { { "balanceId", before.Id }, { "action", "ReceiveHeld" }, { "packages", row["input"]["packages"].ToInt64() },
                { "before", InventoryBson.Balance(before) }, { "after", InventoryBson.Balance(After(row)) } } } } };
        ProcurementStockReceiptRecords.Equal(movement, expected);
    }
}
