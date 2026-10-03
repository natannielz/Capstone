using MongoDB.Bson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Exact immutable lot identity. The owning receipt/conversion history must separately prove its source.</summary>
internal static class InventoryLotSchemas
{
    internal static BsonDocument Validator() {
        var fields = new BsonDocument {
            { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice("inventory.lot") }, { "organizationId", S.Number(1) }, { "skuId", S.Number(1) }, { "definitionId", S.Number(1) },
            { "rootLotId", S.Number(1) }, { "parentLotId", new BsonDocument("anyOf", new BsonArray { S.Number(1), new BsonDocument("bsonType", "null") }) },
            { "origin", S.Choice("openingReceipt", "procurementReceipt", "packageConversion") }, { "sourceId", S.Number(1) }, { "sourceLineId", S.Number(1) },
            { "recordedAt", new BsonDocument("bsonType", "date") },
            { "expiresOn", new BsonDocument("anyOf", new BsonArray { new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } }, new BsonDocument("bsonType", "null") }) }
        };
        return new("$and", new BsonArray {
            new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("recordedAt"),
            new BsonDocument("$expr", new BsonDocument("$cond", new BsonArray {
                new BsonDocument("$eq", new BsonArray { "$origin", "packageConversion" }),
                new BsonDocument("$and", new BsonArray {
                    new BsonDocument("$ne", new BsonArray { "$parentLotId", BsonNull.Value }),
                    new BsonDocument("$ne", new BsonArray { "$parentLotId", "$_id" }), new BsonDocument("$ne", new BsonArray { "$rootLotId", "$_id" }) }),
                new BsonDocument("$and", new BsonArray {
                    new BsonDocument("$eq", new BsonArray { "$parentLotId", BsonNull.Value }), new BsonDocument("$eq", new BsonArray { "$rootLotId", "$_id" }) }) }))
        });
    }
}
