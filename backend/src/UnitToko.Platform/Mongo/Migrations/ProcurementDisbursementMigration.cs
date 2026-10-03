using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Organizations;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Procurement;

namespace UnitToko.Platform.Mongo.Migrations;

// Installs empty private procurement disbursement records; no policy, transfer or accounting fact is seeded.
internal sealed partial class ProcurementDisbursementMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 66;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        await lease.RenewAsync(ct); await RequireHistoryAsync(ct);
        var installs = new List<(string Name, bool Exists, BsonDocument Validator, BsonDocument[] Missing)>();
        foreach (var name in ProcurementDisbursementSchemas.Collections) {
            await lease.RenewAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad();
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
            var keys = ProcurementDisbursementSchemas.Keys(name); var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                    index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !keys.Any(key => index["key"] == key)) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1))) throw Bad();
            var missing = new List<BsonDocument>();
            for (var i = 0; i < keys.Length; i++) {
                var matching = indexes.Where(index => index["key"] == keys[i] || index["name"] == "ux_" + name + "_" + i).ToArray();
                if (matching.Any(index => index["key"] != keys[i] || index["name"] != "_id_" && !index.GetValue("unique", false).AsBoolean)) throw Bad();
                // Mongo creates the inherently unique _id_ index; its metadata need not repeat unique:true.
                if (matching.Length == 0 && keys[i] != new BsonDocument("_id", 1)) missing.Add(keys[i]);
            }
            await CounterAsync(name, ct);
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = ProcurementDisbursementSchemas.Validator(name);
            installs.Add((name, definition is not null, current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current :
                new BsonDocument("$and", new BsonArray { current, required }), missing.ToArray()));
        }
        var prefix = new BsonRegularExpression(@"^procurement\.disbursement(?:[.:]|$)");
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"),
            ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation") })
            if (await database.GetCollection<BsonDocument>(table).WithReadConcern(ReadConcern.Majority).Find(new BsonDocument(field, prefix)).Limit(1).AnyAsync(ct)) throw Bad();
        var evidence = await ProcurementDisbursementEvidenceSchemas.PreflightAsync(database, lease, ct);
        var retained = new Dictionary<string, Dictionary<long, BsonDocument>>(); var maxima = new Dictionary<string, long>();
        foreach (var name in CatalogSchemas.Collections) {
            await lease.RenewAsync(ct); var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw Bad();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
                options.Contains("encryptedFields") || options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error" ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation) ||
                !options.TryGetValue("validator", out var validator) || !validator.IsBsonDocument || !InventorySchemas.Contains(validator.AsBsonDocument, CatalogSchemas.Validator(name))) throw Bad();
            var indexes = await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => index.Contains("expireAfterSeconds")) || !indexes.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1) && !InvoiceStorage.Unsafe(index) && !index.Contains("partialFilterExpression"))) throw Bad();
            var all = await rows.Find(new BsonDocument()).ToListAsync(ct);
            if (all.Any(row => !CatalogSchemas.IsValid(name, row)) || await rows.Find(new BsonDocument("$nor", new BsonArray { CatalogSchemas.Validator(name) })).Limit(1).AnyAsync(ct)) throw Bad();
            retained.Add(name, all.ToDictionary(row => row["_id"].AsInt64)); maxima.Add(name, all.Select(row => row["_id"].AsInt64).DefaultIfEmpty(0).Max());
        }
        await CatalogMigrationGraph.ValidateAsync(database, retained, ct);
        // Request, supplier, approved purchase-order and funding owners retain their independent guards and records.
        // Runtime consumers verify their full source/effect provenance inside the calling transaction.
        foreach (var name in ProcurementRequestSchemas.Collections.Concat(SupplierSchemas.Collections).Concat(PurchaseOrderSchemas.Collections).Concat(ProcurementFundingSchemas.Collections).Concat(SupplierBankSchemas.Collections)) {
            await lease.RenewAsync(ct);
            var request = ProcurementRequestSchemas.Collections.Contains(name);
            var supplier = SupplierSchemas.Collections.Contains(name);
            var order = PurchaseOrderSchemas.Collections.Contains(name); var supplierBank = SupplierBankSchemas.Collections.Contains(name);
            var validator = request ? ProcurementRequestSchemas.Validator(name) : supplier ? SupplierSchemas.Validator(name) : order ? PurchaseOrderSchemas.Validator(name) : supplierBank ? SupplierBankSchemas.Validator(name) : ProcurementFundingSchemas.Validator(name);
            var keys = request ? ProcurementRequestSchemas.Keys(name) : supplier ? SupplierSchemas.Keys(name) : order ? PurchaseOrderSchemas.Keys(name) : supplierBank ? SupplierBankSchemas.Keys(name) : ProcurementFundingSchemas.Keys(name);
            await InventoryRepository.RequireStorageAsync(database, name, validator, keys, ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            if (await rows.Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Bad();
            var maximum = await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
            maxima.Add(name, maximum is null ? 0 : maximum["_id"].AsInt64);
        }
        foreach (var name in new[] { "auditEvents", "idempotencyRecords", "outboxEvents" }) {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw Bad();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
                options.Contains("encryptedFields") || options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
            PaymentEvidenceMigration.RequireIndexes(name, await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct));
        }
        var audits = await database.GetCollection<BsonDocument>("auditEvents").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument()).Project(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (audits.Any(row => !row.TryGetValue("_id", out var id) || !id.IsInt64 || id.AsInt64 is <= 0 or >= long.MaxValue - 1)) throw Bad();
        maxima.Add("auditEvents", audits.Select(row => row["_id"].AsInt64).DefaultIfEmpty(0).Max());
        foreach (var (name, maximum) in maxima) { if (maximum >= long.MaxValue - 1) throw Bad(); await CounterAsync(name, ct); }
        await OrganizationMasterDataService.PreflightAddressSourcesAsync(database, lease, ct);
        await PreflightFinancialSourcesAsync(lease, maxima, ct);
        // Every retained source, graph, storage and counter check precedes DDL or high-water updates.
        await ProcurementDisbursementEvidenceSchemas.ApplyAsync(database, evidence, lease, ct);
        foreach (var item in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { item.Exists ? "collMod" : "create", item.Name },
                { "validator", item.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in item.Missing) await database.GetCollection<BsonDocument>(item.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true, Name = "ux_" + item.Name + "_" +
                    Array.FindIndex(ProcurementDisbursementSchemas.Keys(item.Name), expected => expected == key) }), cancellationToken: ct);
        }
        foreach (var (name, maximum) in maxima) if (maximum > 0) {
            await lease.RenewAsync(ct);
            await database.GetCollection<BsonDocument>("idCounters").WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(new BsonDocument("_id", name),
                Builders<BsonDocument>.Update.Max("value", maximum), new UpdateOptions { IsUpsert = true }, ct);
        }
    }
    private async Task PreflightFinancialSourcesAsync(MigrationLease lease, Dictionary<string, long> maxima, CancellationToken ct) {
        foreach (var (names, validator, keys) in new (string[], Func<string, BsonDocument>, Func<string, BsonDocument[]>)[] {
            (ReceivingAccountSchemas.Collections, ReceivingAccountSchemas.Validator, name => [ReceivingAccountSchemas.Key(name)]),
            (BankStatementSchemas.Collections, BankStatementSchemas.Validator, name => [BankStatementSchemas.Key(name)]),
            (BankReceiptSchemas.Collections, BankReceiptSchemas.Validator, BankReceiptSchemas.Keys),
            (RefundPayoutSchemas.Collections, RefundPayoutSchemas.Validator, RefundPayoutSchemas.Keys) }) {
            foreach (var name in names) {
                await lease.RenewAsync(ct); await InventoryRepository.RequireStorageAsync(database, name, validator(name), keys(name), ct);
                if (await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("$nor", new BsonArray { validator(name) })).Limit(1).AnyAsync(ct)) throw Bad();
                await CounterAsync(name, ct);
                var maximum = await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
                if (maximum is not null && (!maximum["_id"].IsInt64 || maximum["_id"].AsInt64 >= long.MaxValue - 1)) throw Bad();
                maxima.Add(name, maximum is null ? 0 : maximum["_id"].AsInt64);
            }
        }
        await BankTransactionClaimSchemas.RequireStorageAsync(database, ct);
        if (database.Client is not MongoClient client) throw Bad();
        using var tx = await client.StartSessionAsync(cancellationToken: ct);
        tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
        try {
            await BankReceiptService.PreflightPayoutBanksAsync(database, clock, tx, ct);
            await PreflightRetainedBankClaimsAsync(tx, lease, ct);
            var payoutRecords = new RefundPayoutRecords(database, clock, new MongoUnitOfWork(client));
            foreach (var name in new[] { RefundPayoutBson.Drafts, RefundPayoutBson.Payouts }) {
                using var cursor = await database.GetCollection<BsonDocument>(name).Find(tx, new BsonDocument()).ToCursorAsync(ct);
                while (await cursor.MoveNextAsync(ct)) foreach (var row in cursor.Current) {
                    var verified = name == RefundPayoutBson.Drafts ? await payoutRecords.DraftAsync(tx, row["_id"].AsInt64, ct) : await payoutRecords.PayoutAsync(tx, row["_id"].AsInt64, false, ct);
                    var at = name == RefundPayoutBson.Drafts ? verified["createdAt"] : (await payoutRecords.DraftAsync(tx, verified["draftId"].AsInt64, ct))["createdAt"];
                    _ = await BankReceiptService.ReadPayoutBankHistoryAsync(database, tx, verified["source"]["payingBank"].AsBsonDocument, new(at.ToUniversalTime()), ct);
                }
            }
            await tx.AbortTransactionAsync(ct);
        } catch { if (tx.IsInTransaction) await tx.AbortTransactionAsync(CancellationToken.None); throw; }
    }
    private async Task CounterAsync(string name, CancellationToken ct) {
        var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
        if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or >= long.MaxValue - 1)) throw Bad();
    }
    private async Task RequireHistoryAsync(CancellationToken ct) {
        var history = await database.GetCollection<BsonDocument>("schemaMigrations").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(67).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(65).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Bad();
    }
    private static InvalidOperationException Bad() => new("Procurement disbursement migration preflight failed");
}
