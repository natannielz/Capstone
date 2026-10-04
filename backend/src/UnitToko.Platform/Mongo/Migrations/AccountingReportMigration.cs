using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Reporting;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Prospective M84: an empty immutable reporting-read owner; existing financial owners are never rewritten.</summary>
internal sealed class AccountingReportMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 84;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Accounting report migration found incompatible retained storage");
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Version + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version ||
            history.Take(Version - 1).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();
        await lease.RenewAsync(ct);
        try { await AccountingMigration.RequireInstalledStorageAsync(database, ct); await AccountContactStorage.RequireAsync(database, ct); }
        catch (Exception error) when (error is DomainValidationException or PersistenceConflictException or InvalidOperationException) { throw Invalid(); }
        var retained = new Dictionary<string, List<BsonDocument>>();
        foreach (var name in AccountingSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await AccountingReportRecords.DefinitionAsync(database, name, ct) ?? throw Invalid();
            var validator = definition["options"]["validator"].AsBsonDocument;
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Invalid();
            retained.Add(name, await Rows(name).Find(new BsonDocument()).ToListAsync(ct));
        }
        try { AccountingMigrationGraph.Validate(retained); }
        catch (InvalidOperationException) { throw Invalid(); }
        foreach (var (name, maximum) in retained.Select(pair => (pair.Key, pair.Value.Select(row => row["_id"].AsInt64).DefaultIfEmpty(0).Max()))
            .Append(("accountingAccounts", retained["accountingChartVersions"].SelectMany(row => row["mappings"].AsBsonArray)
                .Select(row => row["accountId"].AsInt64).DefaultIfEmpty(0).Max()))) {
            var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is null ? maximum > 0 : !counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < maximum || value.AsInt64 < 0) throw Invalid();
        }
        // M81–M83 are predecessors, never targets. Preserve their stronger validators, indexes and UUIDs.
        foreach (var name in EvidenceImageRevalidationSchemas.Collections) {
            await lease.RenewAsync(ct);
            await PredecessorAsync(name, EvidenceImageRevalidationSchemas.Validator(name), EvidenceImageRevalidationSchemas.Keys(name), ct);
        }
        foreach (var name in AccountContactSchemas.Collections)
            await PredecessorAsync(name, AccountContactSchemas.Validator(name), AccountContactSchemas.Keys(name), ct);
        foreach (var name in PdfEvidenceValidationSchemas.Collections) {
            await lease.RenewAsync(ct);
            await PredecessorAsync(name, PdfEvidenceValidationSchemas.Validator(name), [("ux_" + name, PdfEvidenceValidationSchemas.Key(name), true)], ct);
        }
        var outbox = await AccountingReportRecords.DefinitionAsync(database, "outboxEvents", ct) ?? throw Invalid();
        var outboxValidator = outbox["options"]["validator"].AsBsonDocument;
        if (!AccountingReportRecords.SafeDefinition(outbox, outboxValidator) ||
            !PdfEvidenceValidationSchemas.SupportsOutbox(outboxValidator, PdfEvidenceValidationSchemas.ExtendedOutboxValidator())) throw Invalid();
        PaymentEvidenceMigration.RequireIndexes("outboxEvents", await (await Rows("outboxEvents").Indexes.ListAsync(ct)).ToListAsync(ct));
        if (await Rows("outboxEvents").Find(new BsonDocument("$nor", new BsonArray { outboxValidator })).Limit(1).AnyAsync(ct)) throw Invalid();
        var definitionNew = await AccountingReportRecords.DefinitionAsync(database, AccountingReportRecords.Events, ct);
        if (definitionNew is not null && !AccountingReportRecords.SafeDefinition(definitionNew, AccountingReportSchemas.Validator())) throw Invalid();
        if (await Rows(AccountingReportRecords.Events).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
        var allocator = await Rows("idCounters").Find(new BsonDocument("_id", AccountingReportRecords.Events)).SingleOrDefaultAsync(ct);
        if (allocator is not null && (!allocator.TryGetValue("value", out var consumed) || !consumed.IsInt64 || consumed.AsInt64 != 0)) throw Invalid();
        List<BsonDocument> indexes = definitionNew is null ? [] : await (await Rows(AccountingReportRecords.Events).Indexes.ListAsync(ct)).ToListAsync(ct);
        if (definitionNew is not null && !AccountingReportRecords.SafeIndexes(indexes)) throw Invalid();
        var missing = AccountingReportSchemas.Keys.Where(key => !indexes.Any(row => AccountingReportRecords.Matches(row, key))).ToArray();
        // No old audit/receipt namespace can be relabelled as this new read owner.
        foreach (var (table, field) in new[] { ("authorizationReadEvents", "operation"), ("businessReadEvents", "operation"),
            ("idempotencyRecords", "operation"), ("auditEvents", "operation"), ("auditEvents", "kind"),
            ("authorizationHistory", "operation"), ("outboxEvents", "kind") })
            if (await Rows(table).Find(new BsonDocument(field, new BsonDocument("$in", new BsonArray {
                AccountingReportRecords.BooksOperation, AccountingReportRecords.ReadOperation, AccountingReportRecords.ExportOperation }))).Limit(1).AnyAsync(ct)) throw Invalid();
        // Every candidate and retained row passed before the only DDL below. No seed, counter repair or collMod.
        await lease.RenewAsync(ct);
        if (definitionNew is null) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
            { "create", AccountingReportRecords.Events }, { "validator", AccountingReportSchemas.Validator() },
            { "validationLevel", "strict" }, { "validationAction", "error" },
            { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        foreach (var key in missing) await Rows(AccountingReportRecords.Events).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(key.Key, new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
    }
    private async Task PredecessorAsync(string name, BsonDocument required, (string Name, BsonDocument Key, bool Unique)[] keys, CancellationToken ct)
    {
        var definition = await AccountingReportRecords.DefinitionAsync(database, name, ct) ?? throw Invalid();
        var indexes = await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct);
        if (!AccountingReportRecords.SafeDefinition(definition, required) || !AccountContactStorage.SafeIndexes(indexes, keys) ||
            keys.Any(key => !indexes.Any(row => AccountContactStorage.Matches(row, key))) ||
            await Rows(name).Find(new BsonDocument("$nor", new BsonArray { definition["options"]["validator"] })).Limit(1).AnyAsync(ct)) throw Invalid();
    }
}
