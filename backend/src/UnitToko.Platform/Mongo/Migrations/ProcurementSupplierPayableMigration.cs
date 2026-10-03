using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed class ProcurementSupplierPayableMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 75;
    private sealed record Target(string Name, bool Exists, BsonDocument Validator, BsonDocument[] Missing);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        try { await ApplyCoreAsync(lease, ct); }
        catch (Exception error) when (error is PersistenceConflictException or AuthorizationDeniedException or
            UnitToko.Modules.Contracts.DomainValidationException or InvalidOperationException or ArgumentException or
            KeyNotFoundException or InvalidCastException or FormatException or OverflowException)
        { throw new InvalidOperationException("Procurement supplier payable migration preflight failed", error); }
    }
    private async Task ApplyCoreAsync(MigrationLease lease, CancellationToken ct)
    {
        await lease.RenewAsync(ct);
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(76).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version ||
            history.Take(74).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Bad();
        await AccountingMigration.RequireInstalledStorageAsync(database, ct);
        foreach (var name in ProcurementStockValuationSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, ProcurementStockValuationSchemas.Validator(name),
                ProcurementStockValuationSchemas.Keys(name), ct);
        foreach (var name in ProcurementSettlementSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, ProcurementSettlementSchemas.Validator(name),
                ProcurementSettlementSchemas.Keys(name), ct);
        var targets = new List<Target>(); var maxima = new Dictionary<string, long>();
        foreach (var name in ProcurementSupplierPayableSchemas.Collections)
        {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct))
                .SingleOrDefaultAsync(ct);
            var options = definition?["options"].AsBsonDocument ?? new BsonDocument();
            if (definition is not null && (definition["type"] != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("encryptedFields") || options.Contains("expireAfterSeconds") ||
                options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error" ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation))) throw Bad();
            var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var required = ProcurementSupplierPayableSchemas.Validator(name);
            var validator = previous.ElementCount == 0 ? required : InventorySchemas.Contains(previous, required) ? previous :
                new BsonDocument("$and", new BsonArray { previous, required });
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Bad();
            var keys = ProcurementSupplierPayableSchemas.Keys(name);
            var indexes = definition is null ? [] : await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                index.GetValue("unique", false).AsBoolean && index["name"] != "_id_" && !keys.Any(key => index["key"] == key)) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1))) throw Bad();
            var missing = new List<BsonDocument>();
            foreach (var key in keys)
            {
                var matches = indexes.Where(index => index["key"] == key).ToArray();
                if (matches.Length > 1 || matches.Any(index => index["name"] != "_id_" && !index.GetValue("unique", false).AsBoolean)) throw Bad();
                if (matches.Length == 0 && key != new BsonDocument("_id", 1)) missing.Add(key);
                var group = new BsonDocument(key.Names.Select((field, position) => new BsonElement("k" + position, "$" + field)));
                if (await Rows(name).Aggregate<BsonDocument>(new BsonDocument[] { new("$group", new BsonDocument { { "_id", group }, { "n", new BsonDocument("$sum", 1) } }),
                    new("$match", new BsonDocument("n", new BsonDocument("$gt", 1))), new("$limit", 1) }).AnyAsync(ct)) throw Bad();
            }
            var all = await Rows(name).Find(new BsonDocument()).Project(new BsonDocument("_id", 1)).ToListAsync(ct);
            if (all.Any(row => !row["_id"].IsInt64 || row["_id"].AsInt64 is <= 0 or >= long.MaxValue - 1)) throw Bad();
            maxima[name] = all.Select(row => row["_id"].AsInt64).DefaultIfEmpty(0).Max();
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (name is B.PayableHeads or B.AdvanceHeads) { if (counter is not null) throw Bad(); }
            else if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or >= long.MaxValue - 1)) throw Bad();
            targets.Add(new(name, definition is not null, validator, missing.ToArray()));
        }
        await RetainedAsync(lease, ct);
        foreach (var target in targets)
        {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { target.Exists ? "collMod" : "create", target.Name },
                { "validator", target.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in target.Missing)
                await Rows(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true,
                        Name = "ux_" + target.Name + "_" + Array.FindIndex(ProcurementSupplierPayableSchemas.Keys(target.Name), value => value == key) }),
                    cancellationToken: ct);
        }
        foreach (var (name, maximum) in maxima) if (maximum > 0 && name is not (B.PayableHeads or B.AdvanceHeads)) {
            await lease.RenewAsync(ct); await Rows("idCounters").WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(new BsonDocument("_id", name),
                Builders<BsonDocument>.Update.Max("value", maximum), new UpdateOptions { IsUpsert = true }, ct);
        }
    }
    private async Task RetainedAsync(MigrationLease lease, CancellationToken ct)
    {
        if (database.Client is not MongoClient client) throw Bad();
        var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database);
        var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var requests = new ProcurementRequestService(database, unit, ids, clock, catalog);
        var suppliers = new SupplierService(database, unit, ids, clock, catalog);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog, requests, suppliers);
        var receiving = new ProcurementReceivingService(database, unit, ids, clock, catalog, orders, requests);
        var sources = new StockSourceSetupService(database, unit, ids, clock);
        var valuations = new ProcurementStockValuationService(database, unit, ids, clock, catalog, orders, receiving, sources);
        var valuationRecords = new ProcurementStockValuationRecords(database, unit, clock, catalog, orders, sources);
        var accounts = new ReceivingAccountService(database, unit, ids, clock);
        var statements = new BankStatementService(database, unit, ids, clock, accounts);
        var banks = new BankReceiptService(database, unit, ids, clock, statements);
        var funding = new ProcurementFundingService(database, unit, ids, clock, catalog, orders);
        var beneficiaries = new SupplierBankService(database, unit, ids, clock, catalog);
        var disbursements = new ProcurementDisbursementService(database, unit, ids, clock, catalog, funding, beneficiaries, banks);
        var settlements = new ProcurementSettlementService(database, unit, ids, clock, catalog, disbursements, banks);
        var settlementRecords = new ProcurementSettlementRecords(database, clock, disbursements);
        var payables = new ProcurementSupplierPayableService(database, unit, ids, clock, valuations, settlements);
        using var tx = await client.StartSessionAsync(cancellationToken: ct);
        tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        try
        {
            foreach (var raw in await Rows(ProcurementStockValuationBson.Proposals).Find(tx, new BsonDocument()).Project(new BsonDocument("_id", 1)).ToListAsync(ct)) {
                await lease.RenewAsync(ct); var proposal = await valuationRecords.ProposalAsync(tx, raw["_id"].AsInt64, ct);
                _ = await valuationRecords.DecisionAsync(tx, proposal, ct);
            }
            await valuationRecords.OrphansAsync(tx, ct);
            foreach (var raw in await Rows(ProcurementStockValuationBson.Allocations).Find(tx, new BsonDocument()).Project(new BsonDocument("_id", 1)).ToListAsync(ct)) {
                await lease.RenewAsync(ct); _ = await valuations.ReadPayableSourceAsync(database, tx, raw["_id"].AsInt64, ct);
            }
            foreach (var raw in await Rows(ProcurementSettlementBson.Events).Find(tx, new BsonDocument()).Project(new BsonDocument("_id", 1)).ToListAsync(ct)) {
                await lease.RenewAsync(ct); _ = await settlements.ReadPayableSourceAsync(database, tx, raw["_id"].AsInt64, ct);
            }
            await settlementRecords.OrphansAsync(tx, ct);
            await sources.ReconciliationPreflightAsync(tx, ct);
            await payables.PreflightAsync(tx, ct);
            await tx.AbortTransactionAsync(ct);
        }
        catch { if (tx.IsInTransaction) await tx.AbortTransactionAsync(CancellationToken.None); throw; }
    }
    private static InvalidOperationException Bad() => new("Procurement supplier payable migration preflight failed");
}
