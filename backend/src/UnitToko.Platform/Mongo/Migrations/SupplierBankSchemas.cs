using MongoDB.Bson;
using B = UnitToko.Platform.Procurement.SupplierBankBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class SupplierBankSchemas
{
    internal static readonly string[] Collections = [B.Policies, B.Heads, B.Proposals, B.Observations, B.Decisions, B.Versions];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Boolean => new("bsonType", "bool");
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Array(BsonDocument value, int minimum, int maximum) => new() { { "bsonType", "array" }, { "minItems", minimum }, { "maxItems", maximum }, { "items", value } };
    private static BsonDocument Header() => new() { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
    private static BsonDocument Text(int maximum) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", maximum },
        { "pattern", @"\A[^\p{Z}\s\p{Cc}\p{Cf}](?:[^\p{Cc}\p{Cf}]*[^\p{Z}\s\p{Cc}\p{Cf}])?\z" } };
    private static BsonDocument BankCode() => new() { { "bsonType", "string" }, { "pattern", @"\A[A-Z0-9-]{1,32}\z" } };
    internal static BsonDocument Details() => S.Object(new() { { "bankCode", BankCode() },
        { "accountNumber", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9]{4,34}\z" } } }, { "holderName", Text(160) }, { "currency", S.Choice("IDR") } });
    private static BsonDocument Person() => S.Object(new() { { "userId", S.Number(1) }, { "personId", S.Number(1) }, { "verification", S.Choice("Verified") }, { "version", S.Number(1, long.MaxValue - 1) } });
    internal static BsonDocument Input() => S.Object(new() { { "owner", SupplierSchemas.Owner() }, { "supplierId", S.Number(1) }, { "supplierVersionId", S.Number(1) },
        { "supplierVersion", S.Number(1) }, { "baseVersion", S.Number(0, long.MaxValue - 2) }, { "policyId", S.Number(1) }, { "policyRevision", S.Number(1) }, { "details", Details() } });
    private static BsonDocument PolicyFields() {
        var fields = Header(); fields.AddRange(new BsonDocument { { "owner", SupplierSchemas.Owner() }, { "supplierId", S.Number(1) }, { "supplierVersionId", S.Number(1) }, { "supplierVersion", S.Number(1) },
            { "subjectReference", S.Text(128) }, { "revision", S.Number(1, long.MaxValue - 1) }, { "bankCode", BankCode() }, { "bankName", Text(160) }, { "currency", S.Choice("IDR") },
            { "method", S.Choice("bank-contact", "authenticated-bank-record") }, { "trustedChannelReference", Text(256) }, { "authorizationReference", Text(256) }, { "operatingOwnerReference", Text(256) },
            { "approvalReference", Text(256) }, { "approvedAt", Date }, { "expiresAt", Date }, { "maxObservationAgeSeconds", S.Number(1, 31536000) }, { "allowReferenceOnly", Boolean }, { "active", Boolean } }); return fields;
    }
    internal static BsonDocument Policy() => S.Object(PolicyFields());
    internal static BsonDocument Supplier() => S.Object(new() {
        { "version", SupplierSchemas.Validator("supplierVersions")["$and"][0]["$jsonSchema"].DeepClone() },
        { "proposalId", S.Number(1) }, { "proposalHash", Hash }, { "decisionId", S.Number(1) }, { "decisionHash", Hash }, { "observationId", S.Number(1) }, { "observationHash", Hash }, { "policy", SupplierSchemas.Policy() } });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Policies => [new("_id", 1)],
        B.Heads => [new() { { "owner.organizationId", 1 }, { "owner.shopId", 1 }, { "owner.channelId", 1 }, { "supplierId", 1 } }],
        B.Proposals => [new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Observations or B.Decisions => [new("proposalId", 1), new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, new("auditId", 1)],
        B.Versions => [new() { { "beneficiaryId", 1 }, { "version", 1 } }, new("decisionId", 1)],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        if (!Collections.Contains(name)) throw new ArgumentOutOfRangeException(nameof(name)); var fields = Header(); var checks = new BsonArray(); var dates = new List<string>();
        if (name == B.Policies) {
            fields = PolicyFields(); fields.Add("fence", S.Number(0, long.MaxValue - 1)); dates.AddRange(["approvedAt", "expiresAt"]); checks.Add(Gt("$expiresAt", "$approvedAt"));
        } else {
            fields.AddRange(new BsonDocument { { "owner", SupplierSchemas.Owner() }, { "supplierId", S.Number(1) } });
            if (name == B.Heads) {
                fields.AddRange(new BsonDocument { { "rootProposalId", S.Number(1) }, { "version", S.Number(0, long.MaxValue - 1) }, { "versionId", Nullable(S.Number(1)) },
                    { "fence", S.Number(0, long.MaxValue - 1) }, { "createdAt", Date }, { "updatedAt", Date } }); dates.AddRange(["createdAt", "updatedAt"]);
                checks.Add(Eq(Eq("$version", 0L), Eq("$versionId", BsonNull.Value))); checks.Add(new BsonDocument("$gte", new BsonArray { "$updatedAt", "$createdAt" }));
            } else if (name == B.Versions) {
                fields.AddRange(new BsonDocument { { "beneficiaryId", S.Number(1) }, { "version", S.Number(1, long.MaxValue - 1) }, { "proposalId", S.Number(1) }, { "observationId", S.Number(1) }, { "decisionId", S.Number(1) },
                    { "previousVersionId", Nullable(S.Number(1)) }, { "previousHash", Nullable(Hash) }, { "details", Details() }, { "physicalIdentity", Hash }, { "supplier", Supplier() }, { "policy", Policy() }, { "effectiveAt", Date }, { "snapshotHash", Hash } });
                foreach (var field in new[] { "previousVersionId", "previousHash" }) checks.Add(Eq(Eq("$version", 1L), Eq("$" + field, BsonNull.Value)));
                SupplierChecks(checks, dates, "$supplier.", "effectiveAt"); PolicyChecks(checks, dates, "$policy.", "effectiveAt", "$details.", "$supplier.version."); dates.Add("effectiveAt");
            } else {
                fields.AddRange(new BsonDocument { { "beneficiaryId", S.Number(1) }, { "proposalId", S.Number(1) }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "recordedAt", Date },
                    { "requestKey", Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash }, { "binding", Hash }, { "people", Array(Person(), name == B.Proposals ? 1 : 2, name == B.Decisions ? 3 : name == B.Proposals ? 1 : 2) } });
                fields.Add("reason", Text(1000)); fields.Add("correlationId", Text(128)); fields.Add("auditId", S.Number(1)); dates.Add("recordedAt");
                var users = Map(SafeArray("$people"), "person", "$$person.userId"); checks.Add(Unique(users)); checks.Add(Eq(users, Sort(users))); checks.Add(new BsonDocument("$in", new BsonArray { "$actorId", users }));
                if (name == B.Proposals) {
                    fields.AddRange(new BsonDocument { { "version", S.Number(1, 1) }, { "operation", S.Choice(B.Prefix + "propose") }, { "input", Input() }, { "supplier", Supplier() }, { "sources", SupplierSchemas.Sources() }, { "policy", Policy() } });
                    checks.Add(Eq("$_id", "$proposalId")); checks.Add(Eq("$owner", "$input.owner")); checks.Add(Eq("$supplierId", "$input.supplierId"));
                    checks.Add(Eq("$supplier.version._id", "$input.supplierVersionId")); checks.Add(Eq("$supplier.version.version", "$input.supplierVersion"));
                    checks.Add(Eq("$policy._id", "$input.policyId")); checks.Add(Eq("$policy.revision", "$input.policyRevision"));
                    SupplierChecks(checks, dates, "$supplier.", "recordedAt"); PolicyChecks(checks, dates, "$policy.", "recordedAt", "$input.details.", "$supplier.version."); SourceChecks(checks, dates, "sources");
                } else if (name == B.Observations) {
                    fields.AddRange(new BsonDocument { { "operation", S.Choice(B.Prefix + "observe") }, { "proposalHash", Hash }, { "observed", Details() }, { "sourceReference", Text(256) }, { "policyHash", Hash },
                        { "checkedAt", Date }, { "attested", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { true } } } } }); checks.Add(Eq("$checkedAt", "$recordedAt")); dates.Add("checkedAt");
                    checks.Add(Unique(Map(SafeArray("$people"), "person", "$$person.personId")));
                } else {
                    fields.AddRange(new BsonDocument { { "operation", S.Choice(B.Prefix + "approve", B.Prefix + "reject") }, { "proposalHash", Hash }, { "decision", S.Choice("approve", "reject") },
                        { "observationId", Nullable(S.Number(1)) }, { "versionId", Nullable(S.Number(1)) }, { "bankVersion", Nullable(S.Number(1, long.MaxValue - 1)) }, { "currentVersion", S.Number(0, long.MaxValue - 2) }, { "reviewSources", SupplierSchemas.Sources() } });
                    checks.Add(Eq("$operation", new BsonDocument("$concat", new BsonArray { B.Prefix, "$decision" })));
                    foreach (var field in new[] { "observationId", "versionId", "bankVersion" }) checks.Add(Eq(Eq("$decision", "reject"), Eq("$" + field, BsonNull.Value)));
                    checks.Add(new BsonDocument("$cond", new BsonArray { Eq("$decision", "approve"), Eq("$bankVersion", new BsonDocument("$add", new BsonArray { "$currentVersion", 1L })), true })); SourceChecks(checks, dates, "reviewSources");
                }
            }
        }
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) }; if (checks.Count > 0) terms.Add(new BsonDocument("$expr", new BsonDocument("$and", checks)));
        foreach (var date in dates.Distinct()) terms.Add(S.DateRange(date)); return new("$and", terms);
    }
    private static void SupplierChecks(BsonArray checks, List<string> dates, string prefix, string at) {
        checks.Add(Eq(prefix + "version.supplierId", "$supplierId")); checks.Add(Eq(prefix + "version.owner", "$owner"));
        checks.Add(Eq(prefix + "version.proposalId", prefix + "proposalId")); checks.Add(Eq(prefix + "version.decisionId", prefix + "decisionId")); checks.Add(Eq(prefix + "version.observationId", prefix + "observationId"));
        checks.Add(new BsonDocument("$lte", new BsonArray { prefix + "version.effectiveAt", "$" + at })); dates.Add(prefix[1..] + "version.effectiveAt");
    }
    private static void PolicyChecks(BsonArray checks, List<string> dates, string prefix, string at, string details, string supplier) {
        checks.Add(Eq(prefix + "owner", "$owner")); checks.Add(Eq(prefix + "supplierId", "$supplierId")); checks.Add(Eq(prefix + "supplierVersionId", supplier + "_id")); checks.Add(Eq(prefix + "supplierVersion", supplier + "version"));
        checks.Add(Eq(prefix + "subjectReference", supplier + "details.subjectReference")); checks.Add(Eq(prefix + "bankCode", details + "bankCode")); checks.Add(Eq(prefix + "currency", details + "currency"));
        checks.Add(Eq(prefix + "active", true)); checks.Add(new BsonDocument("$lte", new BsonArray { prefix + "approvedAt", "$" + at })); checks.Add(Gt(prefix + "expiresAt", "$" + at));
        dates.AddRange([prefix[1..] + "approvedAt", prefix[1..] + "expiresAt"]);
    }
    private static void SourceChecks(BsonArray checks, List<string> dates, string prefix) {
        foreach (var (part, kind, id, parent) in new[] { ("organization", "fulfillmentOrganization", "organizationId", (string?)null), ("shop", "shop", "shopId", "organizationId"), ("channel", "channel", "channelId", "organizationId") }) {
            checks.Add(Eq("$" + prefix + "." + part + ".target.id", "$owner." + id)); checks.Add(Eq("$" + prefix + "." + part + ".target.kind", kind));
            checks.Add(Eq("$" + prefix + "." + part + ".target.parentId", parent is null ? BsonNull.Value : "$owner." + parent));
            checks.Add(new BsonDocument("$lte", new BsonArray { "$" + prefix + "." + part + ".profile.approvedAt", "$recordedAt" })); dates.Add(prefix + "." + part + ".profile.approvedAt");
        }
    }
    private static BsonDocument Eq(BsonValue left, BsonValue right) => new("$eq", new BsonArray { left, right });
    private static BsonDocument Gt(BsonValue left, BsonValue right) => new("$gt", new BsonArray { left, right });
    private static BsonDocument SafeArray(string path) => new("$cond", new BsonArray { new BsonDocument("$isArray", path), path, new BsonArray() });
    private static BsonDocument Map(BsonValue input, string variable, BsonValue expression) => new("$map", new BsonDocument { { "input", input }, { "as", variable }, { "in", expression } });
    private static BsonDocument Sort(BsonValue input) => new("$sortArray", new BsonDocument { { "input", input }, { "sortBy", 1 } });
    private static BsonDocument Unique(BsonValue input) => Eq(new BsonDocument("$size", input), new BsonDocument("$size", new BsonDocument("$setUnion", new BsonArray { input, new BsonArray() })));
}
