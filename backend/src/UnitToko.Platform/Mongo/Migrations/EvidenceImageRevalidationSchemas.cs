using MongoDB.Bson;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Separate, append-only image scans. The original validation and lifecycle schemas stay unchanged.</summary>
internal static class EvidenceImageRevalidationSchemas
{
    internal const string Policies = "evidenceImageRevalidationPolicies";
    internal const string Requests = "evidenceImageRevalidationRequests";
    internal const string Jobs = "evidenceImageRevalidationJobs";
    internal const string Results = "evidenceImageRevalidationResults";
    internal const string Audits = "evidenceImageRevalidationAudits";
    internal const string Heads = "evidenceImageRevalidationHeads";
    internal const string Receipts = "evidenceImageRevalidationReceipts";
    internal static readonly string[] Collections = [Policies, Requests, Jobs, Results, Audits, Heads, Receipts];

    internal static BsonDocument Validator(string name) => new("$jsonSchema", DocumentSchema(name));
    private static BsonDocument DocumentSchema(string name) => name switch
    {
        Policies => Object(new() { { "_id", Long() }, { "schemaVersion", OneInt() }, { "consumerId", Text(128) },
            { "storeId", Hash() }, { "pipelineId", Hash() }, { "active", Type("bool") },
            { "revision", Long() }, { "fence", Long(0) }, { "approvedAt", Date() },
            { "expiresAt", Date() }, { "hostAcceptanceRef", Text(256) } }),
        Requests => Object(new() { { "_id", Long() }, { "schemaVersion", OneInt() }, { "skuId", Long() },
            { "reservationId", Long() }, { "versionRowId", Long() }, { "source", Source() },
            { "headRevision", Long(0) }, { "previousRequestId", Nullable(Long()) },
            { "previousRequestHash", Nullable(Hash()) },
            { "policyId", Long() }, { "policyRevision", Long() }, { "pipelineId", Hash() },
            { "consumerId", Text(128) },
            { "actorId", Long() }, { "requestKey", Text(200) }, { "bodyHash", Hash() },
            { "reason", Text(1000) }, { "correlationId", Text(128) }, { "recordedAt", Date() },
            { "requestHash", Hash() } }),
        Jobs => Object(new() { { "_id", Long() }, { "schemaVersion", OneInt() }, { "requestId", Long() }, { "versionRowId", Long() },
            { "consumerId", Text(128) }, { "pipelineId", Hash() }, { "storeId", Hash() },
            { "policyId", Long() }, { "policyRevision", Long() },
            { "sourceKey", Text(200) }, { "kind", Choice("evidence.image-revalidation.requested") },
            { "status", Choice("pending", "leased", "retry", "completed", "dead") },
            { "attempts", Int(0, 1000) }, { "dueAt", Date() }, { "createdAt", Date() },
            { "leaseOwner", Nullable(Text(128)) }, { "leaseToken", Nullable(Text(32)) },
            { "leaseUntil", Nullable(Date()) }, { "claimDeadline", Nullable(Date()) },
            { "resultHash", Nullable(Hash()) }, { "updatedAt", Date() } }),
        Results => Object(new() { { "_id", Long() }, { "schemaVersion", OneInt() }, { "requestId", Long() },
            { "jobId", Long() }, { "source", Source() }, { "requestHash", Hash() },
            { "storeId", Hash() }, { "generation", Hash() }, { "length", Long() },
            { "pipelineId", Hash() }, { "consumerId", Text(128) }, { "policyId", Long() },
            { "policyRevision", Long() }, { "claimToken", Text(32) }, { "claimAttempt", Int(1, 1000) },
            { "checkedAt", Date() }, { "claimUntil", Date() }, { "auditId", Long() },
            { "status", Choice("passed", "rejected") }, { "scanStatus", Choice("clean", "infected") },
            { "decoderStatus", Choice("decoded", "invalid", "unavailable") },
            { "safeFailure", Nullable(Choice("infected_media", "invalid_media", "integrity_mismatch")) },
            { "mime", Nullable(Choice("image/png", "image/jpeg")) },
            { "width", Nullable(Int(1, 32768)) }, { "height", Nullable(Int(1, 32768)) },
            { "scannerEngine", Text(40) }, { "scannerDefinitions", Long() },
            { "scannerDefinitionsAt", Date() }, { "resultHash", Hash() } }),
        Audits => Object(new() { { "_id", Long() }, { "schemaVersion", OneInt() }, { "requestId", Long() }, { "versionRowId", Long() },
            { "resultId", Long() }, { "resultHash", Hash() }, { "jobId", Long() }, { "checkedAt", Date() } }),
        Heads => Object(new() { { "_id", Long() }, { "schemaVersion", OneInt() }, { "revision", Long() },
            { "requestId", Long() }, { "requestHash", Hash() }, { "resultId", Nullable(Long()) },
            { "resultHash", Nullable(Hash()) }, { "status", Choice("pending", "passed", "rejected") },
            { "fence", Long(0) } }),
        Receipts => Object(new() { { "_id", Type("objectId") }, { "schemaVersion", OneInt() },
            { "actorId", Long() }, { "operation", Choice("evidence.image-revalidation.request") },
            { "key", Text(200) }, { "bodyHash", Hash() }, { "requestId", Long() }, { "versionRowId", Long() },
            { "requestHash", Hash() }, { "recordedAt", Date() } }),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch
    {
        Policies => [("ux_image_revalidation_policy_consumer", new("consumerId", 1), true)],
        Requests => [("ux_image_revalidation_request_actor_key", new("actorId", 1) { { "requestKey", 1 } }, true),
            ("ix_image_revalidation_request_version", new("versionRowId", 1) { { "_id", 1 } }, false)],
        Jobs => [("ux_image_revalidation_job_request", new("requestId", 1), true),
            ("ix_image_revalidation_job_claim", new("consumerId", 1) { { "pipelineId", 1 }, { "storeId", 1 }, { "status", 1 }, { "dueAt", 1 }, { "_id", 1 } }, false),
            ("ix_image_revalidation_job_version", new("versionRowId", 1) { { "requestId", 1 } }, false)],
        Results => [("ux_image_revalidation_result_request", new("requestId", 1), true),
            ("ux_image_revalidation_result_audit", new("auditId", 1), true),
            ("ix_image_revalidation_result_version", new("source.versionRowId", 1) { { "requestId", 1 } }, false)],
        Audits => [("ux_image_revalidation_audit_result", new("resultId", 1), true),
            ("ix_image_revalidation_audit_version", new("versionRowId", 1) { { "requestId", 1 } }, false)],
        Heads => [],
        Receipts => [("ux_image_revalidation_receipt_actor_key", new("actorId", 1) { { "operation", 1 }, { "key", 1 } }, true),
            ("ux_image_revalidation_receipt_request", new("requestId", 1), true),
            ("ix_image_revalidation_receipt_version", new("versionRowId", 1) { { "requestId", 1 } }, false)],
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    internal static BsonDocument Source() => Object(new() { { "skuId", Long() }, { "skuRevision", Long() }, { "definitionId", Long() },
        { "definitionVersion", Long() }, { "owner", Owner() }, { "reservationId", Long() },
        { "versionRowId", Long() }, { "reference", Reference() }, { "storeId", Hash() },
        { "generation", Hash() }, { "length", Long() }, { "originalValidationHash", Hash() },
        { "originalMime", Choice("image/png", "image/jpeg") } });
    private static BsonDocument Reference() => Object(new() { { "id", Long() },
        { "version", Long() }, { "sha256", Hash() } });
    private static BsonDocument Owner() => Object(new() { { "kind", Choice("fulfillmentShop", "fulfillmentWarehouse") },
        { "organizationId", Long() }, { "shopId", Long() }, { "channelId", Long() },
        { "warehouseId", Nullable(Long()) } });
    private static BsonDocument Object(BsonDocument fields) => new() { { "bsonType", "object" },
        { "required", new BsonArray(fields.Names) }, { "additionalProperties", false }, { "properties", fields } };
    private static BsonDocument Type(string kind) => new("bsonType", kind);
    private static BsonDocument Date() => Type("date");
    private static BsonDocument OneInt() => new() { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } };
    private static BsonDocument Int(int min, int max) => new() { { "bsonType", "int" }, { "minimum", min }, { "maximum", max } };
    private static BsonDocument Long(long min = 1) => new() { { "bsonType", "long" }, { "minimum", min }, { "maximum", long.MaxValue } };
    private static BsonDocument Text(int max) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", max } };
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", "\\A[0-9a-f]{64}\\z" } };
    private static BsonDocument Choice(params string[] names) => new() { { "bsonType", "string" }, { "enum", new BsonArray(names) } };
    private static BsonDocument Nullable(BsonDocument item) { var copy = item.DeepClone().AsBsonDocument;
        copy["bsonType"] = new BsonArray { copy["bsonType"], "null" };
        if (copy.Contains("enum")) copy["enum"].AsBsonArray.Add(BsonNull.Value);
        return copy; }
}
