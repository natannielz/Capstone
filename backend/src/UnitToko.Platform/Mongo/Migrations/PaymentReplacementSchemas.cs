using MongoDB.Bson;
using UnitToko.Platform.Billing;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class PaymentReplacementSchemas
{
    internal const string IndexName = "ux_payment_replacement_source";
    internal static BsonDocument Key() => new("replacement.source.declarationId", 1);
    internal static BsonDocument Partial() => new("schemaVersion", 2);
    internal static BsonDocument Version() {
        var schema = PaymentDraftSchemas.Validator(PaymentDraftBson.Versions).DeepClone().AsBsonDocument;
        var root = schema["$and"][0]["$jsonSchema"].AsBsonDocument; var fields = root["properties"].AsBsonDocument;
        fields["schemaVersion"]["enum"] = new BsonArray { 2 }; fields["revision"]["enum"] = new BsonArray { 1L };
        fields["previousVersionId"] = new BsonDocument("bsonType", "null"); fields["previousHash"] = new BsonDocument("bsonType", "null");
        fields.Add("replacement", S.Object(new() { { "source", PaymentVerificationSchemas.Source() }, { "reviewRevision", S.Number(1, long.MaxValue - 1) },
            { "actionId", S.Number(1) }, { "actionHash", PaymentSubmissionSchemas.Hash() }, { "binding", PaymentSubmissionSchemas.Hash() } }));
        root["required"].AsBsonArray.Add("replacement"); return schema;
    }
    internal static BsonDocument Validator() => new("$or", new BsonArray { PaymentDraftSchemas.Validator(PaymentDraftBson.Versions), Version() });
}
