using MongoDB.Bson;
using UnitToko.Platform.Inventory;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Draft only. The complete forward inventory migration, preflight and source services own activation.</summary>
internal static class InventoryMovementSchemas
{
    internal static BsonDocument Key() => new("sourceEvent", 1);
    internal static BsonDocument Validator()
    {
        BsonDocument Long(long min = 1) => new() { { "bsonType", "long" }, { "minimum", min } };
        BsonDocument Text(int max) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", max } };
        BsonDocument Code() => new() { { "bsonType", "string" }, { "pattern", @"\A[A-Za-z][A-Za-z0-9._-]{0,63}\z" } };
        BsonDocument Object(BsonDocument properties) => new()
        {
            { "bsonType", "object" }, { "required", new BsonArray(properties.Names) }, { "additionalProperties", false }, { "properties", properties }
        };
        var balance = InventorySchemas.BalanceValidator()["$and"][0]["$jsonSchema"].DeepClone();
        var shape = Object(new()
        {
            { "_id", Long() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", new BsonDocument { { "bsonType", "string" }, { "enum", new BsonArray { "inventory.movement" } } } },
            { "sourceEvent", Text(170) }, { "source", Object(new() { { "namespace", Code() }, { "documentId", Long() }, { "eventKind", Code() }, { "occurrence", Long() } }) },
            { "sourceRevision", Long() }, { "physicalPoolId", Long() }, { "poolRevision", Long() },
            { "actorId", Long() }, { "actorGrantVersion", Long(0) }, { "recordedAt", new BsonDocument("bsonType", "date") },
            { "reason", Text(2000) }, { "correlationId", Text(128) },
            { "legs", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "items", Object(new()
            {
                { "balanceId", Long() }, { "action", new BsonDocument { { "bsonType", "string" }, { "enum", new BsonArray(Enum.GetNames<InventoryBalanceAction>()) } } },
                { "packages", Long() }, { "before", balance }, { "after", balance.DeepClone() }
            }) } } }
        });
        return new("$and", new BsonArray
        {
            new BsonDocument("$jsonSchema", shape),
            new BsonDocument("$expr", Effects()),
            new BsonDocument("recordedAt", new BsonDocument
            {
                { "$gte", new BsonDateTime(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)) },
                { "$lte", new BsonDateTime(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)) }
            }),
            new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$sourceEvent", new BsonDocument("$concat", new BsonArray
            {
                "$source.namespace", ":", new BsonDocument("$toString", "$source.documentId"), ":", "$source.eventKind", ":", new BsonDocument("$toString", "$source.occurrence")
            }) }))
        });
    }
    private static BsonDocument Effects()
    {
        BsonValue Before(string field) => "$$leg.before." + field;
        BsonValue After(string field) => "$$leg.after." + field;
        BsonDocument Binary(string op, BsonValue left, BsonValue right) => new(op, new BsonArray { left, right });
        BsonDocument Eq(BsonValue left, BsonValue right) => Binary("$eq", left, right);
        BsonDocument Add(BsonValue left, BsonValue right) => Binary("$add", left, right);
        BsonDocument Sub(BsonValue left, BsonValue right) => Binary("$subtract", left, right);
        BsonDocument Lte(BsonValue left, BsonValue right) => Binary("$lte", left, right);
        BsonDocument And(IEnumerable<BsonValue> terms) => new("$and", new BsonArray(terms));
        BsonDocument Map(BsonValue value) => new("$map", new BsonDocument { { "input", "$legs" }, { "as", "leg" }, { "in", value } });
        BsonDocument UniqueSize(BsonValue value) => new("$size", new BsonDocument("$setUnion", new BsonArray { Map(value), new BsonArray() }));
        BsonDocument Buckets(string prefix) => And([
            Lte(prefix + ".held", prefix + ".onHand"),
            Lte(prefix + ".expired", Sub(prefix + ".onHand", prefix + ".held")),
            Lte(prefix + ".reserved", Sub(Sub(prefix + ".onHand", prefix + ".held"), prefix + ".expired"))]);
        BsonValue quantity = "$$leg.packages";
        var available = Sub(Sub(Sub(Before("onHand"), Before("held")), Before("expired")), Before("reserved"));
        BsonDocument Branch(string action, BsonValue guard, Dictionary<string, BsonValue> changes) => new()
        {
            { "case", Eq("$$leg.action", action) },
            { "then", And(new[] { "onHand", "reserved", "held", "expired" }
                .Select(field => (BsonValue)Eq(After(field), changes.TryGetValue(field, out var changed) ? changed : Before(field))).Append(guard)) }
        };
        var actions = new BsonArray
        {
            Branch("Receive", Lte(quantity, Sub(long.MaxValue, Before("onHand"))), new() { ["onHand"] = Add(Before("onHand"), quantity) }),
            Branch("ReceiveHeld", Lte(quantity, Sub(long.MaxValue, Before("onHand"))), new() { ["onHand"] = Add(Before("onHand"), quantity), ["held"] = Add(Before("held"), quantity) }),
            Branch("Reserve", Lte(quantity, available), new() { ["reserved"] = Add(Before("reserved"), quantity) }),
            Branch("Release", Lte(quantity, Before("reserved")), new() { ["reserved"] = Sub(Before("reserved"), quantity) }),
            Branch("Dispatch", Lte(quantity, Before("reserved")), new() { ["onHand"] = Sub(Before("onHand"), quantity), ["reserved"] = Sub(Before("reserved"), quantity) }),
            Branch("Hold", Lte(quantity, available), new() { ["held"] = Add(Before("held"), quantity) }),
            Branch("ReleaseHold", Lte(quantity, Before("held")), new() { ["held"] = Sub(Before("held"), quantity) }),
            Branch("Expire", Lte(quantity, available), new() { ["expired"] = Add(Before("expired"), quantity) }),
            Branch("ExpireHeld", Lte(quantity, Before("held")), new() { ["held"] = Sub(Before("held"), quantity), ["expired"] = Add(Before("expired"), quantity) })
        };
        var fields = new List<BsonValue>
        {
            Eq(Before("_id"), "$$leg.balanceId"), Eq(After("_id"), "$$leg.balanceId"),
            Eq(Before("physicalPoolId"), "$physicalPoolId"), Eq(After("physicalPoolId"), "$physicalPoolId"),
            Binary("$lt", Before("version"), long.MaxValue), Eq(After("version"), Add(Before("version"), 1L)),
            Buckets("$$leg.before"), Buckets("$$leg.after"),
            new BsonDocument("$switch", new BsonDocument { { "branches", actions }, { "default", false } })
        };
        foreach (var field in new[] { "skuId", "definitionId", "lotId", "location" }) fields.Add(Eq(Before(field), After(field)));
        return And([
            Eq(new BsonDocument("$size", "$legs"), UniqueSize("$$leg.balanceId")),
            Eq(UniqueSize("$$leg.before.location"), 1),
            new BsonDocument("$allElementsTrue", new BsonArray { Map(And(fields)) })]);
    }
}
