using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Procurement.ProcurementAdvanceReturnBson;
using P = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;
using V = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed class ProcurementAdvanceReturnMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 76;
    private sealed record Target(string Name, bool Exists, BsonDocument Validator, BsonDocument[] Missing, BsonDocument[] Keys);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        try { await ApplyCoreAsync(lease, ct); }
        catch (Exception error) when (error is PersistenceConflictException or AuthorizationDeniedException or
            UnitToko.Modules.Contracts.DomainValidationException or InvalidOperationException or ArgumentException or
            KeyNotFoundException or InvalidCastException or FormatException or OverflowException)
        { throw new InvalidOperationException("Procurement advance return migration preflight failed", error); }
    }
    private async Task ApplyCoreAsync(MigrationLease lease, CancellationToken ct)
    {
        await lease.RenewAsync(ct);
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(77).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version ||
            history.Take(75).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Bad();
        await AccountingMigration.RequireInstalledStorageAsync(database, ct);
        var targets = new List<Target>(); var maxima = new Dictionary<string, long>();
        foreach (var name in B.Collections.Concat(ProcurementAdvanceReturnSchemas.ExtendedPayableCollections).Append(V.Parties))
        {
            await lease.RenewAsync(ct);
            var fresh = B.Collections.Contains(name);
            var previousRequired = fresh ? null : name == V.Parties ? PaymentVerificationSchemas.Validator(name) : ProcurementSupplierPayableSchemas.Validator(name);
            var required = fresh ? ProcurementAdvanceReturnSchemas.Validator(name) : name == V.Parties ? PaymentReceiptPurposeSchemas.Validator() : ProcurementAdvanceReturnSchemas.CurrentPayableValidator(name);
            var keys = fresh ? ProcurementAdvanceReturnSchemas.Keys(name) : name == V.Parties ? PaymentVerificationSchemas.Keys(name) : ProcurementSupplierPayableSchemas.Keys(name);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            if (!fresh && definition is null) throw Bad();
            var options = definition?["options"].AsBsonDocument ?? new BsonDocument();
            if (definition is not null && (definition["type"] != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("encryptedFields") || options.Contains("expireAfterSeconds") ||
                options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error" ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation))) throw Bad();
            var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument;
            var validator = fresh ? previous.ElementCount == 0 ? required : InventorySchemas.Contains(previous, required) ? previous :
                new BsonDocument("$and", new BsonArray { previous, required }) :
                PaymentManualReviewMigration.ExtendValidator(previous, previousRequired!, required, name);
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Bad();
            var indexes = definition is null ? [] : await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                index.GetValue("unique", false).AsBoolean && index["name"] != "_id_" && !keys.Any(key => index["key"] == key)) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1))) throw Bad();
            var missing = new List<BsonDocument>();
            foreach (var key in keys) {
                var matches = indexes.Where(index => index["key"] == key).ToArray();
                if (matches.Length > 1 || matches.Any(index => index["name"] != "_id_" && !index.GetValue("unique", false).AsBoolean)) throw Bad();
                if (matches.Length == 0 && key != new BsonDocument("_id", 1)) missing.Add(key);
                var group = new BsonDocument(key.Names.Select((field, position) => new BsonElement("k" + position, "$" + field)));
                if (await Rows(name).Aggregate<BsonDocument>(new BsonDocument[] {
                    new("$group", new BsonDocument { { "_id", group }, { "n", new BsonDocument("$sum", 1) } }),
                    new("$match", new BsonDocument("n", new BsonDocument("$gt", 1))), new("$limit", 1) }).AnyAsync(ct)) throw Bad();
            }
            if (fresh) {
                var all = await Rows(name).Find(new BsonDocument()).Project(new BsonDocument("_id", 1)).ToListAsync(ct);
                if (all.Any(row => !row["_id"].IsInt64 || row["_id"].AsInt64 is <= 0 or >= long.MaxValue - 1)) throw Bad();
                maxima[name] = all.Select(row => row["_id"].AsInt64).DefaultIfEmpty(0).Max();
                var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
                if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or >= long.MaxValue - 1)) throw Bad();
            }
            targets.Add(new(name, definition is not null, validator, missing.ToArray(), keys));
        }
        var reverseIndexes = new List<(string Table, string Name, BsonDocument Key)>();
        foreach (var candidate in ProcurementAdvanceReturnSchemas.ReverseIndexes) {
            var exists = await (await database.ListCollectionNamesAsync(new ListCollectionNamesOptions { Filter = new BsonDocument("name", candidate.Table) }, ct)).AnyAsync(ct);
            if (!exists && !B.Collections.Contains(candidate.Table)) throw Bad();
            var indexes = exists ? await (await Rows(candidate.Table).Indexes.ListAsync(ct)).ToListAsync(ct) : [];
            var matching = indexes.Where(index => index["key"] == candidate.Key).ToArray();
            if (matching.Length > 1 || indexes.Any(index => index["name"] == candidate.Name && index["key"] != candidate.Key) ||
                matching.Any(index => InvoiceStorage.Unsafe(index) || index.GetValue("unique", false).AsBoolean || index.Contains("partialFilterExpression"))) throw Bad();
            if (matching.Length == 0) reverseIndexes.Add(candidate);
        }
        // No DDL, counters or effects occur before every retained owner has been reconstructed.
        await RetainedAsync(lease, ct);
        foreach (var target in targets) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { target.Exists ? "collMod" : "create", target.Name },
                { "validator", target.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in target.Missing) await Rows(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true,
                    Name = "ux_" + target.Name + "_" + Array.FindIndex(target.Keys, value => value == key) }), cancellationToken: ct);
        }
        foreach (var (table, name, key) in reverseIndexes) {
            await lease.RenewAsync(ct);
            await Rows(table).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key,
                new CreateIndexOptions { Name = name, Unique = false }), cancellationToken: ct);
        }
        foreach (var (name, maximum) in maxima) if (maximum > 0) {
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
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog, requests, new(database, unit, ids, clock, catalog));
        var receiving = new ProcurementReceivingService(database, unit, ids, clock, catalog, orders, requests);
        var valuations = new ProcurementStockValuationService(database, unit, ids, clock, catalog, orders, receiving, new(database, unit, ids, clock));
        var banks = new BankReceiptService(database, unit, ids, clock, new(database, unit, ids, clock, new(database, unit, ids, clock)));
        var disbursements = new ProcurementDisbursementService(database, unit, ids, clock, catalog,
            new(database, unit, ids, clock, catalog, orders), new(database, unit, ids, clock, catalog), banks);
        var settlements = new ProcurementSettlementService(database, unit, ids, clock, catalog, disbursements, banks);
        var payables = new ProcurementSupplierPayableService(database, unit, ids, clock, valuations, settlements);
        var returns = new ProcurementAdvanceReturnService(database, unit, ids, clock, settlements, banks, payables, requests);
        using var tx = await client.StartSessionAsync(cancellationToken: ct);
        tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        try {
            await payables.PreflightAsync(tx, ct); await lease.RenewAsync(ct);
            await returns.PreflightAsync(tx, ct); await lease.RenewAsync(ct);
            var verified = new PaymentVerificationRecords(database);
            foreach (var row in await Rows(V.Decisions).Find(tx, new BsonDocument()).Project(new BsonDocument("_id", 1)).ToListAsync(ct)) {
                await lease.RenewAsync(ct);
                var decision = await verified.RawAsync(tx, row["_id"].AsInt64, ct);
                var drafts = new PaymentDraftRecords(database);
                var head = await drafts.HeadAsync(tx, decision["source"]["recordedBy"].AsInt64, decision["declarationId"].AsInt64, ct);
                var version = await drafts.VersionAsync(tx, head["versionId"].AsInt64, ct);
                var submitted = await new PaymentSubmissionRecords(database).ReadAsync(tx, head["submissionId"].AsInt64, ct);
                if (!InvoiceRecords.Equal(decision["source"], PaymentDraftBson.SubmittedSource(head, version, submitted))) throw Bad();
                var bank = await BankReceiptService.ReadSupplierReturnHistoryAsync(database, tx, decision["receiptId"].AsInt64, ct);
                if (decision["receiptHash"] != bank.Bank.Receipt["snapshotHash"]) throw Bad();
            }
            foreach (var party in await Rows(V.Parties).Find(tx, new BsonDocument("schemaVersion", 1)).ToListAsync(ct)) {
                await lease.RenewAsync(ct);
                _ = await new PaymentReceiptPurposeRecords(database).CustomerAsync(tx, party["_id"].AsInt64,
                    party["receiptHash"], party["party"].AsBsonDocument, true, ct);
            }
            await tx.AbortTransactionAsync(ct);
        } catch { if (tx.IsInTransaction) await tx.AbortTransactionAsync(CancellationToken.None); throw; }
    }
    private static InvalidOperationException Bad() => new("Procurement advance return migration preflight failed");
}
