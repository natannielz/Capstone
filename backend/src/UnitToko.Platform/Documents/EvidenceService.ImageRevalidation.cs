using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Documents;

internal sealed record ImageRevalidationOriginal(BsonDocument Source, ExpectedPublication Publication,
    StorageIoBudget Budget, BsonDocument OriginalValidation, BsonDocument ReadPolicy);

internal sealed partial class EvidenceService
{
    // Called only under CatalogAuthoringService's current, scoped authoring callback for a NEW request.
    // Neither upload reservation lifetime nor the age of the first clean result is extended.
    internal Task<ImageRevalidationOriginal> CatalogImageRevalidationOriginalAsync(IClientSessionHandle tx,
        CatalogImageUploadTarget current, long reservationId, EvidenceRef reference, CancellationToken ct) =>
        GuardAsync<ImageRevalidationOriginal>(tx, async () =>
        {
            if (current.Target.Kind != EvidenceTargetKind.CatalogSku || current.DefinitionId is not > 0 ||
                current.DefinitionVersion is not > 0 || reservationId <= 0) throw Invalid("catalog_photo_source_unavailable");
            var bundle = await LoadAsync(tx, reservationId, ct);
            if (ReadTarget(bundle.Reservation) != current.Target ||
                !bundle.Reservation["owner"].Equals(CatalogRepository.Owner(current.Owner)) ||
                bundle.Reservation["purpose"] != EvidencePurpose.CatalogImage.ToString() ||
                bundle.Reservation["admittedDefinitionId"] != current.DefinitionId ||
                bundle.Reservation["admittedDefinitionVersion"] != current.DefinitionVersion ||
                Reference(bundle) != reference || bundle.Version["storeId"] != registration.StoreId)
                throw Conflict("catalog_photo_source_unavailable");
            var original = await HistoricalValidationAsync(tx, bundle, ct) ?? throw EvidenceValidationRecords.Broken();
            if (original["status"] != "passed" || original["mime"].AsString is not ("image/png" or "image/jpeg") ||
                !bundle.Version["sha256"].Equals(new BsonString(reference.Sha256)))
                throw Conflict("catalog_photo_source_unavailable");
            var lifecycle = await RequireHistoricalLifecycleAsync(tx, bundle, original, ct);
            if (lifecycle is null || lifecycle["state"] != "validated") throw EvidenceValidationRecords.Broken();
            var policy = await TouchReadPolicyAsync(tx, registration.StoreId, false, ct);
            if (bundle.Version["length"].AsInt64 > policy["maximumBytes"].AsInt64) throw Conflict("evidence_read_not_eligible");
            var source = new BsonDocument { { "skuId", current.Target.Id }, { "skuRevision", current.Revision },
                { "definitionId", current.DefinitionId.Value },
                { "definitionVersion", current.DefinitionVersion.Value }, { "owner", CatalogRepository.Owner(current.Owner) },
                { "reservationId", reservationId }, { "versionRowId", bundle.Version["_id"] },
                { "reference", ReadReference(reference) }, { "storeId", bundle.Version["storeId"] },
                { "generation", bundle.Version["generation"] }, { "length", bundle.Version["length"] },
                { "originalValidationHash", original["resultHash"] }, { "originalMime", original["mime"] } };
            return new(source, Publication(bundle), new StorageIoBudget(policy["maximumBytes"].AsInt64,
                TimeSpan.FromMilliseconds(policy["verificationDurationMs"].AsInt64),
                (int)bundle.Reservation["admittedLimits"]["bufferBytes"].AsInt64), original, policy);
        });

    // Worker-side immutable source recheck. The request's signed authoring decision is proved
    // separately; this method does not manufacture an actor or bypass current source state.
    internal Task<ImageRevalidationOriginal> ImageRevalidationOriginalAsync(IClientSessionHandle tx,
        BsonDocument source, CancellationToken ct) => GuardAsync<ImageRevalidationOriginal>(tx, async () =>
        {
            if (!await Rows("evidenceImageRevalidationRequests").Find(tx, new BsonDocument("source", source)).AnyAsync(ct))
                throw EvidenceImageRevalidationRecords.Broken();
            var catalogRepository = new CatalogRepository(database);
            var sku = await catalogRepository.GetAsync(tx, "skus", source["skuId"].AsInt64, ct);
            if (sku["active"] != true || sku["revision"] != source["skuRevision"] ||
                sku["currentDefinitionId"] != source["definitionId"] ||
                !sku["authoringOwner"].Equals(source["owner"])) throw Conflict("catalog_photo_source_unavailable");
            var definition = await catalogRepository.GetAsync(tx, "variantDefinitions", source["definitionId"].AsInt64, ct);
            var family = await catalogRepository.GetAsync(tx, "productFamilies", sku["familyId"].AsInt64, ct);
            var category = await catalogRepository.GetAsync(tx, "catalogCategories", family["categoryId"].AsInt64, ct);
            if (definition["version"] != source["definitionVersion"] || definition["skuId"] != sku["_id"] ||
                family["active"] != true || category["active"] != true ||
                !family["authoringOwner"].Equals(source["owner"]) ||
                !category["authoringOwner"].Equals(source["owner"]))
                throw Conflict("catalog_photo_source_unavailable");
            var current = new CatalogImageUploadTarget(new EvidenceTargetRef(EvidenceTargetKind.CatalogSku,
                source["skuId"].AsInt64), CatalogRepository.Owner(source["owner"].AsBsonDocument),
                sku["revision"].AsInt64, definition["_id"].AsInt64, definition["version"].AsInt64);
            var evidenceRef = new EvidenceRef(source["reference"]["id"].AsInt64,
                source["reference"]["version"].AsInt64, source["reference"]["sha256"].AsString);
            var original = await CatalogImageRevalidationOriginalAsync(tx, current, source["reservationId"].AsInt64, evidenceRef, ct);
            if (!original.Source.Equals(source)) throw EvidenceImageRevalidationRecords.Broken();
            return original;
        });
}
