using MongoDB.Bson;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class PaymentReceiptPurposeSchemas
{
    // The existing customer owner remains byte-for-byte v1. Supplier returns add a
    // distinct variant under the SAME receipt key, so competing purposes cannot win.
    internal static BsonDocument Validator() => new("$or", new BsonArray {
        PaymentVerificationSchemas.Validator(B.Parties), SupplierValidator()
    });

    internal static BsonDocument SupplierValidator() => new("$and", new BsonArray {
        new BsonDocument("$jsonSchema", S.Object(new() {
            { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 2 } } } },
            { "kind", S.Choice("supplier_advance_return") }, { "receiptHash", PaymentSubmissionSchemas.Hash() },
            { "supplierId", S.Number(1) }, { "orderId", S.Number(1) }, { "firstReturnId", S.Number(1) },
            { "settlementIds", new BsonDocument { { "bsonType", "array" }, { "minItems", 1 },
                { "maxItems", UnitToko.Modules.Procurement.ProcurementAdvanceReturnRules.MaximumLines }, { "uniqueItems", true }, { "items", S.Number(1) } } },
            { "firstReturnHash", PaymentSubmissionSchemas.Hash() }, { "recordedAt", new BsonDocument("bsonType", "date") },
            { "snapshotHash", PaymentSubmissionSchemas.Hash() }, { "fence", S.Number(0, long.MaxValue - 1) }
        })), S.DateRange("recordedAt")
    });
}
