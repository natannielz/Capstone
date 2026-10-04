using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Platform.Billing;

internal static class PaymentVerificationStorage
{
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct) {
        foreach (var name in PaymentVerificationSchemas.Collections) {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            if (definition is null || definition.GetValue("type", "") != "collection") throw B.Broken();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error" ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation) ||
                !(InventorySchemas.Contains(options.GetValue("validator", new BsonDocument()).AsBsonDocument, PaymentVerificationSchemas.Validator(name)) ||
                    name == B.Decisions && InventorySchemas.Contains(options.GetValue("validator", new BsonDocument()).AsBsonDocument, PaymentManualReviewSchemas.VerificationValidator()) ||
                    name == B.Parties && InventorySchemas.Contains(options.GetValue("validator", new BsonDocument()).AsBsonDocument, PaymentReceiptPurposeSchemas.Validator()))) throw B.Broken();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct); var keys = PaymentVerificationSchemas.Keys(name);
            bool Matches(BsonDocument index, BsonDocument key) => index["key"].Equals(key) && (index["name"] == "_id_" || index.GetValue("unique", false).AsBoolean) && !InvoiceStorage.Unsafe(index) && !index.Contains("partialFilterExpression");
            if (!indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)) && !InvoiceStorage.Unsafe(index)) ||
                keys.Any(key => !indexes.Any(index => Matches(index, key))) || indexes.Any(index => InvoiceStorage.Unsafe(index) || index.Contains("partialFilterExpression") ||
                    index.GetValue("unique", false).AsBoolean && index["name"] != "_id_" && !keys.Any(key => Matches(index, key)))) throw B.Broken();
        }
    }
}
