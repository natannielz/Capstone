using MongoDB.Bson;
using UnitToko.Platform.Fulfillment;
using R = UnitToko.Platform.Fulfillment.ShipmentCourierRecords;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class ShipmentCourierSchemas
{
    internal static readonly string[] Collections = [R.Anchors, R.Heads, R.Events, R.Bindings];
    private static BsonDocument N(long min = 1, long max = long.MaxValue - 2) => S.Number(min, max);
    private static BsonDocument H => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument D => new("bsonType", "date");
    private static BsonDocument Null(BsonDocument item) => new("anyOf", new BsonArray { item, new BsonDocument("bsonType", "null") });
    private static BsonDocument Scope => ShipmentPreparationSchemas.Validator(ShipmentPreparationBson.Heads)["$and"][0]["$jsonSchema"]["properties"]["scope"].DeepClone().AsBsonDocument;
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", name == R.Bindings ? S.Text(80) : N() },
            { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        var checks = new BsonArray();
        if (name == R.Anchors) {
            fields.Add("origin", S.Choice("dispatch", "migration120")); fields.Add("dispatchHash", H);
            fields.Add("courierId", N()); fields.Add("courierName", S.Text(256)); fields.Add("scope", Scope); fields.Add("recordedAt", D);
            fields.Add("legacyBindingCount", N(0)); fields.Add("legacyBindingHash", H); checks.Add(S.DateRange("recordedAt"));
        } else if (name == R.Heads) {
            fields.Add("anchorHash", H); fields.Add("revision", N(1, R.MaximumChanges + 1)); fields.Add("courierId", N()); fields.Add("courierName", S.Text(256));
            fields.Add("effectiveAt", D); fields.Add("eventId", Null(N())); fields.Add("eventHash", H); fields.Add("scope", Scope); fields.Add("fence", N(0, long.MaxValue - 1));
            fields.Add("assignedUserIds", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 1 }, { "items", N() } });
            checks.Add(S.DateRange("effectiveAt")); checks.Add(new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$assignedUserIds", new BsonArray { "$courierId" } })));
        } else if (name == R.Events) {
            fields.Add("shipmentId", N()); fields.Add("revision", N(2, R.MaximumChanges + 1)); fields.Add("previousHash", H); fields.Add("previousCourierId", N());
            fields.Add("courierId", N()); fields.Add("courierName", S.Text(256)); fields.Add("courierGrantVersion", N()); fields.Add("courierProfileVersion", N());
            fields.Add("anchorHash", H); fields.Add("dispatchHash", H); fields.Add("actorId", N()); fields.Add("actorGrantVersion", N()); fields.Add("recordedAt", D);
            fields.Add("requestKey", S.Text(128)); fields.Add("bodyHash", H); fields.Add("review", H); fields.Add("reason", S.Text(2000)); fields.Add("correlationId", S.Text(128)); fields.Add("auditId", N());
            checks.Add(S.DateRange("recordedAt"));
        } else if (name == R.Bindings) {
            fields.Add("kind", S.Choice("attempt", "challenge", "receipt")); fields.Add("recordId", N()); fields.Add("shipmentId", N()); fields.Add("anchorHash", H);
            fields.Add("assignmentRevision", N(1, R.MaximumChanges + 1)); fields.Add("assignmentHash", H); fields.Add("sourceHash", H); fields.Add("auditId", Null(N()));
        } else throw new ArgumentOutOfRangeException(nameof(name));
        checks.Insert(0, new BsonDocument("$jsonSchema", S.Object(fields))); return new("$and", checks);
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        R.Anchors => [],
        R.Heads => [("ix_shipment_courier_current", new() { { "courierId", 1 }, { "_id", -1 } }, false),
            ("ix_shipment_courier_scope", new() { { "scope.fulfillmentOrganizationId", 1 }, { "scope.shopId", 1 }, { "scope.channelId", 1 }, { "_id", -1 } }, false)],
        R.Events => [("ux_shipment_courier_revision", new() { { "shipmentId", 1 }, { "revision", 1 } }, true),
            ("ux_shipment_courier_operation", new() { { "actorId", 1 }, { "requestKey", 1 } }, true)],
        R.Bindings => [("ux_shipment_courier_binding", new() { { "kind", 1 }, { "recordId", 1 } }, true),
            ("ix_shipment_courier_binding_shipment", new("shipmentId", 1), false)],
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
}
