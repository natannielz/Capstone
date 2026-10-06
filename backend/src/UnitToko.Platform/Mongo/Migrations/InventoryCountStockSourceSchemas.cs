using MongoDB.Bson;
using UnitToko.Platform.Inventory;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class InventoryCountStockSourceSchemas
{
    internal const string Heads = "inventoryCountSourceHeads", Transitions = "inventoryCountSourceTransitions", Associations = "inventoryCountSourceAssociations";
    internal static readonly string[] Collections = [Heads, Transitions, Associations];
    private static BsonDocument Id(long minimum = 1) => S.Number(minimum, long.MaxValue - 2);
    private static BsonDocument Hash() => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    internal static BsonDocument PoolBinding() => S.Object(new() {
        { "poolId", S.Number(1) }, { "poolRevision", S.Number(1) }, { "setupDecisionId", S.Number(1) },
        { "configurationHash", Hash() }, { "physicalKey", S.Input()["properties"]["physicalKey"].DeepClone() },
        { "label", S.Input()["properties"]["label"].DeepClone() }, { "active", new BsonDocument("bsonType", "bool") } });
    internal static BsonDocument Configuration() {
        var value = StockSourceSuspensionSchemas.Validator()["$and"][0]["$jsonSchema"]["properties"]["configuration"].DeepClone().AsBsonDocument;
        value["properties"]["mappings"].AsBsonDocument.Add("maxItems", 4096);
        return value;
    }
    internal static BsonDocument Validator(string name)
    {
        var fields = new BsonDocument { { "_id", Id() },
            { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(name == Heads ? "inventory.countsource.head" : name == Transitions ? "inventory.countsource.transition" : "inventory.countsource.association") } };
        var date = new BsonDocument("bsonType", "date");
        if (name == Heads) {
            fields.Add("location", S.Location()); fields.Add("setupDecisionId", S.Number(1)); fields.Add("revision", Id());
            fields.Add("lastTransitionId", S.Number(1)); fields.Add("lastHash", Hash()); fields.Add("updatedAt", date); fields.Add("fence", Id(0));
        } else if (name == Transitions) {
            fields.Add("poolId", S.Number(1)); fields.Add("revision", Id()); fields.Add("previousId", Nullable(S.Number(1)));
            fields.Add("previousHash", Nullable(Hash())); fields.Add("sourceKind", S.Choice("setup", "count")); fields.Add("sourceId", S.Number(1));
            fields.Add("sourceHash", Hash()); fields.Add("sourceAuditId", S.Number(1)); fields.Add("actorId", S.Number(1)); fields.Add("actorGrantVersion", S.Number(1));
            fields.Add("recordedAt", date); fields.Add("poolRevision", S.Number(1)); fields.Add("beforePoolFence", S.Number(0)); fields.Add("afterPoolFence", S.Number(0));
            fields.Add("observationId", Nullable(S.Number(1))); fields.Add("transitionHash", Hash());
        } else if (name == Associations) {
            fields.Add("observationId", S.Number(1)); fields.Add("poolId", S.Number(1));
            fields.Add("location", StockCountSchemas.Validator(StockCountBson.Collection)["$and"][0]["$jsonSchema"]["properties"]["location"].DeepClone());
            fields.Add("poolBinding", PoolBinding()); fields.Add("configuration", Configuration());
            fields.Add("observationFingerprint", Hash()); fields.Add("observationBodyHash", Hash()); fields.Add("observationAuditId", S.Number(1));
            fields.Add("actorId", S.Number(1)); fields.Add("actorGrantVersion", S.Number(1)); fields.Add("recordedAt", date);
            fields.Add("requestKey", StockCountSchemas.Validator(StockCountBson.Collection)["$and"][0]["$jsonSchema"]["properties"]["requestKey"].DeepClone());
            fields.Add("people", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 1 }, { "items", S.Object(new() {
                { "userId", S.Number(1) }, { "personId", S.Number(1) }, { "verification", S.Choice("Verified") }, { "version", S.Number(1, long.MaxValue - 1) } }) } });
            fields.Add("associationHash", Hash());
        } else throw new ArgumentException("Unknown Inventory count source owner", nameof(name));
        var rules = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), DuplicateFields(S.Object(fields), "$$ROOT") };
        if (name == Associations) rules.Add(new BsonDocument("$expr", new BsonDocument("$and", new BsonArray {
            Equal("$_id", "$observationId"), Equal("$poolId", "$poolBinding.poolId"),
            Equal("$poolBinding.poolRevision", "$configuration.source.poolRevision"), Equal("$poolBinding.active", "$configuration.source.active"),
            Equal("$poolBinding.physicalKey", "$configuration.source.input.physicalKey"), Equal("$poolBinding.label", "$configuration.source.input.label") })));
        return new("$and", rules);
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        Heads => [],
        Transitions => [
            ("ux_inventory_count_source_pool_revision", new() { { "poolId", 1 }, { "revision", 1 } }, true),
            ("ux_inventory_count_source_event", new() { { "poolId", 1 }, { "sourceKind", 1 }, { "sourceId", 1 } }, true),
            ("ix_inventory_count_source_event", new() { { "sourceKind", 1 }, { "sourceId", 1 } }, false)],
        Associations => [
            ("ux_inventory_count_source_observation", new("observationId", 1), true),
            ("ux_inventory_count_source_actor_key", new() { { "actorId", 1 }, { "requestKey", 1 } }, true),
            ("ix_inventory_count_source_pool", new() { { "poolId", 1 }, { "_id", 1 } }, false)],
        _ => throw new ArgumentException("Unknown Inventory count source owner", nameof(name))
    };
    internal static BsonDocument BoundCountValidator(string name)
    {
        var previous = StockCountSchemas.Validator(name);
        var value = (name == StockCountBson.Collection ? previous : name == "idempotencyRecords" ? previous["$or"][1].AsBsonDocument :
            throw new ArgumentException("Only observation/result format changes", nameof(name))).DeepClone().AsBsonDocument;
        var schema = value["$and"][0]["$jsonSchema"].AsBsonDocument;
        var observation = name == StockCountBson.Collection ? schema : schema["properties"]["result"].AsBsonDocument;
        observation["properties"]["schemaVersion"]["enum"] = new BsonArray { 2 };
        observation["required"].AsBsonArray.Add("poolBinding"); observation["properties"].AsBsonDocument.Add("poolBinding", PoolBinding());
        observation["required"].AsBsonArray.Add("poolSourceHash"); observation["properties"].AsBsonDocument.Add("poolSourceHash", Hash());
        value["$and"].AsBsonArray.Add(DuplicateFields(PoolBinding(), name == StockCountBson.Collection ? "$poolBinding" : "$result.poolBinding"));
        return value;
    }
    internal static BsonDocument CountValidator(string name) => name == "auditEvents" ? StockCountSchemas.Validator(name) :
        new("$or", new BsonArray { StockCountSchemas.Validator(name), BoundCountValidator(name) });
    internal static BsonDocument ExtendCount(string name, BsonDocument current)
    {
        var successor = CountValidator(name);
        if (InventorySchemas.Contains(current, successor)) return current.DeepClone().AsBsonDocument;
        var previous = StockCountSchemas.Validator(name); var replaced = false;
        BsonDocument Visit(BsonDocument value) {
            if (CatalogSchemas.SemanticEquals(value, previous)) { replaced = true; return successor.DeepClone().AsBsonDocument; }
            var copy = value.DeepClone().AsBsonDocument;
            if (copy.TryGetValue("$and", out var terms) && terms.IsBsonArray)
                for (var index = 0; index < terms.AsBsonArray.Count; index++)
                    if (terms[index].IsBsonDocument) terms.AsBsonArray[index] = Visit(terms[index].AsBsonDocument);
            return copy;
        }
        var result = Visit(current);
        if (!replaced) throw InventoryCountStockSourceMigration.Invalid();
        return result;
    }
    private static BsonDocument Equal(BsonValue left, BsonValue right) => new("$eq", new BsonArray { left, right });
    private static BsonDocument DuplicateFields(BsonDocument shape, string path) => new("$expr", Structure(shape, path));
    private static BsonValue Structure(BsonDocument shape, string path)
    {
        if (shape.GetValue("bsonType", BsonNull.Value) == "date") return new BsonDocument("$and", new BsonArray {
            new BsonDocument("$gte", new BsonArray { path, new BsonDateTime(DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)) }),
            new BsonDocument("$lte", new BsonArray { path, new BsonDateTime(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)) }) });
        if (shape.GetValue("bsonType", BsonNull.Value) == "array") {
            var values = new BsonDocument("$cond", new BsonArray { new BsonDocument("$isArray", path), path, new BsonArray() });
            return new BsonDocument("$allElementsTrue", new BsonArray { new BsonDocument("$map", new BsonDocument {
                { "input", values }, { "as", "item" }, { "in", Structure(shape["items"].AsBsonDocument, "$$item") } }) });
        }
        if (shape.GetValue("bsonType", BsonNull.Value) != "object") return BsonBoolean.True;
        var pairs = new BsonDocument("$objectToArray", new BsonDocument("$cond", new BsonArray {
            Equal(new BsonDocument("$type", path), "object"), path, new BsonDocument() }));
        var names = new BsonDocument("$map", new BsonDocument { { "input", pairs }, { "as", "field" }, { "in", "$$field.k" } });
        var rules = new BsonArray { Equal(new BsonDocument("$size", names), new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { names, new BsonArray() }))) };
        foreach (var property in shape["properties"].AsBsonDocument)
            rules.Add(Structure(property.Value.AsBsonDocument, path == "$$ROOT" ? "$" + property.Name : path + "." + property.Name));
        return new BsonDocument("$and", rules);
    }
}
