using MongoDB.Bson;
using UnitToko.Platform.Billing;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class PaymentCorrectionSchemas
{
    internal static readonly string[] Collections = [PaymentCorrectionBson.Heads, PaymentCorrectionBson.Versions];
    internal static BsonDocument Key(string name) => name == PaymentCorrectionBson.Heads ? new("_id", 1) : new() { { "declarationId", 1 }, { "revision", 1 } };
    private static BsonDocument Nullable(BsonDocument rule) => new("anyOf", new BsonArray { rule, new BsonDocument("bsonType", "null") });
    private static BsonDocument Person() => new("oneOf", new BsonArray {
        S.Object(new() { { "userId", S.Number(1) }, { "personId", new BsonDocument("bsonType", "null") }, { "verification", S.Choice("Unknown") }, { "version", S.Number(0, 0) } }),
        S.Object(new() { { "userId", S.Number(1) }, { "personId", S.Number(1) }, { "verification", S.Choice("Verified") }, { "version", S.Number(1) } }) });
    internal static BsonDocument Fields() => new() { { "bsonType", "array" }, { "minItems", 8 }, { "maxItems", 8 }, { "items", S.Object(new() {
        { "field", S.Choice("amount", "currency", "transfer_date", "transfer_time", "recipient", "account", "bank", "reference") }, { "value", Nullable(S.Text(256)) } }) } };
    internal static BsonDocument Binding() => S.Object(new() { { "declarationRevision", S.Number(1) }, { "submissionId", S.Number(1) }, { "evidence", PaymentSubmissionSchemas.Reference() }, { "extractionId", S.Number(1) } });
    internal static BsonDocument Validator(string name) {
        var date = new BsonDocument("bsonType", "date");
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "revision", S.Number(1, long.MaxValue - 1) }, { "binding", Binding() } };
        if (name == PaymentCorrectionBson.Heads) fields.AddRange(new BsonDocument { { "state", S.Choice("submitted") }, { "versionId", S.Number(1) }, { "fence", S.Number(0, long.MaxValue - 1) }, { "createdAt", date }, { "updatedAt", date } });
        else if (name == PaymentCorrectionBson.Versions) {
            fields.AddRange(new BsonDocument { { "declarationId", S.Number(1) }, { "previousVersionId", Nullable(S.Number(1)) }, { "previousHash", Nullable(PaymentSubmissionSchemas.Hash()) },
                { "recordedBy", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "personAtCorrection", Person() }, { "recordedAt", date },
                { "beforeFields", Fields() }, { "fields", Fields() }, { "requestKey", S.Text(128) }, { "bodyHash", PaymentSubmissionSchemas.Hash() }, { "snapshotHash", PaymentSubmissionSchemas.Hash() } });
            S.Metadata(fields);
        } else throw new ArgumentOutOfRangeException(nameof(name));
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange(name == PaymentCorrectionBson.Heads ? "createdAt" : "recordedAt") };
        if (name == PaymentCorrectionBson.Heads) { terms.Add(S.DateRange("updatedAt")); terms.Add(new BsonDocument("$expr", new BsonDocument("$gte", new BsonArray { "$updatedAt", "$createdAt" }))); }
        return new("$and", terms);
    }
}
