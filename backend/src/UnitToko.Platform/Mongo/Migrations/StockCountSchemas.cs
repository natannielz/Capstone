using MongoDB.Bson;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class StockCountSchemas
{
    internal static readonly string[] Collections = [StockCountBson.Collection, "auditEvents", "idempotencyRecords"];
    internal static BsonDocument Selection(string name) => name switch
    {
        "auditEvents" => new("kind", StockCountBson.AuditKind),
        "idempotencyRecords" => new("$or", new BsonArray
        {
            new BsonDocument("operation", StockCountBson.Operation),
            new BsonDocument("stockCount", new BsonDocument("$exists", true))
        }),
        StockCountBson.Collection => new(),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    internal static BsonDocument Validator(string name)
    {
        var shape = Shape(name);
        var structural = Structure(shape, "$$ROOT");
        var rules = name == StockCountBson.Collection ? ObservationRules("$") :
            name == "idempotencyRecords" ? ObservationRules("$result.") : BsonBoolean.True;
        var required = new BsonDocument("$and", new BsonArray
        {
            new BsonDocument("$jsonSchema", shape), new BsonDocument("$expr", And(structural, rules))
        });
        return name == StockCountBson.Collection ? required :
            new("$or", new BsonArray { new BsonDocument("$nor", new BsonArray { Selection(name) }), required });
    }
    private static BsonDocument Shape(string name) => name switch
    {
        StockCountBson.Collection => Observation(),
        "auditEvents" => Object(new()
        {
            { "_id", Long() }, { "schemaVersion", Schema() }, { "kind", Choice(StockCountBson.AuditKind) },
            { "operation", Choice(StockCountBson.Operation) }, { "observationId", Long() },
            { "actorId", Long() }, { "actorGrantVersion", Long() }, { "issuer", Text(2048) }, { "subject", Text(256) },
            { "location", Scope(true) }, { "createdAt", Date() }, { "requestKey", Key() }, { "bodyHash", Hash() },
            { "fingerprint", Hash() }, { "reason", Text(1000) }, { "correlationId", Text(128) }
        }),
        "idempotencyRecords" => Object(new()
        {
            { "_id", new BsonDocument("bsonType", "objectId") }, { "schemaVersion", Schema() },
            { "actorId", Long() }, { "operation", Choice(StockCountBson.Operation) }, { "key", Key() },
            { "bodyHash", Hash() }, { "createdAt", Date() }, { "result", Observation() },
            { "stockCount", Object(new() { { "schemaVersion", Schema() }, { "observationId", Long() },
                { "auditId", Long() }, { "issuer", Text(2048) }, { "subject", Text(256) } }) }
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    private static BsonDocument Observation() => Object(new()
    {
        { "_id", Long() }, { "schemaVersion", Schema() }, { "revision", new BsonDocument { { "bsonType", "long" }, { "enum", new BsonArray { 1L } } } },
        { "location", Scope(true) }, { "actorId", Long() }, { "issuer", Text(2048) }, { "subject", Text(256) },
        { "actorGrantVersion", Long() }, { "recordedAt", Date() }, { "requestKey", Key() }, { "bodyHash", Hash() },
        { "definition", Object(new()
            {
                { "owner", Scope(false) }, { "skuId", Long() }, { "skuRevision", Long() }, { "familyId", Long() },
                { "familyRevision", Long() }, { "categoryId", Long() }, { "categoryRevision", Long() },
                { "definitionId", Long() }, { "definitionVersion", Long() },
                { "skuCode", Text(64, @"\A[A-Z0-9._-]{1,64}\z") }, { "physicalItemKey", Text(128) },
                { "packageLabel", Text(160) }, { "packageKind", Choice("loose", "sealed", "other") },
                { "baseUnitCode", Text(64) }, { "baseUnitsPerPackage", Integer(1, int.MaxValue) }
            }) },
        { "packages", Integer(0, 1000000) }, { "baseUnits", Long(0) }, { "observedLotLabel", Nullable(Text(160)) },
        { "baseline", Choice("unavailable") }, { "coverage", Choice("single-observation") }, { "auditId", Long() },
        { "reason", Text(1000) }, { "correlationId", Text(128) }, { "fingerprint", Hash() }
    });
    private static BsonDocument Scope(bool warehouse) => Object(new()
    {
        { "kind", warehouse ? Choice("fulfillmentWarehouse") : Choice("fulfillmentShop", "fulfillmentWarehouse") },
        { "organizationId", Long() }, { "shopId", Long() }, { "channelId", Long() },
        { "warehouseId", warehouse ? Long() : Nullable(Long()) }
    });
    private static BsonValue ObservationRules(string path) => And(
        Equal(path + "baseUnits", new BsonDocument("$multiply", new BsonArray
        { Number(path + "packages"), Number(path + "definition.baseUnitsPerPackage") })),
        Equal(path + "location.organizationId", path + "definition.owner.organizationId"),
        Equal(path + "location.shopId", path + "definition.owner.shopId"),
        Equal(path + "location.channelId", path + "definition.owner.channelId"),
        new BsonDocument("$cond", new BsonArray
        {
            Equal(path + "definition.owner.kind", "fulfillmentShop"),
            Equal(new BsonDocument("$type", path + "definition.owner.warehouseId"), "null"),
            Equal(path + "location.warehouseId", path + "definition.owner.warehouseId")
        }));
    private static BsonDocument Number(string path) => new("$convert", new BsonDocument
        { { "input", path }, { "to", "long" }, { "onError", 0L }, { "onNull", 0L } });
    private static BsonValue Structure(BsonDocument shape, string path)
    {
        var type = shape["bsonType"];
        if (type.IsBsonArray)
        {
            var nonnull = shape.DeepClone().AsBsonDocument;
            nonnull["bsonType"] = type.AsBsonArray.First(item => item != "null");
            return new BsonDocument("$or", new BsonArray { Equal(new BsonDocument("$type", path), "null"), Structure(nonnull, path) });
        }
        if (type == "date") return And(
            new BsonDocument("$gte", new BsonArray { path, new BsonDateTime(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)) }),
            new BsonDocument("$lte", new BsonArray { path, new BsonDateTime(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)) }));
        if (type != "object") return BsonBoolean.True;
        var pairs = new BsonDocument("$objectToArray", new BsonDocument("$cond", new BsonArray
            { Equal(new BsonDocument("$type", path), "object"), path, new BsonDocument() }));
        var names = new BsonDocument("$map", new BsonDocument { { "input", pairs }, { "as", "field" }, { "in", "$$field.k" } });
        var rules = new List<BsonValue>
        {
            Equal(new BsonDocument("$size", names), new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { names, new BsonArray() })))
        };
        foreach (var field in shape["properties"].AsBsonDocument)
            rules.Add(Structure(field.Value.AsBsonDocument, path + "." + field.Name));
        return And(rules.ToArray());
    }
    private static BsonDocument Object(BsonDocument properties) => new()
        { { "bsonType", "object" }, { "required", new BsonArray(properties.Names) }, { "additionalProperties", false }, { "properties", properties } };
    private static BsonDocument Long(long minimum = 1) => new() { { "bsonType", "long" }, { "minimum", minimum } };
    private static BsonDocument Integer(int minimum, int maximum) => new() { { "bsonType", "int" }, { "minimum", minimum }, { "maximum", maximum } };
    private static BsonDocument Nullable(BsonDocument value)
    {
        var copy = value.DeepClone().AsBsonDocument;
        copy["bsonType"] = new BsonArray { copy["bsonType"], "null" };
        return copy;
    }
    private static BsonDocument Schema() => new() { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } };
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Choice(params string[] values) => new() { { "bsonType", "string" }, { "enum", new BsonArray(values) } };
    private static BsonDocument Text(int maximum, string pattern = @"\A[^\s\x00-\x1f\x7f-\x9f](?:[^\x00-\x1f\x7f-\x9f]*[^\s\x00-\x1f\x7f-\x9f])?\z") =>
        new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum }, { "pattern", pattern } };
    private static BsonDocument Key() => Text(128, @"\A[A-Za-z0-9._:-]{1,128}\z");
    private static BsonDocument Hash() => Text(64, @"\A[0-9a-f]{64}\z");
    private static BsonDocument Equal(BsonValue left, BsonValue right) => new("$eq", new BsonArray { left, right });
    private static BsonDocument And(params BsonValue[] rules) => new("$and", new BsonArray(rules));
}
