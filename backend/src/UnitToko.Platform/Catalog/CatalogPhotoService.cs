using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Catalog;

internal sealed record CatalogPhotoSelected(long AssociationId, long SkuId, long SkuRevision, long PhotoTargetRevision,
    long DefinitionId, long DefinitionVersion, string AssociationHash, CatalogImageProof Proof);

/// <summary>A separate reviewed primary-image producer. It never writes the legacy imageAssetIds fields.</summary>
internal sealed class CatalogPhotoService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
    CatalogAuthoringService catalog, EvidenceService evidence)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);

    internal Task<T?> WithBuyerSelectedAsync<T>(ActorContext actor, string publicId,
        Func<IClientSessionHandle, CatalogPhotoSelected, CancellationToken, Task<T>> read, CancellationToken ct) where T : class
    {
        var query = new CatalogDefinitionQueryService(database, new AuthorizationStore(database, unit, clock), clock);
        return query.ReadPhotoWithinAsync<T>(actor, publicId, async (tx, sku, definition, token) => {
            var selected = await RequireCurrentSelectionAsync(tx, sku, definition, token);
            return selected is null ? null : await read(tx, selected, token);
        }, ct);
    }

    internal Task<T?> WithStaffSelectedAsync<T>(ActorContext actor, long skuId,
        Func<IClientSessionHandle, CatalogPhotoSelected, CancellationToken, Task<T>> read, CancellationToken ct) where T : class
    {
        if (skuId <= 0) throw new DomainValidationException("invalid_catalog_photo_request", "A SKU is required");
        return unit.ExecuteAsync((tx, token) => catalog.WithCatalogImageUploadTargetAsync(database, tx, actor,
            new EvidenceTargetRef(EvidenceTargetKind.CatalogSku, skuId), null, async (source, inner) => {
                var repository = new CatalogRepository(database);
                var sku = await repository.GetAsync(tx, "skus", skuId, inner);
                if (source.Revision != sku["revision"].AsInt64 ||
                    source.DefinitionId != sku["currentDefinitionId"].AsInt64 ||
                    source.DefinitionVersion is not > 0 ||
                    !CatalogRepository.Owner(source.Owner).Equals(sku["authoringOwner"].AsBsonDocument))
                    throw CatalogPhotoRecords.Broken();
                var definition = await repository.GetAsync(tx, "variantDefinitions", source.DefinitionId.Value, inner);
                var selected = await RequireCurrentSelectionAsync(tx, sku, definition, inner);
                return selected is null ? null : await read(tx, selected, inner);
            }, token), ct);
    }

    internal async Task<CatalogPrimaryPhotoCurrent> CurrentAsync(ActorContext actor, long skuId, CancellationToken ct = default)
    {
        if (skuId <= 0) throw new DomainValidationException("invalid_catalog_photo_request", "A SKU is required");
        await CatalogPhotoStorage.RequireAsync(database, ct);
        return await unit.ExecuteAsync((tx, token) => catalog.WithCatalogImageUploadTargetAsync(database, tx, actor,
            new EvidenceTargetRef(EvidenceTargetKind.CatalogSku, skuId), null, async (source, inner) => {
                if (source.DefinitionId is not > 0 || source.DefinitionVersion is not > 0)
                    throw CatalogPhotoRecords.Broken();
                var head = await Rows(CatalogPhotoRecords.Heads).Find(tx, F.Eq("_id", skuId)).SingleOrDefaultAsync(inner);
                if (head is null) {
                    if (await Rows(CatalogPhotoRecords.Photos).Find(tx, F.Eq("skuId", skuId)).AnyAsync(inner) ||
                        await Rows(CatalogPhotoRecords.Receipts).Find(tx, F.Eq("result.skuId", skuId)).AnyAsync(inner) ||
                        await Rows(CatalogPhotoRecords.Audits).Find(tx, F.Eq("skuId", skuId)).AnyAsync(inner))
                        throw CatalogPhotoRecords.Broken();
                    return new CatalogPrimaryPhotoCurrent(new(skuId), source.Revision, new(source.DefinitionId.Value),
                        source.DefinitionVersion.Value, 0, null);
                }
                if (!CatalogRepository.Positive(head, "currentPhotoId")) throw CatalogPhotoRecords.Broken();
                await RequireHeadAsync(tx, head["currentPhotoId"].AsInt64, inner);
                var photo = await Rows(CatalogPhotoRecords.Photos).Find(tx, F.Eq("_id", head["currentPhotoId"])).SingleOrDefaultAsync(inner)
                    ?? throw CatalogPhotoRecords.Broken();
                if (!photo["owner"].Equals(CatalogRepository.Owner(source.Owner))) throw CatalogPhotoRecords.Broken();
                var selection = new CatalogPrimaryPhotoSelection(new(photo["_id"].AsInt64), photo["reservationId"].AsInt64,
                    CatalogPhotoRecords.Ref(photo["reference"].AsBsonDocument), photo["mime"].AsString,
                    CatalogRepository.Date(photo["recordedAt"]),
                    photo["definitionId"].AsInt64 == source.DefinitionId.Value &&
                    photo["definitionVersion"].AsInt64 == source.DefinitionVersion.Value);
                return new CatalogPrimaryPhotoCurrent(new(skuId), source.Revision, new(source.DefinitionId.Value),
                    source.DefinitionVersion.Value, head["revision"].AsInt64, selection);
            }, token), ct);
    }

    // Caller must invoke this only inside CatalogVisibilityStore.ReadWithinAsync after its
    // scoped SKU selector has selected the exact row. The returned facts are not a byte grant:
    // a separate protected opener must recheck current read policy and private storage.
    internal async Task<CatalogPhotoSelected?> RequireCurrentSelectionAsync(IClientSessionHandle tx,
        BsonDocument selectedSku, BsonDocument selectedDefinition, CancellationToken ct)
    {
        if (tx is null || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) ||
            selectedSku is null || selectedDefinition is null || !CatalogRepository.Positive(selectedSku, "_id"))
            throw CatalogPhotoRecords.Broken();
        await CatalogPhotoStorage.RequireAsync(database, ct);
        var repository = new CatalogRepository(database);
        var sku = await repository.GetAsync(tx, "skus", selectedSku["_id"].AsInt64, ct);
        if (!sku.Equals(selectedSku) || !sku["active"].AsBoolean ||
            !CatalogRepository.Positive(selectedDefinition, "_id")) throw CatalogPhotoRecords.Broken();
        var definition = await repository.GetAsync(tx, "variantDefinitions", selectedDefinition["_id"].AsInt64, ct);
        if (!definition.Equals(selectedDefinition)) throw CatalogPhotoRecords.Broken();
        CatalogAuthoringService.RequirePair(sku, definition);
        var head = await Rows(CatalogPhotoRecords.Heads).Find(tx, F.Eq("_id", sku["_id"])).SingleOrDefaultAsync(ct);
        if (head is null) {
            if (await Rows(CatalogPhotoRecords.Photos).Find(tx, F.Eq("skuId", sku["_id"])).AnyAsync(ct) ||
                await Rows(CatalogPhotoRecords.Receipts).Find(tx, F.Eq("result.skuId", sku["_id"])).AnyAsync(ct) ||
                await Rows(CatalogPhotoRecords.Audits).Find(tx, F.Eq("skuId", sku["_id"])).AnyAsync(ct))
                throw CatalogPhotoRecords.Broken();
            return null;
        }
        if (!CatalogRepository.Positive(head, "currentPhotoId")) throw CatalogPhotoRecords.Broken();
        await RequireHeadAsync(tx, head["currentPhotoId"].AsInt64, ct);
        if (head["definitionId"] != definition["_id"] || head["definitionVersion"] != definition["version"])
            return null; // A revised package definition needs a newly reviewed primary photo.
        var photo = await Rows(CatalogPhotoRecords.Photos).Find(tx, F.Eq("_id", head["currentPhotoId"])).SingleOrDefaultAsync(ct)
            ?? throw CatalogPhotoRecords.Broken();
        if (!photo["owner"].Equals(sku["authoringOwner"])) throw CatalogPhotoRecords.Broken();
        var source = new CatalogImageUploadTarget(new EvidenceTargetRef(EvidenceTargetKind.CatalogSku, sku["_id"].AsInt64),
            CatalogRepository.Owner(photo["owner"].AsBsonDocument), photo["skuRevision"].AsInt64,
            photo["definitionId"].AsInt64, photo["definitionVersion"].AsInt64);
        var proof = await evidence.RequireHistoricalCatalogImageAsync(tx, source, photo["reservationId"].AsInt64,
            CatalogPhotoRecords.Ref(photo["reference"].AsBsonDocument), ct);
        if (proof.VersionRowId != photo["versionRowId"].AsInt64 || proof.ValidationHash != photo["validationHash"].AsString ||
            proof.StoreId != photo["storeId"].AsString || proof.Generation != photo["generation"].AsString ||
            proof.Mime != photo["mime"].AsString) throw CatalogPhotoRecords.Broken();
        return new CatalogPhotoSelected(photo["_id"].AsInt64, sku["_id"].AsInt64, sku["revision"].AsInt64,
            photo["skuRevision"].AsInt64,
            definition["_id"].AsInt64, definition["version"].AsInt64, photo["snapshotHash"].AsString, proof);
    }

    internal async Task<CatalogImageProof?> RequireCurrentAssociationAsync(IClientSessionHandle tx,
        BsonDocument selectedSku, BsonDocument selectedDefinition, CancellationToken ct) =>
        (await RequireCurrentSelectionAsync(tx, selectedSku, selectedDefinition, ct))?.Proof;

    internal async Task<CatalogPrimaryPhotoResult> AssociateAsync(ActorContext actor, CatalogAssociatePrimaryPhoto input,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(metadata);
        if (input.SkuId.Value <= 0 || input.ExpectedSkuRevision <= 0 || input.ExpectedPhotoRevision < 0 ||
            input.ExpectedPhotoRevision >= 256 || input.ReservationId <= 0 || input.Reference is null ||
            !CatalogReceiptRules.Key(metadata.IdempotencyKey) || metadata.IdempotencyKey != metadata.IdempotencyKey.Trim())
            throw new DomainValidationException("invalid_catalog_photo_request", "A current SKU, completed image and command key are required");
        var reason = CatalogAuthoringPolicy.Text(metadata.Reason, 1000);
        var correlation = CatalogAuthoringPolicy.Text(metadata.CorrelationId, 128);
        var body = new BsonDocument { { "skuId", input.SkuId.Value }, { "expectedSkuRevision", input.ExpectedSkuRevision },
            { "expectedPhotoRevision", input.ExpectedPhotoRevision },
            { "reservationId", input.ReservationId }, { "reference", CatalogPhotoRecords.Ref(input.Reference) }, { "reason", reason } };
        var bodyHash = CatalogRepository.Digest(body);
        await CatalogPhotoStorage.RequireAsync(database, ct);
        var savedReceipt = await Rows(CatalogPhotoRecords.Receipts).Find(F.Eq("actorId", actor.UserId) &
            F.Eq("operation", CatalogPhotoRecords.Operation) & F.Eq("key", metadata.IdempotencyKey)).SingleOrDefaultAsync(ct);
        if (savedReceipt is not null) {
            var saved = await LookupAsync(actor, metadata.IdempotencyKey, ct) ?? throw CatalogPhotoRecords.Broken();
            if (savedReceipt["bodyHash"].AsString != bodyHash) throw new PersistenceConflictException("idempotency_key_reused", "Photo command key was used for another image");
            return saved;
        }
        var id = await ids.NextAsync(CatalogPhotoRecords.Photos, ct);
        var auditId = await ids.NextAsync(CatalogPhotoRecords.Audits, ct);
        return await evidence.WithCatalogImageOriginalAsync(actor,
            new EvidenceTargetRef(EvidenceTargetKind.CatalogSku, input.SkuId.Value), input.ExpectedSkuRevision,
            input.ReservationId, input.Reference, async (claim, handle, started, outer) =>
            await unit.ExecuteAsync(async (tx, token) => {
            var (proof, final) = await evidence.RequireVerifiedCatalogImageAsync(tx, actor, claim, handle, started, token);
            var commandResult = await catalog.WithCatalogImageUploadTargetAsync(database, tx, actor,
            new EvidenceTargetRef(EvidenceTargetKind.CatalogSku, input.SkuId.Value), null, async (source, inner) =>
            {
                var filter = F.Eq("actorId", actor.UserId) & F.Eq("operation", CatalogPhotoRecords.Operation) & F.Eq("key", metadata.IdempotencyKey);
                var previous = await Rows(CatalogPhotoRecords.Receipts).Find(tx, filter).SingleOrDefaultAsync(inner);
                if (previous is not null) {
                    var recoveredResult = await RequireReceiptAsync(tx, previous, actor.UserId, metadata.IdempotencyKey, inner);
                    await RequireHeadAsync(tx, recoveredResult.AssociationId.Value, inner);
                    if (previous["bodyHash"].AsString != bodyHash) throw new PersistenceConflictException("idempotency_key_reused", "Photo command key was used for another image");
                    if (!CatalogRepository.Owner(source.Owner).Equals(previous["owner"])) throw CatalogPhotoRecords.Broken();
                    return recoveredResult;
                }
                if (source.Revision != input.ExpectedSkuRevision) throw new PersistenceConflictException("catalog_revision_conflict", "SKU changed; reload its photo source");
                if (source.DefinitionId is not > 0 || source.DefinitionVersion is not > 0) throw CatalogPhotoRecords.Broken();
                var bound = await evidence.RequireHistoricalCatalogImageAsync(tx, source, input.ReservationId, input.Reference, inner);
                if (bound != proof) throw CatalogPhotoRecords.Broken();
                var head = await Rows(CatalogPhotoRecords.Heads).Find(tx, F.Eq("_id", input.SkuId.Value)).SingleOrDefaultAsync(inner);
                BsonValue previousPhotoId = BsonNull.Value;
                if (head is null) {
                    if (input.ExpectedPhotoRevision != 0 || await Rows(CatalogPhotoRecords.Photos).Find(tx,
                            F.Eq("skuId", input.SkuId.Value)).AnyAsync(inner) ||
                        await Rows(CatalogPhotoRecords.Receipts).Find(tx, F.Eq("result.skuId", input.SkuId.Value)).AnyAsync(inner) ||
                        await Rows(CatalogPhotoRecords.Audits).Find(tx, F.Eq("skuId", input.SkuId.Value)).AnyAsync(inner))
                        throw new PersistenceConflictException("catalog_photo_revision_conflict", "Primary photo changed; reload it");
                } else {
                    if (!CatalogRepository.Fields(head, "_id", "schemaVersion", "revision", "currentPhotoId", "photoHash",
                            "definitionId", "definitionVersion", "fence") || head["schemaVersion"] != 1 ||
                        !CatalogRepository.Positive(head, "revision") || !CatalogRepository.Positive(head, "currentPhotoId") ||
                        !head["fence"].IsInt64 || head["fence"].AsInt64 is < 0 or >= long.MaxValue - 1 ||
                        !CatalogReceiptRules.Hash(head["photoHash"])) throw CatalogPhotoRecords.Broken();
                    if (head["revision"].AsInt64 != input.ExpectedPhotoRevision)
                        throw new PersistenceConflictException("catalog_photo_revision_conflict", "Primary photo changed; reload it");
                    var prior = await Rows(CatalogPhotoRecords.Photos).Find(tx, F.Eq("_id", head["currentPhotoId"])).SingleOrDefaultAsync(inner)
                        ?? throw CatalogPhotoRecords.Broken();
                    CatalogPhotoRecords.RequirePhoto(prior);
                    if (prior["skuId"] != head["_id"] || prior["photoRevision"] != head["revision"] ||
                        prior["snapshotHash"] != head["photoHash"] || prior["definitionId"] != head["definitionId"] ||
                        prior["definitionVersion"] != head["definitionVersion"]) throw CatalogPhotoRecords.Broken();
                    var priorReceipt = await Rows(CatalogPhotoRecords.Receipts).Find(tx, F.Eq("photoId", prior["_id"])).SingleOrDefaultAsync(inner)
                        ?? throw CatalogPhotoRecords.Broken();
                    _ = await RequireReceiptAsync(tx, priorReceipt, prior["actorId"].AsInt64, prior["requestKey"].AsString, inner);
                    previousPhotoId = prior["_id"];
                }
                var photoRevision = input.ExpectedPhotoRevision + 1;
                if (proof.ReservationId != input.ReservationId || proof.Reference != input.Reference) throw CatalogPhotoRecords.Broken();
                var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
                var createdResult = new CatalogPrimaryPhotoResult(id, input.SkuId, new(source.DefinitionId.Value), source.DefinitionVersion.Value,
                    source.Revision, photoRevision, now);
                var row = new BsonDocument { { "_id", id.Value }, { "schemaVersion", 1 }, { "skuId", input.SkuId.Value },
                    { "skuRevision", source.Revision }, { "definitionId", source.DefinitionId.Value },
                    { "definitionVersion", source.DefinitionVersion.Value }, { "owner", CatalogRepository.Owner(source.Owner) },
                    { "photoRevision", photoRevision }, { "previousPhotoId", previousPhotoId },
                    { "reservationId", proof.ReservationId }, { "versionRowId", proof.VersionRowId },
                    { "reference", CatalogPhotoRecords.Ref(proof.Reference) }, { "validationHash", proof.ValidationHash },
                    { "mime", proof.Mime }, { "storeId", proof.StoreId }, { "generation", proof.Generation },
                    { "actorId", actor.UserId }, { "recordedAt", now.UtcDateTime }, { "reason", reason },
                    { "correlationId", correlation }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", bodyHash } };
                row["snapshotHash"] = CatalogPhotoRecords.Hash(row);
                CatalogPhotoRecords.RequirePhoto(row);
                await Rows(CatalogPhotoRecords.Photos).InsertOneAsync(tx, row, cancellationToken: inner);
                var audit = new BsonDocument { { "_id", auditId.Value }, { "schemaVersion", 1 }, { "photoId", id.Value },
                    { "skuId", input.SkuId.Value },
                    { "actorId", actor.UserId }, { "operation", CatalogPhotoRecords.Operation }, { "bodyHash", bodyHash },
                    { "snapshotHash", row["snapshotHash"] }, { "recordedAt", now.UtcDateTime } };
                await Rows(CatalogPhotoRecords.Audits).InsertOneAsync(tx, audit, cancellationToken: inner);
                await Rows(CatalogPhotoRecords.Receipts).InsertOneAsync(tx, new BsonDocument {
                    { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "actorId", actor.UserId },
                    { "operation", CatalogPhotoRecords.Operation }, { "key", metadata.IdempotencyKey }, { "bodyHash", bodyHash },
                    { "owner", CatalogRepository.Owner(source.Owner) }, { "photoId", id.Value }, { "auditId", auditId.Value },
                    { "result", CatalogPhotoRecords.Result(createdResult) }, { "recordedAt", now.UtcDateTime } }, cancellationToken: inner);
                var nextHead = new BsonDocument { { "_id", input.SkuId.Value }, { "schemaVersion", 1 },
                    { "revision", photoRevision }, { "currentPhotoId", id.Value }, { "photoHash", row["snapshotHash"] },
                    { "definitionId", source.DefinitionId.Value }, { "definitionVersion", source.DefinitionVersion.Value },
                    { "fence", head is null ? 0L : head["fence"].AsInt64 + 1 } };
                if (head is null) await Rows(CatalogPhotoRecords.Heads).InsertOneAsync(tx, nextHead, cancellationToken: inner);
                else if ((await Rows(CatalogPhotoRecords.Heads).ReplaceOneAsync(tx,
                    F.Eq("_id", head["_id"]) & F.Eq("revision", head["revision"]) & F.Eq("fence", head["fence"]) &
                    F.Eq("currentPhotoId", head["currentPhotoId"]) & F.Eq("photoHash", head["photoHash"]),
                    nextHead, cancellationToken: inner)).ModifiedCount != 1)
                    throw new PersistenceConflictException("catalog_photo_revision_conflict", "Primary photo changed; reload it");
                return createdResult;
            }, token);
            final();
            await evidence.ReleaseCatalogAssociationAsync(tx, claim, token);
            final();
            return commandResult;
            }, outer), ct);
    }

    internal async Task<CatalogPrimaryPhotoResult?> LookupAsync(ActorContext actor, string key, CancellationToken ct = default)
    {
        if (!CatalogReceiptRules.Key(key)) throw new DomainValidationException("invalid_catalog_photo_request", "A canonical command key is required");
        await CatalogPhotoStorage.RequireAsync(database, ct);
        return await unit.ExecuteAsync(async (tx, token) => {
            var receipt = await Rows(CatalogPhotoRecords.Receipts).Find(tx, F.Eq("actorId", actor.UserId) &
                F.Eq("operation", CatalogPhotoRecords.Operation) & F.Eq("key", key)).SingleOrDefaultAsync(token);
            if (receipt is null) return null;
            var photo = await Rows(CatalogPhotoRecords.Photos).Find(tx, F.Eq("_id", receipt["photoId"])).SingleOrDefaultAsync(token)
                ?? throw CatalogPhotoRecords.Broken();
            return await catalog.WithCatalogPhotoRecoveryOwnerAsync(database, tx, actor,
                photo["skuId"].AsInt64, async (owner, inner) => {
                    if (!CatalogRepository.Owner(owner).Equals(photo["owner"])) throw CatalogPhotoRecords.Broken();
                    var result = await RequireReceiptAsync(tx, receipt, actor.UserId, key, inner);
                    await RequireHeadAsync(tx, result.AssociationId.Value, inner);
                    return result;
                }, token);
        }, ct);
    }

    private async Task<CatalogPrimaryPhotoResult> RequireReceiptAsync(IClientSessionHandle tx, BsonDocument receipt,
        long actorId, string key, CancellationToken ct)
    {
        if (!CatalogRepository.Fields(receipt, "_id", "schemaVersion", "actorId", "operation", "key", "bodyHash", "owner",
            "photoId", "auditId", "result", "recordedAt") || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 ||
            receipt["actorId"] != actorId || receipt["operation"] != CatalogPhotoRecords.Operation || receipt["key"] != key ||
            !CatalogReceiptRules.Hash(receipt["bodyHash"]) || !receipt["owner"].IsBsonDocument ||
            !CatalogRepository.Positive(receipt, "photoId") || !CatalogRepository.Positive(receipt, "auditId") ||
            !receipt["result"].IsBsonDocument || !CatalogSchemas.ValidDate(receipt["recordedAt"])) throw CatalogPhotoRecords.Broken();
        var photo = await Rows(CatalogPhotoRecords.Photos).Find(tx, F.Eq("_id", receipt["photoId"])).SingleOrDefaultAsync(ct)
            ?? throw CatalogPhotoRecords.Broken();
        CatalogPhotoRecords.RequirePhoto(photo);
        var audit = await Rows(CatalogPhotoRecords.Audits).Find(tx, F.Eq("_id", receipt["auditId"])).SingleOrDefaultAsync(ct);
        if (audit is null || !CatalogRepository.Fields(audit, "_id", "schemaVersion", "photoId", "skuId", "actorId", "operation",
                "bodyHash", "snapshotHash", "recordedAt") || audit["schemaVersion"] != 1 || audit["photoId"] != photo["_id"] ||
            audit["skuId"] != photo["skuId"] ||
            audit["actorId"] != actorId || audit["operation"] != CatalogPhotoRecords.Operation || audit["bodyHash"] != photo["bodyHash"] ||
            audit["snapshotHash"] != photo["snapshotHash"] || audit["recordedAt"] != photo["recordedAt"] ||
            receipt["bodyHash"] != photo["bodyHash"] || receipt["owner"] != photo["owner"] ||
            receipt["recordedAt"] != photo["recordedAt"]) throw CatalogPhotoRecords.Broken();
        var result = CatalogPhotoRecords.Result(receipt["result"].AsBsonDocument);
        if (result.AssociationId.Value != photo["_id"].AsInt64 || result.SkuId.Value != photo["skuId"].AsInt64 ||
            result.DefinitionId.Value != photo["definitionId"].AsInt64 || result.DefinitionVersion != photo["definitionVersion"].AsInt64 ||
            result.SkuRevision != photo["skuRevision"].AsInt64 || result.PhotoRevision != photo["photoRevision"].AsInt64 ||
            result.RecordedAt != CatalogRepository.Date(photo["recordedAt"]))
            throw CatalogPhotoRecords.Broken();
        var historical = new CatalogImageUploadTarget(new EvidenceTargetRef(EvidenceTargetKind.CatalogSku, photo["skuId"].AsInt64),
            CatalogRepository.Owner(photo["owner"].AsBsonDocument), photo["skuRevision"].AsInt64,
            photo["definitionId"].AsInt64, photo["definitionVersion"].AsInt64);
        var proof = await evidence.RequireHistoricalCatalogImageAsync(tx, historical, photo["reservationId"].AsInt64,
            CatalogPhotoRecords.Ref(photo["reference"].AsBsonDocument), ct);
        if (proof.VersionRowId != photo["versionRowId"].AsInt64 || proof.ValidationHash != photo["validationHash"].AsString ||
            proof.Mime != photo["mime"].AsString || proof.StoreId != photo["storeId"].AsString ||
            proof.Generation != photo["generation"].AsString) throw CatalogPhotoRecords.Broken();
        if (photo["photoRevision"].AsInt64 > 1) {
            var prior = await Rows(CatalogPhotoRecords.Photos).Find(tx, F.Eq("_id", photo["previousPhotoId"])).SingleOrDefaultAsync(ct)
                ?? throw CatalogPhotoRecords.Broken();
            CatalogPhotoRecords.RequirePhoto(prior);
            if (prior["skuId"] != photo["skuId"] || prior["photoRevision"].AsInt64 + 1 != photo["photoRevision"].AsInt64)
                throw CatalogPhotoRecords.Broken();
            var priorReceipt = await Rows(CatalogPhotoRecords.Receipts).Find(tx, F.Eq("photoId", prior["_id"])).SingleOrDefaultAsync(ct)
                ?? throw CatalogPhotoRecords.Broken();
            _ = await RequireReceiptAsync(tx, priorReceipt, prior["actorId"].AsInt64, prior["requestKey"].AsString, ct);
        }
        return result;
    }

    private async Task RequireHeadAsync(IClientSessionHandle tx, long associationId, CancellationToken ct)
    {
        var selected = await Rows(CatalogPhotoRecords.Photos).Find(tx, F.Eq("_id", associationId)).SingleOrDefaultAsync(ct)
            ?? throw CatalogPhotoRecords.Broken();
        CatalogPhotoRecords.RequirePhoto(selected);
        var head = await Rows(CatalogPhotoRecords.Heads).Find(tx, F.Eq("_id", selected["skuId"])).SingleOrDefaultAsync(ct)
            ?? throw CatalogPhotoRecords.Broken();
        if (!CatalogRepository.Fields(head, "_id", "schemaVersion", "revision", "currentPhotoId", "photoHash",
            "definitionId", "definitionVersion", "fence") || head["schemaVersion"] != 1 ||
            !CatalogRepository.Positive(head, "revision") || head["revision"].AsInt64 > 256 ||
            !CatalogRepository.Positive(head, "currentPhotoId") || !CatalogReceiptRules.Hash(head["photoHash"]) ||
            !head["fence"].IsInt64 || head["fence"].AsInt64 < 0) throw CatalogPhotoRecords.Broken();
        var current = await Rows(CatalogPhotoRecords.Photos).Find(tx, F.Eq("_id", head["currentPhotoId"])).SingleOrDefaultAsync(ct)
            ?? throw CatalogPhotoRecords.Broken();
        CatalogPhotoRecords.RequirePhoto(current);
        if (current["skuId"] != head["_id"] || current["photoRevision"] != head["revision"] ||
            current["snapshotHash"] != head["photoHash"] || current["definitionId"] != head["definitionId"] ||
            current["definitionVersion"] != head["definitionVersion"]) throw CatalogPhotoRecords.Broken();
        var receipt = await Rows(CatalogPhotoRecords.Receipts).Find(tx, F.Eq("photoId", current["_id"])).SingleOrDefaultAsync(ct)
            ?? throw CatalogPhotoRecords.Broken();
        _ = await RequireReceiptAsync(tx, receipt, current["actorId"].AsInt64, current["requestKey"].AsString, ct);
        for (var count = 0; count < 256; count++) {
            if (current["_id"].AsInt64 == associationId) return;
            if (current["previousPhotoId"].IsBsonNull) throw CatalogPhotoRecords.Broken();
            current = await Rows(CatalogPhotoRecords.Photos).Find(tx, F.Eq("_id", current["previousPhotoId"])).SingleOrDefaultAsync(ct)
                ?? throw CatalogPhotoRecords.Broken();
            CatalogPhotoRecords.RequirePhoto(current);
            if (current["skuId"] != selected["skuId"]) throw CatalogPhotoRecords.Broken();
        }
        throw CatalogPhotoRecords.Broken();
    }
}
