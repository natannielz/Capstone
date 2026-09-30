using MongoDB.Bson;
using UnitToko.Platform.Billing;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;
using B = UnitToko.Platform.Billing.ReceivingAccountBson;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class ReceivingAccountSchemas
{
    internal static readonly string[] Collections = [B.Policies, B.Heads, B.Proposals, B.Observations, B.Decisions, B.Versions];
    private static BsonDocument Null => new("bsonType", "null");
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, Null });
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Date => new("bsonType", "date");
    internal static BsonDocument Scope() => S.Object(new() { { "fulfillmentOrganizationId", S.Number(1) }, { "shopId", S.Number(1) }, { "channelId", S.Number(1) }, { "warehouseId", Nullable(S.Number(1)) } });
    internal static BsonDocument Details() => S.Object(new() { { "bankCode", S.Text(32) }, { "accountNumber", S.Text(34) }, { "holderName", S.Text(160) }, { "currency", S.Choice("IDR") } });
    internal static BsonDocument Person() => new("oneOf", new BsonArray {
        S.Object(new() { { "userId", S.Number(1) }, { "personId", Null }, { "verification", S.Choice("Unknown") }, { "version", S.Number(0,0) } }),
        S.Object(new() { { "userId", S.Number(1) }, { "personId", S.Number(1) }, { "verification", S.Choice("Verified") }, { "version", S.Number(1) } }) });
    private static BsonDocument PolicyFields() => new() { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
        { "revision", S.Number(1, long.MaxValue-1) }, { "scope", Scope() }, { "bankCode", S.Text(32) }, { "bankName", S.Text(160) },
        { "method", S.Choice("bank-contact", "authenticated-bank-record") }, { "trustedChannelReference", S.Text(256) }, { "operatingOwnerReference", S.Text(256) },
        { "approvalReference", S.Text(256) }, { "active", new BsonDocument("bsonType", "bool") }, { "approvedAt", Date }, { "expiresAt", Date }, { "maxObservationAgeSeconds", S.Number(1,31536000) } };
    internal static BsonDocument Key(string name) => name switch {
        B.Observations or B.Decisions => new("proposalId", 1), B.Versions => new() { { "accountId", 1 }, { "version", 1 } }, _ => new("_id", 1) };
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        var dates = new List<string>();
        if (name == B.Policies) { fields = PolicyFields(); fields.Add("fence", S.Number(0,long.MaxValue-1)); dates.AddRange(["approvedAt", "expiresAt"]); }
        else if (name == B.Heads) { fields.AddRange(new BsonDocument { { "scope", Scope() }, { "rootProposalId", S.Number(1) }, { "version", S.Number(0,long.MaxValue-1) },
            { "versionId", Nullable(S.Number(1)) }, { "fence", S.Number(0,long.MaxValue-1) }, { "createdAt", Date }, { "updatedAt", Date } }); dates.AddRange(["createdAt", "updatedAt"]); }
        else if (name == B.Versions) { fields.AddRange(new BsonDocument { { "accountId", S.Number(1) }, { "version", S.Number(1,long.MaxValue-1) }, { "scope", Scope() },
            { "proposalId", S.Number(1) }, { "observationId", S.Number(1) }, { "decisionId", S.Number(1) }, { "previousVersionId", Nullable(S.Number(1)) }, { "previousHash", Nullable(Hash) },
            { "details", Details() }, { "physicalIdentity", Hash }, { "effectiveAt", Date }, { "snapshotHash", Hash } }); dates.Add("effectiveAt"); }
        else {
            fields.AddRange(new BsonDocument { { "accountId", S.Number(1) }, { "proposalId", S.Number(1) }, { "scope", Scope() }, { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) },
                { "operation", S.Choice(B.Prefix+"propose", B.Prefix+"observe", B.Prefix+"approve", B.Prefix+"reject", B.Prefix+"withdraw") }, { "recordedAt", Date },
                { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash }, { "people", new BsonDocument { { "bsonType", "array" }, { "minItems",1 }, { "maxItems",3 }, { "items", Person() } } } });
            S.Metadata(fields); dates.Add("recordedAt");
            if (name == B.Proposals) fields.AddRange(new BsonDocument { { "isNewAccount", new BsonDocument("bsonType", "bool") }, { "baseVersion", S.Number(0,long.MaxValue-2) }, { "details", Details() }, { "policy", S.Object(PolicyFields()) } });
            else if (name == B.Observations) { fields.AddRange(new BsonDocument { { "details", Details() }, { "policyHash", Hash }, { "sourceReference", S.Text(256) }, { "checkedAt", Date }, { "attested", new BsonDocument("enum", new BsonArray { true }) } }); dates.Add("checkedAt"); }
            else if (name == B.Decisions) fields.AddRange(new BsonDocument { { "decision", S.Choice("approve", "reject", "withdraw") }, { "observationId", Nullable(S.Number(1)) }, { "versionId", Nullable(S.Number(1)) }, { "accountVersion", Nullable(S.Number(1,long.MaxValue-1)) } });
            else throw new ArgumentOutOfRangeException(nameof(name));
        }
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) }; foreach (var field in dates) terms.Add(S.DateRange(field));
        if (name == B.Policies) terms.Add(new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$expiresAt", "$approvedAt" })));
        return new("$and", terms);
    }
}
