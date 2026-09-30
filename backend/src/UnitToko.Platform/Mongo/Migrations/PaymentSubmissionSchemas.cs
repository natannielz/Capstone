using MongoDB.Bson;
using UnitToko.Platform.Billing;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class PaymentSubmissionSchemas
{
    internal static readonly string[] Collections = ["paymentSubmissions", "evidencePins", "evidencePinAudits"];
    internal static BsonDocument Key(string name) => name switch {
        "paymentSubmissions" => new("declarationId", 1), "evidencePins" => new() { { "associationKind", 1 }, { "associationId", 1 } },
        "evidencePinAudits" => new("pinId", 1), _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Reference() => S.Object(new() { { "id", S.Number(1) }, { "version", S.Number(1, 1) }, { "sha256", Hash() } });
    internal static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Person() => new("oneOf", new BsonArray {
        S.Object(new() { { "userId", S.Number(1) }, { "personId", new BsonDocument("bsonType", "null") }, { "verification", S.Choice("Unknown") }, { "version", S.Number(0, 0) } }),
        S.Object(new() { { "userId", S.Number(1) }, { "personId", S.Number(1) }, { "verification", S.Choice("Verified") }, { "version", S.Number(1) } }) });
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        if (name == "paymentSubmissions") {
            foreach (var key in new[] { "declarationId", "declarationVersionId", "declarationRevision", "recordedBy", "submittedBy", "uploaderId", "reservationId", "pinId", "actorGrantVersion" }) fields.Add(key, S.Number(1));
            fields.AddRange(new BsonDocument { { "reviewRevision", S.Number(1, 1) }, { "state", S.Choice("submitted") }, { "declarationHash", Hash() }, { "evidence", Reference() },
                { "submittedAt", new BsonDocument("bsonType", "date") }, { "personAtSubmission", Person() }, { "requestKey", S.Text(128) }, { "bodyHash", Hash() }, { "snapshotHash", Hash() } });
            S.Metadata(fields);
        } else if (name == "evidencePins") {
            foreach (var key in new[] { "associationId", "declarationId", "declarationVersionId", "declarationRevision", "reservationId", "versionRowId", "lifecycleRevision", "actorId", "actorGrantVersion", "validationPolicyId", "validationPolicyRevision", "readPolicyId", "readPolicyRevision" }) fields.Add(key, S.Number(1));
            fields.AddRange(new BsonDocument { { "associationKind", S.Choice("PaymentSubmission") }, { "reviewRevision", S.Number(1, 1) }, { "reference", Reference() },
                { "storeId", Hash() }, { "generation", Hash() }, { "validationHash", Hash() }, { "associationHash", Hash() }, { "pinHash", Hash() }, { "createdAt", new BsonDocument("bsonType", "date") } });
            S.Metadata(fields);
        } else if (name == "evidencePinAudits") {
            foreach (var key in new[] { "pinId", "associationId", "versionRowId", "actorId" }) fields.Add(key, S.Number(1));
            fields.AddRange(new BsonDocument { { "pinHash", Hash() }, { "reason", S.Text(1000) }, { "correlationId", S.Text(128) }, { "createdAt", new BsonDocument("bsonType", "date") } });
        } else throw new ArgumentOutOfRangeException(nameof(name));
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange(name == "paymentSubmissions" ? "submittedAt" : "createdAt") });
    }
    internal static BsonDocument DeclarationValidator() {
        var next = PaymentDraftSchemas.Validator(PaymentDraftBson.Heads).DeepClone().AsBsonDocument;
        var shape = next["$and"][0]["$jsonSchema"].AsBsonDocument;
        shape["properties"]["schemaVersion"]["enum"] = new BsonArray { 2 };
        shape["properties"]["state"] = S.Choice("submitted");
        shape["properties"]["submissionId"] = S.Number(1); shape["properties"]["reviewRevision"] = S.Number(1, 1);
        shape["required"].AsBsonArray.Add("submissionId"); shape["required"].AsBsonArray.Add("reviewRevision");
        return new("$or", new BsonArray { PaymentDraftSchemas.Validator(PaymentDraftBson.Heads), next });
    }
    internal static BsonDocument ExtendDeclaration(BsonDocument current) {
        var before = PaymentDraftSchemas.Validator(PaymentDraftBson.Heads); var after = DeclarationValidator(); var found = false;
        BsonDocument Replace(BsonDocument row) {
            if (CatalogSchemas.SemanticEquals(row, before) || CatalogSchemas.SemanticEquals(row, after)) { found = true; return after.DeepClone().AsBsonDocument; }
            var copy = row.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray) copy["$and"] = new BsonArray(terms.AsBsonArray.Select(value => value.IsBsonDocument ? Replace(value.AsBsonDocument) : value.DeepClone()));
            return copy;
        }
        var result = Replace(current); return found ? result : throw new InvalidOperationException("Payment submission migration requires the accepted declaration validator");
    }
}
