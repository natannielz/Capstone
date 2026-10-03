using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Organizations;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Modules.Contracts;

namespace UnitToko.Platform.Mongo.Migrations;

// Installs empty supplier claims and quarantine dispositions; no supplier action, stock or financial release is inferred.
internal sealed partial class SupplierClaimMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 71;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        try { await ApplyCoreAsync(lease, ct); }
        catch (PersistenceConflictException) { throw Bad(); }
        catch (AuthorizationDeniedException) { throw Bad(); }
        catch (DomainValidationException) { throw Bad(); }
    }
    private async Task ApplyCoreAsync(MigrationLease lease, CancellationToken ct) {
        await lease.RenewAsync(ct); await RequireHistoryAsync(ct);
        var installs = new List<(string Name, bool Exists, BsonDocument Validator, BsonDocument[] Missing)>();
        foreach (var name in SupplierClaimSchemas.Collections) {
            await lease.RenewAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad();
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
            var keys = NewKeys(name); var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
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
            await RequireEmptyCounterAsync(name, ct);
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = NewValidator(name);
            installs.Add((name, definition is not null, current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current :
                new BsonDocument("$and", new BsonArray { current, required }), missing.ToArray()));
        }
        var prefix = new BsonRegularExpression(@"^procurement\.supplierclaim(?:[.:]|$)");
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"),
            ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation"), ("journalEntries", "source.namespace") })
            if (await database.GetCollection<BsonDocument>(table).WithReadConcern(ReadConcern.Majority).Find(new BsonDocument(field, prefix)).Limit(1).AnyAsync(ct)) throw Bad();
        await RequireInstalledClaimsAsync(ct);
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
        // Retained storage is checked before the historical owner proofs below and before all DDL.
        foreach (var name in ProcurementRequestSchemas.Collections.Concat(SupplierSchemas.Collections).Concat(PurchaseOrderSchemas.Collections).Concat(ProcurementFundingSchemas.Collections).Concat(SupplierBankSchemas.Collections).Concat(ProcurementDisbursementSchemas.Collections).Concat(ProcurementSettlementSchemas.Collections).Concat(ProcurementReceivingSchemas.Collections).Concat(ProcurementInspectionSchemas.Collections)) {
            await lease.RenewAsync(ct);
            var request = ProcurementRequestSchemas.Collections.Contains(name);
            var supplier = SupplierSchemas.Collections.Contains(name);
            var order = PurchaseOrderSchemas.Collections.Contains(name); var supplierBank = SupplierBankSchemas.Collections.Contains(name); var disbursement = ProcurementDisbursementSchemas.Collections.Contains(name); var settlement = ProcurementSettlementSchemas.Collections.Contains(name); var receiving = ProcurementReceivingSchemas.Collections.Contains(name); var inspection = ProcurementInspectionSchemas.Collections.Contains(name);
            var validator = request ? ProcurementRequestSchemas.Validator(name) : supplier ? SupplierSchemas.Validator(name) : order ? PurchaseOrderSchemas.Validator(name) : supplierBank ? SupplierBankSchemas.Validator(name) : disbursement ? ProcurementDisbursementSchemas.Validator(name) : settlement ? ProcurementSettlementSchemas.Validator(name) : receiving ? ProcurementReceivingSchemas.Validator(name) : inspection ? ProcurementInspectionSchemas.Validator(name) : ProcurementFundingSchemas.Validator(name);
            var keys = request ? ProcurementRequestSchemas.Keys(name) : supplier ? SupplierSchemas.Keys(name) : order ? PurchaseOrderSchemas.Keys(name) : supplierBank ? SupplierBankSchemas.Keys(name) : disbursement ? ProcurementDisbursementSchemas.Keys(name) : settlement ? ProcurementSettlementSchemas.Keys(name) : receiving ? ProcurementReceivingSchemas.Keys(name) : inspection ? ProcurementInspectionSchemas.Keys(name) : ProcurementFundingSchemas.Keys(name);
            await InventoryRepository.RequireStorageAsync(database, name, validator, keys, ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            if (await rows.Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Bad();
            var maximum = await rows.Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
            if (name is ProcurementReceivingBson.Heads or ProcurementInspectionBson.Heads) {
                if (await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).AnyAsync(ct)) throw Bad();
            } else maxima.Add(name, maximum is null ? 0 : maximum["_id"].AsInt64);
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
        await SupplierDocumentEvidenceSchemas.RequireRetainedStorageForClaimsAsync(database, lease, ct);
        foreach (var (name, maximum) in await new ProcurementReceivingMigration(database, clock).PreflightRetainedForInspectionAsync(lease, ct)) maxima.Add(name, maximum);
        await new ProcurementInspectionMigration(database, clock).PreflightRetainedReceivingForSupplierDocumentAsync(lease, ct);
        await new SupplierDocumentMigration(database, clock).PreflightRetainedInspectionsForClaimsAsync(lease, ct);
        await PreflightSupplierDocumentsAsync(lease, maxima, ct);
        // Every retained source, graph, storage and counter check precedes DDL or high-water updates.
        // M67 canonical claim storage and every retained financial row remain unchanged.
        foreach (var item in installs) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { item.Exists ? "collMod" : "create", item.Name },
                { "validator", item.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in item.Missing) await database.GetCollection<BsonDocument>(item.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true, Name = "ux_" + item.Name + "_" +
                    Array.FindIndex(NewKeys(item.Name), expected => expected == key) }), cancellationToken: ct);
        }
        foreach (var (name, maximum) in maxima) if (maximum > 0) {
            await lease.RenewAsync(ct);
            await database.GetCollection<BsonDocument>("idCounters").WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(new BsonDocument("_id", name),
                Builders<BsonDocument>.Update.Max("value", maximum), new UpdateOptions { IsUpsert = true }, ct);
        }
    }
    private async Task CounterAsync(string name, CancellationToken ct) {
        var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
        if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or >= long.MaxValue - 1)) throw Bad();
    }
    private async Task RequireEmptyCounterAsync(string name, CancellationToken ct) {
        var row = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
        if (row is not null && (!row.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 != 0)) throw Bad();
    }
    private async Task RequireInstalledClaimsAsync(CancellationToken ct) {
        await ProcurementBankClaimSchemas.RequireStorageAsync(database, ct);
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", BankTransactionClaims.Collection) }, ct)).SingleAsync(ct);
        if (!InventorySchemas.Contains(definition["options"]["validator"].AsBsonDocument, ProcurementBankClaimSchemas.Validator()) ||
            await database.GetCollection<BsonDocument>(BankTransactionClaims.Collection).WithReadConcern(ReadConcern.Majority)
                .Find(new BsonDocument("$nor", new BsonArray { ProcurementBankClaimSchemas.Validator() })).Limit(1).AnyAsync(ct)) throw Bad();
    }
    private async Task RequireHistoryAsync(CancellationToken ct) {
        var history = await database.GetCollection<BsonDocument>("schemaMigrations").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(72).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(70).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Bad();
    }
    private static InvalidOperationException Bad() => new("Supplier claim migration preflight failed");
    private static BsonDocument[] NewKeys(string name) => SupplierClaimSchemas.Keys(name);
    private static BsonDocument NewValidator(string name) => SupplierClaimSchemas.Validator(name);
}
