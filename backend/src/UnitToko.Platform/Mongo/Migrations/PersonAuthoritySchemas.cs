using MongoDB.Bson;

namespace UnitToko.Platform.Mongo.Migrations;

// Frozen v8 producer formats. Runtime readers also validate these shapes before trusting BSON.
internal static class PersonAuthoritySchemas
{
    internal static readonly string[] Collections = ["personEvidenceSources", "personRecords", "personAccountBindings", "personEquivalenceGroups", "personEvidenceVersions"];
    private static readonly BsonDateTime Earliest = new(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));
    private static readonly BsonDateTime Latest = new(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));

    private static BsonDocument Properties(string collection)
    {
        var fields = new BsonDocument { { "_id", Long() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        void Ids(params string[] names) { foreach (var name in names) fields.Add(name, Long()); }
        void Provenance()
        {
            Ids("requestedBy", "approvedBy", "approvalId", "approvalVersion"); fields.Add("reviewedAt", Date());
        }
        void Lifetime() { fields.Add("effectiveAt", Date()); fields.Add("expiresAt", new BsonDocument("bsonType", new BsonArray { "date", "null" })); }
        if (collection != "personEvidenceVersions") { Ids("version"); fields.Add("fence", Long(0)); }
        switch (collection)
        {
            case "personEvidenceSources":
                fields.Add("key", Text(100)); fields.Add("status", Choice("approved", "suspended", "revoked"));
                fields.Add("adapterId", Text(128)); fields.Add("policyReference", Text(256)); Provenance(); Lifetime(); break;
            case "personRecords":
                Ids("sourceId", "currentEvidenceId", "currentEquivalenceId"); fields.Add("sourceSubjectKey", Text(256));
                fields.Add("status", Choice("active", "suspended", "disputed")); break;
            case "personAccountBindings":
                Ids("personId", "evidenceId"); fields.Add("verification", Choice("verified", "disputed"));
                fields.Add("status", Choice("active", "suspended", "revoked")); Provenance(); Lifetime(); fields.Add("reason", Text(2000)); break;
            case "personEquivalenceGroups":
                Ids("canonicalPersonId"); fields.Add("memberPersonIds", Members()); fields.Add("memberUserIds", Members());
                fields.Add("status", Choice("active", "suspended", "disputed", "superseded")); Provenance(); break;
            case "personEvidenceVersions":
                Ids("personId", "evidenceVersion", "sourceId", "sourceVersion"); fields.Add("sourceSubjectKey", Text(256));
                fields.Add("sourceReference", Text(512)); fields.Add("sourceDigest", new BsonDocument { { "bsonType", "string" }, { "minLength", 64 }, { "maxLength", 64 }, { "pattern", "^[0-9a-f]{64}\\z" } });
                fields.Add("adapterId", Text(128)); Provenance(); fields.Add("createdAt", Date()); fields.Add("reason", Text(2000)); Lifetime(); break;
            default: throw new ArgumentOutOfRangeException(nameof(collection));
        }
        return fields;
    }

    internal static BsonDocument Validator(string collection)
    {
        var properties = Properties(collection);
        var comparisons = new BsonArray();
        foreach (var field in properties.Where(field => field.Value.AsBsonDocument["bsonType"] == "date"))
        {
            comparisons.Add(Compare("$gte", "$" + field.Name, Earliest));
            comparisons.Add(Compare("$lte", "$" + field.Name, Latest));
        }
        if (properties.Contains("requestedBy")) comparisons.Add(Compare("$ne", "$requestedBy", "$approvedBy"));
        if (collection == "personAccountBindings") comparisons.Add(Compare("$ne", "$_id", "$approvedBy"));
        if (properties.Contains("effectiveAt"))
        {

            comparisons.Add(new BsonDocument("$or", new BsonArray
            {
                Compare("$in", new BsonDocument("$type", "$expiresAt"), new BsonArray { "missing", "null" }),
                new BsonDocument("$and", new BsonArray { Compare("$gt", "$expiresAt", "$effectiveAt"), Compare("$lte", "$expiresAt", Latest) })
            }));
        }
        // createdAt is the creation time of approved immutable evidence, not the source event.
        if (properties.Contains("createdAt")) comparisons.Add(Compare("$lte", "$reviewedAt", "$createdAt"));
        if (collection == "personEquivalenceGroups") comparisons.Add(Compare("$in", "$canonicalPersonId",
            new BsonDocument("$cond", new BsonArray { new BsonDocument("$isArray", "$memberPersonIds"), "$memberPersonIds", new BsonArray() })));
        var schema = new BsonDocument { { "bsonType", "object" }, { "required", new BsonArray(properties.Names.Where(name => name != "expiresAt")) },
            { "additionalProperties", false }, { "properties", properties } };
        return comparisons.Count == 0 ? new BsonDocument("$jsonSchema", schema) : new BsonDocument("$and", new BsonArray
            { new BsonDocument("$jsonSchema", schema), new BsonDocument("$expr", new BsonDocument("$and", comparisons)) });
    }

    internal static bool IsValid(string collection, BsonDocument row)
    {
        if (!Collections.Contains(collection) || row.Names.Distinct(StringComparer.Ordinal).Count() != row.ElementCount) return false;
        var fields = Properties(collection);
        if (row.Names.Any(name => !fields.Contains(name)) || fields.Names.Any(name => name != "expiresAt" && !row.Contains(name))) return false;
        foreach (var field in row)
        {
            var rule = fields[field.Name].AsBsonDocument;
            var value = field.Value;
            if (field.Name == "expiresAt") { if (!value.IsBsonNull && !ValidDate(value)) return false; continue; }
            switch (rule["bsonType"].AsString)
            {
                case "long": if (!value.IsInt64 || value.AsInt64 < rule["minimum"].AsInt64) return false; break;
                case "int": if (!value.IsInt32 || value.AsInt32 != 1) return false; break;
                case "date": if (!ValidDate(value)) return false; break;
                case "string":
                    if (!value.IsString) return false;
                    var text = value.AsString;
                    if (rule.TryGetValue("enum", out var choices)) { if (!choices.AsBsonArray.Contains(value)) return false; }
                    else if (text.Length < rule["minLength"].AsInt32 || text.Length > rule["maxLength"].AsInt32 || text.Any(char.IsControl)) return false;
                    if (field.Name == "sourceDigest" && text.Any(character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f'))) return false;
                    break;
                case "array":
                    if (!value.IsBsonArray || value.AsBsonArray.Count is < 1 or > 256) return false;
                    long previous = 0;
                    foreach (var member in value.AsBsonArray) { if (!member.IsInt64 || member.AsInt64 <= previous) return false; previous = member.AsInt64; }
                    break;
                default: return false;
            }
        }
        if (row.Contains("requestedBy") && row["requestedBy"] == row["approvedBy"]) return false;
        if (collection == "personAccountBindings" && row["_id"] == row["approvedBy"]) return false;
        if (row.Contains("effectiveAt") && (
            row.TryGetValue("expiresAt", out var expiry) && !expiry.IsBsonNull && expiry.CompareTo(row["effectiveAt"]) <= 0)) return false;
        if (row.Contains("createdAt") && row["reviewedAt"].CompareTo(row["createdAt"]) > 0) return false;
        return collection != "personEquivalenceGroups" || row["memberPersonIds"].AsBsonArray.Contains(row["canonicalPersonId"]);
    }

    private static bool ValidDate(BsonValue value) => value.IsBsonDateTime && value.AsBsonDateTime.MillisecondsSinceEpoch >= Earliest.MillisecondsSinceEpoch && value.AsBsonDateTime.MillisecondsSinceEpoch <= Latest.MillisecondsSinceEpoch;
    private static BsonDocument Compare(string operation, BsonValue left, BsonValue right) => new(operation, new BsonArray { left, right });
    private static BsonDocument Long(long minimum = 1) => new() { { "bsonType", "long" }, { "minimum", minimum } };
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Choice(params string[] choices) => new() { { "bsonType", "string" }, { "enum", new BsonArray(choices) } };
    private static BsonDocument Text(int maximum) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum }, { "pattern", "^[^\\x00-\\x1f\\x7f-\\x9f]+\\z" } };
    private static BsonDocument Members() => new() { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 256 }, { "uniqueItems", true }, { "items", Long() } };
}
