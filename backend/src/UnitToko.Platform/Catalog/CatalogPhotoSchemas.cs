using MongoDB.Bson;

namespace UnitToko.Platform.Catalog;

internal static class CatalogPhotoSchemas
{
    internal static readonly string[] Collections = [CatalogPhotoRecords.Photos, CatalogPhotoRecords.Heads, CatalogPhotoRecords.Audits, CatalogPhotoRecords.Receipts];
    internal static (string Name, BsonDocument Key, bool Unique)[] Keys(string collection) => collection switch {
        CatalogPhotoRecords.Photos => [("ux_catalog_photo_sku_revision", new BsonDocument { { "skuId", 1 }, { "photoRevision", 1 } }, true),
            ("ux_catalog_photo_reservation", new BsonDocument("reservationId", 1), true)],
        CatalogPhotoRecords.Heads => [],
        CatalogPhotoRecords.Audits => [("ux_catalog_photo_audit_photo", new BsonDocument("photoId", 1), true),
            ("ix_catalog_photo_audit_sku", new BsonDocument { { "skuId", 1 }, { "_id", 1 } }, false)],
        CatalogPhotoRecords.Receipts => [("ux_catalog_photo_actor_key", new BsonDocument { { "actorId", 1 }, { "operation", 1 }, { "key", 1 } }, true),
            ("ux_catalog_photo_receipt_photo", new BsonDocument("photoId", 1), true),
            ("ix_catalog_photo_receipt_sku", new BsonDocument { { "result.skuId", 1 }, { "_id", 1 } }, false)],
        _ => throw new ArgumentOutOfRangeException(nameof(collection)) };
    private static BsonDocument T(string type) => new("bsonType", type);
    private static BsonDocument Obj(params (string Name, BsonDocument Rule)[] properties) => new() {
        { "bsonType", "object" }, { "additionalProperties", false },
        { "required", new BsonArray(properties.Select(property => property.Name)) },
        { "properties", new BsonDocument(properties.Select(property => new BsonElement(property.Name, property.Rule))) } };
    private static BsonDocument Positive() => new() { { "bsonType", "long" }, { "minimum", 1L } };
    private static BsonDocument Hash() => new() { { "bsonType", "string" }, { "pattern", "^[0-9a-f]{64}$" } };
    private static BsonDocument Text(int max) => new() { { "bsonType", "string" }, { "minLength", 1 }, { "maxLength", max } };
    private static BsonDocument Owner() => Obj(("kind", Text(32)), ("organizationId", Positive()),
        ("shopId", Positive()), ("channelId", Positive()), ("warehouseId", new BsonDocument("bsonType", new BsonArray { "long", "null" })));
    internal static BsonDocument Validator(string collection) {
        BsonDocument schema = collection switch {
            CatalogPhotoRecords.Photos => Obj(("_id", Positive()), ("schemaVersion", new BsonDocument("enum", new BsonArray { 1 })),
                ("skuId", Positive()), ("skuRevision", Positive()), ("definitionId", Positive()), ("definitionVersion", Positive()),
                ("owner", Owner()), ("photoRevision", Positive()), ("previousPhotoId", new BsonDocument("bsonType", new BsonArray { "long", "null" })),
                ("reservationId", Positive()), ("versionRowId", Positive()),
                ("reference", Obj(("id", Positive()), ("version", Positive()), ("sha256", Hash()))),
                ("validationHash", Hash()), ("mime", new BsonDocument("enum", new BsonArray { "image/png", "image/jpeg" })),
                ("storeId", Text(128)), ("generation", Text(128)), ("actorId", Positive()), ("recordedAt", T("date")),
                ("reason", Text(1000)), ("correlationId", Text(128)), ("requestKey", Text(128)),
                ("bodyHash", Hash()), ("snapshotHash", Hash())),
            CatalogPhotoRecords.Heads => Obj(("_id", Positive()), ("schemaVersion", new BsonDocument("enum", new BsonArray { 1 })),
                ("revision", Positive()), ("currentPhotoId", Positive()), ("photoHash", Hash()),
                ("definitionId", Positive()), ("definitionVersion", Positive()),
                ("fence", new BsonDocument { { "bsonType", "long" }, { "minimum", 0L } })),
            CatalogPhotoRecords.Audits => Obj(("_id", Positive()), ("schemaVersion", new BsonDocument("enum", new BsonArray { 1 })),
                ("photoId", Positive()), ("skuId", Positive()), ("actorId", Positive()), ("operation", new BsonDocument("enum", new BsonArray { CatalogPhotoRecords.Operation })),
                ("bodyHash", Hash()), ("snapshotHash", Hash()), ("recordedAt", T("date"))),
            CatalogPhotoRecords.Receipts => Obj(("_id", T("objectId")), ("schemaVersion", new BsonDocument("enum", new BsonArray { 1 })),
                ("actorId", Positive()), ("operation", new BsonDocument("enum", new BsonArray { CatalogPhotoRecords.Operation })),
                ("key", Text(128)), ("bodyHash", Hash()), ("owner", Owner()), ("photoId", Positive()), ("auditId", Positive()),
                ("result", Obj(("associationId", Positive()), ("skuId", Positive()), ("definitionId", Positive()),
                    ("definitionVersion", Positive()), ("skuRevision", Positive()), ("photoRevision", Positive()),
                    ("recordedAt", T("date")))), ("recordedAt", T("date"))),
            _ => throw new ArgumentOutOfRangeException(nameof(collection)) };
        return new BsonDocument("$jsonSchema", schema);
    }
}
