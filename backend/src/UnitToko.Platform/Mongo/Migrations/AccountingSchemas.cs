using MongoDB.Bson;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class AccountingSchemas
{
    internal static readonly string[] Collections = ["accountingBooks", "accountingChartVersions", "journalEntries", "periods", "accountingPeriodHistory", "accountingAuditEvents"];
    private const string CodePattern = @"\A[A-Za-z][A-Za-z0-9._-]{0,63}\z";
    private const string MonthPattern = @"\A(?!0000)[0-9]{4}-(0[1-9]|1[0-2])\z";
    private const string DecimalPattern = @"\A(?:0|[1-9][0-9]*)(?:\.[0-9]{1,2})?\z";
    internal static BsonDocument Validator(string collection)
    {
        var shape = Shape(collection);
        var sequence = 0;
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", shape), new BsonDocument("$expr", And(Structure(shape, "$$ROOT", ref sequence), Rules(collection))) });
    }

    private static BsonDocument Shape(string name) => name switch
    {
        "accountingBooks" => Row(new() { { "key", Book() }, { "canonicalKey", Text(160) }, { "version", Long() }, { "fence", Long(0) }, { "active", Bool() }, { "chartVersionId", Long() }, { "closedThrough", Nullable(Month()) } }),
        "accountingChartVersions" => Row(new() { { "bookId", Long() }, { "version", Long() }, { "active", Bool() }, { "mappings", Array(Object(new() { { "mappingKey", Code() }, { "accountId", Long() }, { "accountCode", Code() }, { "active", Bool() } }), 1, 1000) } }),
        "journalEntries" => Row(new() { { "bookId", Long() }, { "bookKey", Book() }, { "source", Source() }, { "sourceEvent", Text(200) }, { "sourceRevision", Long() }, { "fingerprint", Hash() }, { "actorId", Long() }, { "actorGrantVersion", Long(0) }, { "dimensions", Dimensions() }, { "businessDate", Text(10, @"\A(?!0000)[0-9]{4}-[0-9]{2}-[0-9]{2}\z", 10) }, { "month", Month() }, { "currency", Currency() }, { "chartId", Long() }, { "chartVersion", Long() }, { "total", Money() }, { "lines", Array(Line(), 2, 100) }, { "reversalOf", Nullable(Long()) }, { "createdAt", Date() }, { "reason", Text(2000) }, { "correlationId", Text(128) } }),
        "periods" => Row(new() { { "bookId", Long() }, { "month", Month() }, { "version", Long() }, { "dataRevision", Long(0) }, { "state", Choice("open", "submitted", "approved", "closed") }, { "approvedRevision", Nullable(Long(0)) }, { "snapshotHash", Nullable(Hash()) }, { "lastHistoryId", Nullable(Long()) } }),
        "accountingPeriodHistory" => Row(new() { { "bookId", Long() }, { "periodId", Long() }, { "month", Month() }, { "version", Long() }, { "dataRevision", Long(0) }, { "stage", Choice("submitted", "approved", "invalidated", "closed") }, { "previousHistoryId", Nullable(Long()) }, { "snapshotHash", Nullable(Hash()) }, { "actorId", Long() }, { "actorGrantVersion", Long(0) }, { "createdAt", Date() }, { "reason", Text(2000) }, { "correlationId", Text(128) } }),
        "accountingAuditEvents" => Row(new() { { "bookId", Long() }, { "eventKey", Text(200) }, { "kind", Choice("journal.posted", "journal.reversed", "period.closed", "period.invalidated") }, { "journalId", Nullable(Long()) }, { "periodId", Nullable(Long()) }, { "fingerprint", Nullable(Hash()) }, { "actorId", Long() }, { "actorGrantVersion", Long(0) }, { "createdAt", Date() }, { "reason", Text(2000) }, { "correlationId", Text(128) } }),
        _ => throw new ArgumentOutOfRangeException(nameof(name))};
    private static BsonValue Rules(string name) => name switch
    {
        "accountingBooks" => Eq("$canonicalKey", CanonicalBook("$key")),
        "accountingChartVersions" => And(Unique(Map(SafeArray("$mappings"), "mapping", "$$mapping.mappingKey")), All(Map(SafeArray("$mappings"), "left", All(Map(SafeArray("$mappings"), "right", And(Or(Ne("$$left.accountId", "$$right.accountId"), Eq("$$left.accountCode", "$$right.accountCode")), Or(Ne("$$left.accountCode", "$$right.accountCode"), Eq("$$left.accountId", "$$right.accountId")))))))),
        "journalEntries" => And(Eq("$sourceEvent", SourceKey("$source")), Eq("$currency", "$bookKey.currency"), Eq("$dimensions.organizationId", "$bookKey.organizationId"), Eq("$dimensions.shopId", "$bookKey.shopId"), Eq("$dimensions.channelId", "$bookKey.channelId"), Or(Eq(Type("$dimensions.branchId"), "null"), Ne(Type("$dimensions.buyingOrganizationId"), "null")), Ne("$businessDate", "0001-01-01"), Eq("$businessDate", new BsonDocument("$dateToString", new BsonDocument { { "date", SafeBusinessDate() }, { "format", "%Y-%m-%d" }, { "timezone", "UTC" }, { "onNull", "" } })), Eq("$month", new BsonDocument("$substrCP", new BsonArray { StringValue("$businessDate"), 0, 7 })), Precision("$total"), Gt("$total", new BsonDecimal128(0m)), Eq("$total", new BsonDocument("$sum", Map(SafeArray("$lines"), "line", "$$line.debit"))), Eq("$total", new BsonDocument("$sum", Map(SafeArray("$lines"), "line", "$$line.credit"))), All(Map(SafeArray("$lines"), "line", And(Precision("$$line.debit"), Precision("$$line.credit"), Or(And(Gt("$$line.debit", new BsonDecimal128(0m)), Eq("$$line.credit", new BsonDecimal128(0m))), And(Eq("$$line.debit", new BsonDecimal128(0m)), Gt("$$line.credit", new BsonDecimal128(0m))))))), Or(Eq(Type("$reversalOf"), "null"), Ne("$reversalOf", "$_id"))),
        "periods" => Or(And(Eq("$state", "open"), Eq(Type("$approvedRevision"), "null"), Eq(Type("$snapshotHash"), "null")), And(Eq("$state", "submitted"), Eq(Type("$approvedRevision"), "null"), Eq(Type("$snapshotHash"), "string"), Eq(Type("$lastHistoryId"), "long")), And(new BsonDocument("$in", new BsonArray { "$state", new BsonArray { "approved", "closed" } }), Eq("$approvedRevision", "$dataRevision"), Eq(Type("$snapshotHash"), "string"), Eq(Type("$lastHistoryId"), "long"))),
        "accountingPeriodHistory" => Or(And(Eq("$stage", "invalidated"), Eq(Type("$snapshotHash"), "null")), And(Ne("$stage", "invalidated"), Eq(Type("$snapshotHash"), "string"))),
        "accountingAuditEvents" => Or(And(new BsonDocument("$in", new BsonArray { "$kind", new BsonArray { "journal.posted", "journal.reversed" } }), Eq(Type("$journalId"), "long"), Eq(Type("$periodId"), "null"), Eq(Type("$fingerprint"), "string")), And(new BsonDocument("$in", new BsonArray { "$kind", new BsonArray { "period.closed", "period.invalidated" } }), Eq(Type("$journalId"), "null"), Eq(Type("$periodId"), "long"))),
        _ => BsonBoolean.True
    };
    private static BsonDocument Source() => Object(new() { { "namespace", Code() }, { "documentId", Long() }, { "eventKind", Code() }, { "occurrence", Long() } });
    private static BsonDocument Book() => Object(new() { { "organizationId", Long() }, { "shopId", Long() }, { "channelId", Long() }, { "currency", Currency() } });
    private static BsonDocument Dimensions() => Object(new() { { "organizationId", Long() }, { "shopId", Long() }, { "channelId", Long() }, { "warehouseId", Nullable(Long()) }, { "buyingOrganizationId", Nullable(Long()) }, { "branchId", Nullable(Long()) } });
    private static BsonDocument Line() => Object(new() { { "mappingKey", Code() }, { "accountId", Long() }, { "accountCode", Code() }, { "debit", Money() }, { "credit", Money() } });
    private static BsonDocument Row(BsonDocument fields)
    {
        fields.InsertAt(0, new("schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } }));
        fields.InsertAt(0, new("_id", Long()));
        return Object(fields);
    }

    private static BsonDocument Object(BsonDocument properties) => new()
    {
        {
            "bsonType",
            "object"
        },
        {
            "required",
            new BsonArray(properties.Names)
        },
        {
            "additionalProperties",
            false
        },
        {
            "properties",
            properties
        }
    };
    private static BsonDocument Array(BsonDocument items, int minimum, int maximum) => new()
    {
        {
            "bsonType",
            "array"
        },
        {
            "minItems",
            minimum
        },
        {
            "maxItems",
            maximum
        },
        {
            "items",
            items
        }
    };
    private static BsonDocument Long(long minimum = 1) => new()
    {
        {
            "bsonType",
            "long"
        },
        {
            "minimum",
            minimum
        }
    };
    private static BsonDocument Nullable(BsonDocument rule)
    {
        var copy = rule.DeepClone().AsBsonDocument;
        copy["bsonType"] = new BsonArray
        {
            copy["bsonType"],
            "null"
        };
        return copy;
    }

    private static BsonDocument Text(int maximum, string pattern = @"\A[^\s\x00-\x1f\x7f-\x9f](?:[^\x00-\x1f\x7f-\x9f]*[^\s\x00-\x1f\x7f-\x9f])?\z", int minimum = 1) => new()
    {
        {
            "bsonType",
            "string"
        },
        {
            "minLength",
            minimum
        },
        {
            "maxLength",
            maximum
        },
        {
            "pattern",
            pattern
        }
    };
    private static BsonDocument Code() => Text(64, CodePattern);
    private static BsonDocument Month() => Text(7, MonthPattern, 7);
    private static BsonDocument Hash() => Text(64, @"\A[0-9a-f]{64}\z", 64);
    private static BsonDocument Currency() => Text(3, @"\A[A-Z]{3}\z", 3);
    private static BsonDocument Bool() => new("bsonType", "bool");
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Money() => new()
    {
        {
            "bsonType",
            "decimal"
        },
        {
            "minimum",
            new BsonDecimal128(0m)
        },
        {
            "maximum",
            new BsonDecimal128(999999999999999.99m)
        }
    };
    private static BsonDocument Choice(params string[] values) => new()
    {
        {
            "bsonType",
            "string"
        },
        {
            "enum",
            new BsonArray(values)
        }
    };
    private static BsonValue Structure(BsonDocument shape, string path, ref int sequence)
    {
        var type = shape["bsonType"];
        if (type.IsBsonArray)
            return BsonBoolean.True;
        if (type == "array")
        {
            var variable = "value" + sequence++;
            return All(Map(SafeArray(path), variable, Structure(shape["items"].AsBsonDocument, "$$" + variable, ref sequence)));
        }

        if (type == "date")
            return And(new BsonDocument("$gte", new BsonArray { path, new BsonDateTime(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)) }), new BsonDocument("$lte", new BsonArray { path, new BsonDateTime(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)) }));
        if (type != "object")
            return BsonBoolean.True;
        var pairs = new BsonDocument("$objectToArray", new BsonDocument("$cond", new BsonArray { Eq(Type(path), "object"), path, new BsonDocument() }));
        var rules = new List<BsonValue>
        {
            Unique(Map(pairs, "field", "$$field.k"))
        };
        foreach (var property in shape["properties"].AsBsonDocument)
            rules.Add(Structure(property.Value.AsBsonDocument, path + "." + property.Name, ref sequence));
        return And(rules.ToArray());
    }

    private static BsonDocument SafeBusinessDate() => new("$dateFromString", new BsonDocument { { "dateString", StringValue("$businessDate") }, { "format", "%Y-%m-%d" }, { "timezone", "UTC" }, { "onError", BsonNull.Value }, { "onNull", BsonNull.Value } });
    private static BsonDocument CanonicalBook(string path) => new("$concat", new BsonArray { StringValue(path + ".organizationId"), ":", StringValue(path + ".shopId"), ":", StringValue(path + ".channelId"), ":", StringValue(path + ".currency") });
    private static BsonDocument SourceKey(string path) => new("$concat", new BsonArray { StringValue(path + ".namespace"), ":", StringValue(path + ".documentId"), ":", StringValue(path + ".eventKind"), ":", StringValue(path + ".occurrence") });
    private static BsonDocument StringValue(string path) => new("$convert", new BsonDocument { { "input", path }, { "to", "string" }, { "onError", "" }, { "onNull", "" } });
    private static BsonDocument Precision(string path) => new("$regexMatch", new BsonDocument { { "input", StringValue(path) }, { "regex", DecimalPattern } });
    private static BsonDocument Type(string path) => new("$type", path);
    private static BsonValue SafeArray(string path) => new BsonDocument("$cond", new BsonArray { new BsonDocument("$isArray", path), path, new BsonArray() });
    private static BsonDocument Map(BsonValue input, string variable, BsonValue expression) => new("$map", new BsonDocument { { "input", input }, { "as", variable }, { "in", expression } });
    private static BsonDocument All(BsonValue value) => new("$allElementsTrue", new BsonArray { value });
    private static BsonDocument Unique(BsonValue value) => Eq(new BsonDocument("$size", value), new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { value, new BsonArray() })));
    private static BsonDocument Eq(BsonValue left, BsonValue right) => new("$eq", new BsonArray { left, right });
    private static BsonDocument Ne(BsonValue left, BsonValue right) => new("$ne", new BsonArray { left, right });
    private static BsonDocument Gt(BsonValue left, BsonValue right) => new("$gt", new BsonArray { left, right });
    private static BsonDocument And(params BsonValue[] values) => new("$and", new BsonArray(values));
    private static BsonDocument Or(params BsonValue[] values) => new("$or", new BsonArray(values));
}
