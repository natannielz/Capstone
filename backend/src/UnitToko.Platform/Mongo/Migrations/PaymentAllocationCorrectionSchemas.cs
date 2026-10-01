using MongoDB.Bson;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using C = UnitToko.Platform.Billing.PaymentAllocationCorrectionBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class PaymentAllocationCorrectionSchemas
{
    internal static readonly string[] Collections = [C.Policies, C.Events];
    internal static BsonDocument PolicyFields() {
        var fields = PaymentAllocationSchemas.PolicyFields(); fields.Add("correctionProcedureReference", S.Text(256)); return fields;
    }
    internal static BsonDocument[] Keys(string name) => PaymentAllocationSchemas.Keys(name == C.Policies ? B.Policies : name == C.Events ? B.Events : throw new ArgumentOutOfRangeException(nameof(name)));
    internal static BsonDocument Validator(string name) {
        var validator = PaymentAllocationSchemas.Validator(name == C.Policies ? B.Policies : name == C.Events ? B.Events : throw new ArgumentOutOfRangeException(nameof(name)));
        var schema = validator["$and"][0]["$jsonSchema"].AsBsonDocument; var fields = schema["properties"].AsBsonDocument;
        if (name == C.Policies) fields.Add("correctionProcedureReference", S.Text(256));
        else {
            fields["operation"] = S.Choice(C.Operation); fields.Add("originalAllocationId", S.Number(1)); fields.Add("originalAllocationHash", PaymentSubmissionSchemas.Hash());
            fields.Add("originalJournalId", S.Number(1)); fields.Add("originalJournalFingerprint", PaymentSubmissionSchemas.Hash()); fields.Add("correctionPolicy", S.Object(PolicyFields()));
            fields.Add("people", new BsonDocument { { "bsonType", "array" }, { "minItems", 2 }, { "maxItems", 3 }, { "items", ReceivingAccountSchemas.Person() } });
            var line = fields["lines"]["items"].AsBsonDocument; var amounts = line["properties"]["amount"].DeepClone().AsBsonDocument;
            line["properties"].AsBsonDocument.Add("originallyAllocated", amounts); var zero = amounts.DeepClone().AsBsonDocument; zero["minimum"] = new BsonDecimal128(0m);
            line["properties"].AsBsonDocument.Add("previouslyReversed", zero); line["required"] = new BsonArray(line["properties"].AsBsonDocument.Names);
        }
        schema["required"] = new BsonArray(fields.Names); return validator;
    }
}
