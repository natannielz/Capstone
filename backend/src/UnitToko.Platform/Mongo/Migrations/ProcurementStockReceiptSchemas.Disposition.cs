using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Procurement;

namespace UnitToko.Platform.Mongo.Migrations;

// Exact M72 successor: preserve every M71 row and global physical-capacity sequence.
internal static partial class ProcurementStockReceiptSchemas
{
    internal const string V1Index = "ux_procurementDisposition_v1_claim", V2Index = "ux_procurementDisposition_v2_receipt";
    internal static BsonDocument V1Key => new() { { "claimId", 1 }, { "claimLineNumber", 1 } };
    internal static BsonDocument V2Key => new("receiptId", 1);
    internal static BsonDocument V1Filter => new() { { "schemaVersion", 1 }, { "kind", "supplier_claim" } };
    internal static BsonDocument V2Filter => new() { { "schemaVersion", 2 }, { "kind", "held_receipt" } };
    internal static BsonDocument[] GlobalDispositionKeys => SupplierClaimSchemas.Keys(SupplierClaimSchemas.PhysicalAllocations).Skip(1).ToArray();
    internal static BsonDocument Extend(BsonDocument current) {
        var before = SupplierClaimSchemas.Validator(SupplierClaimSchemas.PhysicalAllocations); var after = DispositionValidator(); var found = false;
        BsonDocument Replace(BsonDocument row) {
            if (CatalogSchemas.SemanticEquals(row, after)) { found = true; return row.DeepClone().AsBsonDocument; }
            if (CatalogSchemas.SemanticEquals(row, before)) { found = true; return after.DeepClone().AsBsonDocument; }
            var copy = row.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                copy["$and"] = new BsonArray(terms.AsBsonArray.Select(term => term.IsBsonDocument ? Replace(term.AsBsonDocument) : term.DeepClone()));
            return copy;
        }
        var result = Replace(current); return found ? result : throw ProcurementStockReceiptBson.Broken();
    }
    private static bool Plain(BsonDocument index, BsonDocument key) => index["key"] == key && index.GetValue("unique", false).AsBoolean &&
        !InvoiceStorage.Unsafe(index) && !index.Contains("partialFilterExpression");
    private static bool Partial(BsonDocument index, string name, BsonDocument key, BsonDocument filter) => index["name"] == name && index["key"] == key &&
        index.GetValue("unique", false).AsBoolean && !InvoiceStorage.Unsafe(index) && index.GetValue("partialFilterExpression", BsonNull.Value) == filter;
    internal static async Task RequireDispositionStorageAsync(IMongoDatabase database, CancellationToken ct) {
        var (current, indexes) = await DefinitionAsync(database, ct);
        var successor = InventorySchemas.Contains(current, DispositionValidator());
        if (!successor && !InventorySchemas.Contains(current, SupplierClaimSchemas.Validator(SupplierClaimSchemas.PhysicalAllocations))) throw ProcurementStockReceiptBson.Broken();
        RequireIndexes(indexes, successor, transition: false);
    }
    internal sealed record Transition(BsonDocument Validator, string[] LegacyIndexes);
    internal static async Task<Transition> PreflightAsync(IMongoDatabase database, CancellationToken ct) {
        var (current, indexes) = await DefinitionAsync(database, ct); var extended = Extend(current);
        RequireIndexes(indexes, InventorySchemas.Contains(current, DispositionValidator()), transition: true);
        // Storage validation precedes complete original-owner reconstruction in migration 72.
        if (await database.GetCollection<BsonDocument>(SupplierClaimSchemas.PhysicalAllocations).WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument("$nor", new BsonArray { DispositionValidator() })).Limit(1).AnyAsync(ct)) throw ProcurementStockReceiptBson.Broken();
        return new(extended, indexes.Where(index => Plain(index, V1Key)).Select(index => index["name"].AsString).ToArray());
    }
    private static async Task<(BsonDocument, List<BsonDocument>)> DefinitionAsync(IMongoDatabase database, CancellationToken ct) {
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", SupplierClaimSchemas.PhysicalAllocations) }, ct)).SingleOrDefaultAsync(ct);
        if (definition is null || definition.GetValue("type", "") != "collection") throw ProcurementStockReceiptBson.Broken();
        var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
            options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error" ||
            options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw ProcurementStockReceiptBson.Broken();
        return (options.GetValue("validator", new BsonDocument()).AsBsonDocument,
            await (await database.GetCollection<BsonDocument>(SupplierClaimSchemas.PhysicalAllocations).Indexes.ListAsync(ct)).ToListAsync(ct));
    }
    private static void RequireIndexes(List<BsonDocument> indexes, bool successor, bool transition) {
        bool First(BsonDocument index) => Partial(index, V1Index, V1Key, V1Filter);
        bool Second(BsonDocument index) => Partial(index, V2Index, V2Key, V2Filter);
        var old = indexes.Any(index => Plain(index, V1Key)); var v1 = indexes.Any(First); var v2 = indexes.Any(Second);
        if (!indexes.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1) && !InvoiceStorage.Unsafe(index) && !index.Contains("partialFilterExpression")) ||
            GlobalDispositionKeys.Any(key => !indexes.Any(index => Plain(index, key))) ||
            (transition ? !old && !v1 : successor ? old || !v1 || !v2 : !old || v1 || v2) ||
            indexes.Any(index => InvoiceStorage.Unsafe(index) ||
                index.Contains("partialFilterExpression") && !First(index) && !Second(index) ||
                index["name"] == V1Index && !First(index) || index["name"] == V2Index && !Second(index) ||
                index.GetValue("unique", false).AsBoolean && index["name"] != "_id_" && !GlobalDispositionKeys.Any(key => Plain(index, key)) &&
                !Plain(index, V1Key) && !First(index) && !Second(index))) throw ProcurementStockReceiptBson.Broken();
    }
    internal static async Task ApplyAsync(IMongoDatabase database, Transition transition, MigrationLease lease, CancellationToken ct) {
        var rows = database.GetCollection<BsonDocument>(SupplierClaimSchemas.PhysicalAllocations).WithWriteConcern(WriteConcern.WMajority);
        foreach (var (name, key, filter) in new[] { (V1Index, V1Key, V1Filter), (V2Index, V2Key, V2Filter) }) {
            await lease.RenewAsync(ct);
            await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions<BsonDocument> {
                Name = name, Unique = true, PartialFilterExpression = filter }), cancellationToken: ct);
        }
        await lease.RenewAsync(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", SupplierClaimSchemas.PhysicalAllocations }, { "validator", transition.Validator },
            { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        foreach (var name in transition.LegacyIndexes) { await lease.RenewAsync(ct); await rows.Indexes.DropOneAsync(name, ct); }
        await RequireDispositionStorageAsync(database, ct);
    }
}
