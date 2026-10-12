using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Fulfillment;

internal static class ShipmentDispatchStorage
{
    // Multiple independent uniqueness rules protect order history and Surat Jalan numbering.
    // The Inventory single-key guard cannot represent this owner's storage contract.
    internal static async Task RequireAsync(IMongoDatabase db, CancellationToken ct, BsonDocument? requiredValidator = null) {
        var definition = await (await db.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "shipments") }, ct)).SingleOrDefaultAsync(ct);
        if (definition is null || definition.GetValue("type", "") != "collection") throw ShipmentDispatchRecords.Invalid();
        var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
        if (options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") ||
            options.GetValue("validationLevel", "strict") != "strict" || options.GetValue("validationAction", "error") != "error" ||
            options.TryGetValue("collation", out var collation) && !Simple(collation) ||
            !options.TryGetValue("validator", out var validator) || !validator.IsBsonDocument ||
            !InventorySchemas.Contains(validator.AsBsonDocument, requiredValidator ?? OrderLineageSchemas.DispatchValidator())) throw ShipmentDispatchRecords.Invalid();
        var indexes = await (await db.GetCollection<BsonDocument>("shipments").Indexes.ListAsync(ct)).ToListAsync(ct);
        if (indexes.Any(index => Unsafe(index) && !ShipmentDispatchMigration.FoundationNumberIndex(index))) throw ShipmentDispatchRecords.Invalid();
        var keys = new[] { ShipmentDispatchSchemas.Key(), ShipmentDispatchSchemas.NumberKey() };
        if (!indexes.Any(index => index["name"] == "_id_" && index["key"].Equals(new BsonDocument("_id", 1)) && !Unsafe(index)) ||
            keys.Any(key => !indexes.Any(index => index["key"].Equals(key) && index.GetValue("unique", false).AsBoolean && !Unsafe(index))) ||
            indexes.Any(index => index.Contains("expireAfterSeconds") || index.GetValue("unique", false).AsBoolean && index["name"] != "_id_" &&
                (Unsafe(index) && !ShipmentDispatchMigration.FoundationNumberIndex(index) || !keys.Any(key => index["key"].Equals(key))))) throw ShipmentDispatchRecords.Invalid();
        // The recognized foundation index is allowed only with its original safe options.
        foreach (var index in indexes.Where(ShipmentDispatchMigration.FoundationNumberIndex)) {
            var copy = index.DeepClone().AsBsonDocument; copy.Remove("partialFilterExpression");
            if (Unsafe(copy)) throw ShipmentDispatchRecords.Invalid();
        }
        ct.ThrowIfCancellationRequested();
    }
    private static bool Simple(BsonValue value) => value.IsBsonDocument && value.AsBsonDocument.Equals(new BsonDocument("locale", "simple"));
    private static bool Unsafe(BsonDocument index) => index.GetValue("sparse", false).AsBoolean || index.GetValue("hidden", false).AsBoolean ||
        index.Contains("partialFilterExpression") || index.Contains("expireAfterSeconds") || index.TryGetValue("collation", out var collation) && !Simple(collation);
}
