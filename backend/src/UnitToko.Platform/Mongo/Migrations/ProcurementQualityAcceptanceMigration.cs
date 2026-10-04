using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed class ProcurementQualityAcceptanceMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 77;
    private sealed record Install(string Name, bool Exists, BsonDocument Validator, BsonDocument[] Unique, BsonDocument[] Support);
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        try { await ApplyCoreAsync(lease, ct); }
        catch (Exception error) when (error is PersistenceConflictException or AuthorizationDeniedException or DomainValidationException or
            InvalidOperationException or KeyNotFoundException or InvalidCastException or FormatException or OverflowException) {
            throw new InvalidOperationException("Procurement quality acceptance migration preflight failed", error);
        }
    }
    private async Task ApplyCoreAsync(MigrationLease lease, CancellationToken ct) {
        await lease.RenewAsync(ct);
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(78).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(76).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Bad();
        // The additive owner never replaces a published predecessor contract or installs its missing guards.
        await ProcurementAdvanceReturnService.RequireReturnStorageAsync(database, ct);
        var targets = new List<Install>(); var maxima = new Dictionary<string, long>();
        foreach (var name in ProcurementQualityAcceptanceSchemas.Collections) {
            await lease.RenewAsync(ct); var definition = await DefinitionAsync(name, ct);
            var required = ProcurementQualityAcceptanceSchemas.Validator(name);
            var current = definition?["options"].AsBsonDocument.GetValue("validator", new BsonDocument()).AsBsonDocument ?? new();
            var validator = current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current : new BsonDocument("$and", new BsonArray { current, required });
            var keys = ProcurementQualityAcceptanceSchemas.Keys(name); var support = ProcurementQualityAcceptanceSchemas.SupportKeys(name);
            var indexes = definition is null ? [] : await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                index.GetValue("unique", false).AsBoolean && index["name"] != "_id_" && !keys.Any(key => index["key"] == key)) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1))) throw Bad();
            var missing = new List<BsonDocument>(); var missingSupport = new List<BsonDocument>();
            for (var i = 0; i < keys.Length; i++) {
                var found = indexes.Where(index => index["key"] == keys[i] || index["name"] == "ux_" + name + "_" + i).ToArray();
                if (found.Length > 1 || found.Any(index => index["key"] != keys[i] || index["name"] != "_id_" && !index.GetValue("unique", false).AsBoolean)) throw Bad();
                if (found.Length == 0 && keys[i] != new BsonDocument("_id", 1)) missing.Add(keys[i]);
                var group = new BsonDocument(keys[i].Names.Select((field, position) => new BsonElement("k" + position, "$" + field)));
                if (await Rows(name).Aggregate<BsonDocument>(new BsonDocument[] { new("$group", new BsonDocument { { "_id", group }, { "n", new BsonDocument("$sum", 1) } }),
                    new("$match", new BsonDocument("n", new BsonDocument("$gt", 1))), new("$limit", 1) }).AnyAsync(ct)) throw Bad();
            }
            for (var i = 0; i < support.Length; i++) {
                var found = indexes.Where(index => index["key"] == support[i] || index["name"] == "ix_" + name + "_" + i).ToArray();
                if (found.Length > 1 || found.Any(index => index["key"] != support[i] || index.GetValue("unique", false).AsBoolean)) throw Bad();
                if (found.Length == 0) missingSupport.Add(support[i]);
            }
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Bad();
            targets.Add(new(name, definition is not null, validator, missing.ToArray(), missingSupport.ToArray()));
            if (name == B.ReceiptHeads) {
                if (await Rows("idCounters").Find(new BsonDocument("_id", name)).AnyAsync(ct)) throw Bad();
            } else await CounterAsync(name, maxima, ct);
        }
        foreach (var name in new[] { ProcurementStockReceiptBson.Receipts, "lots", "stockBalances", "movements" }) {
            await lease.RenewAsync(ct); var validator = ProcurementStockReceiptMigration.Required(name);
            await InventoryRepository.RequireStorageAsync(database, name, validator, ProcurementStockReceiptMigration.Keys(name), ct);
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Bad();
            await CounterAsync(name, maxima, ct);
        }
        await ProcurementStockReceiptSchemas.RequireDispositionStorageAsync(database, ct);
        foreach (var (name, maximum) in await new ProcurementStockReceiptMigration(database, clock).PreflightRetainedOwnersAsync(lease, ct))
            maxima[name] = Math.Max(maxima.GetValueOrDefault(name), maximum);
        await RequireOwnersAsync(lease, ct);
        // Every actual source, retained acceptance and reverse owner is proven before any DDL or counter repair.
        foreach (var target in targets) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { target.Exists ? "collMod" : "create", target.Name }, { "validator", target.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in target.Unique) await Rows(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true, Name = "ux_" + target.Name + "_" +
                    Array.FindIndex(ProcurementQualityAcceptanceSchemas.Keys(target.Name), value => value == key) }), cancellationToken: ct);
            foreach (var key in target.Support) await Rows(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = false, Name = "ix_" + target.Name + "_" +
                    Array.FindIndex(ProcurementQualityAcceptanceSchemas.SupportKeys(target.Name), value => value == key) }), cancellationToken: ct);
        }
        foreach (var (name, maximum) in maxima) if (maximum > 0) {
            await lease.RenewAsync(ct); await Rows("idCounters").WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(new BsonDocument("_id", name),
                Builders<BsonDocument>.Update.Max("value", maximum), new UpdateOptions { IsUpsert = true }, ct);
        }
    }
    private async Task RequireOwnersAsync(MigrationLease lease, CancellationToken ct) {
        if (database.Client is not MongoClient client) throw Bad(); var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database);
        var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog, new(database, unit, ids, clock, catalog), new(database, unit, ids, clock, catalog));
        var records = new ProcurementQualityAcceptanceRecords(database, unit, clock, catalog, orders, new(database, unit, ids, clock));
        using var tx = await client.StartSessionAsync(cancellationToken: ct); tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        try { await lease.RenewAsync(ct); await records.PreflightAsync(tx, ct); await tx.AbortTransactionAsync(ct); }
        catch { if (tx.IsInTransaction) await tx.AbortTransactionAsync(CancellationToken.None); throw; }
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private async Task<BsonDocument?> DefinitionAsync(string name, CancellationToken ct) {
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
        if (definition is null) return null; var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("encryptedFields") || options.Contains("expireAfterSeconds") ||
            options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error" || options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
        return definition;
    }
    private async Task CounterAsync(string name, Dictionary<string, long> maxima, CancellationToken ct) {
        var all = await Rows(name).Find(new BsonDocument()).Project(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (all.Any(row => !row["_id"].IsInt64 || row["_id"].AsInt64 is <= 0 or >= long.MaxValue - 1)) throw Bad();
        var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
        if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or >= long.MaxValue - 1)) throw Bad();
        maxima[name] = Math.Max(maxima.GetValueOrDefault(name), all.Select(row => row["_id"].AsInt64).DefaultIfEmpty(0).Max());
    }
    private static InvalidOperationException Bad() => new("Procurement quality acceptance migration preflight failed");
}
