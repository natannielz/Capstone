using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;

namespace UnitToko.Platform.Mongo.Migrations;

// M67 is an exact additive successor. The M56 factory and every retained v1 row remain unchanged.
internal static class ProcurementBankClaimSchemas
{
    internal const string OwnerKind = "procurementDisbursementSettlement";
    internal const string V1Index = "ux_bankTransactionClaims_v1_owner", V2Index = "ux_bankTransactionClaims_v2_owner";
    internal static BsonDocument CanonicalKey => new("canonicalIdentity", 1);
    internal static BsonDocument V1Key => new() { { "direction", 1 }, { "ownerId", 1 } };
    internal static BsonDocument V2Key => new() { { "ownerKind", 1 }, { "ownerId", 1 } };
    internal static BsonDocument V1Filter => new("schemaVersion", 1);
    internal static BsonDocument V2Filter => new() { { "schemaVersion", 2 }, { "ownerKind", OwnerKind } };
    internal static BsonDocument Validator() {
        var previous = BankTransactionClaimSchemas.Validator();
        var next = previous.DeepClone().AsBsonDocument;
        var schema = next["$and"][0]["$jsonSchema"].AsBsonDocument;
        var properties = schema["properties"].AsBsonDocument;
        properties["schemaVersion"]["enum"] = new BsonArray { 2 };
        properties["direction"]["enum"] = new BsonArray { "debit" };
        properties.Add("ownerKind", new BsonDocument { { "bsonType", "string" }, { "enum", new BsonArray { OwnerKind } } });
        schema["required"].AsBsonArray.Add("ownerKind");
        return new("$or", new BsonArray { previous, next });
    }
    internal static BsonDocument Extend(BsonDocument current) {
        var before = BankTransactionClaimSchemas.Validator(); var after = Validator(); var found = false;
        BsonDocument Replace(BsonDocument row) {
            if (CatalogSchemas.SemanticEquals(row, after)) { found = true; return row.DeepClone().AsBsonDocument; }
            if (CatalogSchemas.SemanticEquals(row, before)) { found = true; return after.DeepClone().AsBsonDocument; }
            var copy = row.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                copy["$and"] = new BsonArray(terms.AsBsonArray.Select(term => term.IsBsonDocument ? Replace(term.AsBsonDocument) : term.DeepClone()));
            return copy;
        }
        var result = Replace(current); return found ? result : throw BankReceiptBson.Broken();
    }
    private static bool Plain(BsonDocument index, BsonDocument key) => index["key"] == key && index.GetValue("unique", false).AsBoolean &&
        !InvoiceStorage.Unsafe(index) && !index.Contains("partialFilterExpression");
    private static bool Partial(BsonDocument index, string name, BsonDocument key, BsonDocument filter) => index["name"] == name && index["key"] == key &&
        index.GetValue("unique", false).AsBoolean && !InvoiceStorage.Unsafe(index) && index.GetValue("partialFilterExpression", BsonNull.Value) == filter;
    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct) {
        var (current, indexes) = await DefinitionAsync(database, ct);
        var successor = InventorySchemas.Contains(current, Validator());
        if (!successor && !InventorySchemas.Contains(current, BankTransactionClaimSchemas.Validator())) throw BankReceiptBson.Broken();
        RequireIndexes(indexes, successor, transition: false);
    }
    internal sealed record Transition(BsonDocument Validator, string[] LegacyIndexes);
    internal static async Task<Transition> PreflightAsync(IMongoDatabase database, CancellationToken ct) {
        var (current, indexes) = await DefinitionAsync(database, ct); var extended = Extend(current);
        RequireIndexes(indexes, InventorySchemas.Contains(current, Validator()), transition: true);
        // A partially installed migration may have DDL, never owner facts created before admission is installed.
        if (await database.GetCollection<BsonDocument>(BankTransactionClaims.Collection).WithReadConcern(ReadConcern.Majority)
            .Find(new BsonDocument("$nor", new BsonArray { BankTransactionClaimSchemas.Validator() })).Limit(1).AnyAsync(ct)) throw BankReceiptBson.Broken();
        return new(extended, indexes.Where(index => Plain(index, V1Key)).Select(index => index["name"].AsString).ToArray());
    }
    private static async Task<(BsonDocument, List<BsonDocument>)> DefinitionAsync(IMongoDatabase database, CancellationToken ct) {
        var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", BankTransactionClaims.Collection) }, ct)).SingleOrDefaultAsync(ct);
        if (definition is null || definition.GetValue("type", "") != "collection") throw BankReceiptBson.Broken();
        var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
            options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error" ||
            options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation)) throw BankReceiptBson.Broken();
        return (options.GetValue("validator", new BsonDocument()).AsBsonDocument,
            await (await database.GetCollection<BsonDocument>(BankTransactionClaims.Collection).Indexes.ListAsync(ct)).ToListAsync(ct));
    }
    private static void RequireIndexes(List<BsonDocument> indexes, bool successor, bool transition) {
        bool First(BsonDocument index) => Partial(index, V1Index, V1Key, V1Filter);
        bool Second(BsonDocument index) => Partial(index, V2Index, V2Key, V2Filter);
        var old = indexes.Any(index => Plain(index, V1Key)); var v1 = indexes.Any(First); var v2 = indexes.Any(Second);
        if (!indexes.Any(index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1) && !InvoiceStorage.Unsafe(index) && !index.Contains("partialFilterExpression")) ||
            !indexes.Any(index => Plain(index, CanonicalKey)) ||
            (transition ? !old && !v1 : successor ? old || !v1 || !v2 : !old || v1 || v2) ||
            indexes.Any(index => InvoiceStorage.Unsafe(index) ||
                index.Contains("partialFilterExpression") && !First(index) && !Second(index) ||
                index["name"] == V1Index && !First(index) || index["name"] == V2Index && !Second(index) ||
                index.GetValue("unique", false).AsBoolean && index["name"] != "_id_" && !Plain(index, CanonicalKey) &&
                !Plain(index, V1Key) && !First(index) && !Second(index))) throw BankReceiptBson.Broken();
    }
    internal static async Task ApplyAsync(IMongoDatabase database, Transition transition, MigrationLease lease, CancellationToken ct) {
        var rows = database.GetCollection<BsonDocument>(BankTransactionClaims.Collection).WithWriteConcern(WriteConcern.WMajority);
        foreach (var (name, key, filter) in new[] { (V1Index, V1Key, V1Filter), (V2Index, V2Key, V2Filter) }) {
            await lease.RenewAsync(ct);
            await rows.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key, new CreateIndexOptions<BsonDocument> {
                Name = name, Unique = true, PartialFilterExpression = filter }), cancellationToken: ct);
        }
        await lease.RenewAsync(ct);
        await database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", BankTransactionClaims.Collection }, { "validator", transition.Validator },
            { "validationLevel", "strict" }, { "validationAction", "error" }, { "writeConcern", WriteConcern.WMajority.ToBsonDocument() } }, cancellationToken: ct);
        foreach (var name in transition.LegacyIndexes) { await lease.RenewAsync(ct); await rows.Indexes.DropOneAsync(name, ct); }
        await RequireStorageAsync(database, ct);
    }
}
