using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Identity;

namespace UnitToko.Platform.Mongo.Migrations;

// Prospective M87. No approval data, migrated primary PDF or finance state is seeded.
internal sealed class PaymentSupplementMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 87;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Payment supplement migration found incompatible retained storage");
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version ||
            history.Take(Version - 1).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();
        var changes = new List<(string Name, bool Exists, (string Name, BsonDocument Key, bool Unique)[] Missing)>();
        foreach (var name in PaymentSupplementSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            if (definition is not null && !AccountContactStorage.SafeDefinition(definition, PaymentSupplementSchemas.Validator(name))) throw Invalid();
            if (await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
            var allocator = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (allocator is not null && (!allocator.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 != 0L)) throw Invalid();
            List<BsonDocument> indexes = definition is null ? [] : await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var keys = PaymentSupplementSchemas.Keys(name);
            if (definition is not null && !PaymentSupplementStorage.SafeIndexes(indexes, keys)) throw Invalid();
            changes.Add((name, definition is not null, keys.Where(key => !indexes.Any(row => AccountContactStorage.Matches(row, key))).ToArray()));
        }
        try { await new PdfDocumentExtractionMigration(database).RequireSupplementPredecessorAsync(lease, ct); }
        catch (Exception error) when (error is DomainValidationException or PersistenceConflictException or InvalidOperationException) { throw Invalid(); }
        var outbox = await AccountContactStorage.DefinitionAsync(database, "outboxEvents", ct) ?? throw Invalid();
        var previous = outbox["options"]["validator"].AsBsonDocument;
        if (!PdfEvidenceAdmissionSchemas.SafeDefinition(outbox)) throw Invalid();
        PaymentEvidenceMigration.RequireIndexes("outboxEvents", await (await Rows("outboxEvents").Indexes.ListAsync(ct)).ToListAsync(ct));
        var next = PaymentSupplementSchemas.ExtendOutbox(previous);
        var documentChanges = new List<(string Name, BsonDocument Before, BsonDocument After)>();
        foreach (var name in PdfDocumentExtractionSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            var before = definition["options"]["validator"].AsBsonDocument;
            var after = PaymentSupplementSchemas.ExtendDocumentOwner(name, before);
            if (await Rows(name).Find(new BsonDocument("sourceKind", PaymentSupplementBson.SourceKind)).Limit(1).AnyAsync(ct) ||
                await Rows(name).Find(new BsonDocument("$nor", new BsonArray { before })).Limit(1).AnyAsync(ct) ||
                await Rows(name).Find(new BsonDocument("$nor", new BsonArray { after })).Limit(1).AnyAsync(ct)) throw Invalid();
            documentChanges.Add((name, before, after));
        }
        foreach (var (name, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("auditEvents", "operation"),
                ("authorizationReadEvents", "operation"), ("businessReadEvents", "operation"), ("authorizationHistory", "operation"), ("outboxEvents", "kind") })
            if (await Rows(name).Find(new BsonDocument(field, new BsonDocument("$in", new BsonArray {
                PaymentSupplementBson.CreateOperation, PaymentSupplementBson.AttachOperation, PaymentSupplementBson.JobKind,
                "billing.payment.supplement.read", "evidence.payment-supplement-pdf.reserve" }))).Limit(1).AnyAsync(ct)) throw Invalid();
        // No existing M85 PaymentDeclaration/PDF is adopted as a new association.
        foreach (var (name, filter) in new[] {
            ("evidenceUploadReservations", new BsonDocument("purpose", "PaymentSupplementPdf")),
            ("evidenceLineages", new BsonDocument("purpose", "PaymentSupplementPdf")),
            ("evidencePins", new BsonDocument("associationKind", "PaymentSupplement")),
            ("pdfDocumentProcessingReadAudits", new BsonDocument("sourceKind", "PaymentSupplement")),
            ("pdfDocumentExtractionResults", new BsonDocument("sourceKind", "PaymentSupplement")) })
            if (await Rows(name).Find(filter).Limit(1).AnyAsync(ct)) throw Invalid();
        if (await Rows("outboxEvents").Find(new BsonDocument("$nor", new BsonArray { previous })).Limit(1).AnyAsync(ct) ||
            await Rows("outboxEvents").Find(new BsonDocument("$nor", new BsonArray { next })).Limit(1).AnyAsync(ct)) throw Invalid();
        var evidenceChanges = new List<(string Name, BsonDocument Before, BsonDocument After, BsonValue Uuid)>();
        foreach (var name in PaymentSupplementEvidenceSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            if (!PdfEvidenceAdmissionSchemas.SafeDefinition(definition) || !definition.TryGetValue("info", out var info) ||
                !info.IsBsonDocument || !info.AsBsonDocument.TryGetValue("uuid", out var uuid) || !uuid.IsBsonBinaryData)
                throw Invalid();
            var before = definition["options"]["validator"].AsBsonDocument;
            var after = PaymentSupplementEvidenceSchemas.Extend(name, before);
            if (name is "evidencePins" or "evidenceReadLeases")
                await InventoryRepository.RequireStorageAsync(database, name, before,
                    name == "evidencePins" ? PaymentSubmissionSchemas.Key(name) : EvidenceAccessSchemas.Key(name), ct);
            else PaymentEvidenceMigration.RequireIndexes(name, await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct));
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { before })).Limit(1).AnyAsync(ct) ||
                await Rows(name).Find(new BsonDocument("$nor", new BsonArray { after })).Limit(1).AnyAsync(ct)) throw Invalid();
            evidenceChanges.Add((name, before.DeepClone().AsBsonDocument, after, uuid.DeepClone()));
        }
        // Predecessor validation and all new-owner checks precede the first DDL.
        foreach (var change in changes) {
            await lease.RenewAsync(ct);
            if (!change.Exists) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
                { "create", change.Name }, { "validator", PaymentSupplementSchemas.Validator(change.Name) }, { "validationLevel", "strict" },
                { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in change.Missing) await Rows(change.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        foreach (var change in documentChanges) {
            await lease.RenewAsync(ct);
            var definition = await AccountContactStorage.DefinitionAsync(database, change.Name, ct) ?? throw Invalid();
            if (definition["options"]["validator"] != change.Before) throw Invalid();
            if (change.After != change.Before) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
                { "collMod", change.Name }, { "validator", change.After }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        }
        await lease.RenewAsync(ct);
        var current = await AccountContactStorage.DefinitionAsync(database, "outboxEvents", ct) ?? throw Invalid();
        if (current["options"]["validator"] != previous) throw Invalid();
        if (next != previous) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "collMod", "outboxEvents" }, { "validator", next }, { "validationLevel", "strict" }, { "validationAction", "error" },
            { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        foreach (var change in evidenceChanges) {
            await lease.RenewAsync(ct);
            var definition = await AccountContactStorage.DefinitionAsync(database, change.Name, ct) ?? throw Invalid();
            if (definition["options"]["validator"] != change.Before || definition["info"]["uuid"] != change.Uuid) throw Invalid();
            if (change.After != change.Before) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
                { "collMod", change.Name }, { "validator", change.After }, { "validationLevel", "strict" }, { "validationAction", "error" },
                { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            var installed = await AccountContactStorage.DefinitionAsync(database, change.Name, ct) ?? throw Invalid();
            if (installed["info"]["uuid"] != change.Uuid || installed["options"]["validator"] != change.After) throw Invalid();
        }
        await PaymentSupplementStorage.RequireAsync(database, ct);
        await PaymentSupplementEvidenceSchemas.RequireAsync(database, ct);
    }
}
