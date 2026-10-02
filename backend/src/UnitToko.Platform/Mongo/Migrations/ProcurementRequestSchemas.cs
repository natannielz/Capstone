using MongoDB.Bson;
using B = UnitToko.Platform.Procurement.ProcurementRequestBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class ProcurementRequestSchemas
{
    internal static readonly string[] Collections = [B.Requests, B.Decisions];
    private static BsonDocument Format => new() { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } };
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Array(BsonDocument item, int min, int max) => new() { { "bsonType", "array" }, { "minItems", min }, { "maxItems", max }, { "items", item } };
    internal static BsonDocument Location() => S.Object(new() { { "organizationId", S.Number(1) }, { "shopId", S.Number(1) }, { "warehouseId", S.Number(1) }, { "channelId", S.Number(1) } });
    internal static BsonDocument Input() => S.Object(new() { { "location", Location() }, { "route", S.Choice("omi_regular", "ddo", "smart", "pasar_kering") },
        { "kind", S.Choice("stock_need", "incoming_notice", "purchase_request") },
        { "businessDate", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A(?!0000|0001-01-01)([0-9]{4})-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } } },
        { "externalReference", Nullable(S.Text(128)) }, { "reason", S.Text(1000) }, { "lines", Array(S.Object(new() {
            { "skuId", S.Number(1) }, { "expectedSkuRevision", S.Number(1) }, { "definitionId", S.Number(1) }, { "definitionVersion", S.Number(1) },
            { "packages", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 1000000 } } }, { "specification", Nullable(S.Text(1000)) } }), 1, 100) } });
    internal static BsonDocument Sources() => S.Object(new(B.SourceNames.Select(name => new BsonElement(name, OrganizationAddressSchemas.Source()))));
    private static BsonDocument CatalogShape(string name) => CatalogSchemas.Validator(name)["$and"][0]["$jsonSchema"].DeepClone().AsBsonDocument;
    internal static BsonDocument Catalog() => S.Object(new() { { "sku", CatalogShape("skus") }, { "family", CatalogShape("productFamilies") },
        { "category", CatalogShape("catalogCategories") }, { "definition", CatalogShape("variantDefinitions") },
        { "audits", S.Object(new() { { "sku", S.Number(1) }, { "family", S.Number(1) }, { "category", S.Number(1) } }) } });
    private static BsonDocument Person => S.Object(new() { { "userId", S.Number(1) }, { "personId", S.Number(1) }, { "verification", S.Choice("Verified") }, { "version", S.Number(1, long.MaxValue - 1) } });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Requests => [new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Decisions => [new("requestId", 1), new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        if (name is not (B.Requests or B.Decisions)) throw new ArgumentOutOfRangeException(nameof(name));
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", Format }, { "requestId", S.Number(1) }, { "version", S.Number(1, 1) },
            { "location", Location() }, { "sources", Sources() }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "recordedAt", Date },
            { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "binding", Hash }, { "snapshotHash", Hash }, { "people", Array(Person, name == B.Requests ? 1 : 2, name == B.Requests ? 1 : 2) } };
        S.Metadata(fields); var checks = new BsonArray(); var dates = new List<string> { "recordedAt" }; var catalogPath = name == B.Requests ? "$catalog" : "$reviewCatalog";
        if (name == B.Requests) {
            fields.Add("operation", S.Choice(B.Prefix + "submit")); fields.Add("input", Input()); fields.Add("catalog", Array(Catalog(), 1, 100));
            checks.Add(Eq("$_id", "$requestId")); checks.Add(Eq("$location", "$input.location")); checks.Add(Eq("$reason", "$input.reason")); checks.Add(Eq("$actorId", Member("userId", 0)));
            checks.Add(new BsonDocument("$cond", new BsonArray { Eq("$input.route", "omi_regular"),
                new BsonDocument("$in", new BsonArray { "$input.kind", new BsonArray { "stock_need", "incoming_notice" } }), Eq("$input.kind", "purchase_request") }));
            checks.Add(new BsonDocument("$or", new BsonArray { new BsonDocument("$ne", new BsonArray { "$input.kind", "incoming_notice" }),
                new BsonDocument("$ne", new BsonArray { "$input.externalReference", BsonNull.Value }) }));
            checks.Add(new BsonDocument("$ne", new BsonArray { new BsonDocument("$dateFromString", new BsonDocument {
                { "dateString", "$input.businessDate" }, { "format", "%Y-%m-%d" }, { "onError", BsonNull.Value }, { "onNull", BsonNull.Value } }), BsonNull.Value }));
            var ids = Map(SafeArray("$input.lines"), "line", "$$line.skuId"); checks.Add(Unique(ids));
            checks.Add(Eq(ids, new BsonDocument("$sortArray", new BsonDocument { { "input", ids }, { "sortBy", 1 } })));
            checks.Add(Eq(Map(SafeArray("$input.lines"), "line", new BsonDocument { { "skuId", "$$line.skuId" }, { "revision", "$$line.expectedSkuRevision" }, { "definitionId", "$$line.definitionId" }, { "definitionVersion", "$$line.definitionVersion" } }),
                Map(SafeArray(catalogPath), "source", new BsonDocument { { "skuId", "$$source.sku._id" }, { "revision", "$$source.sku.revision" }, { "definitionId", "$$source.definition._id" }, { "definitionVersion", "$$source.definition.version" } })));
        } else {
            fields.AddRange(new BsonDocument { { "operation", S.Choice(B.Prefix + "approve", B.Prefix + "reject") }, { "decision", S.Choice("approve", "reject") },
                { "requestHash", Hash }, { "reviewSources", Sources() }, { "reviewCatalog", Array(Catalog(), 0, 100) } });
            checks.Add(Eq("$operation", new BsonDocument("$concat", new BsonArray { B.Prefix, "$decision" })));
            checks.Add(Eq(Eq("$decision", "approve"), new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", SafeArray(catalogPath)), 0 })));
            checks.Add(new BsonDocument("$lt", new BsonArray { Member("userId", 0), Member("userId", 1) }));
            checks.Add(new BsonDocument("$ne", new BsonArray { Member("personId", 0), Member("personId", 1) }));
            checks.Add(new BsonDocument("$in", new BsonArray { "$actorId", SafeArray("$people.userId") }));
            checks.Add(new BsonDocument("$or", new BsonArray { Eq("$decision", "reject"), Eq("$reviewSources", "$sources") }));
        }
        foreach (var (part, kind, id, parent) in new[] { ("organization", "fulfillmentOrganization", "organizationId", (string?)null),
            ("shop", "shop", "shopId", "organizationId"), ("warehouse", "warehouse", "warehouseId", "shopId"), ("channel", "channel", "channelId", "organizationId") })
            foreach (var prefix in name == B.Requests ? new[] { "sources" } : new[] { "sources", "reviewSources" }) {
                checks.Add(Eq("$" + prefix + "." + part + ".target.id", "$location." + id)); checks.Add(Eq("$" + prefix + "." + part + ".target.kind", kind));
                checks.Add(Eq("$" + prefix + "." + part + ".target.parentId", parent is null ? BsonNull.Value : "$location." + parent));
                checks.Add(new BsonDocument("$lte", new BsonArray { "$" + prefix + "." + part + ".profile.approvedAt", "$recordedAt" })); dates.Add(prefix + "." + part + ".profile.approvedAt");
            }
        var catalogChecks = new BsonArray { Eq("$$source.sku.familyId", "$$source.family._id"), Eq("$$source.family.categoryId", "$$source.category._id"),
            Eq("$$source.family.categoryCode", "$$source.category.code"), Eq("$$source.sku.currentDefinitionId", "$$source.definition._id"), Eq("$$source.sku._id", "$$source.definition.skuId"),
            Eq("$$source.sku.authoringOwner", "$$source.family.authoringOwner"), Eq("$$source.sku.authoringOwner", "$$source.category.authoringOwner"),
            Eq("$$source.sku.authoringOwner.organizationId", "$location.organizationId"), Eq("$$source.sku.authoringOwner.shopId", "$location.shopId"),
            Eq("$$source.sku.authoringOwner.channelId", "$location.channelId"), new BsonDocument("$or", new BsonArray {
                Eq("$$source.sku.authoringOwner.kind", "fulfillmentShop"), Eq("$$source.sku.authoringOwner.warehouseId", "$location.warehouseId") }),
            new BsonDocument("$lte", new BsonArray { "$$source.definition.effectiveAt", "$recordedAt" }) };
        foreach (var part in new[] { "sku", "family", "category" }) { catalogChecks.Add(Eq("$$source." + part + ".active", true)); catalogChecks.Add(Eq("$$source." + part + ".fence", 0L)); }
        checks.Add(All(Map(SafeArray(catalogPath), "source", new BsonDocument("$and", catalogChecks))));
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), new BsonDocument("$expr", new BsonDocument("$and", checks)) };
        foreach (var date in dates) terms.Add(S.DateRange(date)); return new("$and", terms);
    }
    private static BsonDocument Eq(BsonValue left, BsonValue right) => new("$eq", new BsonArray { left, right });
    private static BsonDocument SafeArray(string path) => new("$cond", new BsonArray { new BsonDocument("$isArray", path), path, new BsonArray() });
    private static BsonDocument Map(BsonValue input, string variable, BsonValue expression) => new("$map", new BsonDocument { { "input", input }, { "as", variable }, { "in", expression } });
    private static BsonDocument All(BsonValue value) => new("$allElementsTrue", new BsonArray { value });
    private static BsonDocument Unique(BsonValue value) => Eq(new BsonDocument("$size", value), new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { value, new BsonArray() })));
    private static BsonDocument Member(string field, int index) => new("$arrayElemAt", new BsonArray { SafeArray("$people." + field), index });
}
