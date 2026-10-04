using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Identity;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>A new PDF proof owner. Admission targets remain a separate reviewed integration.</summary>
internal sealed class PdfEvidenceValidationMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 83;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("PDF evidence validation migration found incompatible retained storage");
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version ||
            history.Take(Version - 1).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();
        // Existing financial/image, M81 revalidation and M82 contact owners must remain intact before any DDL.
        try { await RequireFinancialPredecessorAsync(lease, ct); await AccountContactStorage.RequireAsync(database, ct); }
        catch (Exception error) when (error is InvalidOperationException or UnitToko.Platform.Mongo.PersistenceConflictException) { throw Invalid(); }
        foreach (var name in EvidenceImageRevalidationSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await DefinitionAsync(name, ct) ?? throw Invalid();
            if (!SafeDefinition(definition, EvidenceImageRevalidationSchemas.Validator(name))) throw Invalid();
            var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var keys = EvidenceImageRevalidationSchemas.Keys(name);
            if (!SafeIndexes(indexes, keys) || keys.Any(key => !indexes.Any(row => Matches(row, key)))) throw Invalid();
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { definition["options"]["validator"] })).Limit(1).AnyAsync(ct)) throw Invalid();
        }
        foreach (var name in AccountContactSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await DefinitionAsync(name, ct) ?? throw Invalid();
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { definition["options"]["validator"] })).Limit(1).AnyAsync(ct)) throw Invalid();
        }
        var changes = new List<(string Name, bool Exists, bool MissingIndex)>();
        foreach (var name in PdfEvidenceValidationSchemas.Collections) {
            await lease.RenewAsync(ct); var definition = await DefinitionAsync(name, ct);
            if (definition is not null && !SafeDefinition(definition, PdfEvidenceValidationSchemas.Validator(name))) throw Invalid();
            // Before this owner exists, neither business rows nor a consumed allocator can be relabelled as an empty installation.
            if (await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 != 0)) throw Invalid();
            List<BsonDocument> indexes = definition is null ? [] : await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            (string Name, BsonDocument Key, bool Unique)[] keys = [("ux_" + name, PdfEvidenceValidationSchemas.Key(name), true)];
            if (definition is not null && !SafeIndexes(indexes, keys)) throw Invalid();
            changes.Add((name, definition is not null, !indexes.Any(row => Matches(row, keys[0]))));
        }
        var outboxDefinition = await DefinitionAsync("outboxEvents", ct) ?? throw Invalid();
        if (!SafeDefinition(outboxDefinition, PdfEvidenceValidationSchemas.PreviousOutboxValidator(), true)) throw Invalid();
        PaymentEvidenceMigration.RequireIndexes("outboxEvents", await (await Rows("outboxEvents").Indexes.ListAsync(ct)).ToListAsync(ct));
        var prior = outboxDefinition["options"]["validator"].AsBsonDocument;
        var next = PdfEvidenceValidationSchemas.ExtendOutbox(prior);
        if (await Rows("outboxEvents").Find(new BsonDocument("kind", PdfEvidenceValidationRecords.Kind)).Limit(1).AnyAsync(ct) ||
            await Rows("outboxEvents").Find(new BsonDocument("$nor", new BsonArray { prior })).Limit(1).AnyAsync(ct) ||
            await Rows("outboxEvents").Find(new BsonDocument("$nor", new BsonArray { next })).Limit(1).AnyAsync(ct)) throw Invalid();
        // All predecessor definitions, allocators, candidates and old rows pass before the first mutation.
        foreach (var change in changes) {
            await lease.RenewAsync(ct);
            if (!change.Exists) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
                { "create", change.Name }, { "validator", PdfEvidenceValidationSchemas.Validator(change.Name) },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            if (change.MissingIndex) await Rows(change.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(PdfEvidenceValidationSchemas.Key(change.Name),
                    new CreateIndexOptions { Name = "ux_" + change.Name, Unique = true }), cancellationToken: ct);
        }
        await lease.RenewAsync(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" }, { "validator", next },
            { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
    }
    private async Task<BsonDocument?> DefinitionAsync(string name, CancellationToken ct) =>
        await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
    private async Task RequireFinancialPredecessorAsync(MigrationLease lease, CancellationToken ct)
    {
        foreach (var name in EvidenceSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await DefinitionAsync(name, ct) ?? throw Invalid();
            var required = name == "outboxEvents" ? PdfEvidenceValidationSchemas.PreviousOutboxValidator() : SupplierDocumentEvidenceSchemas.PreviousValidator(name);
            var current = definition.GetValue("options", new BsonDocument()).AsBsonDocument.GetValue("validator", new BsonDocument()).AsBsonDocument;
            if (!SafeDefinition(definition, current) || (name == "outboxEvents"
                ? !PdfEvidenceValidationSchemas.SupportsOutbox(current, required)
                : !SupplierDocumentEvidenceSchemas.Supports(name, current, required))) throw Invalid();
            PaymentEvidenceMigration.RequireIndexes(name, await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct));
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { current })).Limit(1).AnyAsync(ct)) throw Invalid();
        }
        foreach (var name in EvidenceAccessSchemas.Collections) {
            await lease.RenewAsync(ct);
            await SupplierDocumentEvidenceSchemas.RequireCompatibleStorageAsync(database, name,
                SupplierDocumentEvidenceSchemas.PreviousValidator(name), EvidenceAccessSchemas.Key(name), ct);
        }
        foreach (var name in EvidenceValidationSchemas.Collections) {
            await lease.RenewAsync(ct);
            await UnitToko.Platform.Inventory.InventoryRepository.RequireStorageAsync(database, name,
                EvidenceValidationSchemas.Validator(name), EvidenceValidationSchemas.Key(name), ct);
        }
    }
    private static bool SafeDefinition(BsonDocument definition, BsonDocument required, bool outbox = false)
    {
        var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
        return definition.GetValue("type", "") == "collection" && !options.GetValue("capped", false).AsBoolean &&
            !options.Contains("timeseries") && !options.Contains("expireAfterSeconds") && !options.Contains("encryptedFields") &&
            options.GetValue("validationLevel", "") == "strict" && options.GetValue("validationAction", "") == "error" &&
            (!options.TryGetValue("collation", out var collation) || InvoiceStorage.Simple(collation)) &&
            options.TryGetValue("validator", out var validator) && validator.IsBsonDocument &&
            (outbox ? PdfEvidenceValidationSchemas.SupportsOutbox(validator.AsBsonDocument, required) : InventorySchemas.Contains(validator.AsBsonDocument, required));
    }
    private static bool Matches(BsonDocument row, (string Name, BsonDocument Key, bool Unique) key) =>
        row.GetValue("name", "") == key.Name && row.GetValue("key", BsonNull.Value) == key.Key && row.GetValue("unique", false).AsBoolean == key.Unique &&
        !InvoiceStorage.Unsafe(row) && !row.Contains("partialFilterExpression");
    private static bool SafeIndexes(IReadOnlyList<BsonDocument> indexes, (string Name, BsonDocument Key, bool Unique)[] keys) =>
        indexes.Any(row => row.GetValue("name", "") == "_id_" && row.GetValue("key", BsonNull.Value) == new BsonDocument("_id", 1)) &&
        indexes.All(row => !InvoiceStorage.Unsafe(row) && !row.Contains("partialFilterExpression") &&
            (row.GetValue("name", "") == "_id_" || keys.Any(key => Matches(row, key))));
}
