using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class BankTransactionClaimSchemas
{
    internal static BsonDocument[] Keys => [new("canonicalIdentity", 1), new() { { "direction", 1 }, { "ownerId", 1 } }];
    internal static BsonDocument Validator() {
        var hash = PaymentSubmissionSchemas.Hash();
        var fields = new BsonDocument {
            { "_id", hash }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "canonicalIdentity", hash }, { "direction", S.Choice("credit", "debit") }, { "ownerId", S.Number(1) }, { "ownerHash", hash },
            { "scope", ReceivingAccountSchemas.Scope() }, { "physicalIdentity", hash }, { "identityNamespace", S.Text(80) },
            { "transactionId", S.Text(80) }, { "bookedDate", S.Text(10) },
            { "amount", new BsonDocument { { "bsonType", "decimal" }, { "minimum", new Decimal128(.01m) }, { "maximum", new Decimal128(999999999999999.99m) } } },
            { "importId", S.Number(1) }, { "rowNumber", new BsonDocument { { "bsonType", "int" }, { "minimum", 2 }, { "maximum", 501 } } },
            { "importHash", hash }, { "rowHash", hash }, { "actorId", S.Number(1) }, { "recordedAt", new BsonDocument("bsonType", "date") },
            { "snapshotHash", hash }
        };
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("recordedAt"),
            new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$_id", "$canonicalIdentity" })) });
    }
    internal static async Task RequireStorageAsync(MongoDB.Driver.IMongoDatabase database, CancellationToken ct) {
        var definition = await (await database.ListCollectionsAsync(new MongoDB.Driver.ListCollectionsOptions {
            Filter = new BsonDocument("name", BankTransactionClaims.Collection) }, ct)).SingleOrDefaultAsync(ct);
        if (definition is null || definition.GetValue("type", "") != "collection") throw BankReceiptBson.Broken();
        var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
            options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error" ||
            options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation) ||
            !InventorySchemas.Contains(options.GetValue("validator", new BsonDocument()).AsBsonDocument, Validator())) throw BankReceiptBson.Broken();
        var indexes = await (await database.GetCollection<BsonDocument>(BankTransactionClaims.Collection).Indexes.ListAsync(ct)).ToListAsync(ct);
        bool Match(BsonDocument row, BsonDocument key) => row["key"] == key && row.GetValue("unique", false).AsBoolean &&
            !InvoiceStorage.Unsafe(row) && !row.Contains("partialFilterExpression");
        if (!indexes.Any(row => row["name"] == "_id_" && row["key"] == new BsonDocument("_id", 1) && !InvoiceStorage.Unsafe(row)) ||
            Keys.Any(key => !indexes.Any(row => Match(row, key))) || indexes.Any(row => InvoiceStorage.Unsafe(row) || row.Contains("partialFilterExpression") ||
                row.GetValue("unique", false).AsBoolean && row["name"] != "_id_" && !Keys.Any(key => Match(row, key)))) throw BankReceiptBson.Broken();
    }
}
