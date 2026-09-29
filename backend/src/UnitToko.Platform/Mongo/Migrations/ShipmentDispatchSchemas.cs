using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Fulfillment;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

// Proposed version-24 contract; no earlier deployment or migration is changed.
internal static class ShipmentDispatchSchemas
{
    internal static BsonDocument Key() => new() { { "orderId", 1 }, { "orderSequence", 1 } };
    internal static BsonDocument NumberKey() => new() { { "numberEntity", 1 }, { "numberChannel", 1 }, { "numberYear", 1 }, { "number", 1 } };
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Reference() => S.Object(new() { { "id", S.Number(1) }, { "revision", S.Number(1) } });
    private static BsonDocument Array(BsonDocument item) => new() { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "items", item } };
    private static BsonDocument PreparationField(string field) => ShipmentPreparationSchemas.Validator(ShipmentPreparationBson.Versions)["$and"][0]["$jsonSchema"]["properties"][field].DeepClone().AsBsonDocument;
    internal static BsonDocument Validator() {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice("fulfillment.shipment") }, { "revision", new BsonDocument { { "bsonType", "long" }, { "enum", new BsonArray { 1L } } } },
            { "orderId", S.Number(1) }, { "suborderId", S.Number(1) }, { "orderSequence", S.Number(1) }, { "decisionRevision", S.Number(1, 103) },
            { "numberEntity", S.Number(1) }, { "numberChannel", S.Number(1) }, { "numberYear", S.Number(1, 9999) }, { "number", S.Number(1) },
            { "preparationId", S.Number(1) }, { "preparationVersionId", S.Number(1) }, { "preparationRevision", S.Number(1) },
            { "courierId", S.Number(1) }, { "courierName", S.Text(256) }, { "courierGrantVersion", S.Number(0) }, { "courierProfileVersion", S.Number(1) },
            { "source", PreparationField("source") }, { "scope", PreparationField("scope") }, { "lines", PreparationField("lines") },
            { "pickedLots", Array(S.Object(new() { { "reservationId", S.Number(1) }, { "packages", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", DomainValues.MaximumLineQuantity } } } })) },
            { "stockEffects", Array(S.Object(new() { { "reservationId", S.Number(1) }, { "allocation", OrderSubmissionSchemas.Allocation() },
                { "before", S.Object(new() { { "revision", S.Number(1) }, { "remaining", S.Number(1, 1000000) }, { "released", S.Number(0, 1000000) }, { "dispatched", S.Number(0, 1000000) } }) } })) },
            { "admission", S.Object(new() {
                { "seller", S.Object(new() { { "version", Reference() }, { "name", S.Text(200) }, { "address", S.Text(1000) } }) }, { "operatingPolicy", Reference() },
                { "validUntil", new BsonDocument("bsonType", "date") },
                { "lots", Array(S.Object(new() { { "reservationId", S.Number(1) }, { "sourceAuthority", Reference() }, { "costBasis", Reference() },
                    { "packageCost", new BsonDocument { { "bsonType", "decimal" }, { "minimum", new BsonDecimal128(0m) }, { "maximum", new BsonDecimal128(DomainValues.MaximumMoney) }, { "multipleOf", new BsonDecimal128(.01m) } } } })) }
            }) },
            { "review", Hash() }, { "bodyHash", Hash() }, { "dispatchedBy", S.Number(1) }, { "actorGrantVersion", S.Number(1) },
            { "requestKey", S.Text(128) }, { "recordedAt", new BsonDocument("bsonType", "date") } };
        S.Metadata(fields);
        var consistent = new BsonArray {
                new BsonDocument("$eq", new BsonArray { "$orderId", "$source.orderId" }), new BsonDocument("$eq", new BsonArray { "$suborderId", "$source.suborderId" }),
                new BsonDocument("$eq", new BsonArray { "$numberEntity", "$scope.fulfillmentOrganizationId" }),
                new BsonDocument("$eq", new BsonArray { "$numberChannel", "$scope.channelId" }),
                new BsonDocument("$eq", new BsonArray { "$numberYear", new BsonDocument("$year", new BsonDocument { { "date", "$recordedAt" }, { "timezone", "Asia/Jakarta" } }) }),
                new BsonDocument("$eq", new BsonArray { "$decisionRevision", "$source.revision" }), new BsonDocument("$gte", new BsonArray { "$recordedAt", "$source.orderedAt" }),
                new BsonDocument("$eq", new BsonArray { "$assignedUserIds", new BsonArray { "$courierId" } }) };
        foreach (var field in PreparationField("scope")["properties"].AsBsonDocument) {
            fields.Add(field.Name, field.Value.DeepClone());
            consistent.Add(new BsonDocument("$eq", new BsonArray { "$" + field.Name, "$scope." + field.Name }));
        }
        fields.Add("assignedUserIds", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 1 }, { "items", S.Number(1) } });
        consistent.Add(new BsonDocument("$lt", new BsonArray { "$recordedAt", "$admission.validUntil" }));
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("recordedAt"), S.DateRange("admission.validUntil"),
            new BsonDocument("$expr", new BsonDocument("$and", consistent)) });
    }
}
