using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Procurement;
using UnitToko.Platform.Identity;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Forward-only explicit payment/supplier PDF purposes; no legacy row is reinterpreted as a PDF.</summary>
internal sealed class PdfEvidenceAdmissionMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 85;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("PDF evidence admission migration found incompatible retained storage");
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(Version - 1).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();
        await SupplierDocumentEvidenceSchemas.RequireStorageAsync(database, ct);
        await AccountContactStorage.RequireAsync(database, ct);
        foreach (var name in AccountContactSchemas.Collections)
            await RequirePredecessorAsync(name, AccountContactSchemas.Validator(name), AccountContactSchemas.Keys(name), lease, ct);
        foreach (var name in EvidenceImageRevalidationSchemas.Collections)
            await RequirePredecessorAsync(name, EvidenceImageRevalidationSchemas.Validator(name), EvidenceImageRevalidationSchemas.Keys(name), lease, ct);
        var replacements = new List<(string Name, BsonDocument Previous, BsonDocument Next)>();
        foreach (var name in PdfEvidenceAdmissionSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw Invalid();
            if (!PdfEvidenceAdmissionSchemas.SafeDefinition(definition)) throw Invalid();
            var current = definition["options"]["validator"].AsBsonDocument;
            var next = PdfEvidenceAdmissionSchemas.Extend(name, current);
            PaymentEvidenceMigration.RequireIndexes(name, await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct));
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { current })).Limit(1).AnyAsync(ct) ||
                await Rows(name).Find(new BsonDocument("$nor", new BsonArray { next })).Limit(1).AnyAsync(ct)) throw Invalid();
            replacements.Add((name, current.DeepClone().AsBsonDocument, next));
        }
        // M83 proof storage can contain independently approved policies, but no pre-85 writer owns a PDF admission/job/result.
        foreach (var name in PdfEvidenceValidationSchemas.Collections) {
            await lease.RenewAsync(ct);
            await UnitToko.Platform.Inventory.InventoryRepository.RequireStorageAsync(database, name,
                PdfEvidenceValidationSchemas.Validator(name), PdfEvidenceValidationSchemas.Key(name), ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleAsync(ct);
            if (!PdfEvidenceAdmissionSchemas.SafeDefinition(definition) ||
                await Rows(name).Find(new BsonDocument("$nor", new BsonArray { definition["options"]["validator"] })).Limit(1).AnyAsync(ct)) throw Invalid();
            if (name != PdfEvidenceValidationRecords.Policies && await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
        }
        if (await Rows("outboxEvents").Find(new BsonDocument("kind", PdfEvidenceValidationRecords.Kind)).Limit(1).AnyAsync(ct)) throw Invalid();
        foreach (var name in new[] { "evidenceLineages", "evidenceUploadReservations" })
            if (await Rows(name).Find(new BsonDocument("purpose", new BsonDocument("$in", new BsonArray { "PaymentPdf", "SupplierDocumentPdf" }))).Limit(1).AnyAsync(ct)) throw Invalid();
        foreach (var (name, field) in new[] { ("auditEvents", "evidence.purpose"), ("idempotencyRecords", "operation") }) {
            var values = field == "operation" ? new BsonArray { "evidence.payment-pdf.reserve", "evidence.payment-pdf.complete", "evidence.supplier-document-pdf.reserve", "evidence.supplier-document-pdf.complete" }
                : new BsonArray { "PaymentPdf", "SupplierDocumentPdf" };
            if (await Rows(name).Find(new BsonDocument(field, new BsonDocument("$in", values))).Limit(1).AnyAsync(ct)) throw Invalid();
        }
        // Reconstruct retained original admissions and histories before the first DDL; current user authority is never inferred.
        if (database.Client is not MongoClient client) throw Invalid();
        var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database); var clock = new SystemClock();
        var catalog = new CatalogAuthoringService(database, unit, ids, clock);
        var orders = new PurchaseOrderService(database, unit, ids, clock, catalog,
            new(database, unit, ids, clock, catalog), new(database, unit, ids, clock, catalog));
        var records = new SupplierDocumentRecords(database, unit, clock, catalog, orders);
        await SupplierDocumentEvidenceSchemas.RequireHistoriesAsync(database, lease, (tx, id, token) => records.SubmissionAsync(tx, id, token), ct);
        await RequireCountersAsync(lease, ct);
        foreach (var item in replacements) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", item.Name) }, ct)).SingleAsync(ct);
            if (definition["options"]["validator"] != item.Previous) throw Invalid();
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", item.Name }, { "validator", item.Next },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        }
    }
    private async Task RequirePredecessorAsync(string name, BsonDocument validator, (string Name, BsonDocument Key, bool Unique)[] keys,
        MigrationLease lease, CancellationToken ct)
    {
        await lease.RenewAsync(ct);
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct) ?? throw Invalid();
        var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
        if (!PdfEvidenceAdmissionSchemas.SafeDefinition(definition) || !InventorySchemas.Contains(definition["options"]["validator"].AsBsonDocument, validator) ||
            !AccountContactStorage.SafeIndexes(indexes, keys) || keys.Any(key => !indexes.Any(row => AccountContactStorage.Matches(row, key))) ||
            await Rows(name).Find(new BsonDocument("$nor", new BsonArray { definition["options"]["validator"] })).Limit(1).AnyAsync(ct)) throw Invalid();
    }
    private async Task RequireCountersAsync(MigrationLease lease, CancellationToken ct)
    {
        // Admission reuses the existing shared allocator; M83 policies retain their independent owner.
        foreach (var (counterName, tables) in new (string, string[])[] {
            ("evidenceIds", ["evidenceLineages", "documentVersions", "evidenceUploadReservations", "evidenceUploadAttempts"]),
            ("auditEvents", ["auditEvents"]), (PdfEvidenceValidationRecords.Policies, [PdfEvidenceValidationRecords.Policies]),
            (PdfEvidenceValidationRecords.Audits, [PdfEvidenceValidationRecords.Audits]) }) {
            await lease.RenewAsync(ct); long maximum = 0;
            foreach (var table in tables) {
                var row = await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", -1)).Project(new BsonDocument("_id", 1)).FirstOrDefaultAsync(ct);
                if (row is not null) {
                    if (!row["_id"].IsInt64 || row["_id"].AsInt64 is <= 0 or long.MaxValue) throw Invalid();
                    maximum = Math.Max(maximum, row["_id"].AsInt64);
                }
            }
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", counterName)).SingleOrDefaultAsync(ct);
            if (counter is null ? maximum > 0 : !counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < maximum || value.AsInt64 is < 0 or long.MaxValue) throw Invalid();
            if (counterName == PdfEvidenceValidationRecords.Audits && counter is not null && counter["value"] != 0L) throw Invalid();
        }
        if (await Rows("idCounters").Find(new BsonDocument("_id", PdfEvidenceValidationRecords.Results)).AnyAsync(ct)) throw Invalid();
    }
}
