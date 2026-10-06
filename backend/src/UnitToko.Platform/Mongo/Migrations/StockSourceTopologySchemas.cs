using MongoDB.Bson;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class StockSourceTopologySchemas
{
    internal static BsonDocument Validator() => new("$jsonSchema", StockSourceSetupSchemas.Object(new BsonDocument {
        { "_id", StockSourceSetupSchemas.Number(1, long.MaxValue) },
        { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
        { "kind", StockSourceSetupSchemas.Choice("stocksource.topology.fence") },
        { "revision", StockSourceSetupSchemas.Number(1, long.MaxValue - 2) }
    }));
}
