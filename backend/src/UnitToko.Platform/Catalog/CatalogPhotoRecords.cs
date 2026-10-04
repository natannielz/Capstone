using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Catalog;

internal static class CatalogPhotoRecords
{
    internal const string Photos = "catalogPrimaryPhotos";
    internal const string Heads = "catalogPhotoHeads";
    internal const string Audits = "catalogPhotoAudits";
    internal const string Receipts = "catalogPhotoReceipts";
    internal const string Operation = "catalog.photo.associate";
    internal static AuthorizationDeniedException Broken() => new("catalog_photo_integrity_unavailable");
    internal static string Hash(BsonDocument row) {
        var copy = row.DeepClone().AsBsonDocument; copy.Remove("snapshotHash");
        return CatalogRepository.Digest(copy);
    }
    internal static BsonDocument Ref(EvidenceRef reference) => new() { { "id", reference.Id }, { "version", reference.Version }, { "sha256", reference.Sha256 } };
    internal static EvidenceRef Ref(BsonDocument row) {
        if (!CatalogRepository.Fields(row, "id", "version", "sha256") || !CatalogRepository.Positive(row, "id") ||
            !CatalogRepository.Positive(row, "version") || !row["sha256"].IsString) throw Broken();
        try { return new(row["id"].AsInt64, row["version"].AsInt64, row["sha256"].AsString); }
        catch (DomainValidationException) { throw Broken(); }
    }
    internal static BsonDocument Result(CatalogPrimaryPhotoResult value) => new() {
        { "associationId", value.AssociationId.Value }, { "skuId", value.SkuId.Value }, { "definitionId", value.DefinitionId.Value },
        { "definitionVersion", value.DefinitionVersion }, { "skuRevision", value.SkuRevision },
        { "photoRevision", value.PhotoRevision }, { "recordedAt", value.RecordedAt.UtcDateTime } };
    internal static CatalogPrimaryPhotoResult Result(BsonDocument row) {
        if (!CatalogRepository.Fields(row, "associationId", "skuId", "definitionId", "definitionVersion", "skuRevision", "photoRevision", "recordedAt") ||
            new[] { "associationId", "skuId", "definitionId", "definitionVersion", "skuRevision", "photoRevision" }.Any(field => !CatalogRepository.Positive(row, field)) ||
            !CatalogSchemas.ValidDate(row["recordedAt"])) throw Broken();
        return new(new(row["associationId"].AsInt64), new(row["skuId"].AsInt64), new(row["definitionId"].AsInt64),
            row["definitionVersion"].AsInt64, row["skuRevision"].AsInt64, row["photoRevision"].AsInt64,
            CatalogRepository.Date(row["recordedAt"]));
    }
    internal static void RequirePhoto(BsonDocument row) {
        if (!CatalogRepository.Fields(row, "_id", "schemaVersion", "skuId", "skuRevision", "definitionId", "definitionVersion", "owner",
            "photoRevision", "previousPhotoId", "reservationId", "versionRowId", "reference", "validationHash", "mime", "storeId", "generation", "actorId", "recordedAt",
            "reason", "correlationId", "requestKey", "bodyHash", "snapshotHash") || row["schemaVersion"] != 1 ||
            new[] { "_id", "skuId", "skuRevision", "definitionId", "definitionVersion", "photoRevision", "reservationId", "versionRowId", "actorId" }
                .Any(field => !CatalogRepository.Positive(row, field)) || !row["owner"].IsBsonDocument ||
            row["photoRevision"].AsInt64 > 256 ||
            row["photoRevision"].AsInt64 == 1 && !row["previousPhotoId"].IsBsonNull ||
            row["photoRevision"].AsInt64 > 1 && !CatalogRepository.Positive(row, "previousPhotoId") ||
            !row["reference"].IsBsonDocument || !CatalogSchemas.ValidDate(row["recordedAt"]) ||
            !row["mime"].IsString || row["mime"].AsString is not ("image/png" or "image/jpeg") ||
            !row["validationHash"].IsString || !CatalogReceiptRules.Hash(row["validationHash"]) ||
            !row["snapshotHash"].IsString || row["snapshotHash"].AsString != Hash(row) ||
            !row["bodyHash"].IsString || !CatalogReceiptRules.Hash(row["bodyHash"]) ||
            !row["requestKey"].IsString || !CatalogReceiptRules.Key(row["requestKey"].AsString) ||
            !row["storeId"].IsString || !row["generation"].IsString ||
            !row["reason"].IsString || row["reason"].AsString.Length is < 1 or > 1000 ||
            !row["correlationId"].IsString || row["correlationId"].AsString.Length is < 1 or > 128)
            throw Broken();
        _ = CatalogRepository.Owner(row["owner"].AsBsonDocument); _ = Ref(row["reference"].AsBsonDocument);
    }
}

internal static class CatalogPhotoStorage
{
    internal static async Task RequireAsync(IMongoDatabase database, CancellationToken ct)
    {
        foreach (var name in CatalogPhotoSchemas.Collections)
        {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct))
                .SingleOrDefaultAsync(ct) ?? throw CatalogPhotoRecords.Broken();
            var options = definition.GetValue("options", new BsonDocument()).AsBsonDocument;
            if (definition.GetValue("type", "") != "collection" || options.GetValue("validationLevel", "strict") != "strict" ||
                options.GetValue("validationAction", "error") != "error" || options.GetValue("validator", BsonNull.Value) != CatalogPhotoSchemas.Validator(name))
                throw CatalogPhotoRecords.Broken();
            var indexes = await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct);
            if (!indexes.Any(index => index.GetValue("name", "") == "_id_" && index.GetValue("key", BsonNull.Value) == new BsonDocument("_id", 1)))
                throw CatalogPhotoRecords.Broken();
            foreach (var required in CatalogPhotoSchemas.Keys(name))
                if (!indexes.Any(index => index.GetValue("name", "") == required.Name && index.GetValue("key", BsonNull.Value) == required.Key &&
                    index.GetValue("unique", false).AsBoolean == required.Unique && !index.Contains("partialFilterExpression") && !index.Contains("expireAfterSeconds")))
                    throw CatalogPhotoRecords.Broken();
            if (indexes.Any(index => index.GetValue("name", "") != "_id_" &&
                !CatalogPhotoSchemas.Keys(name).Any(required => index.GetValue("name", "") == required.Name &&
                    index.GetValue("key", BsonNull.Value) == required.Key))) throw CatalogPhotoRecords.Broken();
        }
    }
}
