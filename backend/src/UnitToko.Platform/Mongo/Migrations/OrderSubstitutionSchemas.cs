using MongoDB.Bson;
using UnitToko.Platform.Ordering;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;
using B = UnitToko.Platform.Ordering.OrderSubstitutionBson;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class OrderSubstitutionSchemas
{
    internal static readonly string[] Collections = [B.Proposals, B.Decisions];
    // Version114 extends the current reader while the old migration contract stays immutable.
    internal static BsonDocument StockSourceValidator(string name) {
        var schema = OrderStockSourceSchemas.Validator(name);
        if (name == OrderStockSourceRecords.Transitions) schema["$jsonSchema"]["properties"]["sourceKind"]["enum"].AsBsonArray.Add("substitution");
        return OrderLineageSchemas.Expand(schema);
    }
    private static BsonDocument Number(long min = 1, long max = long.MaxValue - 2) => S.Number(min, max);
    private static BsonDocument Count => new() { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1000000 } };
    private static BsonDocument Hash => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Array(BsonDocument item, int min = 0) => new() { { "bsonType", "array" }, { "minItems", min }, { "maxItems", 100 }, { "items", item } };
    private static BsonDocument OrderProperty(string name) => OrderLineageSchemas.Expand(OrderSubmissionSchemas.Validator(OrderSubmissionBson.Orders)["$and"][0]["$jsonSchema"]["properties"][name].DeepClone().AsBsonDocument);
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    internal static BsonDocument Input() => S.Object(new() { { "orderId", Number() }, { "suborderId", Number() },
        { "expectedOrderRevision", Number(1, 102) }, { "expectedDispatchSequence", Number(0) },
        { "selection", S.Object(new() { { "originalSkuId", Number() }, { "originalLineRevision", Number(0, 103) }, { "originalPackages", Count },
            { "replacement", S.Object(new() { { "skuId", Number() }, { "definitionId", Number() }, { "definitionVersion", Number() },
                { "priceId", Number() }, { "priceVersion", Number() }, { "packages", Count } }) } }) } });
    internal static BsonDocument Validator(string name) {
        if (!Collections.Contains(name)) throw new ArgumentOutOfRangeException(nameof(name));
        var fields = new BsonDocument { { "_id", Number() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(name == B.Proposals ? "ordering.substitution.proposal" : "ordering.substitution.decision") },
            { "orderId", Number() }, { "suborderId", Number() }, { "orderSnapshot", Hash }, { "input", Input() },
            { "createdBy", Number() }, { "buyingOrganizationId", Number() }, { "branchId", Number() }, { "divisionId", Nullable(Number()) },
            { "fulfillmentOrganizationId", Number() }, { "shopId", Number() }, { "warehouseId", Nullable(Number()) }, { "channelId", Number() },
            { "actorId", Number() }, { "actorGrantVersion", Number() }, { "recordedAt", new BsonDocument("bsonType", "date") },
            { "requestKey", S.Text(128) }, { "review", Hash }, { "bodyHash", Hash }, { "recordHash", Hash } };
        S.Metadata(fields);
        if (name == B.Proposals) {
            fields.Add("catalogBefore", OrderProperty("quote")); fields.Add("replacement", OrderProperty("quote"));
            fields.Add("originalRemaining", Count); fields.Add("reservationReview", Hash);
        } else {
            fields.Add("proposalId", Number()); fields.Add("proposalHash", Hash); fields.Add("decision", S.Choice("Accept", "Decline"));
            fields.Add("expectedRevision", Number(1, 102)); fields.Add("revision", Number(2, 103)); fields.Add("dispatchSequence", Number(0));
            fields.Add("catalog", OrderProperty("quote")); fields.Add("terms", OrderProperty("terms"));
            fields.Add("spendingState", S.Choice("Pending", "NotRequired", "Approved", "Rejected"));
            fields.Add("allocations", Array(OrderLineageSchemas.Expand(OrderSubmissionSchemas.Allocation()))); fields.Add("reservationIds", Array(Number()));
            fields.Add("releases", OrderLineageSchemas.Expand(OrderSubmissionSchemas.Validator("orderDecisions")["$and"][0]["$jsonSchema"]["properties"]["releases"].DeepClone().AsBsonDocument));
        }
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("recordedAt") });
    }
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        B.Proposals => [("ux_order_substitution_proposal_key", new() { { "actorId", 1 }, { "requestKey", 1 } }, true),
            ("ix_order_substitution_proposals", new() { { "orderId", 1 }, { "_id", -1 } }, false)],
        B.Decisions => [("ux_order_substitution_decision_key", new() { { "actorId", 1 }, { "requestKey", 1 } }, true),
            ("ux_order_substitution_proposal_decision", new("proposalId", 1), true),
            ("ux_order_substitution_order_revision", new() { { "orderId", 1 }, { "revision", 1 } }, true)],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
}
