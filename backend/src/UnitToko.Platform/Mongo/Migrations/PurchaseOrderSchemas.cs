using MongoDB.Bson;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Procurement.PurchaseOrderBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class PurchaseOrderSchemas
{
    internal static readonly string[] Collections = [B.Proposals, B.Decisions, B.Orders, .. ProcurementRequestPurchaseOrderSchemas.Collections];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Nullable(BsonDocument shape) => new("anyOf", new BsonArray { shape, new BsonDocument("bsonType", "null") });
    private static BsonDocument Array(BsonDocument item, int minimum, int maximum) => new() { { "bsonType", "array" }, { "minItems", minimum }, { "maxItems", maximum }, { "items", item } };
    private static BsonDocument Packages(int minimum = 1) => new() { { "bsonType", "int" }, { "minimum", minimum }, { "maximum", 1000000 } };
    private static BsonDocument Money => new() { { "bsonType", "string" }, { "pattern", @"\A(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" } };
    private static BsonDocument BusinessDate => new() { { "bsonType", "string" }, { "pattern", @"\A(?!0000|0001-01-01)([0-9]{4})-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } };
    private static BsonDocument Person => S.Object(new() { { "userId", S.Number(1) }, { "personId", S.Number(1) }, { "verification", S.Choice("Verified") }, { "version", S.Number(1, long.MaxValue - 1) } });
    internal static BsonDocument Input() => S.Object(new() {
        { "location", ProcurementRequestSchemas.Location() }, { "requestId", S.Number(1) }, { "requestVersion", S.Number(1, 1) }, { "requestDecisionId", S.Number(1) },
        { "supplierId", S.Number(1) }, { "supplierVersionId", S.Number(1) }, { "supplierVersion", S.Number(1) }, { "businessDate", BusinessDate }, { "currency", S.Choice("IDR") },
        { "commercialReference", S.Object(new() { { "kind", S.Choice("quote", "order", "invoice") }, { "reference", S.Text(128) }, { "issuedDate", BusinessDate }, { "validUntil", Date } }) },
        { "externalOrderReference", Nullable(S.Text(128)) }, { "paymentTerms", S.Object(new() { { "kind", S.Choice("full_before_dispatch", "deposit", "on_receipt", "credit") },
            { "description", S.Text(1000) }, { "requiredAdvance", Money }, { "balanceTrigger", S.Choice("before_dispatch", "on_receipt", "due_date") }, { "dueDate", Nullable(BusinessDate) } }) },
        { "lines", Array(S.Object(new() { { "requestSkuId", S.Number(1) }, { "packages", Packages() }, { "sourceSystem", S.Choice("omi", "s_mart", "supplier") },
            { "sourceAccountReference", S.Text(128) }, { "externalItemCode", S.Text(128) }, { "definitionId", S.Number(1) }, { "definitionVersion", S.Number(1) },
            { "netPricePerPackage", Money }, { "taxAmount", Money }, { "supplierChargeAmount", Money } }), 1, 100) } });
    internal static BsonDocument SupplierSource() => S.Object(new() {
        { "supplierId", S.Number(1) }, { "versionId", S.Number(1) }, { "version", S.Number(1) }, { "versionHash", Hash }, { "proposalId", S.Number(1) }, { "proposalHash", Hash },
        { "decisionId", S.Number(1) }, { "decisionHash", Hash }, { "subjectReference", S.Text(128) }, { "displayName", S.Text(160) }, { "policyId", S.Number(1) }, { "policyRevision", S.Number(1) }, { "policyHash", Hash },
        { "mappings", Array(S.Object(new() { { "mapping", SupplierSchemas.Mapping() }, { "source", SupplierSchemas.MappingSource() } }), 1, 100) } });
    internal static BsonDocument Capacity() => S.Object(new() { { "requestSkuId", S.Number(1) }, { "requestedPackages", Packages() }, { "committedPackages", Packages(0) },
        { "availablePackages", Packages(0) }, { "headId", Nullable(S.Number(1)) }, { "version", S.Number(0, long.MaxValue - 1) } });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Proposals => [new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Decisions => [new("proposalId", 1), new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Orders => [new("proposalId", 1), new("approvalId", 1)],
        _ => ProcurementRequestPurchaseOrderSchemas.Keys(name) };
    internal static BsonDocument Validator(string name) {
        if (ProcurementRequestPurchaseOrderSchemas.Collections.Contains(name)) return ProcurementRequestPurchaseOrderSchemas.Validator(name);
        if (name is not (B.Proposals or B.Decisions or B.Orders)) throw new ArgumentOutOfRangeException(nameof(name));
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "version", S.Number(1, 1) }, { "proposalId", S.Number(1) }, { "requestId", S.Number(1) }, { "location", ProcurementRequestSchemas.Location() }, { "recordedAt", Date }, { "snapshotHash", Hash } };
        var checks = new BsonArray(); var dates = new List<string> { "recordedAt" };
        if (name == B.Orders) fields.AddRange(new BsonDocument { { "proposalHash", Hash }, { "approvalId", S.Number(1) }, { "approvalHash", Hash }, { "requestDecisionId", S.Number(1) } });
        else {
            fields.AddRange(new BsonDocument { { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "binding", Hash },
                { "people", Array(Person, name == B.Proposals ? 1 : 2, name == B.Proposals ? 1 : 2) } }); S.Metadata(fields);
            var users = Map(SafeArray("$people"), "person", "$$person.userId"); checks.Add(Unique(users));
            checks.Add(Eq(users, new BsonDocument("$sortArray", new BsonDocument { { "input", users }, { "sortBy", 1 } }))); checks.Add(new BsonDocument("$in", new BsonArray { "$actorId", users }));
            if (name == B.Proposals) {
                fields.AddRange(new BsonDocument { { "operation", S.Choice(B.Prefix + "propose") }, { "input", Input() }, { "requestHash", Hash }, { "requestDecisionHash", Hash },
                    { "sources", ProcurementRequestSchemas.Sources() }, { "supplier", SupplierSource() }, { "capacity", Array(Capacity(), 1, 100) } });
                checks.Add(Eq("$_id", "$proposalId")); checks.Add(Eq("$requestId", "$input.requestId")); checks.Add(Eq("$location", "$input.location"));
                checks.Add(Eq("$supplier.supplierId", "$input.supplierId")); checks.Add(Eq("$supplier.versionId", "$input.supplierVersionId")); checks.Add(Eq("$supplier.version", "$input.supplierVersion"));
                checks.Add(Gt("$input.commercialReference.validUntil", "$recordedAt")); dates.Add("input.commercialReference.validUntil");
                checks.Add(new BsonDocument("$lte", new BsonArray { "$input.commercialReference.issuedDate", "$input.businessDate" }));
                checks.Add(new BsonDocument("$lte", new BsonArray { "$input.commercialReference.issuedDate", JakartaDate("$recordedAt") }));
                checks.Add(new BsonDocument("$lte", new BsonArray { "$input.commercialReference.issuedDate", JakartaDate("$input.commercialReference.validUntil") }));
                foreach (var path in new[] { "$input.businessDate", "$input.commercialReference.issuedDate" }) checks.Add(ValidDate(path));
                var due = Eq("$input.paymentTerms.balanceTrigger", "due_date"); checks.Add(Eq(due, new BsonDocument("$ne", new BsonArray { "$input.paymentTerms.dueDate", BsonNull.Value })));
                checks.Add(new BsonDocument("$or", new BsonArray { new BsonDocument("$not", new BsonArray { due }), new BsonDocument("$and", new BsonArray {
                    ValidDate("$input.paymentTerms.dueDate"), new BsonDocument("$gte", new BsonArray { "$input.paymentTerms.dueDate", "$input.businessDate" }) }) }));
                var ids = Map(SafeArray("$input.lines"), "line", "$$line.requestSkuId"); checks.Add(Unique(ids));
                checks.Add(Eq(ids, new BsonDocument("$sortArray", new BsonDocument { { "input", ids }, { "sortBy", 1 } })));
                checks.Add(Eq(ids, Map(SafeArray("$capacity"), "row", "$$row.requestSkuId")));
                checks.Add(Eq(ids, Map(SafeArray("$supplier.mappings"), "row", "$$row.mapping.skuId")));
                var total = new BsonDocument("$sum", Map(SafeArray("$input.lines"), "line", new BsonDocument("$add", new BsonArray {
                    new BsonDocument("$multiply", new BsonArray { Amount("$$line.netPricePerPackage"), "$$line.packages" }), Amount("$$line.taxAmount"), Amount("$$line.supplierChargeAmount") })));
                checks.Add(new BsonDocument("$lte", new BsonArray { total, new BsonDecimal128(UnitToko.Modules.Contracts.DomainValues.MaximumMoney) }));
                var advance = Amount("$input.paymentTerms.requiredAdvance");
                checks.Add(new BsonDocument("$switch", new BsonDocument { { "branches", new BsonArray {
                    Branch("full_before_dispatch", new BsonDocument("$and", new BsonArray { Eq(advance, total), Eq("$input.paymentTerms.balanceTrigger", "before_dispatch") })),
                    Branch("deposit", new BsonDocument("$and", new BsonArray { Gt(advance, new BsonDecimal128(0m)), new BsonDocument("$lt", new BsonArray { advance, total }) })),
                    Branch("on_receipt", new BsonDocument("$and", new BsonArray { Eq(advance, new BsonDecimal128(0m)), Eq("$input.paymentTerms.balanceTrigger", "on_receipt") })),
                    Branch("credit", new BsonDocument("$and", new BsonArray { Eq(advance, new BsonDecimal128(0m)), Eq("$input.paymentTerms.balanceTrigger", "due_date") })) } }, { "default", false } }));
                SourceChecks(checks, dates, "sources"); CapacityChecks(checks, "$capacity");
            } else {
                fields.AddRange(new BsonDocument { { "operation", S.Choice(B.Prefix + "approve", B.Prefix + "reject") }, { "proposalHash", Hash }, { "decision", S.Choice("approve", "reject") },
                    { "orderId", Nullable(S.Number(1)) }, { "orderVersion", Nullable(S.Number(1, 1)) }, { "reviewSources", ProcurementRequestSchemas.Sources() }, { "reviewSupplier", Nullable(SupplierSource()) }, { "reviewCapacity", Array(Capacity(), 0, 100) } });
                checks.Add(Eq("$operation", new BsonDocument("$concat", new BsonArray { B.Prefix, "$decision" })));
                foreach (var field in new[] { "orderId", "orderVersion", "reviewSupplier" }) checks.Add(Eq(Eq("$decision", "reject"), Eq("$" + field, BsonNull.Value)));
                checks.Add(Eq(Eq("$decision", "reject"), Eq(new BsonDocument("$size", SafeArray("$reviewCapacity")), 0)));
                checks.Add(Unique(Map(SafeArray("$people"), "person", "$$person.personId"))); SourceChecks(checks, dates, "reviewSources"); CapacityChecks(checks, "$reviewCapacity");
            }
        }
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) }; if (checks.Count > 0) terms.Add(new BsonDocument("$expr", new BsonDocument("$and", checks)));
        foreach (var date in dates) terms.Add(S.DateRange(date)); return new("$and", terms);
    }
    private static void CapacityChecks(BsonArray checks, string path) => checks.Add(new BsonDocument("$allElementsTrue", new BsonArray { Map(SafeArray(path), "row", new BsonDocument("$and", new BsonArray {
        Eq("$$row.requestedPackages", new BsonDocument("$add", new BsonArray { "$$row.committedPackages", "$$row.availablePackages" })),
        Eq(Eq("$$row.version", 0L), Eq("$$row.headId", BsonNull.Value)), Eq(Eq("$$row.committedPackages", 0), Eq("$$row.version", 0L)) })) }));
    private static void SourceChecks(BsonArray checks, List<string> dates, string prefix) {
        foreach (var (part, kind, id, parent) in new[] { ("organization", "fulfillmentOrganization", "organizationId", (string?)null), ("shop", "shop", "shopId", "organizationId"),
            ("warehouse", "warehouse", "warehouseId", "shopId"), ("channel", "channel", "channelId", "organizationId") }) {
            checks.Add(Eq("$" + prefix + "." + part + ".target.id", "$location." + id)); checks.Add(Eq("$" + prefix + "." + part + ".target.kind", kind));
            checks.Add(Eq("$" + prefix + "." + part + ".target.parentId", parent is null ? BsonNull.Value : "$location." + parent));
            checks.Add(new BsonDocument("$lte", new BsonArray { "$" + prefix + "." + part + ".profile.approvedAt", "$recordedAt" })); dates.Add(prefix + "." + part + ".profile.approvedAt");
        }
    }
    private static BsonDocument Branch(string kind, BsonValue then) => new() { { "case", Eq("$input.paymentTerms.kind", kind) }, { "then", then } };
    private static BsonDocument Amount(string path) => new("$convert", new BsonDocument { { "input", path }, { "to", "decimal" }, { "onError", new BsonDecimal128(-1m) }, { "onNull", new BsonDecimal128(-1m) } });
    private static BsonDocument ValidDate(string path) => new("$ne", new BsonArray { new BsonDocument("$dateFromString", new BsonDocument { { "dateString", path }, { "format", "%Y-%m-%d" }, { "onError", BsonNull.Value }, { "onNull", BsonNull.Value } }), BsonNull.Value });
    private static BsonDocument JakartaDate(string path) => new("$dateToString", new BsonDocument { { "date", path }, { "format", "%Y-%m-%d" }, { "timezone", "Asia/Jakarta" }, { "onNull", "" } });
    private static BsonDocument Eq(BsonValue left, BsonValue right) => new("$eq", new BsonArray { left, right });
    private static BsonDocument Gt(BsonValue left, BsonValue right) => new("$gt", new BsonArray { left, right });
    private static BsonDocument SafeArray(string path) => new("$cond", new BsonArray { new BsonDocument("$isArray", path), path, new BsonArray() });
    private static BsonDocument Map(BsonValue input, string variable, BsonValue expression) => new("$map", new BsonDocument { { "input", input }, { "as", variable }, { "in", expression } });
    private static BsonDocument Unique(BsonValue value) => Eq(new BsonDocument("$size", value), new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { value, new BsonArray() })));
}
