using MongoDB.Bson;
using B = UnitToko.Platform.Procurement.ProcurementFundingBson;
using P = UnitToko.Platform.Procurement.PurchaseOrderBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class ProcurementFundingSchemas
{
    internal static readonly string[] Collections = [B.Policies, B.Requests, B.Decisions, B.Heads, B.Allocations];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Boolean => new("bsonType", "bool");
    private static BsonDocument Nullable(BsonDocument shape) => new("anyOf", new BsonArray { shape, new BsonDocument("bsonType", "null") });
    private static BsonDocument Array(BsonDocument item, int minimum, int maximum) => new() { { "bsonType", "array" }, { "minItems", minimum }, { "maxItems", maximum }, { "items", item } };
    private static BsonDocument Money(bool positive = false) => new() { { "bsonType", "string" }, { "pattern", positive ? @"\A(?!0\.00\z)(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" : @"\A(?:0|[1-9][0-9]{0,14})\.[0-9]{2}\z" } };
    private static BsonDocument BusinessDate => new() { { "bsonType", "string" }, { "pattern", @"\A(?!0000|0001-01-01)([0-9]{4})-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])\z" } };
    private static BsonDocument Person => S.Object(new() { { "userId", S.Number(1) }, { "personId", S.Number(1) }, { "verification", S.Choice("Verified") }, { "version", S.Number(1, long.MaxValue - 1) } });
    private static BsonDocument Header() => new() { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
    internal static BsonDocument Input() => S.Object(new() { { "location", ProcurementRequestSchemas.Location() }, { "orderId", S.Number(1) }, { "orderVersion", S.Number(1, 1) },
        { "orderApprovalId", S.Number(1) }, { "policyId", S.Number(1) }, { "policyRevision", S.Number(1) }, { "amount", Money(true) }, { "businessDate", BusinessDate }, { "neededBy", BusinessDate } });
    private static BsonDocument PolicyFields() { var fields = Header(); fields.AddRange(new BsonDocument { { "location", ProcurementRequestSchemas.Location() }, { "revision", S.Number(1, long.MaxValue - 1) },
        { "eligibleRoutes", Array(S.Choice("omi_regular", "ddo", "smart", "pasar_kering"), 1, 4) }, { "allowInstallments", Boolean },
        { "reserveUndisbursed", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { true } } } }, { "requiresAdditionalBudgetSource", Boolean }, { "requiresAdditionalLimitSource", Boolean },
        { "operatingOwnerReference", S.Text(256) }, { "procedureReference", S.Text(256) }, { "approvalReference", S.Text(256) }, { "approvedAt", Date }, { "expiresAt", Date }, { "active", Boolean } }); return fields; }
    internal static BsonDocument Policy() => S.Object(PolicyFields());
    internal static BsonDocument Capacity() => S.Object(new() { { "orderId", S.Number(1) }, { "orderVersion", S.Number(1, 1) }, { "requiredAdvance", Money(true) },
        { "authorized", Money() }, { "remaining", Money() }, { "version", S.Number(0, long.MaxValue - 1) } });
    internal static BsonDocument OrderSource() => S.Object(new() {
        { "order", PurchaseOrderSchemas.Validator(P.Orders)["$and"][0]["$jsonSchema"].DeepClone() },
        { "proposal", PurchaseOrderSchemas.Validator(P.Proposals)["$and"][0]["$jsonSchema"].DeepClone() },
        { "approval", PurchaseOrderSchemas.Validator(P.Decisions)["$and"][0]["$jsonSchema"].DeepClone() },
        { "request", ProcurementRequestSchemas.Validator(R.Requests)["$and"][0]["$jsonSchema"].DeepClone() },
        { "requestDecision", ProcurementRequestSchemas.Validator(R.Decisions)["$and"][0]["$jsonSchema"].DeepClone() },
        { "allocations", Array(S.Object(new() { { "id", S.Number(1) }, { "hash", Hash } }), 1, 100) } });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Policies => [new("_id", 1)],
        B.Requests => [new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Decisions => [new("requestId", 1), new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Heads => [new("orderId", 1)],
        B.Allocations => [new("requestId", 1), new("decisionId", 1), new() { { "orderId", 1 }, { "sequence", 1 } }],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        if (!Collections.Contains(name)) throw new ArgumentOutOfRangeException(nameof(name)); var fields = Header(); var checks = new BsonArray(); var dates = new List<string>();
        if (name == B.Policies) {
            fields = PolicyFields(); fields.Add("fence", S.Number(0, long.MaxValue - 1)); PolicyChecks(checks, "$"); dates.AddRange(["approvedAt", "expiresAt"]);
        } else if (name is B.Heads or B.Allocations) {
            fields.AddRange(new BsonDocument { { "orderId", S.Number(1) }, { "orderVersion", S.Number(1, 1) }, { "orderHash", Hash }, { "orderApprovalId", S.Number(1) } });
            if (name == B.Heads) {
                fields.AddRange(new BsonDocument { { "requiredAdvance", Money(true) }, { "authorized", Money(true) }, { "version", S.Number(1, long.MaxValue - 1) },
                    { "fence", S.Number(0, long.MaxValue - 1) }, { "createdAt", Date }, { "updatedAt", Date } });
                checks.Add(new BsonDocument("$lte", new BsonArray { Amount("$authorized"), Amount("$requiredAdvance") }));
                checks.Add(new BsonDocument("$gte", new BsonArray { "$updatedAt", "$createdAt" })); dates.AddRange(["createdAt", "updatedAt"]);
            } else {
                fields.AddRange(new BsonDocument { { "requestId", S.Number(1) }, { "requestHash", Hash }, { "decisionId", S.Number(1) }, { "decisionHash", Hash }, { "amount", Money(true) },
                    { "sequence", S.Number(1, long.MaxValue - 1) }, { "previousAllocationId", Nullable(S.Number(1)) }, { "previousHash", Nullable(Hash) }, { "recordedAt", Date }, { "snapshotHash", Hash } });
                foreach (var field in new[] { "previousAllocationId", "previousHash" }) checks.Add(Eq(Eq("$sequence", 1L), Eq("$" + field, BsonNull.Value))); dates.Add("recordedAt");
            }
        } else {
            fields.AddRange(new BsonDocument { { "requestId", S.Number(1) }, { "version", S.Number(1, 1) }, { "orderId", S.Number(1) }, { "location", ProcurementRequestSchemas.Location() },
                { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "recordedAt", Date }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash }, { "binding", Hash },
                { "people", Array(Person, name == B.Requests ? 1 : 2, name == B.Requests ? 1 : 2) } }); S.Metadata(fields); dates.Add("recordedAt");
            var users = Map(SafeArray("$people"), "person", "$$person.userId"); checks.Add(Unique(users)); checks.Add(Eq(users, Sort(users))); checks.Add(new BsonDocument("$in", new BsonArray { "$actorId", users }));
            if (name == B.Requests) {
                fields.AddRange(new BsonDocument { { "operation", S.Choice(B.Prefix + "request") }, { "input", Input() }, { "orderSource", OrderSource() },
                    { "sources", ProcurementRequestSchemas.Sources() }, { "policy", Policy() }, { "capacity", Capacity() } });
                checks.Add(Eq("$_id", "$requestId")); checks.Add(Eq("$orderId", "$input.orderId")); checks.Add(Eq("$location", "$input.location")); checks.Add(Eq("$location", "$policy.location"));
                checks.Add(Eq("$policy._id", "$input.policyId")); checks.Add(Eq("$policy.revision", "$input.policyRevision")); checks.Add(Eq("$orderSource.order._id", "$orderId"));
                checks.Add(Eq("$orderSource.order.version", "$input.orderVersion")); checks.Add(Eq("$orderSource.order.approvalId", "$input.orderApprovalId")); checks.Add(Eq("$orderSource.order.location", "$location"));
                checks.Add(Eq("$orderSource.approval.decision", "approve")); checks.Add(Eq("$orderSource.order.approvalId", "$orderSource.approval._id")); checks.Add(Eq("$orderSource.order.proposalId", "$orderSource.proposal._id"));
                checks.Add(Eq("$orderSource.order.approvalHash", "$orderSource.approval.snapshotHash")); checks.Add(Eq("$orderSource.order.proposalHash", "$orderSource.proposal.snapshotHash"));
                checks.Add(Eq("$capacity.orderId", "$orderId")); checks.Add(Eq("$capacity.orderVersion", "$input.orderVersion")); checks.Add(Eq("$capacity.requiredAdvance", "$orderSource.proposal.input.paymentTerms.requiredAdvance"));
                checks.Add(new BsonDocument("$lte", new BsonArray { "$orderSource.order.recordedAt", "$recordedAt" }));
                checks.Add(new BsonDocument("$gte", new BsonArray { "$input.neededBy", "$input.businessDate" })); checks.Add(ValidDate("$input.businessDate")); checks.Add(ValidDate("$input.neededBy"));
                PolicyChecks(checks, "$policy."); checks.Add(Eq("$policy.active", true)); checks.Add(Eq("$policy.requiresAdditionalBudgetSource", false)); checks.Add(Eq("$policy.requiresAdditionalLimitSource", false));
                checks.Add(new BsonDocument("$in", new BsonArray { "$orderSource.request.input.route", SafeArray("$policy.eligibleRoutes") }));
                checks.Add(new BsonDocument("$lte", new BsonArray { "$policy.approvedAt", "$recordedAt" })); checks.Add(Gt("$policy.expiresAt", "$recordedAt"));
                checks.Add(new BsonDocument("$lte", new BsonArray { Amount("$input.amount"), Amount("$capacity.remaining") }));
                checks.Add(new BsonDocument("$or", new BsonArray { "$policy.allowInstallments", new BsonDocument("$and", new BsonArray { Eq(Amount("$capacity.authorized"), new BsonDecimal128(0m)), Eq("$input.amount", "$capacity.requiredAdvance") }) }));
                dates.AddRange(["policy.approvedAt", "policy.expiresAt"]); CapacityChecks(checks, "$capacity."); SourceChecks(checks, dates, "sources");
            } else {
                fields.AddRange(new BsonDocument { { "operation", S.Choice(B.Prefix + "approve", B.Prefix + "reject") }, { "requestHash", Hash }, { "decision", S.Choice("approve", "reject") },
                    { "authorizationId", Nullable(S.Number(1)) }, { "authorizedAmount", Nullable(Money(true)) }, { "reviewSources", ProcurementRequestSchemas.Sources() }, { "reviewPolicy", Nullable(Policy()) }, { "reviewCapacity", Nullable(Capacity()) } });
                checks.Add(Eq("$operation", new BsonDocument("$concat", new BsonArray { B.Prefix, "$decision" })));
                foreach (var field in new[] { "authorizationId", "authorizedAmount", "reviewPolicy", "reviewCapacity" }) checks.Add(Eq(Eq("$decision", "reject"), Eq("$" + field, BsonNull.Value)));
                checks.Add(Unique(Map(SafeArray("$people"), "person", "$$person.personId"))); var approval = new BsonArray();
                CapacityChecks(approval, "$reviewCapacity."); PolicyChecks(approval, "$reviewPolicy.");
                approval.Add(Eq("$reviewCapacity.orderId", "$orderId")); approval.Add(Eq("$reviewPolicy.location", "$location")); approval.Add(Eq("$reviewPolicy.active", true));
                approval.Add(Eq("$reviewPolicy.requiresAdditionalBudgetSource", false)); approval.Add(Eq("$reviewPolicy.requiresAdditionalLimitSource", false));
                approval.Add(new BsonDocument("$lte", new BsonArray { "$reviewPolicy.approvedAt", "$recordedAt" })); approval.Add(Gt("$reviewPolicy.expiresAt", "$recordedAt"));
                approval.Add(new BsonDocument("$lte", new BsonArray { Amount("$authorizedAmount"), Amount("$reviewCapacity.remaining") }));
                approval.Add(new BsonDocument("$or", new BsonArray { "$reviewPolicy.allowInstallments", new BsonDocument("$and", new BsonArray { Eq(Amount("$reviewCapacity.authorized"), new BsonDecimal128(0m)), Eq("$authorizedAmount", "$reviewCapacity.requiredAdvance") }) }));
                checks.Add(new BsonDocument("$cond", new BsonArray { Eq("$decision", "approve"), new BsonDocument("$and", approval), true })); SourceChecks(checks, dates, "reviewSources");
            }
        }
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) }; if (checks.Count > 0) terms.Add(new BsonDocument("$expr", new BsonDocument("$and", checks)));
        foreach (var date in dates) terms.Add(S.DateRange(date)); return new("$and", terms);
    }
    private static void PolicyChecks(BsonArray checks, string prefix) { checks.Add(Gt(prefix + "expiresAt", prefix + "approvedAt"));
        var routes = SafeArray(prefix + "eligibleRoutes"); checks.Add(Unique(routes)); checks.Add(Eq(routes, Sort(routes))); }
    private static void CapacityChecks(BsonArray checks, string prefix) {
        checks.Add(Eq(Amount(prefix + "requiredAdvance"), new BsonDocument("$add", new BsonArray { Amount(prefix + "authorized"), Amount(prefix + "remaining") })));
        checks.Add(Eq(Eq(prefix + "version", 0L), Eq(Amount(prefix + "authorized"), new BsonDecimal128(0m))));
    }
    private static void SourceChecks(BsonArray checks, List<string> dates, string prefix) {
        foreach (var (part, kind, id, parent) in new[] { ("organization", "fulfillmentOrganization", "organizationId", (string?)null), ("shop", "shop", "shopId", "organizationId"),
            ("warehouse", "warehouse", "warehouseId", "shopId"), ("channel", "channel", "channelId", "organizationId") }) {
            checks.Add(Eq("$" + prefix + "." + part + ".target.id", "$location." + id)); checks.Add(Eq("$" + prefix + "." + part + ".target.kind", kind));
            checks.Add(Eq("$" + prefix + "." + part + ".target.parentId", parent is null ? BsonNull.Value : "$location." + parent));
            checks.Add(new BsonDocument("$lte", new BsonArray { "$" + prefix + "." + part + ".profile.approvedAt", "$recordedAt" })); dates.Add(prefix + "." + part + ".profile.approvedAt");
        }
    }
    private static BsonDocument Eq(BsonValue left, BsonValue right) => new("$eq", new BsonArray { left, right });
    private static BsonDocument Gt(BsonValue left, BsonValue right) => new("$gt", new BsonArray { left, right });
    private static BsonDocument SafeArray(string path) => new("$cond", new BsonArray { new BsonDocument("$isArray", path), path, new BsonArray() });
    private static BsonDocument Map(BsonValue input, string variable, BsonValue expression) => new("$map", new BsonDocument { { "input", input }, { "as", variable }, { "in", expression } });
    private static BsonDocument Sort(BsonValue array) => new("$sortArray", new BsonDocument { { "input", array }, { "sortBy", 1 } });
    private static BsonDocument Unique(BsonValue array) => Eq(new BsonDocument("$size", array), new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { array, new BsonArray() })));
    private static BsonDocument Amount(string path) => new("$convert", new BsonDocument { { "input", path }, { "to", "decimal" }, { "onError", new BsonDecimal128(-1m) }, { "onNull", new BsonDecimal128(-1m) } });
    private static BsonDocument ValidDate(string path) => new("$ne", new BsonArray { new BsonDocument("$dateFromString", new BsonDocument { { "dateString", path }, { "format", "%Y-%m-%d" }, { "onError", BsonNull.Value }, { "onNull", BsonNull.Value } }), BsonNull.Value });
}
