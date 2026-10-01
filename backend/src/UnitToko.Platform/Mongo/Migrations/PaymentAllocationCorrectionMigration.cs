using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Inventory;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using C = UnitToko.Platform.Billing.PaymentAllocationCorrectionBson;

namespace UnitToko.Platform.Mongo.Migrations;
internal sealed class PaymentAllocationCorrectionMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 52;
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var installs = new List<(string Name, bool Exists, BsonDocument Validator, BsonDocument[] Keys)>();
        foreach (var name in PaymentAllocationCorrectionSchemas.Collections) {
            await lease.RenewAsync(ct); var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Bad(); var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") || options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
            var keys = PaymentAllocationCorrectionSchemas.Keys(name); var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") || index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !keys.Any(key => index["key"].Equals(key))) || definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Bad();
            var missing = new List<BsonDocument>(); for (var i = 0; i < keys.Length; i++) {
                var key = keys[i]; var expected = "ux_" + name + "_" + i; var matching = indexes.Where(index => index["key"].Equals(key) || index["name"] == expected).ToArray();
                if (matching.Any(index => !index["key"].Equals(key) || !index.GetValue("unique", false).AsBoolean)) throw Bad(); if (matching.Length == 0) missing.Add(key);
            }
            var current = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = PaymentAllocationCorrectionSchemas.Validator(name);
            installs.Add((name, definition is not null, current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current : new("$and", new BsonArray { current, required }), missing.ToArray()));
        }
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") }) if (await database.GetCollection<BsonDocument>(table).WithReadConcern(ReadConcern.Majority).Find(new BsonDocument(field, new BsonRegularExpression(@"^billing\.payment\.allocation\.correct(?:[.:]|$)"))).Limit(1).AnyAsync(ct)) throw Bad();
        if (await database.GetCollection<BsonDocument>("journalEntries").Find(new BsonDocument { { "source.namespace", "billing.payment-allocation" }, { "source.eventKind", new BsonDocument("$ne", "allocated") } }).Limit(1).AnyAsync(ct)) throw Bad();
        foreach (var name in PaymentAllocationSchemas.Collections) await InventoryRepository.RequireStorageAsync(database, name, PaymentAllocationSchemas.Validator(name), PaymentAllocationSchemas.Keys(name), ct);
        using (var tx = await database.Client.StartSessionAsync(cancellationToken: ct)) {
            tx.StartTransaction(new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority)); var records = new PaymentAllocationRecords(database); var sources = new HashSet<long>(); var receipts = new HashSet<long>(); var invoices = new HashSet<long>(); var count = 0L; var maximum = 0L;
            async Task Validate(BsonDocument row) { if (sources.Add(row["_id"].AsInt64)) await records.PreflightSourceAsync(tx, row, ct); }
            using var cursor = await records.Rows(B.Events).Find(tx, new BsonDocument()).ToCursorAsync(ct);
            while (await cursor.MoveNextAsync(ct)) foreach (var candidate in cursor.Current) {
                await lease.RenewAsync(ct); var row = await records.RawAsync(tx, candidate["_id"].AsInt64, ct); await Validate(row); count++; maximum = Math.Max(maximum, row["_id"].AsInt64);
                if (receipts.Add(row["receiptId"].AsInt64)) await records.BalanceAsync(tx, true, row["receiptId"].AsInt64, row["party"].AsBsonDocument, row["receiptTotal"], row["receiptHash"], Validate, ct);
                foreach (var line in row["lines"].AsBsonArray) if (invoices.Add(line["invoiceId"].AsInt64)) await records.BalanceAsync(tx, false, line["invoiceId"].AsInt64, row["party"].AsBsonDocument, line["source"]["total"], line["source"]["snapshotHash"], Validate, ct);
            }
            foreach (var (table, expected) in new[] { (B.Receipts, (long)receipts.Count), (B.Invoices, (long)invoices.Count) }) if (await records.Rows(table).CountDocumentsAsync(tx, new BsonDocument(), cancellationToken: ct) != expected) throw Bad();
            foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") }) if (await records.Rows(table).CountDocumentsAsync(tx, new BsonDocument(field, B.Operation), cancellationToken: ct) != count) throw Bad();
            if (await records.Rows("journalEntries").CountDocumentsAsync(tx, new BsonDocument("source.namespace", "billing.payment-allocation"), cancellationToken: ct) != count) throw Bad();
            var counter = await records.Rows("idCounters").Find(tx, new BsonDocument("_id", B.Events)).SingleOrDefaultAsync(ct);
            if (counter is null ? maximum > 0 : !counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 < maximum || value.AsInt64 is < 0 or >= long.MaxValue - 1) throw Bad();
            await tx.AbortTransactionAsync(ct);
        }
        foreach (var item in installs) {
            await lease.RenewAsync(ct); await database.RunCommandAsync<BsonDocument>(new BsonDocument { { item.Exists ? "collMod" : "create", item.Name }, { "validator", item.Validator }, { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in item.Keys) await database.GetCollection<BsonDocument>(item.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true, Name = "ux_" + item.Name + "_" + Array.FindIndex(PaymentAllocationCorrectionSchemas.Keys(item.Name), expected => expected.Equals(key)) }), cancellationToken: ct);
        }
    }
    private static InvalidOperationException Bad() => new("Payment allocation correction migration preflight failed");
}
