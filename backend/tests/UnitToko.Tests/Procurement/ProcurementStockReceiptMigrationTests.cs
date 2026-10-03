using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementStockReceiptMigrationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Historical_fixture_stock_and_ledger_movements_are_preserved_when_empty_receipt_suffix_is_removed() {
        await using var data = await ShipmentDispatchTestData.CreateAsync(fixture);
        await data.DispatchAsync(await data.PreviewAsync(), "legacy-stock-before72");
        var db = data.Order.Database;
        foreach (var name in new[] { "lots", "stockBalances", "movements" })
            Assert.True(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).AnyAsync());
        foreach (var name in new[] { "lots", "stockBalances", "procurementStockReceipts" })
            Assert.False(await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", name)).AnyAsync());
        Assert.True((await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", "movements")).SingleAsync())["value"].AsInt64 > 0);
        foreach (var source in new[] { "ordering.order", "fulfillment.shipment" })
            Assert.True(await db.GetCollection<BsonDocument>("movements").Find(new BsonDocument("source.namespace", source)).AnyAsync());
        var shared = await SharedStockAsync(db); var history = await HistoryAsync(db);
        await RestoreV71Async(db);
        Assert.Equal(shared, await SharedStockAsync(db)); Assert.Equal(new BsonArray(history.Take(71)), await HistoryAsync(db));
    }

    [Fact]
    public async Task Empty_forward_install_preserves_identity_and_stronger_guards_and_refuses_unproved_legacy_stock_or_unknown_partial_indexes_before_DDL() {
        await using var data = await StoreTestData.CreateAsync(fixture); await RestoreV71Async(data.Database);
        var guard = new BsonDocument("_id", new BsonDocument("$ne", 999999L));
        var original = (await DefinitionAsync(data.Database, "movements"))["options"]["validator"].AsBsonDocument;
        await SetValidatorAsync(data.Database, "movements", new("$and", new BsonArray { ProcurementStockReceiptMigration.PreviousMovementValidator(), guard }));
        var allocations = data.Database.GetCollection<BsonDocument>(SupplierClaimSchemas.PhysicalAllocations);
        await allocations.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("receiptId", 1), new CreateIndexOptions<BsonDocument> {
            Name = "unknown_partial_receipt", Unique = true, PartialFilterExpression = new BsonDocument("schemaVersion", 2) }));
        try { await RefusesAsync(data.Database, data.Clock); } finally { await allocations.Indexes.DropOneAsync("unknown_partial_receipt"); }
        var lot = new BsonDocument { { "_id", 9001L }, { "schemaVersion", 1 }, { "kind", "inventory.lot" }, { "organizationId", 100L }, { "skuId", 8001L },
            { "definitionId", 8002L }, { "rootLotId", 9001L }, { "parentLotId", BsonNull.Value }, { "origin", "procurementReceipt" }, { "sourceId", 9002L },
            { "sourceLineId", 1L }, { "recordedAt", data.Clock.UtcNow.UtcDateTime }, { "expiresOn", BsonNull.Value } };
        await data.Database.GetCollection<BsonDocument>("lots").InsertOneAsync(lot);
        try { await RefusesAsync(data.Database, data.Clock); } finally { Assert.Equal(1, (await data.Database.GetCollection<BsonDocument>("lots").DeleteOneAsync(new BsonDocument("_id", 9001L))).DeletedCount); }
        var before = await StateAsync(data.Database); var definitions = await DefinitionsAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync(); Assert.Equal(before, await StateAsync(data.Database));
        foreach (var definition in definitions) Assert.Equal(definition["info"]["uuid"], (await DefinitionAsync(data.Database, definition["name"].AsString))["info"]["uuid"]);
        Assert.True(InventorySchemas.Contains((await DefinitionAsync(data.Database, "movements"))["options"]["validator"].AsBsonDocument, InventoryMovementSchemas.Validator()));
        Assert.True(InventorySchemas.Contains((await DefinitionAsync(data.Database, "movements"))["options"]["validator"].AsBsonDocument, guard));
        await ProcurementStockReceiptSchemas.RequireDispositionStorageAsync(data.Database, default);
        var current = await DefinitionsAsync(data.Database); await new CollectionMigrations(data.Database, data.Clock).ApplyAsync(); Assert.Equal(current, await DefinitionsAsync(data.Database));
        Assert.NotEqual(original, (await DefinitionAsync(data.Database, "movements"))["options"]["validator"]);
    }

    [WindowsStorageFact]
    [SupportedOSPlatform("windows")]
    public async Task Genuine_claim_and_held_receipt_history_survive_resumed_forward_install_but_missing_owner_movement_or_effect_proof_refuses_without_changes() {
        await using var data = await ProcurementStockReceiptTestData.CreateAsync(fixture);
        var claim = await data.Claims.RequestAsync("retained-claim"); var receipt = await data.RecordAsync("retained-held");
        var saved = await data.Rows("procurementStockReceipts").Find(new BsonDocument("_id", receipt["receiptId"])).SingleAsync();
        Assert.Equal(2, (await data.Rows(SupplierClaimSchemas.PhysicalAllocations).Find(new BsonDocument()).ToListAsync()).Count);
        Assert.Equal(receipt, await data.Service.LookupAsync(data.Maker, ProcurementStockReceiptTestData.Operation, "retained-held"));
        var installed = await HistoryAsync(data.Database); var shared = await SharedStockAsync(data.Database);
        var dispositionDefinition = await DefinitionAsync(data.Database, SupplierClaimSchemas.PhysicalAllocations);
        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(() => RestoreV71Async(data.Database));
        Assert.Equal(installed, await HistoryAsync(data.Database)); Assert.Equal(shared, await SharedStockAsync(data.Database));
        Assert.Equal(dispositionDefinition, await DefinitionAsync(data.Database, SupplierClaimSchemas.PhysicalAllocations));
        Assert.Equal(saved, await data.Rows("procurementStockReceipts").Find(new BsonDocument("_id", receipt["receiptId"])).SingleAsync());
        // Explicit retry rehearsal after real completed-owner verification; the ordinary predecessor helper refuses populated suffixes.
        Assert.Equal(1, (await data.Rows("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 72 }, { "state", "applied" } })).DeletedCount);
        foreach (var (table, filter) in new[] { ("procurementStockReceipts", new BsonDocument("_id", receipt["receiptId"])),
            ("movements", new BsonDocument("_id", receipt["movementId"])), ("stockBalances", new BsonDocument("_id", receipt["balanceId"])),
            ("procurementQuarantineDispositions", new BsonDocument("_id", saved["disposition"]["allocationId"])),
            ("auditEvents", new BsonDocument("_id", saved["auditId"])), ("idempotencyRecords", new BsonDocument { { "operation", ProcurementStockReceiptTestData.Operation }, { "key", "retained-held" } }) }) {
            var row = await data.Rows(table).Find(filter).SingleAsync(); Assert.Equal(1, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", row["_id"]))).DeletedCount);
            try { await RefusesAsync(data.Database, data.Clock); } finally { await data.Rows(table).InsertOneAsync(row); }
        }
        var before = await StateAsync(data.Database); var expected = before.DeepClone().AsBsonDocument;
        var policy = await data.Rows(SupplierTestData.Policies).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).FirstAsync();
        Assert.DoesNotContain(expected["idCounters"].AsBsonArray, row => row["_id"] == SupplierTestData.Policies);
        expected["idCounters"] = new BsonArray(expected["idCounters"].AsBsonArray.Append(new BsonDocument { { "_id", SupplierTestData.Policies }, { "value", policy["_id"] } }).OrderBy(row => row["_id"].AsString, StringComparer.Ordinal));
        var definitions = await DefinitionsAsync(data.Database); var indexes = await IndexesAsync(data.Database);
        await new CollectionMigrations(data.Database, data.Clock).ApplyAsync();
        Assert.Equal(expected, await StateAsync(data.Database)); Assert.Equal(definitions, await DefinitionsAsync(data.Database)); Assert.Equal(indexes, await IndexesAsync(data.Database));
        Assert.Equal(receipt, await data.Service.LookupAsync(data.Maker, ProcurementStockReceiptTestData.Operation, "retained-held"));
        Assert.Equal(claim, await data.Claims.Service.LookupAsync(data.Claims.Maker, SupplierClaimTestData.RequestOperation, "retained-claim"));
        var rows = await data.Rows(SupplierClaimSchemas.PhysicalAllocations).Find(new BsonDocument()).Sort(new BsonDocument("sequence", 1)).ToListAsync();
        Assert.Equal(1, rows[0]["schemaVersion"].AsInt32); Assert.Equal("supplier_claim", rows[0]["kind"].AsString);
        Assert.Equal(2, rows[1]["schemaVersion"].AsInt32); Assert.Equal("held_receipt", rows[1]["kind"].AsString); Assert.Equal(rows[0]["snapshotHash"], rows[1]["previousHash"]);
    }

    internal static async Task RestoreV71Async(IMongoDatabase db) {
        var history = await HistoryAsync(db); if (history.Count <= 71) { Assert.All(history, row => Assert.Equal("applied", row["state"])); return; }
        Assert.Equal(72, history.Count); Assert.All(history, row => Assert.Equal("applied", row["state"]));
        Assert.False(await db.GetCollection<BsonDocument>("procurementStockReceipts").Find(new BsonDocument()).AnyAsync());
        // Historical order fixtures insert shared lots/balances directly; their real reserve/dispatch
        // movements use the shared movement allocator. None proves or substitutes for an M72 receipt.
        foreach (var table in new[] { "procurementStockReceipts", "lots", "stockBalances" })
            Assert.False(await db.GetCollection<BsonDocument>("idCounters").Find(new BsonDocument("_id", table)).AnyAsync());
        Assert.False(await db.GetCollection<BsonDocument>(SupplierClaimSchemas.PhysicalAllocations).Find(new BsonDocument("$or", new BsonArray {
            new BsonDocument("schemaVersion", 2), new BsonDocument("kind", "held_receipt"), new BsonDocument("receiptId", new BsonDocument("$exists", true)) })).AnyAsync());
        Assert.False(await db.GetCollection<BsonDocument>("movements").Find(new BsonDocument("legs.action", "ReceiveHeld")).AnyAsync());
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"),
            ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation"), ("journalEntries", "source.namespace"),
            ("journalEntries", "sourceEvent"), ("movements", "source.namespace"), ("movements", "sourceEvent") })
            Assert.False(await db.GetCollection<BsonDocument>(table).Find(new BsonDocument(field, new BsonRegularExpression(@"^procurement\.stockreceipt(?:[.:]|$)"))).AnyAsync());
        var shared = await SharedStockAsync(db);
        var definition = await DefinitionAsync(db, SupplierClaimSchemas.PhysicalAllocations); var found = false;
        BsonDocument Restore(BsonDocument current) {
            if (CatalogSchemas.SemanticEquals(current, ProcurementStockReceiptSchemas.DispositionValidator())) { found = true; return SupplierClaimSchemas.Validator(SupplierClaimSchemas.PhysicalAllocations); }
            var copy = current.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray) copy["$and"] = new BsonArray(terms.AsBsonArray.Select(term => term.IsBsonDocument ? Restore(term.AsBsonDocument) : term.DeepClone()));
            return copy;
        }
        var restored = Restore(definition["options"]["validator"].AsBsonDocument); Assert.True(found);
        await SetValidatorAsync(db, SupplierClaimSchemas.PhysicalAllocations, restored);
        var allocations = db.GetCollection<BsonDocument>(SupplierClaimSchemas.PhysicalAllocations);
        await allocations.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(ProcurementStockReceiptSchemas.V1Key, new CreateIndexOptions { Unique = true, Name = "ux_" + SupplierClaimSchemas.PhysicalAllocations + "_0" }));
        foreach (var name in new[] { ProcurementStockReceiptSchemas.V1Index, ProcurementStockReceiptSchemas.V2Index }) await allocations.Indexes.DropOneAsync(name);
        Assert.Equal(1, (await db.GetCollection<BsonDocument>("schemaMigrations").DeleteOneAsync(new BsonDocument { { "_id", 72 }, { "state", "applied" } })).DeletedCount);
        Assert.Equal(new BsonArray(history.Take(71)), await HistoryAsync(db));
        Assert.Equal(shared, await SharedStockAsync(db));
    }
    private static async Task RefusesAsync(IMongoDatabase db, IClock clock) {
        var definitions = await DefinitionsAsync(db); var indexes = await IndexesAsync(db); var state = await StateAsync(db); var history = new BsonArray((await HistoryAsync(db)).Take(71));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectionMigrations(db, clock).ApplyAsync()); Assert.Equal("Procurement stock receipt migration preflight failed", error.Message);
        Assert.Equal(definitions, await DefinitionsAsync(db)); Assert.Equal(indexes, await IndexesAsync(db)); Assert.Equal(state, await StateAsync(db));
        Assert.Equal(history, new BsonArray((await HistoryAsync(db)).Take(71))); Assert.False(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument { { "_id", 72 }, { "state", "applied" } }).AnyAsync());
    }
    private static Task SetValidatorAsync(IMongoDatabase db, string name, BsonDocument validator) => db.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } });
    private static async Task<BsonDocument> DefinitionAsync(IMongoDatabase db, string name) => await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
    private static async Task<List<BsonDocument>> DefinitionsAsync(IMongoDatabase db) => (await (await db.ListCollectionsAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToList();
    private static async Task<BsonArray> HistoryAsync(IMongoDatabase db) => new(await db.GetCollection<BsonDocument>("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task<BsonDocument> SharedStockAsync(IMongoDatabase db) {
        var result = new BsonDocument();
        foreach (var name in new[] { "lots", "stockBalances", "movements", "idCounters" }) result[name] = new BsonDocument {
            { "rows", new BsonArray(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()) },
            { "definition", await DefinitionAsync(db, name) },
            { "indexes", new BsonArray((await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString)) }
        };
        return result;
    }
    private static async Task<BsonDocument> StateAsync(IMongoDatabase db) {
        var result = new BsonDocument(); foreach (var definition in await DefinitionsAsync(db)) {
            var name = definition["name"].AsString; if (name is "schemaMigrations" or "migrationLocks") continue;
            result[name] = new BsonArray(await db.GetCollection<BsonDocument>(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        } return result;
    }
    private static async Task<BsonDocument> IndexesAsync(IMongoDatabase db) {
        var result = new BsonDocument(); foreach (var definition in await DefinitionsAsync(db)) result[definition["name"].AsString] = new BsonArray((await (await db.GetCollection<BsonDocument>(definition["name"].AsString).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString)); return result;
    }
}
