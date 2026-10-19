using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Identity;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Owner-scoped provenance seals: Inventory receipt origins and Fulfillment shipment dispatches.
/// Each owner has its own collection; rows carry only hashes, membership ids and a MAC.</summary>
internal static class ProvenanceSealSchemas
{
    internal const string Collection = "inventoryReceiptOriginSeals", ReceiptOriginKind = "inventory.receiptOrigin";
    internal const string ShipmentDispatchCollection = "shipmentDispatchSeals", ShipmentDispatchKind = "fulfillment.shipmentDispatch";
    internal static readonly (string Collection, string Kind)[] Stores = [(Collection, ReceiptOriginKind), (ShipmentDispatchCollection, ShipmentDispatchKind)];
    private static BsonDocument Hash => new() { { "bsonType", "string" }, { "pattern", "^[0-9a-f]{64}$" } };
    private static BsonDocument Array(BsonDocument item, int maximum) => new() { { "bsonType", "array" }, { "maxItems", maximum }, { "items", item } };
    internal static readonly (string Name, BsonDocument Key, bool Unique)[] Keys = [("kind_owner", new BsonDocument { { "kind", 1 }, { "ownerId", 1 } }, true)];
    /// <summary>Checkpoint seal stores additionally require MAC-covered derived facts with a facts version.</summary>
    internal static BsonDocument Validator(string kind, bool facts) {
        if (!facts) return Validator(kind);
        var schema = Validator(kind).DeepClone().AsBsonDocument; var json = schema["$jsonSchema"].AsBsonDocument;
        json["properties"]["facts"] = new BsonDocument { { "bsonType", "object" }, { "required", new BsonArray { "factsVersion" } },
            { "properties", new BsonDocument("factsVersion", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 } }) } };
        json["required"].AsBsonArray.Add("facts");
        return schema;
    }
    internal static BsonDocument Validator(string kind) => new("$jsonSchema", S.Object(new() {
        { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
        { "kind", S.Choice(kind) }, { "ownerId", S.Number(1) }, { "recordHash", Hash },
        { "membership", Array(new BsonDocument { { "bsonType", "object" }, { "required", new BsonArray { "collection", "filter" } }, { "additionalProperties", false },
            { "properties", new BsonDocument { { "collection", S.Text(64) }, { "filter", new BsonDocument("bsonType", "object") }, { "count", S.Number(0) },
                { "members", Array(S.Object(new() { { "id", new BsonDocument("bsonType", new BsonArray { "long", "int", "objectId", "string" }) }, { "hash", Hash } }), 1000) } } } }, 128) },
        { "dependencies", Array(S.Object(new() { { "kind", S.Text(64) }, { "ownerId", S.Number(1) }, { "sealHash", Hash } }), 64) },
        { "keyId", new BsonDocument { { "bsonType", "string" }, { "pattern", "^[A-Za-z0-9-]{1,64}$" } } },
        { "sealedAt", new BsonDocument("bsonType", "date") }, { "mac", Hash } }));
    internal static async Task RequireStorageAsync(IMongoDatabase database, CancellationToken ct)
    {
        foreach (var (name, kind) in Stores) {
            var definition = await AccountContactStorage.DefinitionAsync(database, name, ct);
            var indexes = definition is null ? [] : (await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct)).ToArray();
            if (definition is null || !AccountContactStorage.SafeDefinition(definition, Validator(kind)) || !AccountContactStorage.SafeIndexes(indexes, Keys) ||
                Keys.Any(key => !indexes.Any(index => AccountContactStorage.Matches(index, key))))
                throw new InvalidOperationException("Provenance seal storage is unavailable");
        }
    }
}
