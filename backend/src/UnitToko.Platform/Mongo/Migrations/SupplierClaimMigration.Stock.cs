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

// Shared read-only retained-owner preflight; migration 71 still invokes this at its original boundary.
internal sealed partial class SupplierClaimMigration
{
    internal async Task<Dictionary<string, long>> PreflightRetainedForStockAsync(MigrationLease lease, CancellationToken ct) {
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
        return maxima;
    }
}
