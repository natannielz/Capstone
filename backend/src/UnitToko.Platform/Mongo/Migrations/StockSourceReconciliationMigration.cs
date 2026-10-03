using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Inventory.StockSourceReconciliationBson;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Installs quantity-only reconciliation records after retained stock-owner preflight.</summary>
internal sealed class StockSourceReconciliationMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 74;
    private sealed record Target(string Name, bool Exists, BsonDocument Validator, BsonDocument[] Missing);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        try { await ApplyCoreAsync(lease, ct); }
        catch (Exception error) when (error is PersistenceConflictException or AuthorizationDeniedException or
            UnitToko.Modules.Contracts.DomainValidationException or InvalidOperationException or ArgumentException or
            KeyNotFoundException or InvalidCastException or FormatException or OverflowException)
        { throw new InvalidOperationException("Stock source reconciliation migration preflight failed", error); }
    }
    private async Task ApplyCoreAsync(MigrationLease lease, CancellationToken ct)
    {
        await lease.RenewAsync(ct);
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(75).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(73).Any(row => row["state"] != "applied") ||
            history[^1]["state"] != "applying") throw Bad();
        foreach (var name in StockSourcePolicySchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, StockSourcePolicySchemas.Validator(name), StockSourcePolicySchemas.Key(name), ct);
        foreach (var name in new[] { "inventoryPools", "lots", "stockBalances", "movements", ProcurementStockReceiptBson.Receipts })
        {
            var (validator, keys) = name switch {
                "inventoryPools" => (StockSourceSetupSchemas.Validator(name), new[] { StockSourceSetupSchemas.Key(name) }),
                "lots" => (InventoryLotSchemas.Validator(), new[] { new BsonDocument("_id", 1) }),
                "stockBalances" => (InventorySchemas.BalanceValidator(), new[] { InventorySchemas.BalanceKey() }),
                "movements" => (InventoryMovementSchemas.Validator(), new[] { InventoryMovementSchemas.Key() }),
                _ => (ProcurementStockReceiptSchemas.Validator(name), ProcurementStockReceiptSchemas.Keys(name)) };
            await InventoryRepository.RequireStorageAsync(database, name, validator, keys, ct);
        }
        var targets = new List<Target>(); var maxima = new Dictionary<string, long>();
        foreach (var name in StockSourceReconciliationSchemas.Collections)
        {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var previous = definition?["options"].AsBsonDocument.GetValue("validator", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && (definition["type"] != "collection" || definition["options"].AsBsonDocument.GetValue("capped", false).AsBoolean ||
                definition["options"].AsBsonDocument.Contains("timeseries") || definition["options"].AsBsonDocument.Contains("encryptedFields"))) throw Bad();
            var required = StockSourceReconciliationSchemas.Validator(name);
            var validator = previous.ElementCount == 0 ? required : InventorySchemas.Contains(previous, required) ? previous :
                new BsonDocument("$and", new BsonArray { previous, required });
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Bad();
            var keys = StockSourceReconciliationSchemas.Keys(name);
            var indexes = definition is null ? [] : await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                index.GetValue("unique", false).AsBoolean && index["name"] != "_id_" && !keys.Any(key => index["key"] == key))) throw Bad();
            var missing = new List<BsonDocument>();
            foreach (var key in keys)
            {
                var matches = indexes.Where(index => index["key"] == key).ToArray();
                if (matches.Any(index => index.GetValue("name", "") != "_id_" && !index.GetValue("unique", false).AsBoolean) || matches.Length > 1) throw Bad();
                if (matches.Length == 0 && key != new BsonDocument("_id", 1)) missing.Add(key);
                var group = new BsonDocument(key.Names.Select((field, position) => new BsonElement("k" + position, "$" + field)));
                if (await Rows(name).Aggregate<BsonDocument>(new BsonDocument[] { new("$group", new BsonDocument { { "_id", group }, { "n", new BsonDocument("$sum", 1) } }),
                    new("$match", new BsonDocument("n", new BsonDocument("$gt", 1))), new("$limit", 1) }).AnyAsync(ct)) throw Bad();
            }
            var ids = await Rows(name).Find(new BsonDocument()).Project(new BsonDocument("_id", 1)).ToListAsync(ct);
            if (ids.Any(row => !row["_id"].IsInt64 || row["_id"].AsInt64 <= 0)) throw Bad();
            maxima[name] = ids.Select(row => row["_id"].AsInt64).DefaultIfEmpty(0).Max();
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 ||
                value.AsInt64 is < 0 or >= long.MaxValue - 1)) throw Bad();
            targets.Add(new(name, definition is not null, validator, missing.ToArray()));
        }
        // Retained M72/M73 receipt, invoice and valuation graphs must still be genuine before new DDL.
        if (database.Client is not MongoClient client) throw Bad();
        var unit = new MongoUnitOfWork(client); var idsAllocator = new IdAllocator(database);
        var catalog = new CatalogAuthoringService(database, unit, idsAllocator, clock);
        var orders = new PurchaseOrderService(database, unit, idsAllocator, clock, catalog,
            new(database, unit, idsAllocator, clock, catalog), new(database, unit, idsAllocator, clock, catalog));
        var sources = new StockSourceSetupService(database, unit, idsAllocator, clock);
        var receipts = new ProcurementStockReceiptRecords(database, unit, clock, catalog, orders, sources);
        var claims = new SupplierClaimRecords(database, unit, clock, catalog, orders);
        var dispositions = new ProcurementQuarantineDisposition(database, claims);
        var valuations = new ProcurementStockValuationRecords(database, unit, clock, catalog, orders, sources);
        using (var tx = await client.StartSessionAsync(cancellationToken: ct))
        {
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
            try
            {
                var verifiedReceipts = new List<BsonDocument>();
                foreach (var raw in await Rows(ProcurementStockReceiptBson.Receipts).Find(tx, new BsonDocument()).ToListAsync(ct))
                { await lease.RenewAsync(ct); verifiedReceipts.Add(await receipts.ReadAsync(tx, raw["_id"].AsInt64, ct)); }
                foreach (var receipt in verifiedReceipts.GroupBy(row => (row["inspectionId"].AsInt64, row["inspectionLineNumber"].AsInt32)).Select(group => group.First()))
                {
                    await lease.RenewAsync(ct);
                    var lineNumber = receipt["inspectionLineNumber"].AsInt32;
                    var inspection = receipt["inspection"].AsBsonDocument;
                    await dispositions.ReadAsync(tx, SupplierClaimBasis.PhysicalReceived, receipt["inspectionId"].AsInt64,
                        lineNumber, inspection["snapshotHash"].AsString,
                        inspection["input"]["lines"][lineNumber - 1]["receivedPackages"].AsInt32,
                        receipt["location"].AsBsonDocument, receipt["orderId"].AsInt64, receipt["supplierId"].AsInt64, ct);
                }
                foreach (var raw in await Rows(ProcurementStockValuationBson.Proposals).Find(tx, new BsonDocument()).ToListAsync(ct))
                { await lease.RenewAsync(ct); var proposal = await valuations.ProposalAsync(tx, raw["_id"].AsInt64, ct); _ = await valuations.DecisionAsync(tx, proposal, ct); }
                await valuations.OrphansAsync(tx, ct);
                await sources.ReconciliationPreflightAsync(tx, ct);
                await tx.AbortTransactionAsync(ct);
            }
            catch { if (tx.IsInTransaction) await tx.AbortTransactionAsync(CancellationToken.None); throw; }
        }
        foreach (var target in targets)
        {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { target.Exists ? "collMod" : "create", target.Name },
                { "validator", target.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in target.Missing)
                await Rows(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true,
                        Name = "ux_" + target.Name + "_" + Array.FindIndex(StockSourceReconciliationSchemas.Keys(target.Name), value => value == key) }),
                    cancellationToken: ct);
        }
        foreach (var (name, maximum) in maxima) if (maximum > 0)
        {
            await lease.RenewAsync(ct);
            await Rows("idCounters").WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(new BsonDocument("_id", name),
                Builders<BsonDocument>.Update.Max("value", maximum), new UpdateOptions { IsUpsert = true }, ct);
        }
    }
    private static InvalidOperationException Bad() => new("Stock source reconciliation migration preflight failed");
}
