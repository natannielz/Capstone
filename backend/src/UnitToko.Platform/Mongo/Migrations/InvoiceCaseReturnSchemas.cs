using MongoDB.Bson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class InvoiceCaseReturnSchemas
{
    internal static BsonDocument Validator() => new("$jsonSchema", S.Object(new() { { "_id", S.Number(1) },
        { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } }, { "invoiceHash", PaymentSubmissionSchemas.Hash() }, { "fence", S.Number(1, long.MaxValue - 1) } }));
}
