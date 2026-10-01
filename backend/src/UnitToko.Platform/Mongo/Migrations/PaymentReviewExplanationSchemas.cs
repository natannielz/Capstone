using MongoDB.Bson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class PaymentReviewExplanationSchemas
{
    internal static BsonDocument Validator() {
        var original = PaymentReviewActionSchemas.Validator();
        var terms = original["$and"].AsBsonArray.DeepClone().AsBsonArray;
        var schema = terms[0]["$jsonSchema"].AsBsonDocument; var fields = schema["properties"].AsBsonDocument;
        fields["schemaVersion"] = new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 2 } } };
        fields["action"] = S.Choice("reject", "request_replacement"); fields["state"] = S.Choice("rejected", "replacement_requested"); fields["assigneeId"] = S.Number(1);
        fields.Add("publicExplanation", S.Text(1000)); schema["required"].AsBsonArray.Add("publicExplanation");
        return new("$or", new BsonArray { original, new BsonDocument("$and", terms) });
    }
}
