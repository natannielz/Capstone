using MongoDB.Bson;
using UnitToko.Platform.Ordering;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class BranchOrderRequestSchemas
{
    internal static readonly string[] Collections = [BranchOrderRequestBson.Requests, BranchOrderRequestBson.Decisions];
    internal static BsonDocument[] Keys(string name) => name == BranchOrderRequestBson.Requests
        ? [new BsonDocument { { "createdBy", 1 }, { "requestKey", 1 } }]
        : [new BsonDocument("requestId", 1), new BsonDocument { { "decidedBy", 1 }, { "requestKey", 1 } }];
    internal static BsonDocument[] SupportKeys(string name) => name == BranchOrderRequestBson.Requests
        ? [new BsonDocument { { "fulfillmentOrganizationId", 1 }, { "shopId", 1 }, { "_id", -1 } },
           new BsonDocument { { "createdBy", 1 }, { "branchId", 1 }, { "_id", -1 } }]
        : [];
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Hash => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Long(long min = 1) => S.Number(min, long.MaxValue - 2);
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Owner() => CatalogSchemas.Validator("skus")["$and"][0]["$jsonSchema"]["properties"]["authoringOwner"].DeepClone().AsBsonDocument;
    internal static BsonDocument Validator(string name)
    {
        if (name == BranchOrderRequestBson.Requests) {
            var line = S.Object(new() { { "skuId", Long() }, { "definitionId", Long() }, { "definitionVersion", Long() },
                { "packages", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1000000 } } },
                { "skuCode", S.Text(64) }, { "familyName", S.Text(160) }, { "packageLabel", S.Text(160) },
                { "packageKind", S.Choice("sealed", "loose", "other") }, { "baseUnitCode", S.Text(64) },
                { "baseUnitsPerPackage", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1000000 } } }, { "owner", Owner() } });
            var fields = new BsonDocument { { "_id", Long() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } }, { "kind", S.Choice("ordering.branchrequest") },
                { "number", S.Text(48) }, { "fence", Long(0) }, { "state", S.Choice("pending", "accepted", "rejected") },
                { "decisionId", Nullable(Long()) }, { "createdBy", Long() }, { "buyingOrganizationId", Long() },
                { "branchId", Long() }, { "divisionId", Nullable(Long()) }, { "fulfillmentOrganizationId", Long() },
                { "shopId", Long() }, { "warehouseId", Nullable(Long()) }, { "channelId", Long() },
                { "input", S.Object(new() { { "context", OrderDraftSchemas.Context() },
                    { "lines", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 100 },
                        { "items", S.Object(new() { { "skuId", Long() }, { "definitionId", Long() }, { "definitionVersion", Long() },
                            { "packages", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1000000 } } } }) } } },
                    { "note", Nullable(S.Text(1000)) } }) },
                { "lines", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 100 }, { "items", line } } },
                { "labels", S.Object(new() { { "buyingOrganization", S.Text(200) }, { "branch", S.Text(200) },
                    { "division", Nullable(S.Text(200)) }, { "fulfillmentOrganization", S.Text(200) }, { "shop", S.Text(200) },
                    { "warehouse", Nullable(S.Text(200)) }, { "channel", S.Text(200) } }) },
                { "recordedAt", Date }, { "actorGrantVersion", Long() }, { "requestKey", S.Text(128) }, { "reason", S.Text(1000) },
                { "correlationId", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash }, { "auditId", Long() } };
            return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("recordedAt"),
                new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray {
                    new BsonDocument("$eq", new BsonArray { "$state", "pending" }),
                    new BsonDocument("$eq", new BsonArray { "$decisionId", BsonNull.Value }) })) });
        }
        if (name == BranchOrderRequestBson.Decisions) {
            var fields = new BsonDocument { { "_id", Long() }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } }, { "kind", S.Choice("ordering.branchrequest.decision") },
                { "requestId", Long() }, { "requestHash", Hash }, { "action", S.Choice("accept", "reject") }, { "binding", Hash },
                { "expectedVersion", Long() }, { "decidedBy", Long() }, { "actorGrantVersion", Long() }, { "recordedAt", Date },
                { "requestKey", S.Text(128) }, { "reason", S.Text(1000) }, { "correlationId", S.Text(128) },
                { "bodyHash", Hash }, { "auditId", Long() } };
            return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("recordedAt"),
                new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$expectedVersion", 1L })) });
        }
        throw new ArgumentException("Unknown request store", nameof(name));
    }
}
