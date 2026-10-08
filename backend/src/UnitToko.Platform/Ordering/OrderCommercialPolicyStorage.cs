using MongoDB.Bson;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderCommercialPolicyBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Ordering;

internal static class OrderCommercialPolicySchemas
{
    internal static readonly string[] Collections = [B.Heads, B.Proposals, B.Decisions, B.Versions];
    private static BsonDocument Null => new("bsonType", "null");
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, Null });
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Format => new() { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } };
    internal static BsonDocument Target() => S.Object(new() { { "kind", S.Choice("branch", "division") }, { "id", S.Number(1) },
        { "seller", S.Object(new() { { "organizationId", S.Number(1) }, { "shopId", S.Number(1) }, { "channelId", S.Number(1) }, { "currency", S.Choice("IDR") } }) } });
    private static BsonDocument Money() => new() { { "bsonType", "decimal" }, { "minimum", new BsonDecimal128(0m) },
        { "maximum", new BsonDecimal128(UnitToko.Modules.Contracts.DomainValues.MaximumMoney) }, { "multipleOf", new BsonDecimal128(.01m) } };
    internal static BsonDocument Rule() => S.Object(new() {
        { "applicability", S.Object(new() { { "budget", S.Choice("required", "not_applicable") }, { "costCenter", S.Choice("required", "not_applicable") },
            { "spending", S.Choice("required", "not_applicable") }, { "creditHold", S.Choice("required", "not_applicable") } }) },
        { "charges", Money() }, { "taxTreatment", S.Choice("taxable_exclusive", "exempt") },
        { "taxRate", new BsonDocument { { "bsonType", "decimal" }, { "minimum", new BsonDecimal128(0m) }, { "maximum", new BsonDecimal128(100m) }, { "multipleOf", new BsonDecimal128(.0001m) } } },
        { "exemptionReference", Nullable(S.Text(1000)) }, { "taxBase", S.Choice("subtotal", "subtotal_and_charges") },
        { "taxRounding", S.Choice("line", "document") }, { "rounding", S.Choice("half_even", "half_up") },
        { "terms", S.Object(new() { { "grouping", S.Choice("receipt", "buying_branch_period_channel", "buying_org_period_channel") },
            { "termOrigin", S.Choice("issue_date", "earliest_receipt", "latest_receipt") }, { "termDays", new BsonDocument { { "bsonType", "int" }, { "minimum", 0 }, { "maximum", 365 } } },
            { "dayRule", S.Choice("calendar_days", "working_days") },
            { "workingDays", new BsonDocument { { "bsonType", "array" }, { "maxItems", 7 }, { "uniqueItems", true }, { "items", new BsonDocument { { "bsonType", "int" }, { "minimum", 0 }, { "maximum", 6 } } } } },
            { "holidays", new BsonDocument { { "bsonType", "array" }, { "maxItems", 730 }, { "uniqueItems", true }, { "items", new BsonDocument { { "bsonType", "string" }, { "pattern", "^[0-9]{4}-[0-9]{2}-[0-9]{2}$(?![\\s\\S])" } } } } },
            { "numberingProcedure", S.Text(1000) }, { "reissueProcedure", S.Text(1000) }, { "taxDocumentProcess", S.Text(1000) } }) } });
    private static BsonDocument SellerVersions() => S.Object(new() {
        { "organizationScopeVersion", S.Number(1, long.MaxValue - 1) }, { "organizationProfileVersion", S.Number(1, long.MaxValue - 1) },
        { "shopScopeVersion", S.Number(1, long.MaxValue - 1) }, { "shopProfileVersion", S.Number(1, long.MaxValue - 1) },
        { "channelScopeVersion", S.Number(1, long.MaxValue - 1) }, { "channelProfileVersion", S.Number(1, long.MaxValue - 1) } });
    internal static BsonDocument Details() => S.Object(new() { { "rule", Rule() }, { "validFrom", Date }, { "validUntil", Date } });
    internal static BsonDocument Input() => S.Object(new() { { "target", Target() },
        { "expectedScopeVersion", S.Number(1, long.MaxValue - 1) }, { "expectedProfileVersion", S.Number(1, long.MaxValue - 1) },
        { "sellerVersions", SellerVersions() }, { "expectedPolicyVersion", S.Number(0, long.MaxValue - 2) }, { "rule", Rule() }, { "validFrom", Date }, { "validUntil", Date } });
    internal static BsonDocument Source() => OrganizationAddressSchemas.Source();
    private static BsonDocument Person() => S.Object(new() { { "userId", S.Number(1) }, { "personId", S.Number(1) },
        { "verification", S.Choice("Verified") }, { "version", S.Number(1, long.MaxValue - 1) } });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Heads => [new() { { "target.kind", 1 }, { "target.id", 1 }, { "target.seller.organizationId", 1 }, { "target.seller.shopId", 1 }, { "target.seller.channelId", 1 }, { "target.seller.currency", 1 } }],
        B.Proposals => [new() { { "actorId", 1 }, { "requestKey", 1 } }],
        B.Decisions => [new("proposalId", 1), new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }],
        B.Versions => [new() { { "policyId", 1 }, { "version", 1 } }, new("proposalId", 1), new("decisionId", 1)],
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", Format }, { "target", Target() } };
        var dates = new List<string>(); var checks = new BsonArray();
        if (name == B.Heads) {
            fields.AddRange(new BsonDocument { { "rootProposalId", S.Number(1) }, { "version", S.Number(0, long.MaxValue - 1) },
                { "versionId", Nullable(S.Number(1)) }, { "createdAt", Date }, { "updatedAt", Date }, { "fence", S.Number(0, long.MaxValue - 1) } });
            dates.AddRange(["createdAt", "updatedAt"]);
            checks.Add(new BsonDocument("$or", new BsonArray {
                And(Eq("$version", 0L), Eq("$versionId", BsonNull.Value)),
                And(new("$gte", new BsonArray { "$version", 1L }), new("$ne", new BsonArray { "$versionId", BsonNull.Value })) }));
            checks.Add(new BsonDocument("$gte", new BsonArray { "$updatedAt", "$createdAt" }));
        } else {
            fields.AddRange(new BsonDocument { { "policyId", S.Number(1) }, { "proposalId", S.Number(1) }, { "source", Source() },
                { "sellerSources", S.Object(new() { { "organization", Source() }, { "shop", Source() }, { "channel", Source() } }) }, { "details", Details() }, { "snapshotHash", Hash } });
            dates.AddRange(["source.profile.approvedAt", "details.validFrom", "details.validUntil"]);
            checks.Add(new BsonDocument("$lt", new BsonArray { "$details.validFrom", "$details.validUntil" }));
            foreach (var role in new[] { "organization", "shop", "channel" }) dates.Add("sellerSources." + role + ".profile.approvedAt");
            checks.Add(new BsonDocument("$or", new BsonArray {
                And(Eq("$details.rule.taxTreatment", "taxable_exclusive"), new("$gt", new BsonArray { "$details.rule.taxRate", new BsonDecimal128(0m) }), Eq("$details.rule.exemptionReference", BsonNull.Value)),
                And(Eq("$details.rule.taxTreatment", "exempt"), Eq("$details.rule.taxRate", new BsonDecimal128(0m)), new("$ne", new BsonArray { "$details.rule.exemptionReference", BsonNull.Value })) }));
            checks.Add(Eq("$target.id", "$source.target.id")); checks.Add(Eq("$target.kind", "$source.target.kind"));
            checks.Add(Eq("$source.profile.id", "$source.target.id")); checks.Add(Eq("$source.profile.kind", "$source.target.kind"));
            checks.Add(Eq("$source.profile.parentId", "$source.target.parentId"));
            if (name == B.Versions) {
                fields.AddRange(new BsonDocument { { "version", S.Number(1, long.MaxValue - 1) }, { "decisionId", S.Number(1) },
                    { "previousVersionId", Nullable(S.Number(1)) }, { "previousHash", Nullable(Hash) }, { "effectiveAt", Date } }); dates.Add("effectiveAt");
                checks.Add(new BsonDocument("$or", new BsonArray {
                    And(Eq("$version", 1L), Eq("$previousVersionId", BsonNull.Value), Eq("$previousHash", BsonNull.Value)),
                    And(new("$gte", new BsonArray { "$version", 2L }), new("$ne", new BsonArray { "$previousVersionId", BsonNull.Value }),
                        new("$ne", new BsonArray { "$previousHash", BsonNull.Value })) }));
                checks.Add(new BsonDocument("$lte", new BsonArray { "$source.profile.approvedAt", "$effectiveAt" }));
                foreach (var role in new[] { "organization", "shop", "channel" }) checks.Add(new BsonDocument("$lte", new BsonArray { "$sellerSources." + role + ".profile.approvedAt", "$effectiveAt" }));
                checks.Add(new BsonDocument("$lt", new BsonArray { "$effectiveAt", "$details.validUntil" }));
            } else if (name is B.Proposals or B.Decisions) {
                fields.AddRange(new BsonDocument { { "revision", S.Number(1, 1) }, { "baseVersion", S.Number(0, long.MaxValue - 2) },
                    { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "recordedAt", Date }, { "requestKey", S.Text(128) },
                    { "bodyHash", Hash }, { "binding", Hash }, { "people", new BsonDocument { { "bsonType", "array" },
                        { "minItems", name == B.Proposals ? 1 : 2 }, { "maxItems", name == B.Proposals ? 1 : 2 }, { "items", Person() } } } });
                S.Metadata(fields); dates.Add("recordedAt");
                checks.Add(new BsonDocument("$lte", new BsonArray { "$source.profile.approvedAt", "$recordedAt" }));
                foreach (var role in new[] { "organization", "shop", "channel" }) checks.Add(new BsonDocument("$lte", new BsonArray { "$sellerSources." + role + ".profile.approvedAt", "$recordedAt" }));
                if (name == B.Proposals) {
                    fields.Add("operation", S.Choice(B.Prefix + "propose")); fields.Add("input", Input());
                    checks.Add(new BsonDocument("$lt", new BsonArray { "$recordedAt", "$details.validUntil" }));
                    checks.Add(Eq("$_id", "$proposalId")); checks.Add(Eq("$target", "$input.target"));
                    checks.Add(Eq("$details.rule", "$input.rule")); checks.Add(Eq("$details.validFrom", "$input.validFrom")); checks.Add(Eq("$details.validUntil", "$input.validUntil")); checks.Add(Eq("$baseVersion", "$input.expectedPolicyVersion"));
                    checks.Add(Eq("$source.target.version", "$input.expectedScopeVersion")); checks.Add(Eq("$source.profile.version", "$input.expectedProfileVersion"));
                    foreach (var role in new[] { "organization", "shop", "channel" }) {
                        checks.Add(Eq("$sellerSources." + role + ".target.version", "$input.sellerVersions." + role + "ScopeVersion"));
                        checks.Add(Eq("$sellerSources." + role + ".profile.version", "$input.sellerVersions." + role + "ProfileVersion"));
                    }
                    checks.Add(Eq("$actorId", Member("userId", 0)));
                } else {
                    fields.AddRange(new BsonDocument { { "operation", S.Choice(B.Prefix + "approve", B.Prefix + "reject") }, { "decision", S.Choice("approve", "reject") },
                        { "versionId", Nullable(S.Number(1)) }, { "policyVersion", Nullable(S.Number(1, long.MaxValue - 1)) },
                        { "input", S.Object(new() { { "proposalId", S.Number(1) }, { "expectedRevision", S.Number(1, 1) }, { "decision", S.Choice("approve", "reject") } }) } });
                    checks.Add(Eq("$proposalId", "$input.proposalId")); checks.Add(Eq("$decision", "$input.decision"));
                    checks.Add(new BsonDocument("$lt", new BsonArray { Member("userId", 0), Member("userId", 1) }));
                    checks.Add(new BsonDocument("$ne", new BsonArray { Member("personId", 0), Member("personId", 1) }));
                    checks.Add(new BsonDocument("$in", new BsonArray { "$actorId", new BsonDocument("$cond", new BsonArray {
                        new BsonDocument("$isArray", "$people.userId"), "$people.userId", new BsonArray() }) }));
                    checks.Add(new BsonDocument("$or", new BsonArray {
                        And(Eq("$decision", "approve"), Eq("$operation", B.Prefix + "approve"), new("$ne", new BsonArray { "$versionId", BsonNull.Value }),
                            Eq("$policyVersion", new BsonDocument("$add", new BsonArray { "$baseVersion", 1L })), new("$lt", new BsonArray { "$recordedAt", "$details.validUntil" })),
                        And(Eq("$decision", "reject"), Eq("$operation", B.Prefix + "reject"), Eq("$versionId", BsonNull.Value), Eq("$policyVersion", BsonNull.Value)) }));
                }
            } else throw new ArgumentOutOfRangeException(nameof(name));
        }
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) };
        foreach (var date in dates) terms.Add(S.DateRange(date));
        if (checks.Count > 0) terms.Add(new BsonDocument("$expr", new BsonDocument("$and", checks)));
        return new("$and", terms);
    }
    private static BsonDocument Eq(BsonValue left, BsonValue right) => new("$eq", new BsonArray { left, right });
    private static BsonDocument And(params BsonDocument[] terms) => new("$and", new BsonArray(terms));
    private static BsonDocument Member(string field, int index) => new("$arrayElemAt", new BsonArray {
        new BsonDocument("$cond", new BsonArray { new BsonDocument("$isArray", "$people." + field), "$people." + field, new BsonArray() }), index });
}
