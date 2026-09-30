using MongoDB.Bson;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class EvidenceValidationSchemas
{
    internal static readonly string[] Collections = ["evidenceValidationPolicies", "evidenceValidationResults", "evidenceValidationAudits"];
    internal static BsonDocument Key(string name) => name switch { "evidenceValidationPolicies" => new("consumerId", 1), "evidenceValidationAudits" => new("versionRowId", 1), _ => new("outboxId", 1) };
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", Long() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        switch (name) {
            case "evidenceValidationPolicies": fields.AddRange(new BsonDocument {
                { "consumerId", Text(128) }, { "storeId", Hash() }, { "pipelineId", Hash() }, { "active", Type("bool") }, { "revision", Long() }, { "fence", Long(0) },
                { "approvedAt", Type("date") }, { "expiresAt", Type("date") }, { "hostAcceptanceRef", Text(256) } }); break;
            case "evidenceValidationResults": fields.AddRange(new BsonDocument {
                { "outboxId", Type("objectId") }, { "source", Source() }, { "storeId", Hash() }, { "generation", Hash() }, { "length", Long() },
                { "pipelineId", Hash() }, { "consumerId", Text(128) }, { "policyId", Long() }, { "policyRevision", Long() }, { "claimToken", Token() }, { "claimAttempt", Int(1,1000) },
                { "checkedAt", Type("date") }, { "claimUntil", Type("date") }, { "auditId", Long() }, { "resultHash", Hash() }, { "status", Choice("passed", "rejected") },
                { "scanStatus", Choice("clean", "infected", "unavailable") }, { "decoderStatus", Choice("decoded", "invalid", "unavailable") }, { "safeFailure", Nullable(Choice("infected_media", "invalid_media", "integrity_mismatch")) },
                { "mime", Nullable(Choice("image/jpeg", "image/png")) }, { "width", Nullable(Int(1,32768)) }, { "height", Nullable(Int(1,32768)) },
                { "scannerEngine", Nullable(Text(40)) }, { "scannerDefinitions", Nullable(Long()) }, { "scannerDefinitionsAt", Nullable(Type("date")) } }); break;
            case "evidenceValidationAudits": fields.AddRange(new BsonDocument { { "versionRowId", Long() }, { "resultHash", Hash() }, { "outboxId", Type("objectId") },
                { "pipelineId", Hash() }, { "consumerId", Text(128) }, { "policyId", Long() }, { "policyRevision", Long() }, { "claimToken", Token() }, { "claimAttempt", Int(1,1000) }, { "checkedAt", Type("date") }, { "claimUntil", Type("date") } }); break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
        var shape = new BsonDocument("$jsonSchema", Object(fields));
        if (name == "evidenceValidationPolicies") return new("$and", new BsonArray { shape, new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$expiresAt", "$approvedAt" })) });
        if (name == "evidenceValidationAudits") return new("$and", new BsonArray { shape, new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$claimUntil", "$checkedAt" })) });
        return new("$and", new BsonArray { shape, new BsonDocument("$expr", new BsonDocument("$and", new BsonArray {
            Eq("$_id", "$source.versionRowId"), new BsonDocument("$gt", new BsonArray { "$claimUntil", "$checkedAt" }),
            new BsonDocument("$lte", new BsonArray { "$length", 10L * 1024 * 1024 }),
            new BsonDocument("$ne", new BsonArray { "$scannerEngine", BsonNull.Value }),
            new BsonDocument("$ne", new BsonArray { "$scannerDefinitions", BsonNull.Value }),
            new BsonDocument("$ne", new BsonArray { "$scannerDefinitionsAt", BsonNull.Value }),
            new BsonDocument("$lte", new BsonArray { "$scannerDefinitionsAt", "$checkedAt" }),
            new BsonDocument("$or", new BsonArray {
                new BsonDocument("$and", new BsonArray { Eq("$status", "passed"), Eq("$scanStatus", "clean"), Eq("$decoderStatus", "decoded"), Eq("$safeFailure", BsonNull.Value),
                    new BsonDocument("$in", new BsonArray { "$mime", new BsonArray { "image/jpeg", "image/png" } }),
                    new BsonDocument("$gt", new BsonArray { "$width", 0 }), new BsonDocument("$gt", new BsonArray { "$height", 0 }),
                    new BsonDocument("$lte", new BsonArray { new BsonDocument("$multiply", new BsonArray { "$width", "$height" }), 25_000_000 }) }),
                new BsonDocument("$and", new BsonArray { Eq("$status", "rejected"), Eq("$mime", BsonNull.Value), Eq("$width", BsonNull.Value), Eq("$height", BsonNull.Value),
                    new BsonDocument("$or", new BsonArray {
                        new BsonDocument("$and", new BsonArray { Eq("$safeFailure", "infected_media"), Eq("$scanStatus", "infected"), Eq("$decoderStatus", "unavailable") }),
                        new BsonDocument("$and", new BsonArray { Eq("$safeFailure", "invalid_media"), Eq("$scanStatus", "clean"), Eq("$decoderStatus", "invalid") }),
                        new BsonDocument("$and", new BsonArray { Eq("$safeFailure", "integrity_mismatch"), new BsonDocument("$in", new BsonArray { "$scanStatus", new BsonArray { "clean", "infected" } }), Eq("$decoderStatus", "unavailable") }) }) }) }) })) });
    }
    internal static BsonDocument Receipt() => Object(new() { { "versionRowId", Long() }, { "resultHash", Hash() }, { "auditId", Long() }, { "pipelineId", Hash() } });
    internal static BsonDocument OutboxValidator() => new("$or", new BsonArray {
        new BsonDocument("kind", new BsonDocument("$ne", "evidence.validation.requested")),
        new BsonDocument("$jsonSchema", new BsonDocument { { "bsonType", "object" }, { "properties", new BsonDocument {
            { "processingFence", Long(0) }, { "processingReceipt", Receipt() }, { "leaseOwner", Text(128) }, { "leaseToken", Token() }, { "leaseUntil", Type("date") },
            { "claimStartedAt", Type("date") }, { "claimDeadline", Type("date") }, { "attemptLimit", Int(1,1000) }, { "deadAt", Type("date") }, { "completedAt", Type("date") }, { "lastFailureCode", Text(100) } } } }) });
    internal static BsonDocument Source() => Object(new() { { "lineageId", Long() }, { "versionRowId", Long() }, { "reservationId", Long() }, { "attemptId", Long() },
        { "contentVersion", One() }, { "contentRevision", One() }, { "sha256", Hash() }, { "lifecycleRevision", Long() } });
    private static BsonDocument Type(string kind) => new("bsonType", kind);
    private static BsonDocument Long(long minimum = 1) => new() { { "bsonType", "long" }, { "minimum", minimum }, { "maximum", long.MaxValue } };
    private static BsonDocument Int(int minimum, int maximum) => new() { { "bsonType", "int" }, { "minimum", minimum }, { "maximum", maximum } };
    private static BsonDocument One() { var value = Long(); value["enum"] = new BsonArray { 1L }; return value; }
    private static BsonDocument Text(int maximum) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum }, { "pattern", @"\A[^\x00-\x1f\x7f-\x9f]+\z" } };
    private static BsonDocument Hash() { var value = Text(64); value["pattern"] = @"\A[0-9a-f]{64}\z"; return value; }
    private static BsonDocument Token() { var value = Text(32); value["pattern"] = @"\A[0-9a-f]{32}\z"; return value; }
    private static BsonDocument Choice(params string[] values) => new() { { "bsonType", "string" }, { "enum", new BsonArray(values) } };
    private static BsonDocument Nullable(BsonDocument rule) { rule["bsonType"] = new BsonArray { rule["bsonType"], "null" }; if (rule.TryGetValue("enum", out var choices)) choices.AsBsonArray.Add(BsonNull.Value); return rule; }
    private static BsonDocument Object(BsonDocument fields) => new() { { "bsonType", "object" }, { "required", new BsonArray(fields.Names) }, { "additionalProperties", false }, { "properties", fields } };
    private static BsonDocument Eq(BsonValue a, BsonValue b) => new("$eq", new BsonArray { a, b });
}
