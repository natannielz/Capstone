using MongoDB.Bson;
using B = UnitToko.Platform.Accounting.AccountingBookSetupBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class AccountingBookSetupSchemas
{
    internal static readonly string[] Collections = [B.Proposals, B.Decisions];
    private static BsonDocument Format => new() { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } };
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Code => new() { { "bsonType", "string" }, { "pattern", @"\A[A-Za-z][A-Za-z0-9._-]{0,63}\z" }, { "maxLength", 64 } };
    private static BsonDocument Array(BsonDocument item, int min, int max) => new() { { "bsonType", "array" }, { "minItems", min }, { "maxItems", max }, { "items", item } };
    internal static BsonDocument Key() => S.Object(new() { { "organizationId", S.Number(1) }, { "shopId", S.Number(1) }, { "channelId", S.Number(1) }, { "currency", S.Choice("IDR") } });
    internal static BsonDocument Input() => S.Object(new() { { "key", Key() }, { "initialMonth", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A(?!0000)[0-9]{4}-(0[1-9]|1[0-2])\z" } } },
        { "mappings", Array(S.Object(new() { { "mappingKey", Code }, { "accountCode", Code } }), 1, 1000) } });
    internal static BsonDocument Sources() => S.Object(new() { { "organization", OrganizationAddressSchemas.Source() },
        { "shop", OrganizationAddressSchemas.Source() }, { "channel", OrganizationAddressSchemas.Source() } });
    private static BsonDocument Person => S.Object(new() { { "userId", S.Number(1) }, { "personId", S.Number(1) }, { "verification", S.Choice("Verified") }, { "version", S.Number(1, long.MaxValue - 1) } });
    internal static BsonDocument[] Keys(string name) => name switch {
        B.Proposals => [new() { { "actorId", 1 }, { "requestKey", 1 } }],
        B.Decisions => [new("proposalId", 1), new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }],
        _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name) {
        if (name is not (B.Proposals or B.Decisions)) throw new ArgumentOutOfRangeException(nameof(name));
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", Format }, { "proposalId", S.Number(1) }, { "version", S.Number(1, 1) },
            { "key", Key() }, { "sources", Sources() }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "recordedAt", Date },
            { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "binding", Hash }, { "snapshotHash", Hash }, { "people", Array(Person, name == B.Proposals ? 1 : 2, name == B.Proposals ? 1 : 2) } };
        S.Metadata(fields);
        var checks = new BsonArray(); var dates = new List<string> { "recordedAt" };
        if (name == B.Proposals) {
            fields.Add("operation", S.Choice(B.Prefix + "propose")); fields.Add("input", Input());
            checks.Add(Eq("$_id", "$proposalId")); checks.Add(Eq("$key", "$input.key"));
            checks.Add(Eq("$actorId", Member("userId", 0)));
            checks.Add(Unique(Map(SafeArray("$input.mappings"), "mapping", "$$mapping.mappingKey")));
        } else {
            fields.AddRange(new BsonDocument { { "operation", S.Choice(B.Prefix + "approve", B.Prefix + "reject") }, { "decision", S.Choice("approve", "reject") },
                { "proposalHash", Hash }, { "reviewSources", Sources() }, { "bookId", Nullable(S.Number(1)) }, { "chartId", Nullable(S.Number(1)) }, { "periodId", Nullable(S.Number(1)) },
                { "accounts", Array(S.Object(new() { { "mappingKey", Code }, { "accountId", S.Number(1) }, { "accountCode", Code }, { "active", new BsonDocument { { "bsonType", "bool" }, { "enum", new BsonArray { true } } } } }), 0, 1000) } });
            checks.Add(Eq("$operation", new BsonDocument("$concat", new BsonArray { B.Prefix, "$decision" })));
            foreach (var field in new[] { "bookId", "chartId", "periodId" }) checks.Add(Eq(Eq("$decision", "approve"), new BsonDocument("$ne", new BsonArray { "$" + field, BsonNull.Value })));
            checks.Add(Eq(Eq("$decision", "approve"), new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", new BsonDocument("$cond", new BsonArray {
                new BsonDocument("$isArray", "$accounts"), "$accounts", new BsonArray() })), 0 })));
            checks.Add(new BsonDocument("$lt", new BsonArray { Member("userId", 0), Member("userId", 1) }));
            checks.Add(new BsonDocument("$ne", new BsonArray { Member("personId", 0), Member("personId", 1) }));
            checks.Add(new BsonDocument("$in", new BsonArray { "$actorId", SafeArray("$people.userId") }));
            checks.Add(Unique(Map(SafeArray("$accounts"), "mapping", "$$mapping.mappingKey")));
            checks.Add(All(Map(SafeArray("$accounts"), "left", All(Map(SafeArray("$accounts"), "right",
                Eq(Eq("$$left.accountId", "$$right.accountId"), Eq("$$left.accountCode", "$$right.accountCode")))))));
        }
        foreach (var (namePart, kind, id) in new[] { ("organization", "fulfillmentOrganization", "organizationId"), ("shop", "shop", "shopId"), ("channel", "channel", "channelId") }) {
            checks.Add(Eq("$sources." + namePart + ".target.id", "$key." + id)); checks.Add(Eq("$sources." + namePart + ".target.kind", kind));
            checks.Add(new BsonDocument("$lte", new BsonArray { "$sources." + namePart + ".profile.approvedAt", "$recordedAt" }));
            dates.Add("sources." + namePart + ".profile.approvedAt");
            if (name == B.Decisions) {
                checks.Add(Eq("$reviewSources." + namePart + ".target.id", "$key." + id)); checks.Add(Eq("$reviewSources." + namePart + ".target.kind", kind));
                checks.Add(new BsonDocument("$lte", new BsonArray { "$reviewSources." + namePart + ".profile.approvedAt", "$recordedAt" }));
                dates.Add("reviewSources." + namePart + ".profile.approvedAt");
            }
        }
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
