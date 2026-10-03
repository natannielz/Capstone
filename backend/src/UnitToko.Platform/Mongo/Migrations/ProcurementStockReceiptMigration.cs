using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Procurement;

namespace UnitToko.Platform.Mongo.Migrations;

internal sealed partial class ProcurementStockReceiptMigration(IMongoDatabase database, IClock clock) : IMigrationStep
{
    public int Version => 72;
    private sealed record Install(string Name, bool Exists, BsonDocument Validator, BsonDocument[] Missing);
    public async Task ApplyAsync(MigrationLease lease, CancellationToken ct) {
        try { await ApplyCoreAsync(lease, ct); }
        catch (Exception error) when (error is PersistenceConflictException or AuthorizationDeniedException or DomainValidationException or
            KeyNotFoundException or InvalidCastException or FormatException or OverflowException) { throw Bad(); }
    }
    private async Task ApplyCoreAsync(MigrationLease lease, CancellationToken ct) {
        await lease.RenewAsync(ct);
        var history = await Rows("schemaMigrations").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).Limit(73).ToListAsync(ct);
        if (!MigrationHistory.IsValid(history, Version) || history.Count != Version || history.Take(71).Any(row => row["state"] != "applied") || history[^1]["state"] != "applying") throw Bad();
        var targets = new List<Install>(); var maxima = new Dictionary<string, long>();
        foreach (var name in new[] { ProcurementStockReceiptBson.Receipts, "lots", "stockBalances", "movements" }) {
            await lease.RenewAsync(ct); var required = Required(name); var definition = await DefinitionAsync(name, ct);
            if (name != ProcurementStockReceiptBson.Receipts && definition is null) throw Bad();
            var current = definition?["options"].AsBsonDocument.GetValue("validator", new BsonDocument()).AsBsonDocument ?? new();
            var validator = name == "movements" ? ExtendMovement(current) : current.ElementCount == 0 ? required : InventorySchemas.Contains(current, required) ? current : new BsonDocument("$and", new BsonArray { current, required });
            var indexes = definition is null ? [] : await (await Rows(name).Indexes.ListAsync(ct)).ToListAsync(ct); var keys = Keys(name);
            if (indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                index.GetValue("unique", false).AsBoolean && index["name"] != "_id_" && !keys.Any(key => index["key"] == key)) ||
                definition is not null && !indexes.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1))) throw Bad();
            var missing = new List<BsonDocument>();
            for (var i = 0; i < keys.Length; i++) {
                var found = indexes.Where(index => index["key"] == keys[i] || index["name"] == "ux_" + name + "_" + i).ToArray();
                if (found.Any(index => index["key"] != keys[i] || index["name"] != "_id_" && !index.GetValue("unique", false).AsBoolean)) throw Bad();
                if (found.Length == 0 && keys[i] != new BsonDocument("_id", 1)) missing.Add(keys[i]);
                var group = new BsonDocument(keys[i].Names.Select((field, position) => new BsonElement("k" + position, "$" + field)));
                if (await Rows(name).Aggregate<BsonDocument>(new BsonDocument[] { new("$group", new BsonDocument { { "_id", group }, { "n", new BsonDocument("$sum", 1) } }),
                    new("$match", new BsonDocument("n", new BsonDocument("$gt", 1))), new("$limit", 1) }).AnyAsync(ct)) throw Bad();
            }
            if (await Rows(name).Find(new BsonDocument("$nor", new BsonArray { validator })).Limit(1).AnyAsync(ct)) throw Bad();
            targets.Add(new(name, definition is not null, validator, missing.ToArray())); await CounterAsync(name, maxima, ct);
        }
        var disposition = await ProcurementStockReceiptSchemas.PreflightAsync(database, ct);
        foreach (var (name, maximum) in await new SupplierClaimMigration(database, clock).PreflightRetainedForStockAsync(lease, ct)) maxima.Add(name, maximum);
        await RequireSourcePoliciesAsync(lease, maxima, ct);
        await RequireOwnersAsync(lease, maxima, ct);
        // No DDL, index removal, allocator update or synthetic stock is permitted before the complete proof above.
        foreach (var target in targets) {
            await lease.RenewAsync(ct);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument { { target.Exists ? "collMod" : "create", target.Name }, { "validator", target.Validator },
                { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
            foreach (var key in target.Missing) await Rows(target.Name).WithWriteConcern(WriteConcern.WMajority).Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions { Unique = true, Name = "ux_" + target.Name + "_" + Array.FindIndex(Keys(target.Name), value => value == key) }), cancellationToken: ct);
        }
        await ProcurementStockReceiptSchemas.ApplyAsync(database, disposition, lease, ct);
        foreach (var (name, maximum) in maxima) if (maximum > 0) {
            await lease.RenewAsync(ct); await Rows("idCounters").WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(new BsonDocument("_id", name),
                Builders<BsonDocument>.Update.Max("value", maximum), new UpdateOptions { IsUpsert = true }, ct);
        }
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name).WithReadConcern(ReadConcern.Majority);
    private async Task<BsonDocument?> DefinitionAsync(string name, CancellationToken ct) {
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
        if (definition is null) return null; var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (definition.GetValue("type", "") != "collection" || options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("encryptedFields") || options.Contains("expireAfterSeconds") ||
            options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error" || options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw Bad();
        return definition;
    }
    private async Task CounterAsync(string name, Dictionary<string, long> maxima, CancellationToken ct) {
        var all = await Rows(name).Find(new BsonDocument()).Project(new BsonDocument("_id", 1)).ToListAsync(ct);
        if (all.Any(row => !row["_id"].IsInt64 || row["_id"].AsInt64 is <= 0 or >= long.MaxValue - 1)) throw Bad();
        var high = all.Select(row => row["_id"].AsInt64).DefaultIfEmpty(0).Max();
        var counter = await Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync(ct);
        if (counter is not null && (!counter.TryGetValue("value", out var value) || !value.IsInt64 || value.AsInt64 is < 0 or >= long.MaxValue - 1)) throw Bad();
        maxima[name] = Math.Max(maxima.GetValueOrDefault(name), high);
    }
    internal static BsonDocument Required(string name) => name switch {
        "lots" => InventoryLotSchemas.Validator(), "stockBalances" => InventorySchemas.BalanceValidator(), "movements" => InventoryMovementSchemas.Validator(),
        _ => ProcurementStockReceiptSchemas.Validator(name) };
    internal static BsonDocument[] Keys(string name) => name switch {
        "lots" => [new("_id", 1)], "stockBalances" => [InventorySchemas.BalanceKey()], "movements" => [InventoryMovementSchemas.Key()],
        _ => ProcurementStockReceiptSchemas.Keys(name) };
    // A previously unregistered draft may be present in isolated fixtures. Only its exact known action branch is replaced.
    internal static BsonDocument PreviousMovementValidator() {
        var previous = InventoryMovementSchemas.Validator();
        void Visit(BsonValue value) {
            if (value.IsBsonDocument) foreach (var field in value.AsBsonDocument.Elements.ToArray()) {
                if (field.Name == "enum" && field.Value.IsBsonArray) field.Value.AsBsonArray.Remove("ReceiveHeld");
                if (field.Name == "branches" && field.Value.IsBsonArray) {
                    var items = field.Value.AsBsonArray;
                    foreach (var item in items.Where(item => item.IsBsonDocument && item.AsBsonDocument.TryGetValue("case", out var condition) &&
                        condition == new BsonDocument("$eq", new BsonArray { "$$leg.action", "ReceiveHeld" })).ToArray()) items.Remove(item);
                }
                Visit(field.Value);
            } else if (value.IsBsonArray) foreach (var item in value.AsBsonArray) Visit(item);
        }
        Visit(previous); return previous;
    }
    private static BsonDocument ExtendMovement(BsonDocument current) {
        var prior = PreviousMovementValidator(); var required = InventoryMovementSchemas.Validator();
        BsonDocument Replace(BsonDocument value) {
            if (CatalogSchemas.SemanticEquals(value, prior)) return required.DeepClone().AsBsonDocument;
            var result = value.DeepClone().AsBsonDocument;
            if (result.TryGetValue("$and", out var terms) && terms.IsBsonArray) result["$and"] = new BsonArray(terms.AsBsonArray.Select(term => term.IsBsonDocument ? Replace(term.AsBsonDocument) : term.DeepClone()));
            return result;
        }
        var extended = Replace(current); return extended.ElementCount == 0 ? required : InventorySchemas.Contains(extended, required) ? extended : new("$and", new BsonArray { extended, required });
    }
    private static InvalidOperationException Bad() => new("Procurement stock receipt migration preflight failed");
}
