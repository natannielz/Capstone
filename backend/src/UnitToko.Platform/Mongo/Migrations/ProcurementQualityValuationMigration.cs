using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Procurement;
using D = UnitToko.Platform.Procurement.SupplierDocumentBson;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed class ProcurementQualityValuationMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 79;
    private sealed record Install(string Name, BsonDocument Validator);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        try { await ApplyCoreAsync(lease, ct); }
        catch (Exception error) when (error is PersistenceConflictException or AuthorizationDeniedException or DomainValidationException or
            InvalidOperationException or ArgumentException or KeyNotFoundException or InvalidCastException or FormatException or OverflowException) {
            throw new InvalidOperationException("Procurement quality valuation migration preflight failed", error);
        }
    }
    private async Task ApplyCoreAsync(MigrationLease lease, CancellationToken ct) {
        await lease.RenewAsync(ct);
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Bad();
        await RequirePredecessorAsync(ct);
        var installs = new List<Install>();
        foreach (var name in ProcurementQualityValuationSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw Bad();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") ||
                options.Contains("encryptedFields") || options.Contains("expireAfterSeconds") || options.GetValue("validationLevel", "strict") != "strict" ||
                options.GetValue("validationAction", "error") != "error" || options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
            var legacy = name == D.Matches ? SupplierDocumentSchemas.Validator(name) : ProcurementStockValuationSchemas.Validator(name);
            var current = name == D.Matches ? ProcurementQualityValuationSchemas.PrePdfDocumentValidator(name) : ProcurementQualityValuationSchemas.CurrentValuationValidator(name);
            var extended = PaymentManualReviewMigration.ExtendValidator(options.GetValue("validator", new BsonDocument()).AsBsonDocument, legacy, current, name);
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { extended })).Limit(1).AnyAsync(ct)) throw Bad();
            var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var keys = name == D.Matches ? SupplierDocumentSchemas.Keys(name) : ProcurementStockValuationSchemas.Keys(name);
            if (!indexes.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1)) ||
                indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                    index.GetValue("unique", false).AsBoolean && index["name"] != "_id_" && !keys.Any(key => index["key"] == key))) throw Bad();
            foreach (var key in keys) {
                var matching = indexes.Where(index => index["key"] == key).ToArray();
                if (matching.Length != 1 || matching[0]["name"] != "_id_" && !matching[0].GetValue("unique", false).AsBoolean) throw Bad();
            }
            // These existing owners already allocate IDs. An absent/lagging counter is corruption,
            // not a reason for this extension to alter retained business or allocation state.
            var maximum = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or >= long.MaxValue - 1) ||
                maximum is not null && (counter is null || counter["value"].AsInt64 < maximum["_id"].AsInt64)) throw Bad();
            installs.Add(new(name, extended));
        }
        var missing = new List<(string Table, string Name, BsonDocument Key)>();
        foreach (var candidate in ProcurementQualityValuationSchemas.LookupIndexes) {
            var indexes = await (await Rows(candidate.Table).Indexes.ListAsync(ct)).ToListAsync(ct);
            var matching = indexes.Where(index => index["key"] == candidate.Key || index["name"] == candidate.Name).ToArray();
            if (matching.Length > 1 || matching.Any(index => index["key"] != candidate.Key || InvoiceStorage.Unsafe(index) ||
                index.GetValue("unique", false).AsBoolean || index.Contains("partialFilterExpression"))) throw Bad();
            if (matching.Length == 0) missing.Add(candidate);
        }
        if (database.Client is not MongoClient client) throw Bad();
        var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database); var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog, new(database, unit, ids, clock, catalog), new(database, unit, ids, clock, catalog));
        await lease.RenewAsync(ct);
        await ProcurementMixedQualityPreflight.RequireAsync(database, unit, clock, catalog, orders, new StockSourceSetupService(database, unit, ids, clock), ct);
        // No schema/index changes precede complete retained v1/v2, private-original and quality proofs.
        foreach (var install in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", install.Name }, { "validator", install.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        }
        foreach (var (table, name, key) in missing) {
            await lease.RenewAsync(ct);
            await Rows(table).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key,
                new CreateIndexOptions { Name = name, Unique = false }), cancellationToken: ct);
        }
    }
    private async Task RequirePredecessorAsync(CancellationToken ct) {
        await ProcurementAdvanceReturnService.RequireReturnStorageAsync(database, ct);
        foreach (var name in ProcurementQualityAcceptanceSchemas.Collections) {
            await InventoryRepository.RequireStorageAsync(database, name, ProcurementQualityAcceptanceSchemas.Validator(name), ProcurementQualityAcceptanceSchemas.Keys(name), ct);
            var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            foreach (var key in ProcurementQualityAcceptanceSchemas.SupportKeys(name))
                if (!indexes.Any(index => index["key"] == key && !InvoiceStorage.Unsafe(index) && !index.GetValue("unique", false).AsBoolean && !index.Contains("partialFilterExpression"))) throw Bad();
        }
        foreach (var name in SupplierDocumentSchemas.Collections.Except(ProcurementQualityValuationSchemas.Collections))
            await InventoryRepository.RequireStorageAsync(database, name, SupplierDocumentSchemas.Validator(name), SupplierDocumentSchemas.Keys(name), ct);
        foreach (var name in ProcurementStockValuationSchemas.Collections.Except(ProcurementQualityValuationSchemas.Collections))
            await InventoryRepository.RequireStorageAsync(database, name, ProcurementStockValuationSchemas.Validator(name), ProcurementStockValuationSchemas.Keys(name), ct);
        await AccountingMigration.RequireInstalledStorageAsync(database, ct);
    }
    private static InvalidOperationException Bad() => new("Procurement quality valuation migration preflight failed");
}
