using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Ordering;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

/// <summary>Closed draft contracts for the order owner. Not yet an applied migration or production activation.
/// Connected isolated fixtures install these explicitly; a quiescent forward migration is required for release.</summary>
internal static class OrderSubmissionSchemas
{
    internal static readonly string[] Collections = [OrderSubmissionBson.Orders, OrderSubmissionBson.Suborders, OrderSubmissionBson.Reservations];
    internal static BsonDocument Key(string name) => name == OrderSubmissionBson.Reservations ? new() { { "orderId", 1 }, { "allocation.balanceId", 1 } } : new("_id", 1);
    private static BsonDocument Money() => new() { { "bsonType", "decimal" }, { "minimum", new BsonDecimal128(0m) }, { "maximum", new BsonDecimal128(DomainValues.MaximumMoney) }, { "multipleOf", new BsonDecimal128(.01m) } };
    private static BsonDocument Reference() => S.Object(new() { { "id", S.Number(1) }, { "revision", S.Number(1) } });
    private static BsonDocument Array(BsonDocument item, int min = 1) => new() { { "bsonType", "array" }, { "minItems", min }, { "maxItems", 100 }, { "items", item } };
    private static BsonDocument Nullable(BsonDocument value, string kind) { value["bsonType"] = new BsonArray { kind, "null" }; return value; }
    private static BsonDocument Owner() => CatalogSchemas.Validator("skus")["$and"][0]["$jsonSchema"]["properties"]["authoringOwner"].AsBsonDocument.DeepClone().AsBsonDocument;
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    internal static BsonDocument Allocation() => S.Object(new() { { "skuId", S.Number(1) }, { "authority", Reference() },
        { "poolId", S.Number(1) }, { "poolRevision", S.Number(1) }, { "balanceId", S.Number(1) }, { "balanceRevision", S.Number(1) },
        { "definitionId", S.Number(1) }, { "lotId", S.Number(1) }, { "packages", S.Number(1, 1000000) },
        { "location", S.Object(new() { { "organizationId", S.Number(1) }, { "shopId", S.Number(1) }, { "warehouseId", S.Number(1) }, { "channelId", S.Number(1) } }) } });
    internal static BsonDocument Validator(string name)
    {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "kind", S.Choice(name == OrderSubmissionBson.Orders ? "ordering.order" : name == OrderSubmissionBson.Suborders ? "ordering.suborder" : "ordering.reservation") },
            { "revision", S.Number(1) }, { "fence", S.Number(0, long.MaxValue - 1) } };
        if (name == OrderSubmissionBson.Orders) {
            fields.Add("createdBy", S.Number(1)); fields.Add("buyingOrganizationId", S.Number(1)); fields.Add("branchId", S.Number(1));
            fields.Add("divisionId", Nullable(S.Number(1), "long")); fields.Add("input", OrderDraftSchemas.Input());
            fields.Add("quote", S.Object(new() { { "currency", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[A-Z]{3}\z" } } },
                { "catalogSubtotal", Money() }, { "lines", Array(S.Object(new() {
                    { "familyId", S.Number(1) }, { "familyName", S.Text(160) }, { "publicId", S.Text(36) }, { "skuCode", S.Text(64) }, { "owner", Owner() },
                    { "definition", CatalogSchemas.Validator("variantDefinitions")["$and"][0]["$jsonSchema"].DeepClone() },
                    { "price", S.Object(new() { { "_id", S.Number(1) }, { "skuId", S.Number(1) }, { "version", S.Number(1) }, { "currency", S.Text(3) }, { "unitPrice", Money() }, { "effectiveAt", new BsonDocument("bsonType", "date") } }) },
                    { "packages", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1000000 } } }, { "baseUnits", S.Number(1) }, { "subtotal", Money() } })) } }));
            fields.Add("terms", S.Object(new() { { "policy", Reference() }, { "taxPolicy", Reference() }, { "creditDecision", Reference() },
                { "spendingPolicy", Reference() }, { "spending", S.Object(new() { { "mode", S.Choice("EveryOrder", "AboveThreshold", "NotRequired") }, { "threshold", Nullable(Money(), "decimal") } }) },
                { "buyer", S.Object(new() { { "version", Reference() }, { "name", S.Text(200) }, { "address", S.Text(1000) } }) },
                { "deliveryVersion", Reference() }, { "tax", Money() }, { "charges", Money() }, { "total", Money() } }));
            fields.Add("allocations", Array(Allocation())); fields.Add("suborderIds", Array(S.Number(1)));
            fields.Add("initialSpendingState", S.Choice("Pending", "NotRequired")); fields.Add("orderedAt", new BsonDocument("bsonType", "date"));
            fields.Add("review", Hash()); fields.Add("snapshotHash", Hash()); fields.Add("bodyHash", Hash()); fields.Add("requestKey", S.Text(128));
            fields.Add("actorGrantVersion", S.Number(1)); S.Metadata(fields);
        } else if (name == OrderSubmissionBson.Suborders) {
            fields.Add("orderId", S.Number(1)); fields.Add("owner", Owner()); fields.Add("skuIds", Array(S.Number(1)));
            fields.Add("status", S.Choice("awaitingSpending", "awaitingAcceptance")); fields.Add("catalogSubtotal", Money());
        } else if (name == OrderSubmissionBson.Reservations) {
            fields.Add("orderId", S.Number(1)); fields.Add("suborderId", S.Number(1)); fields.Add("allocation", Allocation());
            fields.Add("remaining", S.Number(0, 1000000)); fields.Add("dispatched", S.Number(0, 1000000)); fields.Add("released", S.Number(0, 1000000));
        } else throw new ArgumentException("Unknown order store", nameof(name));
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) };
        if (name == OrderSubmissionBson.Orders) terms.Add(S.DateRange("orderedAt"));
        if (name == OrderSubmissionBson.Reservations) terms.Add(new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray {
            "$allocation.packages", new BsonDocument("$add", new BsonArray { "$remaining", "$dispatched", "$released" }) })));
        return new("$and", terms);
    }
}
