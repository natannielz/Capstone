using MongoDB.Bson;
using UnitToko.Platform.Catalog;
using S = UnitToko.Platform.Mongo.Migrations.StockSourceSetupSchemas;

namespace UnitToko.Platform.Mongo.Migrations;

internal static class OperationalScanBindingSchemas
{
    internal static readonly (string Name, BsonDocument Key, bool Unique)[] Keys = [
        ("ux_operational_scan_command", new() { { "actorId", 1 }, { "operation", 1 }, { "requestKey", 1 } }, true),
        ("ux_operational_scan_receipt", new("receiptId", 1), true)];
    private static BsonDocument Hash => new() { { "bsonType", "string" }, { "pattern", "^[0-9a-f]{64}\\z" } };
    private static BsonDocument Nullable(BsonDocument shape) => new("anyOf", new BsonArray { shape, new BsonDocument("bsonType", "null") });
    private static BsonDocument Array(BsonDocument item) => new() { { "bsonType", "array" }, { "minItems", 0 }, { "maxItems", 100 }, { "items", item } };
    private static BsonDocument Selection => S.Object(new() {
        { "identifier", Nullable(S.Object(new() { { "identifierId", S.Number(1) }, { "identifierRevision", S.Number(1) }, { "skuId", S.Number(1) },
            { "definitionId", S.Number(1) }, { "definitionVersion", S.Number(1) }, { "kind", S.Choice("skuCode", "gtin", "supplier", "omi", "smart") },
            { "namespace", S.Text(256) }, { "canonicalValue", S.Text(128) } })) },
        { "source", Nullable(S.Object(new() { { "orderId", S.Number(1) }, { "orderVersion", S.Number(1) }, { "inspectionId", S.Number(1) },
            { "inspectionRevision", S.Number(1, 1) }, { "inspectionLineNumber", new BsonDocument { { "bsonType", "int" }, { "minimum", 1 }, { "maximum", 100 } } },
            { "skuId", S.Number(1) }, { "definitionId", S.Number(1) }, { "definitionVersion", S.Number(1) },
            { "sourceSystem", S.Choice("supplier", "omi", "s_mart") }, { "sourceAccount", S.Text(128) }, { "sourceCode", S.Text(128) } })) }
    });
    // These are authenticated owner snapshots, not arbitrary API input. Their full owner-specific
    // canonical schemas and hashes are revalidated by the helper before insert and every recovery.
    private static BsonDocument Snapshot => new("bsonType", "object");
    internal static BsonDocument Validator() => new("$jsonSchema", S.Object(new() {
        { "_id", S.Number(1) }, { "schemaVersion", new BsonDocument { { "bsonType", "int" }, { "enum", new BsonArray { 1 } } } },
        { "actorId", S.Number(1) }, { "operation", S.Choice(OperationalScanCommandBindings.Operations) }, { "requestKey", new BsonDocument {
            { "bsonType", "string" }, { "pattern", "^[A-Za-z0-9._:-]{1,128}\\z" } } }, { "businessHash", Hash },
        { "mode", S.Choice("manual", "scanned", "legacy-manual") }, { "selections", Array(Selection) }, { "selectionHash", Hash },
        { "receiptId", new BsonDocument("bsonType", "objectId") }, { "receiptHash", Hash }, { "ownerAuditId", S.Number(1) }, { "ownerAuditHash", Hash },
        { "evidence", Array(S.Object(new() { { "selection", Selection }, { "assignment", Nullable(Snapshot) }, { "sku", Nullable(Snapshot) },
            { "definition", Nullable(Snapshot) }, { "approvedSource", Nullable(S.Object(new() { { "orderSource", Snapshot }, { "inspection", Snapshot } })) } })) },
        { "recordedAt", new BsonDocument("bsonType", "date") }, { "fingerprint", Hash }
    }));
}
