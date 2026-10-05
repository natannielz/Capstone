using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;
using D = UnitToko.Tests.Procurement.ProcurementStockReceiptTestData;
using B = UnitToko.Platform.Procurement.ProcurementStockReceiptBson;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class HeldReceiptMovementHistoryTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Authentic_initial_held_receipt_is_read_only_and_missing_mutated_or_unowned_later_effects_cannot_launder_release()
    {
        await using var data = await D.CreateAsync(fixture);
        var result = await data.RecordAsync("held-history-original", data.Input("SYNTHETIC-HELD-HISTORY", 2));
        var receipt = await data.Rows(B.Receipts).Find(new BsonDocument("_id", result["receiptId"])).SingleAsync();
        var helper = new HeldReceiptMovementHistory(data.Database, data.Unit, data.Clock);
        var owner = new ProcurementStockReceiptRecords(data.Database, data.Unit, data.Clock,
            data.Inspection.Receiving.Procurement.Catalog, data.Inspection.Receiving.Orders.Service, data.Sources);
        Task<BsonDocument> ReadOwnerAsync() => data.Unit.ExecuteAsync((tx, ct) => owner.ReadAsync(tx, receipt["_id"].AsInt64, ct));
        var preserved = await SnapshotAsync(data);
        // This positive begins with the existing receipt owner: genuine approved order, native
        // private inspection, independent source/policy and shared disposition capacity proof.
        var state = await data.Unit.ExecuteAsync(async (tx, ct) => {
            var actual = await owner.ReadAsync(tx, receipt["_id"].AsInt64, ct);
            return await helper.RequireAsync(tx, actual, ct);
        });
        Assert.Equal(InventoryHeldReceiptWriter.After(receipt), state.Original);
        Assert.Equal(state.Original, state.Current);
        Assert.Equal(2L, state.Current.Balance.OnHand);
        Assert.Equal(2L, state.Current.Balance.Held);
        Assert.Equal(0L, state.Current.Balance.Reserved);
        Assert.Equal(2L, state.Current.Balance.Revision);
        Assert.Equal(new[] { result["movementId"].AsInt64 }, state.MovementIds);
        Assert.Equal(preserved, await SnapshotAsync(data));
        Assert.False((await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync())["active"].AsBoolean);

        // The helper cannot borrow another client's transaction, and a failed proof aborts it.
        var settings = fixture.Client.Settings.Clone(); settings.ApplicationName = "HeldHistoryForeign-" + Guid.NewGuid().ToString("N");
        using (var foreign = new MongoClient(settings))
        using (var tx = await foreign.StartSessionAsync()) {
            tx.StartTransaction();
            Assert.Equal("inventory_transaction_required", (await Assert.ThrowsAsync<DomainValidationException>(() =>
                helper.RequireAsync(tx, receipt))).Code);
            Assert.False(tx.IsInTransaction);
        }
        Assert.Equal(preserved, await SnapshotAsync(data));

        foreach (var (table, identity) in new[] {
            (B.Receipts, receipt["_id"]), ("lots", receipt["lotId"]), ("movements", receipt["movementId"]),
            ("auditEvents", receipt["auditId"]) }) {
            var actual = await data.Rows(table).Find(new BsonDocument("_id", identity)).SingleAsync();
            Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", identity))).DeletedCount);
            try {
                var damaged = await SnapshotAsync(data);
                Assert.Equal("held_receipt_movement_history_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() =>
                    data.Unit.ExecuteAsync((tx, ct) => helper.RequireAsync(tx, receipt, ct)))).Code);
                if (table == "movements")
                    Assert.Equal("procurement_stock_receipt_integrity_unavailable",
                        (await Assert.ThrowsAsync<PersistenceConflictException>(ReadOwnerAsync)).Code);
                Assert.Equal(damaged, await SnapshotAsync(data));
            } finally { await data.Rows(table).InsertOneAsync(actual); }
            Assert.Equal(preserved, await SnapshotAsync(data));
        }
        var movement = await data.Rows("movements").Find(new BsonDocument("_id", receipt["movementId"])).SingleAsync();
        var changed = movement.DeepClone().AsBsonDocument; changed["reason"] = "NEGATIVE changed immutable movement reason";
        Assert.Equal(1L, (await data.Rows("movements").ReplaceOneAsync(movement, changed)).ModifiedCount);
        try {
            var damaged = await SnapshotAsync(data);
            Assert.Equal("held_receipt_movement_history_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() =>
                data.Unit.ExecuteAsync((tx, ct) => helper.RequireAsync(tx, receipt, ct)))).Code);
            Assert.Equal(damaged, await SnapshotAsync(data));
        } finally {
            Assert.Equal(1L, (await data.Rows("movements").ReplaceOneAsync(new BsonDocument("_id", movement["_id"]), movement)).ModifiedCount);
        }
        Assert.Equal(preserved, await SnapshotAsync(data));

        var originalBalance = await data.Rows("stockBalances").Find(new BsonDocument("_id", receipt["balanceId"])).SingleAsync();
        var before = InventoryBson.ReadBalance(originalBalance);
        var after = before with { Balance = StockBalancePolicy.ReleaseHold(before.Balance, before.Balance.Revision, 1) };
        // Deliberate NEGATIVE isolated corruption: a mathematically valid release is not an
        // approved release. No producer, acceptance or production active flag is fabricated.
        Assert.Equal(1L, (await data.Rows("stockBalances").ReplaceOneAsync(originalBalance, InventoryBson.Balance(after))).ModifiedCount);
        try {
            var damaged = await SnapshotAsync(data);
            Assert.Equal("held_receipt_movement_history_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() =>
                data.Unit.ExecuteAsync((tx, ct) => helper.RequireAsync(tx, receipt, ct)))).Code);
            Assert.Equal(damaged, await SnapshotAsync(data));
            var id = (await new IdAllocator(data.Database).NextAsync("movements")).Value;
            var pool = await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync();
            var source = new InventorySource("procurement.held-release", 999001, "releaseHold", 1);
            var change = new InventoryBalanceChange(before.Id, before.PhysicalPoolId, before.Balance.Key,
                before.Balance.Revision, InventoryBalanceAction.ReleaseHold, 1);
            var negative = InventoryMovementBson.Create(id, data.Maker,
                new(source, 1, new(data.PoolId, before.Balance.Key.Location, pool["revision"].AsInt64), [change]),
                [before], [after], new("NEGATIVE unowned release; never an approval", "held-history-negative"), data.Clock.UtcNow);
            await data.Rows("movements").InsertOneAsync(negative);
            try {
                var unsupported = await SnapshotAsync(data);
                Assert.Equal("held_receipt_movement_source_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() =>
                    data.Unit.ExecuteAsync((tx, ct) => helper.RequireAsync(tx, receipt, ct)))).Code);
                Assert.Equal("procurement_stock_receipt_integrity_unavailable",
                    (await Assert.ThrowsAsync<PersistenceConflictException>(ReadOwnerAsync)).Code);
                Assert.Equal(unsupported, await SnapshotAsync(data));
            } finally {
                Assert.Equal(1L, (await data.Rows("movements").DeleteOneAsync(new BsonDocument("_id", id))).DeletedCount);
            }
        } finally {
            Assert.Equal(1L, (await data.Rows("stockBalances").ReplaceOneAsync(new BsonDocument("_id", before.Id), originalBalance)).ModifiedCount);
        }
        Assert.Equal(preserved, await SnapshotAsync(data));
        var restored = await data.Unit.ExecuteAsync((tx, ct) => helper.RequireAsync(tx, receipt, ct));
        Assert.Equal(state.Original, restored.Original);
        Assert.Equal(state.Current, restored.Current);
        Assert.Equal(state.MovementIds.ToArray(), restored.MovementIds.ToArray());
        Assert.Equal(receipt, await ReadOwnerAsync());
        Assert.Equal(preserved, await SnapshotAsync(data));
    }

    private static async Task<BsonDocument> SnapshotAsync(D data)
    {
        var result = new BsonDocument();
        foreach (var table in new[] { B.Receipts, "lots", "stockBalances", "movements", "inventoryPools",
            "procurementQuarantineDispositionHeads", "procurementQuarantineDispositions", "auditEvents", "idempotencyRecords", "outboxEvents",
            "journalEntries", "cashMovements", "paymentSubmissions", "paymentAllocations", "orders", "reservations", "shipments" })
            result[table] = new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
}
