using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Billing;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

// Version-29 deployment contract. No request installs DDL; later format changes need a forward migration.
internal static class InvoiceSchemas
{
    internal static readonly string[] Collections = [InvoiceBson.Invoices, InvoiceBson.Allocations];
    internal static BsonDocument NumberKey() => new() { { "numberEntity", 1 }, { "numberChannel", 1 }, { "numberYear", 1 }, { "number", 1 } };
    internal static BsonDocument CommandKey() => new() { { "issuedBy", 1 }, { "requestKey", 1 } };
    internal static BsonDocument AllocationKey() => new() { { "receiptId", 1 }, { "skuId", 1 } };
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Money() => new() { { "bsonType", "decimal" }, { "minimum", new BsonDecimal128(0m) }, { "maximum", new BsonDecimal128(DomainValues.MaximumMoney) }, { "multipleOf", new BsonDecimal128(.01m) } };
    private static BsonDocument Object() => new("bsonType", "object");
    private static BsonDocument Array(BsonDocument item, int max) => new() { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", max }, { "items", item } };
    private static BsonDocument Nullable(BsonDocument item) => new("anyOf", new BsonArray { item, new BsonDocument("bsonType", "null") });
    private static BsonDocument Date() => new("bsonType", "date");
    private static BsonDocument Day() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z" } };
    internal static BsonDocument Validator(string name) {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } }, { "issuedAt", Date() } };
        if (name == InvoiceBson.Invoices) {
            foreach (var field in new[] { "numberEntity", "numberChannel", "number", "issuedBy", "actorGrantVersion" }) fields.Add(field, S.Number(1));
            fields.Add("numberYear", S.Number(1, 9999)); fields.Add("postingFence", S.Number(0, 1));
            fields.Add("issueDate", Day()); fields.Add("dueDate", Day()); fields.Add("orderIds", Array(S.Number(1), InvoicePolicy.MaximumReceipts));
            fields.Add("sources", Array(S.Object(new() { { "scope", Object() }, { "snapshot", Object() } }), InvoicePolicy.MaximumReceipts));
            fields.Add("scopes", Array(Object(), InvoicePolicy.MaximumReceipts)); fields.Add("terms", Object());
            var line = new BsonDocument();
            foreach (var field in new[] { "allocationId", "orderId", "shipmentId", "receiptId", "skuId", "orderedBaseUnits", "acceptedBaseUnits" }) line.Add(field, S.Number(1));
            line.Add("previousBaseUnits", S.Number(0)); line.Add("previousNet", Money());
            line.Add("skuCode", S.Text(160)); line.Add("productName", S.Text(300)); line.Add("definition", Object()); line.Add("price", Object());
            foreach (var field in new[] { "net", "tax", "charges" }) line.Add(field, Money());
            fields.Add("lines", Array(S.Object(line), InvoicePolicy.MaximumLines));
            fields.Add("totals", S.Object(new() { { "net", Money() }, { "tax", Money() }, { "charges", Money() }, { "total", Money() } }));
            foreach (var field in new[] { "review", "bodyHash" }) fields.Add(field, Hash());
            fields.Add("journalId", Nullable(S.Number(1))); fields.Add("journalFingerprint", Nullable(Hash())); fields.Add("snapshotHash", Nullable(Hash()));
            fields.Add("document", new BsonDocument { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", 1048576 } });
            fields.Add("documentHash", Hash()); fields.Add("documentLength", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 2097152 } });
            fields.Add("requestKey", S.Text(128)); S.Metadata(fields);
        } else if (name == InvoiceBson.Allocations) {
            foreach (var field in new[] { "invoiceId", "orderId", "shipmentId", "receiptId", "skuId", "definitionId", "acceptedBaseUnits" }) fields.Add(field, S.Number(1));
            fields.Add("previousBaseUnits", S.Number(0));
            foreach (var field in new[] { "previousNet", "net", "tax", "charges" }) fields.Add(field, Money());
            fields.Add("rounding", S.Choice("half_even", "half_up"));
        } else throw new ArgumentException("Unknown invoice storage");
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("issuedAt") });
    }
}
