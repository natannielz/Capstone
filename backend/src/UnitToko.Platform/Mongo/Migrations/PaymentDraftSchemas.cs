using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using B = UnitToko.Platform.Billing.PaymentDraftBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class PaymentDraftSchemas
{
    internal static readonly string[] Collections = [B.Heads, B.Versions];
    internal static BsonDocument Key(string name) => name == B.Heads ? new("_id", 1) : new() { { "declarationId", 1 }, { "revision", 1 } };
    private static BsonDocument Nullable(BsonDocument value) => new("anyOf", new BsonArray { value, new BsonDocument("bsonType", "null") });
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Money() => new() { { "bsonType", "decimal" }, { "minimum", new BsonDecimal128(.01m) }, { "maximum", new BsonDecimal128(DomainValues.MaximumMoney) }, { "multipleOf", new BsonDecimal128(.01m) } };
    private static BsonDocument Array(BsonDocument value, int maximum) => new() { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", maximum }, { "items", value } };
    internal static BsonDocument Scopes() => Array(S.Object(new() {
        { "buyingOrganizationId", S.Number(1) }, { "branchId", Nullable(S.Number(1)) }, { "divisionId", Nullable(S.Number(1)) },
        { "fulfillmentOrganizationId", S.Number(1) }, { "shopId", S.Number(1) }, { "warehouseId", Nullable(S.Number(1)) },
        { "channelId", S.Number(1) }, { "createdBy", S.Number(1) } }), PaymentDraftPolicy.MaximumInvoices * InvoicePolicy.MaximumReceipts);
    private static BsonDocument Party() => S.Object(new() { { "buyingOrganizationId", S.Number(1) }, { "fulfillmentOrganizationId", S.Number(1) }, { "shopId", S.Number(1) }, { "channelId", S.Number(1) } });
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "revision", S.Number(1, long.MaxValue - 1) }, { "recordedBy", S.Number(1) }, { "party", Party() }, { "scopes", Scopes() } };
        var date = new BsonDocument("bsonType", "date");
        if (name == B.Heads) {
            fields.Add("versionId", S.Number(1)); fields.Add("state", S.Choice("draft")); fields.Add("fence", S.Number(0, long.MaxValue - 1));
            fields.Add("createdAt", date); fields.Add("updatedAt", date);
        } else if (name == B.Versions) {
            fields.Add("declarationId", S.Number(1)); fields.Add("previousVersionId", Nullable(S.Number(1))); fields.Add("previousHash", Nullable(Hash()));
            fields.Add("recordedAt", date); fields.Add("actorGrantVersion", S.Number(1)); fields.Add("requestKey", S.Text(128));
            fields.Add("input", S.Object(new() { { "invoiceIds", Array(S.Number(1), PaymentDraftPolicy.MaximumInvoices) }, { "declaredAmount", Money() },
                { "transferDate", new BsonDocument { { "bsonType", "string" }, { "pattern", @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z" } } },
                { "payerName", S.Text(160) }, { "bankReference", Nullable(S.Text(160)) }, { "note", Nullable(S.Text(1000)) } }));
            fields.Add("invoices", Array(S.Object(new() { { "invoiceId", S.Number(1) }, { "snapshotHash", Hash() }, { "numberEntity", S.Number(1) },
                { "numberChannel", S.Number(1) }, { "numberYear", S.Number(1, 9999) }, { "number", S.Number(1) }, { "total", Money() },
                { "buyerName", S.Text(1000) }, { "sellerName", S.Text(1000) }, { "scopes", Scopes() } }), PaymentDraftPolicy.MaximumInvoices));
            fields.Add("bodyHash", Hash()); fields.Add("snapshotHash", Hash()); S.Metadata(fields);
        } else throw B.Invalid();
        var rules = new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)) };
        if (name == B.Heads) {
            rules.Add(S.DateRange("createdAt")); rules.Add(S.DateRange("updatedAt"));
            rules.Add(new BsonDocument("$expr", new BsonDocument("$gte", new BsonArray { "$updatedAt", "$createdAt" })));
        } else rules.Add(S.DateRange("recordedAt"));
        return new("$and", rules);
    }
}
