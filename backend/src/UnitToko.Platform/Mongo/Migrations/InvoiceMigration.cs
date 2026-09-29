using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed class InvoiceMigration(IMongoDatabase database) : IMigrationStep
{
    public int Version => 29;
    private sealed record Index(string Name, BsonDocument Key, bool Unique = false);
    private sealed record Installation(string Name, bool Exists, BsonDocument Validator, Index[] Create);
    private static Index[] Indexes(string name) => name == InvoiceBson.Invoices ? [
        new("ux_invoice_number", InvoiceSchemas.NumberKey(), true), new("ux_invoice_command", InvoiceSchemas.CommandKey(), true),
        new("ix_invoice_order", new("orderIds", 1)), new("ix_invoice_due", new() { { "dueDate", 1 }, { "_id", 1 } })] : [
        new("ux_invoice_receipt_sku", InvoiceSchemas.AllocationKey(), true), new("ix_invoice_allocation_order", new() { { "orderId", 1 }, { "skuId", 1 }, { "previousBaseUnits", 1 } }),
        new("ix_invoice_allocation_document", new("invoiceId", 1))];
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        var installations = new List<Installation>();
        // Preflight every target, counter and effect before the first target DDL. Historical
        // invoices require an explicit conversion/restore decision, never invented source facts.
        foreach (var name in InvoiceSchemas.Collections) {
            await lease.RenewAsync(ct);
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var rows = database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
            if (await rows.Find(new BsonDocument()).Limit(1).AnyAsync(ct)) throw Invalid("unaccepted invoice data");
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument ?? new();
            if (definition is not null && definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean ||
                options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Invalid("collection options");
            var previous = options.GetValue("validator", new BsonDocument()).AsBsonDocument; var required = InvoiceSchemas.Validator(name);
            var validator = previous.ElementCount == 0 ? required : InventorySchemas.Contains(previous, required) ? previous : new BsonDocument("$and", new BsonArray { previous, required });
            var indexes = definition is null ? [] : await (await rows.Indexes.ListAsync(ct)).ToListAsync(ct); var expected = Indexes(name);
            bool Foundation(BsonDocument index) => name == InvoiceBson.Invoices && (InvoiceStorage.Prototype(index) || InvoiceStorage.FoundationNumber(index));
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") && !Foundation(index) ||
                index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean && !Foundation(index) && !expected.Any(wanted => wanted.Unique && index["key"].Equals(wanted.Key)))) throw Invalid("retention or extra uniqueness");
            if (definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)))) throw Invalid("identity index");
            var create = new List<Index>();
            foreach (var wanted in expected) {
                var matches = indexes.Where(index => !Foundation(index) && (index["name"] == wanted.Name || index["key"].Equals(wanted.Key))).ToArray();
                if (matches.Any(index => !index["key"].Equals(wanted.Key) || index.GetValue("unique", false).AsBoolean != wanted.Unique)) throw Invalid("required index");
                if (matches.Length == 0) create.Add(wanted);
            }
            var counter = await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
            if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or long.MaxValue)) throw Invalid("allocator");
            installations.Add(new(name, definition is not null, validator, [.. create]));
        }
        if (await database.GetCollection<BsonDocument>("idCounters").WithReadConcern(ReadConcern.Majority).Find(new BsonDocument("_id", new BsonRegularExpression(@"^invoice-number\."))).AnyAsync(ct)) throw Invalid("unaccepted invoice numbering");
        var prefix = new BsonRegularExpression(@"^billing\.invoice(?:[.:]|$)");
        foreach (var (name, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new("operation", prefix)),
            ("auditEvents", new("$or", new BsonArray { new BsonDocument("kind", prefix), new BsonDocument("operation", prefix) })),
            ("outboxEvents", new("$or", new BsonArray { new BsonDocument("kind", prefix), new BsonDocument("sourceKey", prefix) })),
            ("journalEntries", new("$or", new BsonArray { new BsonDocument("source.namespace", prefix), new BsonDocument("sourceEvent", prefix) })),
            ("invoiceBalances", new())
        }) {
            await lease.RenewAsync(ct);
            if (await database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority).Find(filter).Limit(1).AnyAsync(ct)) throw Invalid("unaccepted invoice effects");
        }
        foreach (var install in installations) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { install.Exists ? "collMod" : "create", install.Name }, { "validator", install.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var index in install.Create) {
                await lease.RenewAsync(ct);
                await database.GetCollection<BsonDocument>(install.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(index.Key,
                    new CreateIndexOptions { Name = index.Name, Unique = index.Unique }), cancellationToken: ct);
            }
        }
    }
    private static InvalidOperationException Invalid(string reason) => new("Invoice migration preflight failed: " + reason);
}
