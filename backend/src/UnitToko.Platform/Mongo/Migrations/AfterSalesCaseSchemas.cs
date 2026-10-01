using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;
internal static class AfterSalesCaseSchemas
{
    internal static BsonDocument Key() => new() { { "requestedBy", 1 }, { "requestKey", 1 } };
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", @"\A[0-9a-f]{64}\z" } };
    private static BsonDocument Array(BsonDocument item, int maximum) => new() { { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", maximum }, { "items", item } };
    internal static BsonDocument Validator() {
        var line = S.Object(new() { { "receiptId", S.Number(1) }, { "skuId", S.Number(1) }, { "reportedBaseUnits", S.Number(1, DomainValues.MaximumLineQuantity) } });
        var input = S.Object(new() { { "invoiceId", S.Number(1) }, { "binding", Hash() }, { "issue", S.Choice("wrong_item", "damaged", "missing", "other") }, { "lines", Array(line, AfterSalesCasePolicy.MaximumLines) } });
        var sourceLine = new BsonDocument(); foreach (var field in new[] { "orderId", "shipmentId", "receiptId", "skuId", "acceptedBaseUnits" }) sourceLine.Add(field, S.Number(1));
        foreach (var field in new[] { "skuCode", "packageLabel", "baseUnitCode" }) sourceLine.Add(field, S.Text(160)); sourceLine.Add("productName", S.Text(300));
        sourceLine.Add("baseUnitsPerPackage", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 } }); var date = new BsonDocument("bsonType", "date");
        var source = S.Object(new() { { "invoiceId", S.Number(1) }, { "invoiceHash", Hash() }, { "issuedAt", date }, { "number", S.Text(160) },
            { "buyerName", S.Text(1000) }, { "sellerName", S.Text(1000) }, { "scopes", PaymentDraftSchemas.Scopes() }, { "lines", Array(S.Object(sourceLine), InvoicePolicy.MaximumLines) } });
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
            { "revision", S.Number(1, 1) }, { "input", input }, { "source", source }, { "scopes", PaymentDraftSchemas.Scopes() }, { "requestedBy", S.Number(1) },
            { "actorGrantVersion", S.Number(1) }, { "requestedAt", date }, { "bodyHash", Hash() }, { "snapshotHash", Hash() }, { "requestKey", S.Text(128) }, { "fence", S.Number(0, long.MaxValue - 1) } };
        S.Metadata(fields);
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), S.DateRange("requestedAt"), S.DateRange("source.issuedAt"),
            new BsonDocument("$expr", new BsonDocument("$gte", new BsonArray { "$requestedAt", "$source.issuedAt" })) });
    }
}
