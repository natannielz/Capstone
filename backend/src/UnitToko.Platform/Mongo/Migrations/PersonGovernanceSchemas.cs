using MongoDB.Bson;
using System.Text.RegularExpressions;

namespace UnitToko.Platform.Mongo.Migrations;

// Frozen v9 private governance records. Rows are references, never a production appointment.
internal static class PersonGovernanceSchemas
{
    internal static readonly string[] Collections = ["personGovernancePolicies", "personReviewProposals", "personReviewHistory"];
    private static readonly string[] HeadCollections = ["identities", "personAccountBindings", "personEquivalenceGroups", "personEvidenceSources", "personEvidenceVersions", "personRecords"];
    private static readonly BsonDateTime Earliest = new(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc));
    private static readonly BsonDateTime Latest = new(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc));
    private const string Printable = "^[^\\x00-\\x1f\\x7f-\\x9f]+\\z";
    private const string HashPattern = "^[0-9a-f]{64}\\z";

    private static BsonDocument Shape(string collection) => collection switch
    {
        "personGovernancePolicies" => Object(new()
        {
            { "_id", Long() }, { "schemaVersion", SchemaVersion() }, { "status", Choice("active", "suspended", "revoked") },
            { "version", Long() }, { "fence", Long(0) }, { "manifestReference", Text(512) }, { "manifestDigest", Digest() },
            { "effectiveAt", Date() }, { "expiresAt", new BsonDocument("bsonType", new BsonArray { "date", "null" }) }
        }, "expiresAt"),
        "personReviewProposals" => ProposalShape(),
        "personReviewHistory" => Object(new()
        {
            { "_id", Long() }, { "schemaVersion", SchemaVersion() }, { "proposalId", Long() },
            { "transitionVersion", Long() }, { "event", Choice("proposed", "approved", "rejected", "withdrawn") },
            { "actorId", Long() }, { "actorGrantVersion", Long() }, { "actorSecurityVersion", Long() },
            { "createdAt", Date() }, { "reason", Text(2000) }, { "correlationId", Text(128) }, { "proposal", ProposalShape() }
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(collection))
    };
    private static BsonDocument ProposalShape() => Object(new()
    {
        { "_id", Long() }, { "schemaVersion", SchemaVersion() }, { "kind", Choice("equivalence-group-suspension") },
        { "version", Long() }, { "status", Choice("pending", "approved", "rejected", "withdrawn") },
        { "requestedBy", Long() }, { "requesterGrantVersion", Long() }, { "requesterSecurityVersion", Long() },
        { "createdAt", Date() }, { "reason", Text(2000) }, { "correlationId", Text(128) },
        { "target", Object(new()
            {
                { "groupId", Long() }, { "groupVersion", Long() }, { "groupStatus", Choice("active", "disputed") },
                { "canonicalPersonId", Long() }, { "personIds", Members() }, { "userIds", Members() }, { "heads", Heads() }
            }) },
        { "requesterPerson", Person() }, { "policy", Pin() }, { "requestDigest", Digest() },
        { "resolution", Object(new()
            {
                { "actorId", Long() }, { "actorGrantVersion", Long() }, { "actorSecurityVersion", Long() },
                { "decidedAt", Date() }, { "reason", Text(2000) }, { "correlationId", Text(128) },
                { "person", Person() }, { "policy", Pin() }, { "resultGroupVersion", Long() },
                { "affectedRevisions", Array(Object(new()
                    {
                        { "userId", Long() }, { "previousGrantVersion", Long() }, { "grantVersion", Long() },
                        { "previousSecurityVersion", Long() }, { "securityVersion", Long() }
                    }), 0, 256) }
            }) }
    }, "resolution");
    private static BsonDocument Person() => Object(new() { { "userId", Long() }, { "canonicalPersonId", Long() }, { "heads", Heads() } });
    private static BsonDocument Pin() => Object(new() { { "id", Long() }, { "version", Long() }, { "digest", Digest() } });
    private static BsonDocument Heads() => Array(Object(new()
    {
        { "collection", Choice(HeadCollections) }, { "id", Long() }, { "revision", Long() }, { "digest", Digest() }
    }), 1, 2048);
    private static BsonDocument Members() => Array(Long(), 1, 256);

    internal static BsonDocument Validator(string collection)
    {
        var shape = Shape(collection);
        var sequence = 0;
        var expression = new BsonArray { StructuralExpression(shape, "$$ROOT", ref sequence) };
        if (collection == "personGovernancePolicies")
            expression.Add(Or(Compare("$in", Type("$$ROOT.expiresAt"), new BsonArray { "missing", "null" }),
                Compare("$gt", "$$ROOT.expiresAt", "$$ROOT.effectiveAt")));
        else if (collection == "personReviewProposals") expression.Add(ProposalExpression("$$ROOT"));
        else expression.Add(And(ProposalExpression("$$ROOT.proposal"), HistoryExpression()));
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", shape), new BsonDocument("$expr", new BsonDocument("$and", expression)) });
    }

    // JSON schema checks types and closed objects; expression guards add ordered membership,
    // representable dates, duplicate field names, chronology and transition invariants.
    private static BsonValue StructuralExpression(BsonDocument shape, string path, ref int sequence)
    {
        var type = shape["bsonType"];
        if (type.IsBsonArray)
            return Or(Compare("$eq", Type(path), "null"), DateBounds(path));
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
    private static BsonValue ProposalExpression(string path)
    {
        var target = path + ".target"; var resolution = path + ".resolution";
        var approved = And(
            Compare("$ne", resolution + ".actorId", path + ".requestedBy"),
            Compare("$ne", resolution + ".person.canonicalPersonId", path + ".requesterPerson.canonicalPersonId"),
            new BsonDocument("$not", new BsonArray { Compare("$in", resolution + ".person.canonicalPersonId", SafeArray(target + ".personIds")) }),
            new BsonDocument("$not", new BsonArray { Compare("$in", resolution + ".actorId", SafeArray(target + ".userIds")) }),
            Compare("$eq", resolution + ".resultGroupVersion", AddOne(target + ".groupVersion")),
            Compare("$eq", Map(SafeArray(resolution + ".affectedRevisions"), "affected", "$$affected.userId"), SafeArray(target + ".userIds")),
            All(Map(SafeArray(resolution + ".affectedRevisions"), "affected", And(
                Compare("$eq", "$$affected.grantVersion", AddOne("$$affected.previousGrantVersion")),
                Compare("$eq", "$$affected.securityVersion", AddOne("$$affected.previousSecurityVersion"))))));
        var noEffect = And(Compare("$eq", Size(SafeArray(resolution + ".affectedRevisions")), 0),
            Compare("$eq", resolution + ".resultGroupVersion", target + ".groupVersion"),
            Or(Compare("$ne", path + ".status", "withdrawn"), Compare("$eq", resolution + ".actorId", path + ".requestedBy")));
        var terminal = And(
            Compare("$eq", path + ".version", 2L), Compare("$eq", Type(resolution), "object"),
            Compare("$gte", resolution + ".decidedAt", path + ".createdAt"),
            Compare("$eq", resolution + ".person.userId", resolution + ".actorId"), HeadExpression(resolution + ".person.heads"),
            new BsonDocument("$cond", new BsonArray { Compare("$eq", path + ".status", "approved"), approved, noEffect }));
        return And(
            Compare("$eq", path + ".requesterPerson.userId", path + ".requestedBy"),
            SortedMembers(target + ".personIds"), SortedMembers(target + ".userIds"),
            Compare("$in", target + ".canonicalPersonId", SafeArray(target + ".personIds")),
            HeadExpression(target + ".heads"), HeadExpression(path + ".requesterPerson.heads"),
            new BsonDocument("$cond", new BsonArray
            {
                Compare("$eq", path + ".status", "pending"),
                And(Compare("$eq", path + ".version", 1L), Compare("$eq", Type(resolution), "missing")), terminal
            }));
    }
    private static BsonValue HistoryExpression()
    {
        const string p = "$$ROOT.proposal";
        var pending = Compare("$eq", p + ".status", "pending");
        var checks = new BsonArray
        {
            Compare("$eq", "$$ROOT.proposalId", p + "._id"), Compare("$eq", "$$ROOT.transitionVersion", p + ".version"),
            Compare("$eq", "$$ROOT.event", new BsonDocument("$cond", new BsonArray { pending, "proposed", p + ".status" }))
        };
        foreach (var fields in new[]
        {
            ("actorId", "requestedBy", "actorId"), ("actorGrantVersion", "requesterGrantVersion", "actorGrantVersion"),
            ("actorSecurityVersion", "requesterSecurityVersion", "actorSecurityVersion"), ("createdAt", "createdAt", "decidedAt"),
            ("reason", "reason", "reason"), ("correlationId", "correlationId", "correlationId")
        })
            checks.Add(Compare("$eq", "$$ROOT." + fields.Item1, new BsonDocument("$cond",
                new BsonArray { pending, p + "." + fields.Item2, p + ".resolution." + fields.Item3 })));
        return new BsonDocument("$and", checks);
    }
    private static BsonValue HeadExpression(string path)
    {
        var items = SafeArray(path);
        var keys = Map(items, "head", new BsonDocument { { "collection", "$$head.collection" }, { "id", "$$head.id" } });
        return And(Compare("$eq", items, new BsonDocument("$sortArray", new BsonDocument
            { { "input", items }, { "sortBy", new BsonDocument { { "collection", 1 }, { "id", 1 } } } })),
            Compare("$eq", Size(items), Size(new BsonDocument("$setUnion", new BsonArray { keys, new BsonArray() }))));
    }

    internal static bool IsValid(string collection, BsonDocument row)
    {
        if (!Collections.Contains(collection, StringComparer.Ordinal) || !Conforms(row, Shape(collection))) return false;
        if (collection == "personGovernancePolicies")
            return !row.TryGetValue("expiresAt", out var expiry) || expiry.IsBsonNull || expiry.CompareTo(row["effectiveAt"]) > 0;
        if (collection == "personReviewProposals") return ValidProposal(row);
        var proposal = row["proposal"].AsBsonDocument;
        if (!ValidProposal(proposal) || row["proposalId"] != proposal["_id"] || row["transitionVersion"] != proposal["version"]) return false;
        var pending = proposal["status"] == "pending";
        if (row["event"] != (pending ? "proposed" : proposal["status"])) return false;
        var metadata = pending ? proposal : proposal["resolution"].AsBsonDocument;
        return row["actorId"] == metadata[pending ? "requestedBy" : "actorId"] &&
            row["actorGrantVersion"] == metadata[pending ? "requesterGrantVersion" : "actorGrantVersion"] &&
            row["actorSecurityVersion"] == metadata[pending ? "requesterSecurityVersion" : "actorSecurityVersion"] &&
            row["createdAt"] == metadata[pending ? "createdAt" : "decidedAt"] &&
            row["reason"] == metadata["reason"] && row["correlationId"] == metadata["correlationId"];
    }
    private static bool ValidProposal(BsonDocument row)
    {
        var target = row["target"].AsBsonDocument; var person = row["requesterPerson"].AsBsonDocument;
        if (person["userId"] != row["requestedBy"] || !SortedIds(target["personIds"].AsBsonArray) || !SortedIds(target["userIds"].AsBsonArray) ||
            !target["personIds"].AsBsonArray.Contains(target["canonicalPersonId"]) || !SortedHeads(target["heads"].AsBsonArray) ||
            !SortedHeads(person["heads"].AsBsonArray)) return false;
        if (row["status"] == "pending") return row["version"] == 1L && !row.Contains("resolution");
        if (row["version"] != 2L || !row.TryGetValue("resolution", out var raw) || !raw.IsBsonDocument) return false;
        var resolution = raw.AsBsonDocument;
        if (resolution["decidedAt"].CompareTo(row["createdAt"]) < 0 || resolution["person"]["userId"] != resolution["actorId"] ||
            !SortedHeads(resolution["person"]["heads"].AsBsonArray)) return false;
        var affected = resolution["affectedRevisions"].AsBsonArray;
        if (row["status"] != "approved")
            return affected.Count == 0 && resolution["resultGroupVersion"] == target["groupVersion"] &&
                (row["status"] != "withdrawn" || resolution["actorId"] == row["requestedBy"]);
        if (resolution["actorId"] == row["requestedBy"] || target["userIds"].AsBsonArray.Contains(resolution["actorId"]) ||
            resolution["person"]["canonicalPersonId"] == person["canonicalPersonId"] || target["personIds"].AsBsonArray.Contains(resolution["person"]["canonicalPersonId"]) ||
            target["groupVersion"].AsInt64 == long.MaxValue || resolution["resultGroupVersion"].AsInt64 != target["groupVersion"].AsInt64 + 1 ||
            !affected.Select(item => item["userId"]).SequenceEqual(target["userIds"].AsBsonArray)) return false;
        return affected.All(item => item["previousGrantVersion"].AsInt64 != long.MaxValue && item["previousSecurityVersion"].AsInt64 != long.MaxValue &&
            item["grantVersion"].AsInt64 == item["previousGrantVersion"].AsInt64 + 1 &&
            item["securityVersion"].AsInt64 == item["previousSecurityVersion"].AsInt64 + 1);
    }
    private static bool Conforms(BsonValue value, BsonDocument rule)
    {
        var type = rule["bsonType"];
        if (type.IsBsonArray) return value.IsBsonNull || ValidDate(value);
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
                return text.Length >= rule["minLength"].AsInt32 && text.Length <= rule["maxLength"].AsInt32 &&
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
    internal static bool SemanticEquals(BsonValue left, BsonValue right)
    {
        if (left.BsonType != right.BsonType) return false;
        if (left.IsBsonDocument)
        {
            var a = left.AsBsonDocument; var b = right.AsBsonDocument;
            return a.ElementCount == b.ElementCount && a.All(field => b.TryGetValue(field.Name, out var value) && SemanticEquals(field.Value, value));
        }
        return left.IsBsonArray ? left.AsBsonArray.Count == right.AsBsonArray.Count && left.AsBsonArray.Zip(right.AsBsonArray).All(pair => SemanticEquals(pair.First, pair.Second)) : left.Equals(right);
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
    private static BsonDocument AddOne(string path) => new("$add", new BsonArray
        { new BsonDocument("$convert", new BsonDocument { { "input", path }, { "to", "long" }, { "onError", 0L }, { "onNull", 0L } }), 1L });
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
