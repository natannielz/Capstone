using MongoDB.Bson;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class EvidenceAccessSchemas
{
    internal static readonly string[] Collections = ["evidenceReadPolicies", "evidenceLifecycles", "evidenceLifecycleAudits", "evidenceReadLeases", "evidenceReadAudits"];
    internal static BsonDocument Key(string name) => name switch {
        "evidenceReadPolicies" => new("storeId", 1), "evidenceLifecycles" => new("source.reservationId", 1),
        "evidenceLifecycleAudits" => new() { { "versionRowId", 1 }, { "revision", 1 } }, "evidenceReadLeases" => new("token", 1),
        "evidenceReadAudits" => new() { { "leaseId", 1 }, { "revision", 1 } }, _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", Long() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        var conditions = new BsonArray();
        switch (name) {
            case "evidenceReadPolicies": fields.AddRange(new BsonDocument {
                { "storeId", Hash() }, { "active", Type("bool") }, { "revision", Long() }, { "fence", Long(0) }, { "createdAt", Type("date") }, { "updatedAt", Type("date") },
                { "maximumBytes", Long(1, 10 * 1024 * 1024) }, { "verificationDurationMs", Long(100, 60000) }, { "leaseDurationMs", Long(1000, 10000) },
                { "requestDurationMs", Long(1000, 120000) }, { "maximumValidationAgeMs", Long(1000, 86400000) }, { "maximumSignatureAgeMs", Long(1000, 604800000) } });
                conditions.Add(Compare("$lte", "$leaseDurationMs", "$requestDurationMs")); conditions.Add(Compare("$lte", "$verificationDurationMs", "$requestDurationMs")); break;
            case "evidenceLifecycles": fields.AddRange(new BsonDocument {
                { "source", EvidenceValidationSchemas.Source() }, { "validationHash", Hash() }, { "storeId", Hash() }, { "generation", Hash() },
                { "state", Choice("validated", "failed") }, { "revision", Long() }, { "fence", Long(0) }, { "lastAuditId", Long() }, { "createdAt", Type("date") }, { "updatedAt", Type("date") } });
                conditions.Add(Compare("$eq", "$_id", "$source.versionRowId"));
                conditions.Add(Compare("$eq", "$revision", new BsonDocument("$add", new BsonArray { "$source.lifecycleRevision", 1L }))); break;
            case "evidenceLifecycleAudits": fields.AddRange(new BsonDocument {
                { "versionRowId", Long() }, { "revision", Long() }, { "state", Choice("validated", "failed") }, { "validationHash", Hash() }, { "recordHash", Hash() }, { "createdAt", Type("date") } }); break;
            case "evidenceReadLeases": fields.AddRange(new BsonDocument {
                { "versionRowId", Long() }, { "reservationId", Long() }, { "reference", Reference() }, { "storeId", Hash() }, { "generation", Hash() },
                { "validationHash", Hash() }, { "lifecycleRevision", Long() }, { "target", Target() }, { "actor", Actor() }, { "token", Hash() },
                { "state", Choice("provisional", "active", "released") }, { "revision", Long(1, 256) }, { "fence", Long(0) }, { "lastAuditId", Long() },
                { "readPolicyId", Long() }, { "readPolicyRevision", Long() }, { "validationPolicyId", Long() }, { "validationPolicyRevision", Long() },
                { "createdAt", Type("date") }, { "updatedAt", Type("date") }, { "deadline", Type("date") }, { "until", Type("date") }, { "correlationId", Text(128) } });
                conditions.Add(Compare("$gt", "$deadline", "$createdAt")); conditions.Add(Compare("$gt", "$until", "$createdAt")); conditions.Add(Compare("$lte", "$until", "$deadline"));
                conditions.Add(new BsonDocument("$cond", new BsonArray { Compare("$eq", "$state", "provisional"), Compare("$eq", "$revision", 1L), Compare("$gte", "$revision", 2L) })); break;
            case "evidenceReadAudits": fields.AddRange(new BsonDocument {
                { "leaseId", Long() }, { "versionRowId", Long() }, { "revision", Long(1, 256) }, { "kind", Choice("acquired", "activated", "renewed", "released") },
                { "actorId", Long() }, { "recordHash", Hash() }, { "correlationId", Text(128) }, { "createdAt", Type("date") } }); break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
        if (fields.Contains("updatedAt")) conditions.Add(Compare("$gte", "$updatedAt", "$createdAt"));
        var shape = new BsonDocument("$jsonSchema", Object(fields));
        return conditions.Count == 0 ? shape : new("$and", new BsonArray { shape, new BsonDocument("$expr", new BsonDocument("$and", conditions)) });
    }
    internal static BsonDocument Actor() => Object(new() { { "userId", Long() }, { "issuer", Text(512) }, { "subject", Text(512) }, { "grantVersion", Long(0) },
        { "session", Nullable(Object(new() { { "id", Hash() }, { "version", Long() }, { "securityVersion", Long() } })) } });
    private static BsonDocument Target() => Object(new() { { "kind", Choice("PaymentDeclaration") }, { "id", Long() }, { "revision", Long() } });
    private static BsonDocument Reference() => Object(new() { { "id", Long() }, { "version", Long(1, 1) }, { "sha256", Hash() } });
    private static BsonDocument Object(BsonDocument fields) => new() { { "bsonType", "object" }, { "required", new BsonArray(fields.Names) }, { "additionalProperties", false }, { "properties", fields } };
    private static BsonDocument Type(string name) => new("bsonType", name);
    private static BsonDocument Long(long min = 1, long max = long.MaxValue) => new() { { "bsonType", "long" }, { "minimum", min }, { "maximum", max } };
    private static BsonDocument Text(int maximum) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum }, { "pattern", @"\A[^\x00-\x1f\x7f-\x9f]+\z" } };
    private static BsonDocument Hash() { var value = Text(64); value["pattern"] = @"\A[0-9a-f]{64}\z"; return value; }
    private static BsonDocument Choice(params string[] choices) => new() { { "bsonType", "string" }, { "enum", new BsonArray(choices) } };
    private static BsonDocument Nullable(BsonDocument value) { value["bsonType"] = new BsonArray { value["bsonType"], "null" }; return value; }
    private static BsonDocument Compare(string op, BsonValue a, BsonValue b) => new(op, new BsonArray { a, b });
}
