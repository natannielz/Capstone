using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Documents;

/// <summary>CatalogSku request owner. Scanning is performed by the separate finite worker.</summary>
internal sealed class EvidenceImageRevalidationService(IMongoDatabase database, MongoUnitOfWork unit,
    IdAllocator ids, IClock clock, EvidenceStoreRegistration registration, CatalogAuthoringService catalog,
    string consumerId, string pipelineId)
{
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private readonly EvidenceService evidence = new(database, unit, ids, clock, registration);

    internal async Task<CatalogImageRevalidationRequestResult> RequestAsync(ActorContext actor,
        RequestCatalogImageRevalidation input, AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(metadata);
        if (input.SkuId.Value <= 0 || input.ExpectedSkuRevision <= 0 || input.ExpectedProofRevision < 0 ||
            input.ReservationId <= 0 || input.Reference is null ||
            string.IsNullOrWhiteSpace(metadata.IdempotencyKey) || metadata.IdempotencyKey.Length > 200 ||
            metadata.IdempotencyKey != metadata.IdempotencyKey.Trim() ||
            string.IsNullOrWhiteSpace(metadata.Reason) || metadata.Reason.Length > 1000 ||
            metadata.Reason != metadata.Reason.Trim() ||
            string.IsNullOrWhiteSpace(metadata.CorrelationId) || metadata.CorrelationId.Length > 128 ||
            metadata.CorrelationId != metadata.CorrelationId.Trim())
            throw new DomainValidationException("invalid_image_revalidation_request", "A current SKU, private image and command key are required");
        await CatalogPhotoStorage.RequireAsync(database, ct);
        await EvidenceImageRevalidationStorage.RequireAsync(database, ct);
        var body = new BsonDocument { { "skuId", input.SkuId.Value }, { "expectedSkuRevision", input.ExpectedSkuRevision },
            { "expectedProofRevision", input.ExpectedProofRevision }, { "reservationId", input.ReservationId },
            { "reference", new BsonDocument { { "id", input.Reference.Id }, { "version", input.Reference.Version },
                { "sha256", input.Reference.Sha256 } } }, { "reason", metadata.Reason } };
        var bodyHash = EvidenceImageRevalidationRecords.Hash(body, "bodyHash");
        return await unit.ExecuteAsync((tx, token) => catalog.WithCatalogImageUploadTargetAsync(database, tx, actor,
            new EvidenceTargetRef(EvidenceTargetKind.CatalogSku, input.SkuId.Value), input.ExpectedSkuRevision, async (owner, inner) =>
            {
                var existing = await Rows(EvidenceImageRevalidationSchemas.Receipts).Find(tx,
                    F.Eq("actorId", actor.UserId) & F.Eq("operation", EvidenceImageRevalidationRecords.Operation) &
                    F.Eq("key", metadata.IdempotencyKey)).SingleOrDefaultAsync(inner);
                if (existing is not null)
                {
                    if (existing["bodyHash"] != bodyHash) throw new PersistenceConflictException("idempotency_key_reused", "Revalidation key belongs to another request");
                    var saved = await Rows(EvidenceImageRevalidationSchemas.Requests).Find(tx,
                        F.Eq("_id", existing["requestId"])).SingleOrDefaultAsync(inner) ?? throw EvidenceImageRevalidationRecords.Broken();
                    if (saved["requestHash"] != existing["requestHash"] || saved["skuId"] != input.SkuId.Value ||
                        saved["versionRowId"] != existing["versionRowId"] || saved["bodyHash"] != bodyHash)
                        throw EvidenceImageRevalidationRecords.Broken();
                    _ = await EvidenceImageRevalidationRecords.CurrentAsync(database, tx, saved["versionRowId"].AsInt64, inner);
                    return Result(saved, await EvidenceImageRevalidationRecords.ResultAsync(database, tx, saved, inner));
                }
                var source = await evidence.CatalogImageRevalidationOriginalAsync(tx, owner,
                    input.ReservationId, input.Reference, inner);
                var versionId = source.Source["versionRowId"].AsInt64;
                var current = await EvidenceImageRevalidationRecords.CurrentAsync(database, tx, versionId, inner);
                if ((current.Head is null ? 0L : current.Head["revision"].AsInt64) != input.ExpectedProofRevision)
                    throw new PersistenceConflictException("image_revalidation_changed", "Reload the current image scan");
                if (input.ExpectedProofRevision >= 256)
                    throw new PersistenceConflictException("image_revalidation_history_full", "Image scan history needs operator review");
                if (current.Head?.GetValue("status", "") == "pending")
                    throw new PersistenceConflictException("image_revalidation_pending", "Finish the current scan before requesting another");
                var policy = await Rows(EvidenceImageRevalidationSchemas.Policies).FindOneAndUpdateAsync(tx,
                    new BsonDocument("$and", new BsonArray {
                        EvidenceImageRevalidationSchemas.Validator(EvidenceImageRevalidationSchemas.Policies),
                        new BsonDocument { { "storeId", registration.StoreId }, { "consumerId", consumerId },
                            { "pipelineId", pipelineId }, { "active", true },
                            { "approvedAt", new BsonDocument("$lte", clock.UtcNow.UtcDateTime) },
                            { "expiresAt", new BsonDocument("$gt", clock.UtcNow.UtcDateTime) },
                            { "fence", new BsonDocument("$lt", long.MaxValue) } } }),
                    new BsonDocument("$inc", new BsonDocument("fence", 1L)),
                    new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, inner)
                    ?? throw new PersistenceConflictException("image_revalidation_policy_unavailable", "Reviewed scanner approval is required");
                var requestId = (await ids.NextAsync(EvidenceImageRevalidationSchemas.Requests, inner)).Value;
                var jobId = (await ids.NextAsync(EvidenceImageRevalidationSchemas.Jobs, inner)).Value;
                var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
                var request = new BsonDocument { { "_id", requestId }, { "schemaVersion", 1 },
                    { "skuId", input.SkuId.Value }, { "reservationId", input.ReservationId },
                    { "versionRowId", versionId }, { "source", source.Source },
                    { "headRevision", current.Head is null ? 0L : current.Head["revision"].AsInt64 },
                    { "previousRequestId", current.Head is null ? BsonNull.Value : current.Head["requestId"] },
                    { "previousRequestHash", current.Head is null ? BsonNull.Value : current.Head["requestHash"] },
                    { "policyId", policy["_id"] }, { "policyRevision", policy["revision"] },
                    { "pipelineId", pipelineId }, { "consumerId", consumerId },
                    { "actorId", actor.UserId }, { "requestKey", metadata.IdempotencyKey }, { "bodyHash", bodyHash },
                    { "reason", metadata.Reason }, { "correlationId", metadata.CorrelationId },
                    { "recordedAt", now.UtcDateTime } };
                request["requestHash"] = EvidenceImageRevalidationRecords.Hash(request, "requestHash");
                await Rows(EvidenceImageRevalidationSchemas.Requests).InsertOneAsync(tx, request, cancellationToken: inner);
                await Rows(EvidenceImageRevalidationSchemas.Receipts).InsertOneAsync(tx, new BsonDocument {
                    { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "actorId", actor.UserId },
                    { "operation", EvidenceImageRevalidationRecords.Operation }, { "key", metadata.IdempotencyKey },
                    { "bodyHash", bodyHash }, { "requestId", requestId }, { "versionRowId", versionId },
                    { "requestHash", request["requestHash"] }, { "recordedAt", now.UtcDateTime } }, cancellationToken: inner);
                await Rows(EvidenceImageRevalidationSchemas.Jobs).InsertOneAsync(tx, new BsonDocument {
                    { "_id", jobId }, { "schemaVersion", 1 }, { "requestId", requestId }, { "versionRowId", versionId },
                    { "consumerId", consumerId }, { "pipelineId", pipelineId }, { "storeId", registration.StoreId },
                    { "policyId", policy["_id"] }, { "policyRevision", policy["revision"] },
                    { "sourceKey", $"evidence.image-revalidation:{requestId}" }, { "kind", EvidenceImageRevalidationRecords.Kind },
                    { "status", "pending" }, { "attempts", 0 }, { "dueAt", now.UtcDateTime },
                    { "createdAt", now.UtcDateTime }, { "leaseOwner", BsonNull.Value }, { "leaseToken", BsonNull.Value },
                    { "leaseUntil", BsonNull.Value }, { "claimDeadline", BsonNull.Value },
                    { "resultHash", BsonNull.Value }, { "updatedAt", now.UtcDateTime } }, cancellationToken: inner);
                var head = new BsonDocument { { "_id", versionId }, { "schemaVersion", 1 },
                    { "revision", (current.Head is null ? 0L : current.Head["revision"].AsInt64) + 1 },
                    { "requestId", requestId }, { "requestHash", request["requestHash"] },
                    { "resultId", BsonNull.Value }, { "resultHash", BsonNull.Value }, { "status", "pending" },
                    { "fence", current.Head is null ? 0L : current.Head["fence"].AsInt64 + 1 } };
                if (current.Head is null) await Rows(EvidenceImageRevalidationSchemas.Heads).InsertOneAsync(tx, head, cancellationToken: inner);
                else if ((await Rows(EvidenceImageRevalidationSchemas.Heads).ReplaceOneAsync(tx,
                    current.Head, head, cancellationToken: inner)).ModifiedCount != 1)
                    throw new PersistenceConflictException("image_revalidation_changed", "Reload the current image scan");
                if (clock.UtcNow >= new DateTimeOffset(policy["expiresAt"].ToUniversalTime()))
                    throw new PersistenceConflictException("image_revalidation_policy_unavailable", "Reviewed scanner approval expired");
                return Result(request, null);
            }, token), ct);
    }

    internal static CatalogImageRevalidationRequestResult Result(BsonDocument request, BsonDocument? result) =>
        new(new(request["_id"].AsInt64), new(request["skuId"].AsInt64),
            new(request["versionRowId"].AsInt64), result is null ? "pending" : result["status"].AsString,
            new DateTimeOffset(request["recordedAt"].ToUniversalTime()));

    internal Task<CatalogImageRevalidationRequestResult?> LookupAsync(ActorContext actor, string key,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200 || key != key.Trim())
            throw new DomainValidationException("invalid_image_revalidation_request", "A canonical command key is required");
        return unit.ExecuteAsync(async (tx, token) =>
        {
            var receipt = await Rows(EvidenceImageRevalidationSchemas.Receipts).Find(tx,
                F.Eq("actorId", actor.UserId) & F.Eq("operation", EvidenceImageRevalidationRecords.Operation) &
                F.Eq("key", key)).SingleOrDefaultAsync(token);
            if (receipt is null) return null;
            return await catalog.WithCatalogPhotoRecoveryOwnerAsync(database, tx, actor,
                (await Rows(EvidenceImageRevalidationSchemas.Requests).Find(tx, F.Eq("_id", receipt["requestId"]))
                    .SingleOrDefaultAsync(token) ?? throw EvidenceImageRevalidationRecords.Broken())["skuId"].AsInt64,
                async (owner, inner) =>
                {
                    var request = await Rows(EvidenceImageRevalidationSchemas.Requests).Find(tx,
                        F.Eq("_id", receipt["requestId"])).SingleOrDefaultAsync(inner)
                        ?? throw EvidenceImageRevalidationRecords.Broken();
                    if (!request["source"]["owner"].Equals(CatalogRepository.Owner(owner)) ||
                        request["requestHash"] != receipt["requestHash"] ||
                        request["versionRowId"] != receipt["versionRowId"]) throw EvidenceImageRevalidationRecords.Broken();
                    var source = request["source"].AsBsonDocument;
                    var historical = new CatalogImageUploadTarget(
                        new EvidenceTargetRef(EvidenceTargetKind.CatalogSku, request["skuId"].AsInt64),
                        owner, source["skuRevision"].AsInt64, source["definitionId"].AsInt64,
                        source["definitionVersion"].AsInt64);
                    var reference = new EvidenceRef(source["reference"]["id"].AsInt64,
                        source["reference"]["version"].AsInt64, source["reference"]["sha256"].AsString);
                    var original = await evidence.RequireHistoricalCatalogImageAsync(tx, historical,
                        request["reservationId"].AsInt64, reference, inner);
                    if (original.ValidationHash != source["originalValidationHash"].AsString ||
                        original.VersionRowId != source["versionRowId"].AsInt64 ||
                        original.StoreId != source["storeId"].AsString ||
                        original.Generation != source["generation"].AsString)
                        throw EvidenceImageRevalidationRecords.Broken();
                    _ = await EvidenceImageRevalidationRecords.CurrentAsync(database, tx, request["versionRowId"].AsInt64, inner);
                    return Result(request, await EvidenceImageRevalidationRecords.ResultAsync(database, tx, request, inner));
                }, token);
        }, ct);
    }

    internal Task<CatalogImageRevalidationCurrent> CurrentAsync(ActorContext actor, long skuId,
        long reservationId, EvidenceRef reference, CancellationToken ct = default)
    {
        if (skuId <= 0 || reservationId <= 0 || reference is null)
            throw new DomainValidationException("invalid_image_revalidation_request", "Select an actual private Catalog SKU image");
        return unit.ExecuteAsync((tx, token) => catalog.WithCatalogImageUploadTargetAsync(database, tx, actor,
            new EvidenceTargetRef(EvidenceTargetKind.CatalogSku, skuId), null, async (owner, inner) =>
            {
                var source = await evidence.CatalogImageRevalidationOriginalAsync(tx, owner, reservationId, reference, inner);
                var (head, result) = await EvidenceImageRevalidationRecords.CurrentAsync(database, tx,
                    source.Source["versionRowId"].AsInt64, inner);
                return new CatalogImageRevalidationCurrent(new(skuId), new(source.Source["versionRowId"].AsInt64),
                    owner.Revision, head?["revision"].AsInt64 ?? 0L, head?["status"].AsString ?? "first_validation",
                    result is null ? null : new DateTimeOffset(result["checkedAt"].ToUniversalTime()),
                    result is not null && result["status"] == "passed" ? result["mime"].AsString : null,
                    result?["pipelineId"].AsString);
            }, token), ct);
    }
}
