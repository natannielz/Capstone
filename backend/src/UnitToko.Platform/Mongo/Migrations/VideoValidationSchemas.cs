using MongoDB.Bson;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class VideoValidationSchemas
{
    internal static readonly string[] Collections = [VideoValidationRecords.Policies, VideoValidationRecords.Results, VideoValidationRecords.Audits];
    internal static BsonDocument Key(string name) => name switch {
        VideoValidationRecords.Policies => new("consumerId", 1), VideoValidationRecords.Results => new("outboxId", 1),
        VideoValidationRecords.Audits => new("versionRowId", 1), _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", Long() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        if (name == VideoValidationRecords.Policies) {
            fields.AddRange(new BsonDocument { { "consumerId", Text(128) }, { "storeId", Hash() }, { "pipelineId", Hash() },
                { "active", Type("bool") }, { "revision", Long() }, { "fence", Long(0) }, { "approvedAt", Type("date") },
                { "expiresAt", Type("date") }, { "hostAcceptanceRef", Text(256) }, { "clamdConfigurationAcceptanceRef", Text(256) } });
            return With(fields, Compare("$gt", "$expiresAt", "$approvedAt"));
        }
        var execution = new BsonDocument { { "outboxId", Type("objectId") }, { "pipelineId", Hash() }, { "consumerId", Text(128) },
            { "policyId", Long() }, { "policyRevision", Long() }, { "claimToken", Token() }, { "claimAttempt", Int(1, 1000) },
            { "checkedAt", Type("date") }, { "claimUntil", Type("date") } };
        if (name == VideoValidationRecords.Audits) {
            fields.AddRange(new BsonDocument { { "versionRowId", Long() }, { "resultHash", Hash() } }); fields.AddRange(execution);
            return With(fields, Compare("$gt", "$claimUntil", "$checkedAt"));
        }
        if (name != VideoValidationRecords.Results) throw new ArgumentOutOfRangeException(nameof(name));
        fields.AddRange(execution);
        fields.AddRange(new BsonDocument { { "source", Source() }, { "original", Original() }, { "storeId", Hash() }, { "generation", Hash() }, { "length", Long() },
            { "auditId", Long() }, { "resultHash", Hash() }, { "status", Choice("passed", "rejected") }, { "scanStatus", Choice("clean", "infected") },
            { "decoderStatus", Choice("decoded", "invalid", "unavailable") }, { "safeFailure", Nullable(Choice("infected_media", "invalid_media", "integrity_mismatch")) },
            { "mime", Nullable(Choice("video/mp4", "video/webm")) }, { "videoCodec", Nullable(Choice("h264", "hevc", "vp8", "vp9")) },
            { "width", Nullable(Int(1)) }, { "height", Nullable(Int(1)) },
            { "durationSeconds", Nullable(new BsonDocument { { "bsonType", "decimal" }, { "minimum", new Decimal128(0m) }, { "maximum", new Decimal128(TimeSpan.MaxValue.Ticks / (decimal)TimeSpan.TicksPerSecond) } }) },
            { "videoFrames", Nullable(Long()) }, { "audioCodec", Nullable(Choice("aac", "opus", "vorbis")) },
            { "audioChannels", Nullable(Int(1)) }, { "audioSampleRate", Nullable(Int(1)) }, { "audioFrames", Nullable(Long(0)) }, { "audioSamples", Nullable(Long(0)) },
            { "scannerEngine", Text(40) }, { "scannerDefinitions", Long() }, { "scannerDefinitionsAt", Type("date") } });
        var facts = new[] { "mime", "videoCodec", "width", "height", "durationSeconds", "videoFrames", "audioCodec", "audioChannels", "audioSampleRate", "audioFrames", "audioSamples" };
        var rejected = new BsonArray { Eq("$status", "rejected") };
        foreach (var fact in facts) rejected.Add(Eq("$" + fact, BsonNull.Value));
        rejected.Add(Or(
            And(Eq("$safeFailure", "infected_media"), Eq("$scanStatus", "infected"), Eq("$decoderStatus", "unavailable")),
            And(Eq("$safeFailure", "invalid_media"), Eq("$scanStatus", "clean"), Eq("$decoderStatus", "invalid")),
            And(Eq("$safeFailure", "integrity_mismatch"), Eq("$decoderStatus", "unavailable"))));
        var audio = Or(
            And(Eq("$audioCodec", BsonNull.Value), Eq("$audioChannels", BsonNull.Value), Eq("$audioSampleRate", BsonNull.Value), Eq("$audioFrames", 0L), Eq("$audioSamples", 0L)),
            And(In("$audioCodec", "aac", "opus", "vorbis"), Compare("$gt", "$audioChannels", 0), Compare("$gt", "$audioSampleRate", 0),
                Compare("$gt", "$audioFrames", 0L), Compare("$gt", "$audioSamples", 0L)));
        return With(fields, And(Eq("$_id", "$source.versionRowId"), Compare("$gt", "$claimUntil", "$checkedAt"),
            Eq("$storeId", "$original.storeId"), Eq("$generation", "$original.generation"), Eq("$length", "$original.length"),
            Eq("$source.reservationId", "$original.reservationId"), Eq("$source.attemptId", "$original.attemptId"),
            Eq("$source.contentRevision", "$original.contentRevision"), Eq("$source.sha256", "$original.sha256"),
            Compare("$lte", "$scannerDefinitionsAt", "$checkedAt"), Or(
                And(Eq("$status", "passed"), Eq("$scanStatus", "clean"), Eq("$decoderStatus", "decoded"), Eq("$safeFailure", BsonNull.Value),
                    Compare("$gt", "$width", 0), Compare("$gt", "$height", 0), Compare("$gt", "$durationSeconds", new Decimal128(0m)), Compare("$gt", "$videoFrames", 0L), audio,
                    Or(And(Eq("$mime", "video/mp4"), In("$videoCodec", "h264", "hevc"), Or(Eq("$audioCodec", BsonNull.Value), Eq("$audioCodec", "aac"))),
                       And(Eq("$mime", "video/webm"), In("$videoCodec", "vp8", "vp9"), Or(Eq("$audioCodec", BsonNull.Value), In("$audioCodec", "opus", "vorbis"))))),
                new BsonDocument("$and", rejected))));
    }
    internal static BsonDocument Source() => EvidenceValidationSchemas.Source();
    internal static BsonDocument Original() => Object(new() { { "storeId", Hash() }, { "reservationId", Long() }, { "attemptId", Long() },
        { "attemptRevision", Long() }, { "stageToken", Hash() }, { "generation", Hash() }, { "contentRevision", Long() }, { "length", Long() }, { "sha256", Hash() } });
    internal static BsonDocument Receipt() => Object(new() { { "evidenceVideoValidation", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { true } } } },
        { "versionRowId", Long() }, { "resultHash", Hash() }, { "auditId", Long() }, { "pipelineId", Hash() } });
    internal static BsonDocument OutboxValidator() => new("$or", new BsonArray {
        new BsonDocument("kind", new BsonDocument("$ne", VideoValidationRecords.Kind)),
        new BsonDocument("$jsonSchema", new BsonDocument { { "bsonType", "object" }, { "properties", new BsonDocument {
            { "processingFence", Long(0) }, { "processingReceipt", Receipt() }, { "leaseOwner", Text(128) }, { "leaseToken", Token() }, { "leaseUntil", Type("date") },
            { "claimStartedAt", Type("date") }, { "claimDeadline", Type("date") }, { "attemptLimit", Int(1, 1000) },
            { "deadAt", Type("date") }, { "completedAt", Type("date") }, { "lastFailureCode", Text(100) } } } }) });
    private static BsonDocument With(BsonDocument fields, BsonDocument condition) => new("$and", new BsonArray { new BsonDocument("$jsonSchema", Object(fields)), new BsonDocument("$expr", condition) });
    private static BsonDocument Type(string kind) => new("bsonType", kind);
    private static BsonDocument Long(long minimum = 1) => new() { { "bsonType", "long" }, { "minimum", minimum }, { "maximum", long.MaxValue } };
    private static BsonDocument Int(int minimum, int maximum = int.MaxValue) => new() { { "bsonType", "int" }, { "minimum", minimum }, { "maximum", maximum } };
    private static BsonDocument Text(int maximum) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum }, { "pattern", @"\A[^\x00-\x1f\x7f-\x9f]+\z" } };
    private static BsonDocument Hash() { var value = Text(64); value["pattern"] = @"\A[0-9a-f]{64}\z"; return value; }
    private static BsonDocument Token() { var value = Text(32); value["pattern"] = @"\A[0-9a-f]{32}\z"; return value; }
    private static BsonDocument Choice(params string[] values) => new() { { "bsonType", "string" }, { "enum", new BsonArray(values) } };
    private static BsonDocument Nullable(BsonDocument rule) { rule["bsonType"] = new BsonArray { rule["bsonType"], "null" }; if (rule.TryGetValue("enum", out var values)) values.AsBsonArray.Add(BsonNull.Value); return rule; }
    private static BsonDocument Object(BsonDocument fields) => new() { { "bsonType", "object" }, { "required", new BsonArray(fields.Names) }, { "additionalProperties", false }, { "properties", fields } };
    private static BsonDocument Eq(BsonValue a, BsonValue b) => Compare("$eq", a, b);
    private static BsonDocument Compare(string operation, BsonValue a, BsonValue b) => new(operation, new BsonArray { a, b });
    private static BsonDocument And(params BsonDocument[] values) => new("$and", new BsonArray(values));
    private static BsonDocument Or(params BsonDocument[] values) => new("$or", new BsonArray(values));
    private static BsonDocument In(BsonValue value, params string[] values) => new("$in", new BsonArray { value, new BsonArray(values) });
}
