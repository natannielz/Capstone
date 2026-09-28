using MongoDB.Bson;
using System.Text;
using System.Text.RegularExpressions;

namespace UnitToko.Platform.Mongo.Migrations;

// Frozen v10 private, session-bound previews. Retained snapshots grant no authority.
internal static class PersonReviewPreviewSchemas
{
    internal static readonly string[] Collections = ["personReviewPreviews"];
    private static readonly BsonDateTime Earliest = new(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));
    private static readonly BsonDateTime Latest = new(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));
    private const string Printable = "^(?=.*[^\\p{Z}\\x09-\\x0d\\x85])[^\\x00-\\x1f\\x7f-\\x9f]+\\z";
    private const string HashPattern = "^[0-9a-f]{64}\\z";
    private const string ReferencePattern = "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\\z";
    private static readonly BsonDocument TargetShape = PersonGovernanceSchemas.Validator("personReviewProposals")
        ["$and"][0]["$jsonSchema"]["properties"]["target"].DeepClone().AsBsonDocument;
    private static readonly string[] ApprovalFields = ["proposalId", "proposalVersion", "proposalDigest", "approvalRequestedBy"];

    private static BsonDocument Shape() => Object(new()
    {
        { "_id", Long() }, { "schemaVersion", SchemaVersion() },
        { "reference", new BsonDocument { { "bsonType", "string" }, { "minLength", 36 }, { "maxLength", 36 }, { "pattern", ReferencePattern } } },
        { "kind", Choice("propose", "approve") }, { "status", Choice("ready", "consumed") }, { "version", Long() },
        { "actorId", Long() }, { "grantVersion", Long() }, { "securityVersion", Long() },
        { "session", Object(new() { { "id", Digest() }, { "version", Long() }, { "securityVersion", Long() } }) },
        { "createdAt", Date() }, { "expiresAt", Date() }, { "selectionDigest", Digest() },
        { "policy", Object(new() { { "id", Long() }, { "version", Long() }, { "digest", Digest() } }) },
        { "target", TargetShape.DeepClone().AsBsonDocument },
        { "profiles", Array(Object(new()
            {
                { "userId", Long() }, { "version", Long() }, { "displayName", NullableText(200) },
                { "verifiedEmail", Text(320) }, { "status", Choice("pending", "active", "suspended", "revoked") }
            }), 1, 258) },
        { "proposalId", Long() }, { "proposalVersion", Long() }, { "proposalDigest", Digest() }, { "approvalRequestedBy", Long() },
        { "consumption", Object(new()
            {
                { "operation", Choice("person.review.propose", "person.review.approve") }, { "keyHash", Digest() },
                { "resultProposalId", Long() }, { "resultHistoryId", Long() }, { "consumedAt", Date() }
            }) }
    }, [.. ApprovalFields, "consumption"]);

    internal static BsonDocument Validator(string collection)
    {
        if (!Collections.Contains(collection, StringComparer.Ordinal)) throw new ArgumentOutOfRangeException(nameof(collection));
        var shape = Shape(); var sequence = 0;
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", shape),
            new BsonDocument("$expr", And(StructuralExpression(shape, "$$ROOT", ref sequence), PreviewExpression())) });
    }

    private static BsonValue PreviewExpression()
    {
        const string p = "$$ROOT";
        var approve = Compare("$eq", p + ".kind", "approve");
        var expectedUsers = new BsonDocument("$cond", new BsonArray
        {
            approve, new BsonDocument("$sortArray", new BsonDocument
            {
                { "input", new BsonDocument("$setUnion", new BsonArray { SafeArray(p + ".target.userIds"), new BsonArray { p + ".approvalRequestedBy" } }) },
                { "sortBy", 1 }
            }), SafeArray(p + ".target.userIds")
        });
        var absentApproval = new BsonDocument("$and", new BsonArray(ApprovalFields.Select(name => Compare("$eq", Type(p + "." + name), "missing"))));
        var requiredApproval = And(Compare("$eq", Type(p + ".proposalId"), "long"), Compare("$eq", p + ".proposalVersion", 1L),
            Compare("$eq", Type(p + ".proposalDigest"), "string"), Compare("$eq", Type(p + ".approvalRequestedBy"), "long"));
        var consumption = p + ".consumption";
        return And(Compare("$gt", p + ".expiresAt", p + ".createdAt"),
            Compare("$eq", p + ".session.securityVersion", p + ".securityVersion"),
            SortedMembers(p + ".target.personIds"), SortedMembers(p + ".target.userIds"),
            Compare("$in", p + ".target.canonicalPersonId", SafeArray(p + ".target.personIds")), HeadExpression(p + ".target.heads"),
            Compare("$eq", Map(SafeArray(p + ".profiles"), "profile", "$$profile.userId"), expectedUsers),
            new BsonDocument("$cond", new BsonArray { approve, requiredApproval, absentApproval }),
            new BsonDocument("$cond", new BsonArray
            {
                Compare("$eq", p + ".status", "ready"),
                And(Compare("$eq", p + ".version", 1L), Compare("$eq", Type(consumption), "missing")),
                And(Compare("$eq", p + ".version", 2L), Compare("$eq", Type(consumption), "object"),
                    Compare("$eq", consumption + ".operation", new BsonDocument("$cond", new BsonArray { approve, "person.review.approve", "person.review.propose" })),
                    Compare("$gte", consumption + ".consumedAt", p + ".createdAt"), Compare("$lt", consumption + ".consumedAt", p + ".expiresAt"),
                    Or(Compare("$ne", p + ".kind", "approve"), Compare("$eq", consumption + ".resultProposalId", p + ".proposalId")))
            }));
    }

    internal static bool IsValid(string collection, BsonDocument row)
    {
        if (!Collections.Contains(collection, StringComparer.Ordinal) || !Conforms(row, Shape())) return false;
        var target = row["target"].AsBsonDocument;
        if (row["expiresAt"].CompareTo(row["createdAt"]) <= 0 || row["session"]["securityVersion"] != row["securityVersion"] ||
            !SortedIds(target["personIds"].AsBsonArray) || !SortedIds(target["userIds"].AsBsonArray) ||
            !target["personIds"].AsBsonArray.Contains(target["canonicalPersonId"]) || !SortedHeads(target["heads"].AsBsonArray)) return false;
        var approve = row["kind"] == "approve";
        if (approve ? ApprovalFields.Any(name => !row.Contains(name)) || row["proposalVersion"] != 1L : ApprovalFields.Any(row.Contains)) return false;
        var expected = target["userIds"].AsBsonArray.Select(id => id.AsInt64);
        if (approve) expected = expected.Append(row["approvalRequestedBy"].AsInt64).Distinct().Order();
        if (!row["profiles"].AsBsonArray.Select(profile => profile["userId"].AsInt64).SequenceEqual(expected)) return false;
        if (row["status"] == "ready") return row["version"] == 1L && !row.Contains("consumption");
        if (row["version"] != 2L || !row.TryGetValue("consumption", out var consumption)) return false;
        return consumption["operation"] == (approve ? "person.review.approve" : "person.review.propose") &&
            consumption["consumedAt"].CompareTo(row["createdAt"]) >= 0 && consumption["consumedAt"].CompareTo(row["expiresAt"]) < 0 &&
            (!approve || consumption["resultProposalId"] == row["proposalId"]);
    }
    private static BsonDocument NullableText(int maximum)
    {
        var rule = Text(maximum); rule["bsonType"] = new BsonArray { "string", "null" }; return rule;
    }

    private static BsonValue StructuralExpression(BsonDocument shape, string path, ref int sequence)
    {
        var type = shape["bsonType"];
        if (type.IsBsonArray)
            {
            var actual = shape.DeepClone().AsBsonDocument; actual["bsonType"] = type.AsBsonArray.First(value => value != "null");
            return Or(Compare("$eq", Type(path), "null"), StructuralExpression(actual, path, ref sequence));
        }
        if (type == "date") return DateBounds(path);
        if (type == "array")
        {
            var item = shape["items"].AsBsonDocument;
            if (item["bsonType"] is BsonString text && text.Value is not ("object" or "array" or "date")) return BsonBoolean.True;
            var variable = "item" + sequence++;
            return All(Map(SafeArray(path), variable, StructuralExpression(item, "$$" + variable, ref sequence)));
        }
        if (type != "object") return BsonBoolean.True;
        var pairs = new BsonDocument("$objectToArray", new BsonDocument("$cond",
            new BsonArray { Compare("$eq", Type(path), "object"), path, new BsonDocument() }));
        var keys = Map(pairs, "field", "$$field.k");
        var conditions = new BsonArray { Compare("$eq", Size(keys), Size(new BsonDocument("$setUnion", new BsonArray { keys, new BsonArray() }))) };
        var required = shape["required"].AsBsonArray;
        foreach (var field in shape["properties"].AsBsonDocument)
        {
            var check = StructuralExpression(field.Value.AsBsonDocument, path + "." + field.Name, ref sequence);
            if (check == BsonBoolean.True) continue;
            conditions.Add(required.Contains(field.Name) ? check : Or(Compare("$eq", Type(path + "." + field.Name), "missing"), check));
        }
        return new BsonDocument("$and", conditions);
    }
    private static BsonValue HeadExpression(string path)
    {
        var items = SafeArray(path);
        var keys = Map(items, "head", new BsonDocument { { "collection", "$$head.collection" }, { "id", "$$head.id" } });
        return And(Compare("$eq", items, new BsonDocument("$sortArray", new BsonDocument
            { { "input", items }, { "sortBy", new BsonDocument { { "collection", 1 }, { "id", 1 } } } })),
            Compare("$eq", Size(items), Size(new BsonDocument("$setUnion", new BsonArray { keys, new BsonArray() }))));
    }

    private static bool Conforms(BsonValue value, BsonDocument rule)
    {
        var type = rule["bsonType"];
        if (type.IsBsonArray)
        {
            if (value.IsBsonNull) return type.AsBsonArray.Contains("null");
            var actual = rule.DeepClone().AsBsonDocument; actual["bsonType"] = type.AsBsonArray.First(item => item != "null");
            return Conforms(value, actual);
        }
        switch (type.AsString)
        {
            case "object":
                if (!value.IsBsonDocument) return false;
                var document = value.AsBsonDocument; var properties = rule["properties"].AsBsonDocument;
                if (document.Names.Distinct(StringComparer.Ordinal).Count() != document.ElementCount ||
                    document.Names.Any(name => !properties.Contains(name)) || rule["required"].AsBsonArray.Any(name => !document.Contains(name.AsString))) return false;
                return document.All(field => Conforms(field.Value, properties[field.Name].AsBsonDocument));
            case "array":
                if (!value.IsBsonArray) return false;
                var array = value.AsBsonArray;
                return array.Count >= rule["minItems"].AsInt32 && array.Count <= rule["maxItems"].AsInt32 &&
                    array.All(item => Conforms(item, rule["items"].AsBsonDocument)) &&
                    array.Distinct().Count() == array.Count;
            case "long": return value.IsInt64 && value.AsInt64 >= rule["minimum"].AsInt64;
            case "int": return value.IsInt32 && value.AsInt32 == 1;
            case "date": return ValidDate(value);
            case "string":
                if (!value.IsString) return false;
                if (rule.TryGetValue("enum", out var choices)) return choices.AsBsonArray.Contains(value);
                var text = value.AsString;
                return text.EnumerateRunes().Count() >= rule["minLength"].AsInt32 && text.EnumerateRunes().Count() <= rule["maxLength"].AsInt32 &&
                    Regex.IsMatch(text, rule["pattern"].AsString, RegexOptions.CultureInvariant);
            default: return false;
        }
    }
    private static bool SortedIds(BsonArray values)
    {
        long previous = 0;
        foreach (var value in values) { if (!value.IsInt64 || value.AsInt64 <= previous) return false; previous = value.AsInt64; }
        return true;
    }
    private static bool SortedHeads(BsonArray values)
    {
        string? previousCollection = null; long previousId = 0;
        foreach (var value in values)
        {
            var collection = value["collection"].AsString; var id = value["id"].AsInt64;
            if (previousCollection is not null)
            {
                var order = StringComparer.Ordinal.Compare(previousCollection, collection);
                if (order > 0 || order == 0 && previousId >= id) return false;
            }
            previousCollection = collection; previousId = id;
        }
        return true;
    }
    private static bool ValidDate(BsonValue value) => value.IsBsonDateTime &&
        value.AsBsonDateTime.MillisecondsSinceEpoch >= Earliest.MillisecondsSinceEpoch && value.AsBsonDateTime.MillisecondsSinceEpoch <= Latest.MillisecondsSinceEpoch;
    private static BsonValue DateBounds(string path) => And(Compare("$gte", path, Earliest), Compare("$lte", path, Latest));
    private static BsonValue SortedMembers(string path) => Compare("$eq", SafeArray(path), new BsonDocument("$sortArray", new BsonDocument { { "input", SafeArray(path) }, { "sortBy", 1 } }));
    private static BsonValue SafeArray(string path) => new BsonDocument("$cond", new BsonArray { new BsonDocument("$isArray", path), path, new BsonArray() });
    private static BsonDocument Type(string path) => new("$type", path);
    private static BsonDocument Size(BsonValue value) => new("$size", value);
    private static BsonDocument Map(BsonValue input, string variable, BsonValue expression) => new("$map", new BsonDocument { { "input", input }, { "as", variable }, { "in", expression } });
    private static BsonDocument All(BsonValue values) => new("$allElementsTrue", new BsonArray { values });
    private static BsonDocument Compare(string operation, BsonValue left, BsonValue right) => new(operation, new BsonArray { left, right });
    private static BsonDocument And(params BsonValue[] values) => new("$and", new BsonArray(values));
    private static BsonDocument Or(params BsonValue[] values) => new("$or", new BsonArray(values));
    private static BsonDocument Object(BsonDocument properties, params string[] optional) => new()
        { { "bsonType", "object" }, { "required", new BsonArray(properties.Names.Where(name => !optional.Contains(name))) }, { "additionalProperties", false }, { "properties", properties } };
    private static BsonDocument Array(BsonDocument item, int minimum, int maximum) => new()
        { { "bsonType", "array" }, { "minItems", minimum }, { "maxItems", maximum }, { "uniqueItems", true }, { "items", item } };
    private static BsonDocument Long(long minimum = 1) => new() { { "bsonType", "long" }, { "minimum", minimum } };
    private static BsonDocument SchemaVersion() => new() { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } };
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Choice(params string[] choices) => new() { { "bsonType", "string" }, { "enum", new BsonArray(choices) } };
    private static BsonDocument Text(int maximum) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum }, { "pattern", Printable } };
    private static BsonDocument Digest() => new() { { "bsonType", "string" }, { "minLength", 64 }, { "maxLength", 64 }, { "pattern", HashPattern } };
}
