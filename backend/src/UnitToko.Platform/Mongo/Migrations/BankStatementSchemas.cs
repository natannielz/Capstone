using MongoDB.Bson;
using UnitToko.Modules.Billing;
using B = UnitToko.Platform.Billing.BankStatementBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class BankStatementSchemas
{
    internal static readonly string[] Collections = [B.Profiles, B.Imports];
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Array(BsonDocument item, int min, int max) => new() { { "bsonType", "array" }, { "minItems", min }, { "maxItems", max }, { "items", item } };
    private static BsonDocument Int(int min, int max) => new() { { "bsonType", "int" }, { "minimum", min }, { "maximum", max } };
    private static BsonDocument Money => new() { { "bsonType", "decimal" }, { "minimum", new Decimal128(0m) }, { "maximum", new Decimal128(999999999999999.99m) } };
    internal static BsonDocument ProfileFields() => new() {
        { "_id", S.Number(1) }, { "schemaVersion", Int(1,1) }, { "revision", S.Number(1,long.MaxValue-1) }, { "scope", ReceivingAccountSchemas.Scope() },
        { "bankCode", S.Text(32) }, { "name", S.Text(160) }, { "parser", S.Choice("bank-csv-v1") }, { "format", S.Object(new() {
            { "delimiter", S.Choice(",", ";", "\t") }, { "dateFormat", S.Choice("yyyy-MM-dd", "dd/MM/yyyy") }, { "decimalSeparator", S.Choice(".", ",") },
            { "creditMarker", S.Text(16) }, { "debitMarker", S.Text(16) }, { "columns", Array(S.Text(80),8,8) }, { "identityNamespace", Nullable(S.Text(80)) } }) },
        { "trustedSourceReference", S.Text(256) }, { "operatingOwnerReference", S.Text(256) }, { "approvalReference", S.Text(256) },
        { "approvedAt", Date }, { "expiresAt", Date }, { "active", new BsonDocument("bsonType", "bool") } };
    internal static BsonDocument Key(string name) => name == B.Imports ? new() { { "actorId", 1 }, { "requestKey", 1 } } : new("_id",1);
    internal static BsonDocument Validator(string name) {
        BsonDocument fields;
        if (name == B.Profiles) { fields = ProfileFields(); fields.Add("fence", S.Number(0,long.MaxValue-1)); }
        else if (name == B.Imports) {
            fields = new() { { "_id", S.Number(1) }, { "schemaVersion", Int(1,1) }, { "scope", ReceivingAccountSchemas.Scope() },
                { "profile", S.Object(ProfileFields()) }, { "account", ReceivingAccountSchemas.Validator(UnitToko.Platform.Billing.ReceivingAccountBson.Versions)["$and"][0]["$jsonSchema"].DeepClone().AsBsonDocument }, { "binding", Hash },
                { "filename", S.Text(160) }, { "sourceReference", S.Text(256) }, { "original", new BsonDocument("bsonType", "binData") },
                { "attested", new BsonDocument("enum", new BsonArray { true }) }, { "state", S.Choice("staged") },
                { "preview", S.Object(new() { { "sourceSha256", Hash }, { "credits", Money }, { "debits", Money },
                    { "rows", Array(S.Object(new() { { "number", Int(2,BankStatementParser.MaximumRows+1) }, { "transactionId", Nullable(S.Text(80)) },
                        { "bookedDate", S.Text(10) }, { "direction", S.Choice("credit","debit") }, { "amount", Money }, { "payerName", Nullable(S.Text(160)) },
                        { "reference", Nullable(S.Text(160)) }, { "identityCandidate", Nullable(Hash) } }),1,BankStatementParser.MaximumRows) },
                    { "issues", Array(S.Object(new() { { "row", Int(2,BankStatementParser.MaximumRows+1) }, { "field", S.Text(32) },
                        { "code", S.Choice("identity_missing","identity_repeated","reference_repeated","payer_unknown") } }),0,BankStatementParser.MaximumRows*4) } }) },
                { "actorId", S.Number(1) }, { "actorGrantVersion", S.Number(1) }, { "personAtImport", ReceivingAccountSchemas.Person() },
                { "operation", S.Choice(B.Operation) }, { "requestKey", S.Text(128) }, { "bodyHash", Hash }, { "snapshotHash", Hash },
                { "recordedAt", Date }, { "fence", S.Number(0,long.MaxValue-1) } };
            S.Metadata(fields);
        } else throw new ArgumentOutOfRangeException(nameof(name));
        var terms = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) };
        if (name == B.Profiles) { terms.Add(S.DateRange("approvedAt")); terms.Add(S.DateRange("expiresAt")); terms.Add(new BsonDocument("$expr", new BsonDocument("$gt", new BsonArray { "$expiresAt", "$approvedAt" }))); }
        else { terms.Add(S.DateRange("recordedAt")); terms.Add(new BsonDocument("$expr",new BsonDocument("$and",new BsonArray {
            new BsonDocument("$gt",new BsonArray { new BsonDocument("$binarySize","$original"),0 }),
            new BsonDocument("$lte",new BsonArray { new BsonDocument("$binarySize","$original"),BankStatementParser.MaximumBytes }) }))); }
        return new("$and", terms);
    }
}
