using MongoDB.Bson;
using UnitToko.Platform.Billing;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;
using B = UnitToko.Platform.Billing.InvoiceReleaseBson;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class InvoiceReleaseSchemas
{
    internal static readonly string[] Collections = [B.Heads, B.Events, B.Bindings, B.Anchors];
    private static BsonDocument Hash => PaymentSubmissionSchemas.Hash();
    private static BsonDocument Object => new("bsonType", "object");
    private static BsonDocument Date => new("bsonType", "date");
    private static BsonDocument NullableNumber() { var shape = S.Number(1); shape["bsonType"] = new BsonArray { "long", "null" }; return shape; }
    private static BsonDocument NullableHash() { var shape = Hash; shape["bsonType"] = new BsonArray { "string", "null" }; return shape; }
    private static BsonDocument Array(BsonDocument item, int maximum) => new() {
        { "bsonType", "array" }, { "minItems", 1 }, { "maxItems", maximum }, { "items", item } };
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string name) => name switch {
        B.Heads => [("ux_invoice_release_selection", new("selectionKey", 1), true)],
        B.Events => [("ux_invoice_release_revision", new() { { "releaseId", 1 }, { "revision", 1 } }, true),
            ("ux_invoice_release_operation", new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, true)],
        B.Bindings or B.Anchors => [], _ => throw new ArgumentOutOfRangeException(nameof(name)) };
    internal static BsonDocument Validator(string name)
    {
        var fields = new BsonDocument { { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } } };
        var rules = new BsonArray();
        if (name == B.Anchors) {
            fields.Add("invoiceHash", Hash); fields.Add("bindingHash", Hash);
            fields.Add("migrationVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { InvoiceReleaseMigration.Number } } });
            rules.Add(true);
        } else if (name == B.Bindings) {
            fields.Add("kind", S.Choice("pre_release", "approved")); fields.Add("migrationVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { InvoiceReleaseMigration.Number } } });
            fields.Add("invoiceId", S.Number(1)); fields.Add("invoiceHash", Hash); fields.Add("termsHash", Hash);
            fields.Add("receiptId", new BsonDocument("bsonType", "objectId")); fields.Add("receiptHash", Hash);
            fields.Add("auditId", S.Number(1)); fields.Add("auditHash", Hash); fields.Add("recordHash", Hash);
            foreach (var field in new[] { "releaseId", "releaseRevision", "proposalId", "decisionId" }) fields.Add(field, NullableNumber());
            fields.Add("proposalHash", NullableHash()); fields.Add("decisionHash", NullableHash());
            rules.Add(new BsonDocument("$eq", new BsonArray { "$_id", "$invoiceId" }));
            var legacy = new BsonArray(); var approved = new BsonArray();
            foreach (var field in new[] { "releaseId", "releaseRevision", "proposalId", "decisionId", "proposalHash", "decisionHash" }) {
                legacy.Add(new BsonDocument("$eq", new BsonArray { "$" + field, BsonNull.Value }));
                approved.Add(new BsonDocument("$ne", new BsonArray { "$" + field, BsonNull.Value }));
            }
            rules.Add(new BsonDocument("$cond", new BsonArray { new BsonDocument("$eq", new BsonArray { "$kind", "pre_release" }),
                new BsonDocument("$and", legacy), new BsonDocument("$and", approved) }));
        } else {
            fields.Add("selectionKey", Hash); fields.Add("scopes", Array(Object, 20));
            if (name == B.Heads) {
                fields.Add("receipts", Array(S.Object(new() { { "shipmentId", S.Number(1) }, { "receiptId", S.Number(1) } }), 20));
                fields.Add("revision", S.Number(1, long.MaxValue - 1)); fields.Add("eventId", S.Number(1)); fields.Add("eventHash", Hash);
                fields.Add("state", S.Choice("propose", "approve", "reject")); fields.Add("fence", S.Number(0, long.MaxValue - 1));
                fields.Add("createdAt", Date); fields.Add("updatedAt", Date);
                rules.Add(new BsonDocument("$lte", new BsonArray { "$createdAt", "$updatedAt" }));
            } else if (name == B.Events) {
                fields.Add("releaseId", S.Number(1)); fields.Add("proposalId", S.Number(1)); fields.Add("revision", S.Number(1, long.MaxValue - 1));
                fields.Add("previousRevision", S.Number(0, long.MaxValue - 2)); fields.Add("previousHash", NullableHash());
                fields.Add("action", S.Choice("propose", "approve", "reject")); fields.Add("operation", S.Choice(B.Prefix + "propose", B.Prefix + "approve", B.Prefix + "reject"));
                fields.Add("input", Object); fields.Add("source", Object); fields.Add("binding", Hash); fields.Add("people", Array(Object, 2));
                fields.Add("actorId", S.Number(1)); fields.Add("actorGrantVersion", S.Number(1)); fields.Add("recordedAt", Date);
                fields.Add("auditId", S.Number(1)); fields.Add("requestKey", S.Text(128)); fields.Add("reason", S.Text(1000));
                fields.Add("correlationId", S.Text(128)); fields.Add("bodyHash", Hash); fields.Add("recordHash", Hash);
                rules.Add(new BsonDocument("$eq", new BsonArray { "$revision", new BsonDocument("$add", new BsonArray { "$previousRevision", 1L }) }));
                rules.Add(new BsonDocument("$eq", new BsonArray { "$operation", new BsonDocument("$concat", new BsonArray { B.Prefix, "$action" }) }));
            } else throw new ArgumentOutOfRangeException(nameof(name));
        }
        return new("$and", new BsonArray { new BsonDocument("$jsonSchema", S.Object(fields)), new BsonDocument("$expr", new BsonDocument("$and", rules)) });
    }
}
