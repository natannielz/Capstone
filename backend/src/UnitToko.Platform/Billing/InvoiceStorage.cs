using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Billing;

internal static class InvoiceStorage
{
    internal static bool Simple(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.Equals(new BsonDocument("locale", "simple"));
    internal static bool Unsafe(BsonDocument index) => index.Contains("expireAfterSeconds") || index.GetValue("sparse", false).AsBoolean ||
        index.GetValue("hidden", false).AsBoolean || index.TryGetValue("collation", out var collation) && !Simple(collation);
    // Foundation's partial prototype index cannot affect canonical invoices, which have neither
    // prototype field. Preserve it; never drop an unknown index to make a deployment succeed.
    internal static bool Prototype(BsonDocument index) => index["name"] == "uq_sourceReceiptLineId_sourceAllocationId" &&
        index["key"].Equals(new BsonDocument { { "sourceReceiptLineId", 1 }, { "sourceAllocationId", 1 } }) && index.GetValue("unique", false).AsBoolean &&
        index.GetValue("partialFilterExpression", BsonNull.Value).Equals(new BsonDocument("$and", new BsonArray {
            new BsonDocument("sourceReceiptLineId", new BsonDocument("$exists", true)), new BsonDocument("sourceAllocationId", new BsonDocument("$exists", true)) }));
    internal static bool FoundationNumber(BsonDocument index) => index["name"] == "uq_numberEntity_numberChannel_numberYear_number" &&
        index["key"].Equals(InvoiceSchemas.NumberKey()) && index.GetValue("unique", false).AsBoolean && index.TryGetValue("partialFilterExpression", out var partial) &&
        InvoiceRecords.Equal(partial, new BsonDocument("$and", new BsonArray(InvoiceSchemas.NumberKey().Names.Select(name => new BsonDocument(name, new BsonDocument("$exists", true))))));
    internal static BsonDocument[] RequiredKeys(string name) => name == InvoiceBson.Invoices ? [InvoiceSchemas.NumberKey(), InvoiceSchemas.CommandKey()] : [InvoiceSchemas.AllocationKey()];
    internal static async Task RequireAsync(IMongoDatabase db, CancellationToken ct) {
        foreach (var name in InvoiceSchemas.Collections) {
            var definition = await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            if (definition is null || definition.GetValue("type", "") != "collection") throw InvoiceRecords.Invalid();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
                options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error" ||
                options.TryGetValue("collation", out var collation) && !Simple(collation) ||
                !InventorySchemas.Contains(options.GetValue("validator", new BsonDocument()).AsBsonDocument, InvoiceSchemas.Validator(name))) throw InvoiceRecords.Invalid();
            var indexes = await (await db.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            var keys = RequiredKeys(name);
            bool Matches(BsonDocument index, BsonDocument key) => index["key"].Equals(key) && index.GetValue("unique", false).AsBoolean && !index.Contains("partialFilterExpression") && !Unsafe(index);
            if (!indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)) && !Unsafe(index)) ||
                keys.Any(key => !indexes.Any(index => Matches(index, key))) || indexes.Any(index => Unsafe(index) ||
                    index.GetValue("unique", false).AsBoolean && index["name"] != "_id_" && !keys.Any(key => Matches(index, key)) && !(name == InvoiceBson.Invoices && (Prototype(index) || FoundationNumber(index))))) throw InvoiceRecords.Invalid();
        }
    }
}
