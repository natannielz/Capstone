using System.Text.RegularExpressions;
using MongoDB.Bson;

namespace UnitToko.Platform.Mongo.Migrations;

// Frozen v13 admission schema; runtime transitions live separately in Documents.
internal static class EvidenceSchemas
{
    internal static readonly string[] Collections = ["evidenceStorePolicies", "evidenceLineages", "documentVersions",
        "evidenceUploadReservations", "evidenceUploadAttempts", "auditEvents", "idempotencyRecords", "outboxEvents"];
    internal static readonly string[] LimitFields = ["maximumContentBytes", "reservationDurationMs", "writerDurationMs", "ioDurationMs", "bufferBytes"];
    internal const string ReserveOperation = "evidence.catalog-image.reserve";
    internal const string CompleteOperation = "evidence.catalog-image.complete";
    private static readonly BsonDateTime Earliest = new(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));
    private static readonly BsonDateTime Latest = new(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));
    private const string Printable = @"\A[^\s\x00-\x1f\x7f-\x9f](?:[^\x00-\x1f\x7f-\x9f]*[^\s\x00-\x1f\x7f-\x9f])?\z";

    internal static BsonDocument Validator(string collection)
    {
        var shape = Shape(collection);
        var required = new BsonDocument("$and", new BsonArray {
            new BsonDocument("$jsonSchema", shape), new BsonDocument("$expr", And(Structure(shape, "$$ROOT"), Rules(collection))) });
        var condition = Condition(collection);
        return condition is null ? required : new("$or", new BsonArray { new BsonDocument("$nor", new BsonArray { condition }), required });
    }
    internal static BsonDocument? Condition(string collection) => collection switch
    {
        "auditEvents" => new("kind", new BsonRegularExpression(@"\Aevidence\.")),
        "idempotencyRecords" => new("$or", new BsonArray {
            new BsonDocument("operation", new BsonRegularExpression(@"\Aevidence\.")),
            new BsonDocument("evidenceAdmission", new BsonDocument("$exists", true)) }),
        "outboxEvents" => new("$or", new BsonArray {
            new BsonDocument("kind", new BsonRegularExpression(@"\Aevidence\.")),
            new BsonDocument("sourceKey", new BsonRegularExpression(@"\Aevidence\.")) }),
        _ => null
    };
    private static bool Applies(string collection, BsonDocument row) => collection switch
    {
        "auditEvents" => Starts(row, "kind"),
        "idempotencyRecords" => Starts(row, "operation") || row.Contains("evidenceAdmission"),
        "outboxEvents" => Starts(row, "kind") || Starts(row, "sourceKey"),
        _ => true
    };
    private static bool Starts(BsonDocument row, string name) =>
        row.TryGetValue(name, out var value) && value.IsString && value.AsString.StartsWith("evidence.", StringComparison.Ordinal);

    private static BsonDocument Shape(string collection) => collection switch
    {
        "evidenceStorePolicies" => Row(new() {
            { "storeId", Hash() }, { "active", Type("bool") }, { "policyVersion", Long() },
            { "maximumContentBytes", Long(1, long.MaxValue / 3) }, { "maximumHeldReservations", Long() },
            { "maximumHeldBytes", Long() }, { "reservationDurationMs", Long(1, uint.MaxValue - 1L) },
            { "writerDurationMs", Long(1, uint.MaxValue - 1L) }, { "ioDurationMs", Long(1, uint.MaxValue - 1L) },
            { "bufferBytes", Long(1, 1048576) }, { "chargedReservations", Long(0) }, { "chargedBytes", Long(0) }, { "fence", Long(0) } }),
        "evidenceLineages" => Row(new() { { "target", Target() }, { "purpose", Choice("CatalogImage") },
            { "owner", Owner() }, { "uploader", Uploader() }, { "firstVersionRowId", Long() } }, false),
        "documentVersions" => Row(new() { { "lineageId", Long() }, { "contentVersion", One() }, { "reservationId", Long() },
            { "attemptId", Long() }, { "state", Choice("reserved", "quarantined") }, { "lifecycleRevision", Long() },
            { "fence", Long(0) }, { "contentRevision", Nullable(One()) }, { "length", Nullable(Long()) },
            { "sha256", Nullable(Hash()) }, { "storeId", Hash() }, { "generation", Hash() }, { "detectedMime", Type("null") },
            { "scanStatus", Choice("unavailable") }, { "decoderStatus", Choice("unavailable") }, { "quarantinedAt", Nullable(Type("date")) } }),
        "evidenceUploadReservations" => Row(new() { { "lineageId", Long() }, { "versionRowId", Long() }, { "contentVersion", One() },
            { "attemptId", Long() }, { "target", Target() }, { "purpose", Choice("CatalogImage") }, { "owner", Owner() },
            { "uploader", Uploader() }, { "policyId", Long() }, { "policyVersion", Long() }, { "admittedTargetRevision", Long() },
            { "admittedDefinitionId", Nullable(Long()) }, { "admittedDefinitionVersion", Nullable(Long()) }, { "admittedLimits", Limits() },
            { "operation", Choice(ReserveOperation) }, { "key", Text(200) }, { "bodyHash", Hash() }, { "expiresAt", Type("date") },
            { "chargedBytes", Long() }, { "chargedReservations", One() }, { "state", Choice("reserved", "quarantined") },
            { "reserveAuditId", Long() }, { "completionAuditId", Nullable(Long()) } }),
        "evidenceUploadAttempts" => Row(new() { { "reservationId", Long() }, { "ticketRevision", One() }, { "storeId", Hash() },
            { "stageToken", Hash() }, { "generation", Hash() }, { "state", Choice("fresh", "writing", "publish_pending", "quarantined", "interrupted") },
            { "claimEpoch", Long(0) }, { "claimToken", Nullable(Hash()) }, { "claimKind", Nullable(Choice("stage", "publication")) },
            { "claimUntil", Nullable(Type("date")) }, { "claimRequestId", Nullable(Hash()) }, { "writerUntil", Nullable(Type("date")) }, { "writerRequestId", Nullable(Hash()) }, { "contentRevision", Nullable(One()) },
            { "length", Nullable(Long()) }, { "sha256", Nullable(Hash()) }, { "lastSafeFailure", Nullable(Text(100, @"\A[a-z][a-z0-9_]{0,99}\z")) },
            { "completionAuditId", Nullable(Long()) } }),
        "auditEvents" => Row(new() { { "kind", Choice("evidence.reserve", "evidence.claim", "evidence.freeze", "evidence.interrupted", "evidence.quarantine", "evidence.status") },
            { "actorId", Long() }, { "actorGrantVersion", Long() }, { "reason", Text(1000) }, { "correlationId", Text(128) },
            { "bodyHash", Nullable(Hash()) }, { "evidence", Object(new() {
                { "reservationId", Long() }, { "attemptId", Long() }, { "versionRowId", Long() }, { "lineageId", Long() },
                { "target", Target() }, { "purpose", Choice("CatalogImage") }, { "owner", Owner() },
                { "from", Revisions() }, { "to", Revisions() } }) } }, false),
        "idempotencyRecords" => Object(new() {
            { "_id", Type("objectId") }, { "schemaVersion", Schema() }, { "actorId", Long() },
            { "operation", Choice(ReserveOperation, CompleteOperation) }, { "key", Text(200) }, { "bodyHash", Hash() },
            { "result", Object(new() {
                { "reservationId", Long() }, { "lineageId", Long() }, { "versionRowId", Long() }, { "attemptId", Long() },
                { "contentVersion", One() }, { "revision", Long() }, { "expiresAt", Type("date") },
                { "sha256", Nullable(Hash()) }, { "lifecycleRevision", Long() } }) },
            { "createdAt", Type("date") }, { "evidenceAdmission", Object(new() { { "schemaVersion", Schema() }, { "reservationId", Long() }, { "auditId", Long() } }) } }),
        "outboxEvents" => Object(new() {
            { "_id", Type("objectId") }, { "schemaVersion", Schema() }, { "sourceKey", Text(200) },
            { "kind", Choice("evidence.validation.requested") }, { "payload", Object(new() {
                { "lineageId", Long() }, { "versionRowId", Long() }, { "reservationId", Long() }, { "attemptId", Long() },
                { "contentVersion", One() }, { "contentRevision", One() }, { "sha256", Hash() }, { "lifecycleRevision", Long() } }) },
            { "status", Choice("pending", "leased", "retry", "completed", "dead") }, { "attempts", new BsonDocument { { "bsonType", "int" }, { "minimum", 0 } } },
            { "dueAt", Type("date") }, { "createdAt", Type("date") } }, false),
        _ => throw new ArgumentOutOfRangeException(nameof(collection))
    };
    private static BsonDocument Rules(string collection) => collection switch
    {
        "evidenceStorePolicies" => And(Compare("$lte", "$writerDurationMs", "$reservationDurationMs"),
            Compare("$lte", "$chargedBytes", "$maximumHeldBytes"), Compare("$lte", "$chargedReservations", "$maximumHeldReservations")),
        "evidenceLineages" => OwnerRule("$owner"),
        "documentVersions" => And(TupleRule(), Or(And(Eq("$state", "reserved"), Null("$quarantinedAt")),
            And(Eq("$state", "quarantined"), Eq(BsonType("$contentRevision"), "long"), Eq(BsonType("$quarantinedAt"), "date")))),
        "evidenceUploadReservations" => And(OwnerRule("$owner"), LimitsRule("$admittedLimits"),
            Eq("$chargedBytes", new BsonDocument("$multiply", new BsonArray { 3L, "$admittedLimits.maximumContentBytes" })),
            Compare("$gt", "$expiresAt", "$createdAt"),
            Or(And(Eq("$target.kind", "CatalogFamily"), Null("$admittedDefinitionId"), Null("$admittedDefinitionVersion")),
                And(Eq("$target.kind", "CatalogSku"), Eq(BsonType("$admittedDefinitionId"), "long"), Eq(BsonType("$admittedDefinitionVersion"), "long"))),
            Or(And(Eq("$state", "reserved"), Null("$completionAuditId")), And(Eq("$state", "quarantined"), Eq(BsonType("$completionAuditId"), "long")))),
        "evidenceUploadAttempts" => And(TupleRule(), Or(And(Eq("$state", "fresh"), Null("$writerUntil"), Null("$writerRequestId")), And(new BsonDocument("$ne", new BsonArray { "$state", "fresh" }), Eq(BsonType("$writerUntil"), "date"), Eq(BsonType("$writerRequestId"), "string"))), Or(
            And(Eq("$state", "fresh"), Eq("$claimEpoch", 0L), EmptyClaim(), Null("$contentRevision"), Null("$lastSafeFailure"), Null("$completionAuditId")),
            And(Eq("$state", "writing"), Compare("$gt", "$claimEpoch", 0L), FullClaim("stage"), Null("$contentRevision"), Null("$lastSafeFailure"), Null("$completionAuditId")),
            And(Eq("$state", "publish_pending"), Compare("$gt", "$claimEpoch", 0L), Or(EmptyClaim(), FullClaim("publication")), Eq(BsonType("$contentRevision"), "long"), Null("$lastSafeFailure"), Null("$completionAuditId")),
            And(Eq("$state", "interrupted"), Compare("$gt", "$claimEpoch", 0L), EmptyClaim(), Null("$contentRevision"), Eq(BsonType("$lastSafeFailure"), "string"), Null("$completionAuditId")),
            And(Eq("$state", "quarantined"), Compare("$gt", "$claimEpoch", 0L), EmptyClaim(), Eq(BsonType("$contentRevision"), "long"), Null("$lastSafeFailure"), Eq(BsonType("$completionAuditId"), "long")))),
        "auditEvents" => OwnerRule("$evidence.owner"),
        "idempotencyRecords" => And(Eq("$evidenceAdmission.reservationId", "$result.reservationId"),
            Or(And(Eq("$operation", ReserveOperation), Null("$result.sha256")), And(Eq("$operation", CompleteOperation), Eq(BsonType("$result.sha256"), "string")))),
        "outboxEvents" => Eq("$sourceKey", new BsonDocument("$concat", new BsonArray { "evidence.quarantined:", String("$payload.versionRowId"), ":", String("$payload.contentRevision") })),
        _ => And()
    };
    private static BsonDocument Limits() => Object(new() { { "maximumContentBytes", Long(1, long.MaxValue / 3) },
        { "reservationDurationMs", Long(1, uint.MaxValue - 1L) }, { "writerDurationMs", Long(1, uint.MaxValue - 1L) },
        { "ioDurationMs", Long(1, uint.MaxValue - 1L) }, { "bufferBytes", Long(1, 1048576) } });
    private static BsonDocument LimitsRule(string path) => Compare("$lte", path + ".writerDurationMs", path + ".reservationDurationMs");
    private static BsonDocument TupleRule() => Or(And(Null("$contentRevision"), Null("$length"), Null("$sha256")),
        And(Eq(BsonType("$contentRevision"), "long"), Eq(BsonType("$length"), "long"), Eq(BsonType("$sha256"), "string")));
    private static BsonDocument EmptyClaim() => And(Null("$claimToken"), Null("$claimKind"), Null("$claimUntil"), Null("$claimRequestId"));
    private static BsonDocument FullClaim(string kind) => And(Eq("$claimKind", kind), Eq(BsonType("$claimToken"), "string"),
        Eq(BsonType("$claimUntil"), "date"), Eq(BsonType("$claimRequestId"), "string"));
    private static BsonDocument OwnerRule(string path) => Or(
        And(Eq(path + ".kind", "fulfillmentShop"), Null(path + ".warehouseId")),
        And(Eq(path + ".kind", "fulfillmentWarehouse"), Eq(BsonType(path + ".warehouseId"), "long")));

    internal static bool IsValid(string collection, BsonDocument row)
    {
        if (!Applies(collection, row)) return true;
        if (!Conforms(row, Shape(collection))) return false;
        if (row.Contains("updatedAt") && row["updatedAt"].AsBsonDateTime.MillisecondsSinceEpoch < row["createdAt"].AsBsonDateTime.MillisecondsSinceEpoch) return false;
        bool OwnerValid(BsonDocument o) => o["kind"] == "fulfillmentShop" ? o["warehouseId"].IsBsonNull : o["warehouseId"].IsInt64;
        bool LimitsValid(BsonDocument l) => l["writerDurationMs"].AsInt64 <= l["reservationDurationMs"].AsInt64;
        if (collection == "evidenceStorePolicies") return LimitsValid(row) && row["chargedBytes"].AsInt64 <= row["maximumHeldBytes"].AsInt64 && row["chargedReservations"].AsInt64 <= row["maximumHeldReservations"].AsInt64;
        if (collection == "evidenceLineages") return OwnerValid(row["owner"].AsBsonDocument);
        if (collection == "auditEvents") return OwnerValid(row["evidence"]["owner"].AsBsonDocument);
        if (collection == "idempotencyRecords") return row["evidenceAdmission"]["reservationId"] == row["result"]["reservationId"] && (row["operation"] == ReserveOperation ? row["result"]["sha256"].IsBsonNull : row["result"]["sha256"].IsString);
        if (collection == "outboxEvents") return row["sourceKey"] == $"evidence.quarantined:{row["payload"]["versionRowId"].AsInt64}:{row["payload"]["contentRevision"].AsInt64}";
        if (collection == "evidenceUploadReservations")
        {
            var limits = row["admittedLimits"].AsBsonDocument;
            return OwnerValid(row["owner"].AsBsonDocument) && LimitsValid(limits) && row["chargedBytes"].AsInt64 == checked(3 * limits["maximumContentBytes"].AsInt64) &&
                row["expiresAt"].AsBsonDateTime.MillisecondsSinceEpoch > row["createdAt"].AsBsonDateTime.MillisecondsSinceEpoch &&
                (row["target"]["kind"] == "CatalogFamily" ? row["admittedDefinitionId"].IsBsonNull && row["admittedDefinitionVersion"].IsBsonNull : row["admittedDefinitionId"].IsInt64 && row["admittedDefinitionVersion"].IsInt64) &&
                (row["state"] == "reserved" ? row["completionAuditId"].IsBsonNull : row["completionAuditId"].IsInt64);
        }
        var frozen = row["contentRevision"].IsInt64;
        if (frozen != row["length"].IsInt64 || frozen != row["sha256"].IsString) return false;
        if (collection == "documentVersions") return row["state"] == "reserved" ? row["quarantinedAt"].IsBsonNull : frozen && row["quarantinedAt"].IsBsonDateTime;
        var empty = new[] { "claimToken", "claimKind", "claimUntil", "claimRequestId" }.All(f => row[f].IsBsonNull);
        bool Claim(string kind) => row["claimToken"].IsString && row["claimKind"] == kind && row["claimUntil"].IsBsonDateTime && row["claimRequestId"].IsString;
        var epoch = row["claimEpoch"].AsInt64;
        if (row["state"] == "fresh" ? !row["writerUntil"].IsBsonNull || !row["writerRequestId"].IsBsonNull : !row["writerUntil"].IsBsonDateTime || !row["writerRequestId"].IsString) return false;
        return row["state"].AsString switch
        {
            "fresh" => epoch == 0 && empty && !frozen && row["lastSafeFailure"].IsBsonNull && row["completionAuditId"].IsBsonNull,
            "writing" => epoch > 0 && Claim("stage") && !frozen && row["lastSafeFailure"].IsBsonNull && row["completionAuditId"].IsBsonNull,
            "publish_pending" => epoch > 0 && (empty || Claim("publication")) && frozen && row["lastSafeFailure"].IsBsonNull && row["completionAuditId"].IsBsonNull,
            "interrupted" => epoch > 0 && empty && !frozen && row["lastSafeFailure"].IsString && row["completionAuditId"].IsBsonNull,
            "quarantined" => epoch > 0 && empty && frozen && row["lastSafeFailure"].IsBsonNull && row["completionAuditId"].IsInt64,
            _ => false
        };
    }

    private static BsonDocument Row(BsonDocument fields, bool mutable = true)
    {
        fields.InsertAt(0, new("_id", Long())); fields.InsertAt(1, new("schemaVersion", Schema()));
        fields.Add("createdAt", Type("date"));
        if (mutable) { fields.Add("revision", Long()); fields.Add("updatedAt", Type("date")); }
        return Object(fields);
    }
    private static BsonDocument Target() => Object(new() { { "kind", Choice("CatalogFamily", "CatalogSku") }, { "id", Long() } });
    private static BsonDocument Uploader() => Object(new() { { "userId", Long() }, { "issuer", Text(512) }, { "subject", Text(512) } });
    private static BsonDocument Owner() => Object(new() { { "kind", Choice("fulfillmentShop", "fulfillmentWarehouse") },
        { "organizationId", Long() }, { "shopId", Long() }, { "channelId", Long() }, { "warehouseId", Nullable(Long()) } });
    private static BsonDocument Revisions() => Object(new() { { "reservation", Long(0) }, { "attempt", Long(0) }, { "lifecycle", Long(0) } });
    private static BsonDocument Object(BsonDocument fields, bool strict = true) => new() {
        { "bsonType", "object" }, { "required", new BsonArray(fields.Names) }, { "additionalProperties", !strict }, { "properties", fields } };
    private static BsonDocument Type(string value) => new("bsonType", value);
    private static BsonDocument Long(long minimum = 1, long maximum = long.MaxValue) => new() { { "bsonType", "long" }, { "minimum", minimum }, { "maximum", maximum } };
    private static BsonDocument One() { var value = Long(); value["enum"] = new BsonArray { 1L }; return value; }
    private static BsonDocument Schema() => new() { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } };
    private static BsonDocument Text(int maximum, string pattern = Printable) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum }, { "pattern", pattern } };
    private static BsonDocument Hash() => Text(64, @"\A[0-9a-f]{64}\z");
    private static BsonDocument Choice(params string[] values) => new() { { "bsonType", "string" }, { "enum", new BsonArray(values) } };
    private static BsonDocument Nullable(BsonDocument rule) { rule["bsonType"] = new BsonArray { rule["bsonType"], "null" }; if (rule.TryGetValue("enum", out var choices)) choices.AsBsonArray.Add(BsonNull.Value); return rule; }
    private static bool Conforms(BsonValue value, BsonDocument rule)
    {
        var type = rule["bsonType"];
        if (type.IsBsonArray) { if (value.IsBsonNull) return true; var copy = rule.DeepClone().AsBsonDocument; copy["bsonType"] = type[0]; return Conforms(value, copy); }
        if (rule.TryGetValue("enum", out var choices) && !choices.AsBsonArray.Contains(value)) return false;
        switch (type.AsString)
        {
            case "object":
                if (!value.IsBsonDocument) return false;
                var row = value.AsBsonDocument; var props = rule["properties"].AsBsonDocument;
                return row.Names.Distinct(StringComparer.Ordinal).Count() == row.ElementCount &&
                    rule["required"].AsBsonArray.All(name => row.Contains(name.AsString)) &&
                    row.All(field => props.Contains(field.Name) ? Conforms(field.Value, props[field.Name].AsBsonDocument) : rule["additionalProperties"].AsBoolean);
            case "long": return value.IsInt64 && value.AsInt64 >= rule["minimum"].AsInt64 && value.AsInt64 <= rule["maximum"].AsInt64;
            case "int": return value.IsInt32 && (!rule.Contains("minimum") || value.AsInt32 >= rule["minimum"].AsInt32);
            case "string": return value.IsString && (rule.Contains("enum") || value.AsString.Length <= rule["maxLength"].AsInt32 &&
                CatalogSchemas.CanonicalText(value.AsString, rule["maxLength"].AsInt32) && Regex.IsMatch(value.AsString, rule["pattern"].AsString, RegexOptions.CultureInvariant));
            case "date": return CatalogSchemas.ValidDate(value);
            case "bool": return value.IsBoolean;
            case "null": return value.IsBsonNull;
            case "objectId": return value.IsObjectId;
            default: return false;
        }
    }
    private static BsonDocument Structure(BsonDocument shape, string path)
    {
        var type = shape["bsonType"];
        if (type.IsBsonArray) { var copy = shape.DeepClone().AsBsonDocument; copy["bsonType"] = type[0]; return Or(Null(path), Structure(copy, path)); }
        if (type == "date") return And(Compare("$gte", path, Earliest), Compare("$lte", path, Latest));
        if (type != "object") return And();
        var pairs = new BsonDocument("$objectToArray", new BsonDocument("$cond", new BsonArray { Eq(BsonType(path), "object"), path, new BsonDocument() }));
        var names = new BsonDocument("$map", new BsonDocument { { "input", pairs }, { "as", "entry" }, { "in", "$$entry.k" } });
        var conditions = new List<BsonValue> { Eq(new BsonDocument("$size", names), new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { names, new BsonArray() }))) };
        foreach (var field in shape["properties"].AsBsonDocument) conditions.Add(Structure(field.Value.AsBsonDocument, path + "." + field.Name));
        if (shape["properties"].AsBsonDocument.Contains("updatedAt")) conditions.Add(Compare("$gte", path + ".updatedAt", path + ".createdAt"));
        return And(conditions.ToArray());
    }
    private static BsonDocument BsonType(string path) => new("$type", path);
    private static BsonDocument String(string path) => new("$convert", new BsonDocument { { "input", path }, { "to", "string" }, { "onError", "" }, { "onNull", "" } });
    private static BsonDocument Eq(BsonValue left, BsonValue right) => Compare("$eq", left, right);
    private static BsonDocument Null(string path) => Eq(BsonType(path), "null");
    private static BsonDocument Compare(string op, BsonValue left, BsonValue right) => new(op, new BsonArray { left, right });
    private static BsonDocument And(params BsonValue[] values) => new("$and", new BsonArray(values));
    private static BsonDocument Or(params BsonValue[] values) => new("$or", new BsonArray(values));
}
