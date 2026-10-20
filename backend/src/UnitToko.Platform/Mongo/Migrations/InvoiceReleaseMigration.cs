using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Billing.InvoiceReleaseBson;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Actual118 predecessor only. Installs empty governed release owners and
/// deterministic pre-release classifications for complete existing invoice graphs.
/// No old invoice/result/journal/receipt byte is changed and no approval is invented.</summary>
internal sealed class InvoiceReleaseMigration(IMongoDatabase database) : IMigrationStep
{
    public const int Number = 119;
    public int Version => Number;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private static InvalidOperationException Invalid() => new("Invoice release migration requires actual118 history, complete original invoice effects and exact resumable pre-release bindings");
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct)
    {
        await lease.RenewAsync(ct);
        var history = await HistoryAsync(ct); var counters = await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        var installs = new List<(string Name, BsonDocument? Definition, BsonDocument[] Indexes)>();
        foreach (var name in InvoiceReleaseSchemas.Collections) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            var indexes = definition is null ? [] : (await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (definition is not null && (!AccountContactStorage.SafeDefinition(definition, InvoiceReleaseSchemas.Validator(name)) ||
                definition["options"]["validator"] != InvoiceReleaseSchemas.Validator(name) || !AccountContactStorage.SafeIndexes(indexes, InvoiceReleaseSchemas.Keys(name)))) throw Invalid();
            var counter = counters.SingleOrDefault(row => row["_id"] == name);
            if (counter is not null && (name is B.Bindings or B.Anchors || !counter.GetValue("value", BsonNull.Value).IsInt64 || counter["value"] != 0L)) throw Invalid();
            if (name is B.Heads or B.Events && await Rows(name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
            installs.Add((name, definition, indexes));
        }
        var prefix = new BsonRegularExpression(@"^billing\.invoice\.release(?:[.:]|$)");
        foreach (var (name, field) in new[] { ("auditEvents", "kind"), ("auditEvents", "operation"), ("idempotencyRecords", "operation"),
            ("outboxEvents", "kind"), ("outboxEvents", "sourceKey"), ("authorizationHistory", "operation") })
            if (await Rows(name).Find(new BsonDocument(field, prefix)).Limit(1).AnyAsync(ct)) throw Invalid();
        await InvoiceStorage.RequireAsync(database, ct); await InvoiceReleaseRecords.RequireEffectStorageAsync(database, ct);
        var original = await Rows(InvoiceBson.Invoices).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        var expected = new Dictionary<long, BsonDocument>(); var expectedAnchors = new Dictionary<long, BsonDocument>();
        foreach (var invoice in original) {
            await lease.RenewAsync(ct);
            using var tx = await database.Client.StartSessionAsync(cancellationToken: ct); tx.StartTransaction();
            try {
                var current = await database.GetCollection<BsonDocument>(InvoiceBson.Invoices).Find(tx, new BsonDocument("_id", invoice["_id"])).SingleAsync(ct);
                InvoiceReleaseRecords.Equal(current, invoice); await InvoiceRecords.VerifyLegacyPersistedAsync(database, tx, current, ct);
                var binding = await InvoiceReleaseRecords.BindingAsync(database, tx, current, null, ct);
                expected.Add(invoice["_id"].AsInt64, binding); expectedAnchors.Add(invoice["_id"].AsInt64, Anchor(binding));
            } finally { await tx.AbortTransactionAsync(ct); }
        }
        await OrphansAsync(original, ct);
        await ExistingAsync(B.Bindings, expected, ct); await ExistingAsync(B.Anchors, expectedAnchors, ct);
        // Snapshot all retained metadata before any new DDL. Operator indexes and
        // every predecessor validator/UUID are preserved, including earlier private evidence.
        var retained = (await (await database.ListCollectionsAsync(cancellationToken: ct)).ToListAsync(ct))
            .Where(row => row.GetValue("type", "") == "collection" && !InvoiceReleaseSchemas.Collections.Contains(row["name"].AsString)).ToArray();
        var retainedIndexes = new Dictionary<string, BsonDocument[]>();
        foreach (var row in retained) retainedIndexes.Add(row["name"].AsString, (await (await Rows(row["name"].AsString).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray());
        foreach (var item in installs) {
            await lease.RenewAsync(ct);
            if (item.Definition is null) await database.RunCommandAsync<BsonDocument>(new BsonDocument {
                { "create", item.Name }, { "validator", InvoiceReleaseSchemas.Validator(item.Name) }, { "validationLevel", "strict" },
                { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() }
            }, cancellationToken: ct);
            foreach (var key in InvoiceReleaseSchemas.Keys(item.Name).Where(key => !item.Indexes.Any(index => AccountContactStorage.Matches(index, key))))
                await database.GetCollection<BsonDocument>(item.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key.Key,
                    new CreateIndexOptions { Name = key.Name, Unique = key.Unique }), cancellationToken: ct);
        }
        foreach (var invoice in original) {
            await lease.RenewAsync(ct); var id = invoice["_id"].AsInt64;
            using var tx = await database.Client.StartSessionAsync(cancellationToken: ct);
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority));
            try {
                var current = await database.GetCollection<BsonDocument>(InvoiceBson.Invoices).Find(tx, new BsonDocument("_id", id)).SingleAsync(ct);
                InvoiceReleaseRecords.Equal(current, invoice); await InvoiceRecords.VerifyLegacyPersistedAsync(database, tx, current, ct);
                InvoiceReleaseRecords.Equal(await InvoiceReleaseRecords.BindingAsync(database, tx, current, null, ct), expected[id]);
                foreach (var (name, row) in new[] { (B.Bindings, expected[id]), (B.Anchors, expectedAnchors[id]) }) {
                    var rows = database.GetCollection<BsonDocument>(name); var existing = await rows.Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct);
                    if (existing is null) await rows.InsertOneAsync(tx, row, cancellationToken: ct); else InvoiceReleaseRecords.Equal(existing, row);
                }
                await tx.CommitTransactionAsync(ct);
            } catch { if (tx.IsInTransaction) await tx.AbortTransactionAsync(CancellationToken.None); throw; }
        }
        await ExistingAsync(B.Bindings, expected, ct, complete: true); await ExistingAsync(B.Anchors, expectedAnchors, ct, complete: true);
        if (!original.SequenceEqual(await Rows(InvoiceBson.Invoices).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct)) ||
            !counters.SequenceEqual(await Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync(ct))) throw Invalid();
        foreach (var before in retained) {
            await lease.RenewAsync(ct); var name = before["name"].AsString;
            var after = await AccountContactStorage.DefinitionAsync(database, name, ct) ?? throw Invalid();
            var indexes = (await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (before["info"]["uuid"] != after["info"]["uuid"] || before["options"] != after["options"] ||
                retainedIndexes[name].Length != indexes.Length || retainedIndexes[name].Any(index => !indexes.Contains(index))) throw Invalid();
        }
        foreach (var item in installs) {
            var after = await AccountContactStorage.DefinitionAsync(database, item.Name, ct) ?? throw Invalid();
            if (item.Definition is { } before && (before["info"]["uuid"] != after["info"]["uuid"] || before["options"] != after["options"])) throw Invalid();
            if (item.Name is B.Heads or B.Events && await Rows(item.Name).Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid();
        }
        await InvoiceReleaseRecords.RequireStorageAsync(database, ct);
        var finalHistory = await HistoryAsync(ct); if (!history.Take(118).SequenceEqual(finalHistory.Take(118))) throw Invalid();
    }
    private async Task<List<BsonDocument>> HistoryAsync(CancellationToken ct) {
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(Number + 1).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(118).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Invalid();
        return history;
    }
    private static BsonDocument Anchor(BsonDocument binding) => new() { { "_id", binding["invoiceId"] }, { "schemaVersion", 1 },
        { "migrationVersion", Number }, { "invoiceHash", binding["invoiceHash"] }, { "bindingHash", binding["recordHash"] } };
    private async Task ExistingAsync(string name, Dictionary<long, BsonDocument> expected, CancellationToken ct, bool complete = false) {
        var rows = await Rows(name).Find(new BsonDocument()).ToListAsync(ct);
        if (complete && rows.Count != expected.Count || rows.Any(row => !row.GetValue("_id", BsonNull.Value).IsInt64 ||
            !expected.TryGetValue(row["_id"].AsInt64, out var match) || !InvoiceRecords.Equal(row, match))) throw Invalid();
    }
    private async Task OrphansAsync(List<BsonDocument> invoices, CancellationToken ct) {
        var byId = invoices.ToDictionary(row => row["_id"].AsInt64);
        foreach (var (name, filter, field) in new (string, BsonDocument, string)[] {
            (InvoiceBson.Allocations, new(), "invoiceId"),
            ("auditEvents", new("$or", new BsonArray { new BsonDocument("kind", InvoiceBson.Operation), new BsonDocument("operation", InvoiceBson.Operation) }), "result.invoiceId"),
            ("idempotencyRecords", new("operation", InvoiceBson.Operation), "result.invoiceId"),
            ("outboxEvents", new("$or", new BsonArray { new BsonDocument("kind", InvoiceBson.Operation),
                new BsonDocument("sourceKey", new BsonRegularExpression(@"^billing\.invoice\.issue(?:[:.]|$)")) }), "payload.invoiceId"),
            ("journalEntries", new("source.namespace", "billing.invoice"), "source.documentId") }) {
            var rows = await Rows(name).Find(filter).ToListAsync(ct);
            foreach (var row in rows) {
                BsonValue id = row; foreach (var part in field.Split('.')) {
                    if (!id.IsBsonDocument || !id.AsBsonDocument.TryGetValue(part, out var next)) throw Invalid(); id = next;
                }
                if (!id.IsInt64 || !byId.TryGetValue(id.AsInt64, out var invoice)) throw Invalid();
                if (name == "outboxEvents" && (row.GetValue("sourceKey", "") != InvoiceBson.Operation + ":" + id.AsInt64 ||
                    row.GetValue("createdAt", BsonNull.Value) != invoice["issuedAt"] || row.GetValue("payload", BsonNull.Value) != new BsonDocument("invoiceId", id))) throw Invalid();
            }
            if (name != InvoiceBson.Allocations && rows.Count != invoices.Count) throw Invalid();
        }
    }
}
