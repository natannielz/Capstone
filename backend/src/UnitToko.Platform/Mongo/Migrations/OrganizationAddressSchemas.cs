using MongoDB.Bson;
using B = UnitToko.Platform.Organizations.OrganizationAddressBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class OrganizationAddressSchemas
{
    internal static readonly string[] Collections = [B.Heads, B.Proposals, B.Decisions, B.Versions];
    private static BsonDocument Null => new("bsonType", "null");
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, Null });
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Format => new() { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } };
    private static BsonDocument Purpose => S.Choice("legal_billing", "delivery", "dispatch");
    private static BsonDocument NodeKind => S.Choice("buyingOrganization", "fulfillmentOrganization", "branch", "division", "shop", "warehouse", "channel");
    internal static BsonDocument Target() => S.Object(new() {
        { "kind", S.Choice("buyingOrganization", "fulfillmentOrganization", "branch", "division", "warehouse") }, { "id", S.Number(1) } });
    internal static BsonDocument Details() => S.Object(new() { { "line1", S.Text(200) }, { "line2", Nullable(S.Text(200)) },
        { "city", S.Text(80) }, { "region", S.Text(80) }, { "postalCode", S.Text(20) },
        { "countryCode", new BsonDocument { { "bsonType", "string" }, { "minLength", 2 }, { "maxLength", 2 }, { "pattern", @"\A[A-Z]{2}\z" } } } });
    internal static BsonDocument Input() => S.Object(new() { { "target", Target() }, { "purpose", Purpose },
        { "expectedScopeVersion", S.Number(1, long.MaxValue - 1) }, { "expectedProfileVersion", S.Number(1, long.MaxValue - 1) },
        { "expectedAddressVersion", S.Number(0, long.MaxValue - 2) }, { "address", Details() } });
    private static BsonDocument Node() => S.Object(new() { { "id", S.Number(1) }, { "kind", NodeKind },
        { "parentId", Nullable(S.Number(1)) }, { "status", S.Choice("active") }, { "version", S.Number(1, long.MaxValue - 1) } });
    internal static BsonDocument Source() => S.Object(new() { { "target", Node() }, { "profile", S.Object(new() {
        { "id", S.Number(1) }, { "kind", NodeKind }, { "parentId", Nullable(S.Number(1)) }, { "code", S.Text(64) }, { "name", S.Text(160) },
        { "version", S.Number(1, long.MaxValue - 1) }, { "approvedProposalId", S.Number(1) }, { "approvalHistoryId", S.Number(1) },
        { "approvalHistoryHash", Hash }, { "approvedBy", S.Number(1) }, { "approvedAt", Date } }) },
        { "hierarchy", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", 3 }, { "items", Node() } } }, { "snapshotHash", Hash } });
    private static BsonDocument Person() => S.Object(new() { { "userId", S.Number(1) }, { "personId", S.Number(1) },
        { "verification", S.Choice("Verified") }, { "version", S.Number(1, long.MaxValue - 1) } });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Heads => [new() { { "target.kind", 1 }, { "target.id", 1 }, { "purpose", 1 } }],
        B.Proposals => [new() { { "actorId", 1 }, { "requestKey", 1 } }],
        B.Decisions => [new("proposalId", 1), new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }],
        B.Versions => [new() { { "addressId", 1 }, { "version", 1 } }, new("proposalId", 1), new("decisionId", 1)],
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", Format }, { "target", Target() }, { "purpose", Purpose } };
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
            fields.AddRange(new BsonDocument { { "addressId", S.Number(1) }, { "proposalId", S.Number(1) }, { "source", Source() }, { "details", Details() }, { "snapshotHash", Hash } });
            dates.Add("source.profile.approvedAt");
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
            } else if (name is B.Proposals or B.Decisions) {
                fields.AddRange(new BsonDocument { { "revision", S.Number(1, 1) }, { "baseVersion", S.Number(0, long.MaxValue - 2) },
                    { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "recordedAt", Date }, { "requestKey", S.Text(128) },
                    { "bodyHash", Hash }, { "binding", Hash }, { "people", new BsonDocument { { "bsonType", "array" },
                        { "minItems", name == B.Proposals ? 1 : 2 }, { "maxItems", name == B.Proposals ? 1 : 2 }, { "items", Person() } } } });
                S.Metadata(fields); dates.Add("recordedAt");
                checks.Add(new BsonDocument("$lte", new BsonArray { "$source.profile.approvedAt", "$recordedAt" }));
                if (name == B.Proposals) {
                    fields.Add("operation", S.Choice(B.Prefix + "propose")); fields.Add("input", Input());
                    checks.Add(Eq("$_id", "$proposalId")); checks.Add(Eq("$target", "$input.target")); checks.Add(Eq("$purpose", "$input.purpose"));
                    checks.Add(Eq("$details", "$input.address")); checks.Add(Eq("$baseVersion", "$input.expectedAddressVersion"));
                    checks.Add(Eq("$source.target.version", "$input.expectedScopeVersion")); checks.Add(Eq("$source.profile.version", "$input.expectedProfileVersion"));
                    checks.Add(Eq("$actorId", Member("userId", 0)));
                } else {
                    fields.AddRange(new BsonDocument { { "operation", S.Choice(B.Prefix + "approve", B.Prefix + "reject") }, { "decision", S.Choice("approve", "reject") },
                        { "versionId", Nullable(S.Number(1)) }, { "addressVersion", Nullable(S.Number(1, long.MaxValue - 1)) },
                        { "input", S.Object(new() { { "proposalId", S.Number(1) }, { "expectedRevision", S.Number(1, 1) }, { "decision", S.Choice("approve", "reject") } }) } });
                    checks.Add(Eq("$proposalId", "$input.proposalId")); checks.Add(Eq("$decision", "$input.decision"));
                    checks.Add(new BsonDocument("$lt", new BsonArray { Member("userId", 0), Member("userId", 1) }));
                    checks.Add(new BsonDocument("$ne", new BsonArray { Member("personId", 0), Member("personId", 1) }));
                    checks.Add(new BsonDocument("$in", new BsonArray { "$actorId", new BsonDocument("$cond", new BsonArray {
                        new BsonDocument("$isArray", "$people.userId"), "$people.userId", new BsonArray() }) }));
                    checks.Add(new BsonDocument("$or", new BsonArray {
                        And(Eq("$decision", "approve"), Eq("$operation", B.Prefix + "approve"), new("$ne", new BsonArray { "$versionId", BsonNull.Value }),
                            Eq("$addressVersion", new BsonDocument("$add", new BsonArray { "$baseVersion", 1L }))),
                        And(Eq("$decision", "reject"), Eq("$operation", B.Prefix + "reject"), Eq("$versionId", BsonNull.Value), Eq("$addressVersion", BsonNull.Value)) }));
                }
            } else throw new ArgumentOutOfRangeException(nameof(name));
        }
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) };
        terms.Add(new BsonDocument("$or", new BsonArray {
            new BsonDocument { { "purpose", "legal_billing" }, { "target.kind", new BsonDocument("$in", new BsonArray { "buyingOrganization", "fulfillmentOrganization" }) } },
            new BsonDocument { { "purpose", "delivery" }, { "target.kind", new BsonDocument("$in", new BsonArray { "branch", "division" }) } },
            new BsonDocument { { "purpose", "dispatch" }, { "target.kind", "warehouse" } } }));
        foreach (var date in dates) terms.Add(S.DateRange(date));
        if (checks.Count > 0) terms.Add(new BsonDocument("$expr", new BsonDocument("$and", checks)));
        return new("$and", terms);
    }
    private static BsonDocument Eq(BsonValue left, BsonValue right) => new("$eq", new BsonArray { left, right });
    private static BsonDocument And(params BsonDocument[] terms) => new("$and", new BsonArray(terms));
    private static BsonDocument Member(string field, int index) => new("$arrayElemAt", new BsonArray {
        new BsonDocument("$cond", new BsonArray { new BsonDocument("$isArray", "$people." + field), "$people." + field, new BsonArray() }), index });
}
