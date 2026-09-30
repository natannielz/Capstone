using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.DocumentProcessing;

internal sealed class EvidenceValidationProcessor
{
    private const string Kind = "evidence.validation.requested";
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly EvidenceStoreRegistration registration;
    private readonly string consumer;
    private readonly OutboxClaimPolicy claimPolicy;
    private readonly EvidenceLimits limits;
    private readonly IContentScanner scanner;
    private readonly IImageDecoder decoder;
    private readonly OutboxStore queue;
    private readonly EvidenceService evidence;
    internal string PipelineId { get; }
    internal EvidenceValidationProcessor(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        EvidenceStoreRegistration registration, string consumer, string pipelineId, OutboxClaimPolicy claimPolicy,
        EvidenceLimits limits, IContentScanner scanner, IImageDecoder decoder) {
        StorageValidation.Token(pipelineId);
        if (consumer.Length is < 1 or > 128 || consumer != consumer.Trim() || consumer.Any(char.IsControl) ||
            !claimPolicy.Kinds.SequenceEqual([Kind]) || limits.MaximumBytes > 10 * 1024 * 1024 || limits.MaximumWidth > 32768 ||
            limits.MaximumHeight > 32768 || limits.MaximumPixels > 25_000_000) throw new ArgumentException("Invalid evidence processor registration");
        this.database = database; this.unit = unit; this.ids = ids; this.clock = clock; this.registration = registration;
        this.consumer = consumer; PipelineId = pipelineId; this.claimPolicy = claimPolicy; this.limits = limits; this.scanner = scanner; this.decoder = decoder;
        queue = new(database, clock); evidence = new(database, unit, ids, clock, registration);
    }
    internal async Task<string> RunOnceAsync(CancellationToken ct = default) {
        if (registration.Store is null || !await Rows("evidenceValidationPolicies").Find(PolicyFilter()).AnyAsync(ct)) return "unavailable";
        var claim = await queue.ClaimAsync(consumer, claimPolicy, ct); if (claim is null) return "idle";
        try {
            await ProcessClaimAsync(claim, ct);
            return await queue.CompleteAsync(claim, ct) ? "completed" : "claim_lost";
        } catch (ProcessingUnavailable ex) {
            var failed = await queue.FailAsync(claim, ex.Code, true, TimeSpan.FromMinutes(1), ct);
            return !failed ? "claim_lost" : claim["attempts"].AsInt32 >= claim["attemptLimit"].AsInt32 ? "dead" : "retry";
        }
    }
    internal async Task ProcessClaimAsync(BsonDocument claim, CancellationToken ct = default) {
        if (registration.Store is null || claim.GetValue("kind", "") != Kind || claim.GetValue("leaseOwner", "") != consumer) throw Unavailable();
        foreach (var name in new[] { "evidenceLifecycles", "evidenceLifecycleAudits" })
            await InventoryRepository.RequireStorageAsync(database, name, EvidenceAccessSchemas.Validator(name), EvidenceAccessSchemas.Key(name), ct);
        var start = await unit.ExecuteAsync(async (tx, token) => {
            var live = await queue.FenceProcessingAsync(tx, claim, null, token);
            var policy = await PolicyAsync(tx, token);
            var original = await evidence.ValidationOriginalAsync(tx, live, token);
            var existing = await EvidenceValidationRecords.ReadAsync(database, tx, live, original.Publication, original.Source, token);
            if (existing is not null) {
                RequirePolicyIdentity(existing, policy);
                await new EvidenceLifecycleStore(database, ids, clock).EnsureAsync(tx, existing, original.Publication, token);
            }
            CheckDeadline(live, policy, token); return (Live: live, Policy: policy, Original: original, Existing: existing);
        }, ct);
        if (start.Existing is not null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remaining = Deadline(start.Live) - clock.UtcNow;
        if (remaining <= TimeSpan.Zero) throw Lost(); timeout.CancelAfter(remaining);
        ContentScanResult scan; ImageDecodeResult decode = new("unavailable"); string? failure = null;
        try {
            await using (var original = await OpenAsync(start.Original, timeout.Token)) scan = await scanner.ScanAsync(original.Stream, timeout.Token);
            if (scan.Status is not ("clean" or "infected")) throw new ProcessingUnavailable("scanner_unavailable");
            if (scan.Length != start.Original.Publication.Content.Length || scan.Sha256 != start.Original.Publication.Content.Sha256) failure = "integrity_mismatch";
            else if (scan.Status == "infected") failure = "infected_media";
            else {
                await using var original = await OpenAsync(start.Original, timeout.Token);
                decode = await decoder.DecodeAsync(original.Stream, limits, timeout.Token);
                if (decode.Status == "invalid_media") failure = "invalid_media";
                else if (decode.Status != "decoded") throw new ProcessingUnavailable(decode.Status == "decode_timeout" ? "decode_timeout" : "decoder_unavailable");
                else if (decode.Length != start.Original.Publication.Content.Length || decode.Sha256 != start.Original.Publication.Content.Sha256 ||
                    decode.Mime is not ("image/png" or "image/jpeg") || !limits.AcceptsImage(decode.Length ?? 0, decode.Width ?? 0, decode.Height ?? 0, decode.Frames ?? 0, decode.Pages ?? 0)) failure = "integrity_mismatch";
            }
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProcessingUnavailable("decode_timeout"); }
        await unit.ExecuteAsync(async (tx, token) => {
            var live = await queue.FenceProcessingAsync(tx, claim, null, token);
            var policy = await PolicyAsync(tx, token);
            if (policy["_id"] != start.Policy["_id"] || policy["revision"] != start.Policy["revision"]) throw Unavailable();
            var original = await evidence.ValidationOriginalAsync(tx, live, token);
            if (!original.Source.Equals(start.Original.Source) || original.Publication != start.Original.Publication) throw EvidenceValidationRecords.Broken();
            var existing = await EvidenceValidationRecords.ReadAsync(database, tx, live, original.Publication, original.Source, token);
            if (existing is not null) {
                RequirePolicyIdentity(existing, policy); await new EvidenceLifecycleStore(database, ids, clock).EnsureAsync(tx, existing, original.Publication, token);
                CheckDeadline(live, policy, token); return true;
            }
            var row = new BsonDocument {
                { "_id", original.Source["versionRowId"] }, { "schemaVersion", 1 }, { "outboxId", live["_id"] }, { "source", original.Source },
                { "storeId", registration.StoreId }, { "generation", original.Publication.Attempt.Generation }, { "length", original.Publication.Content.Length },
                { "pipelineId", PipelineId }, { "consumerId", consumer }, { "policyId", policy["_id"] }, { "policyRevision", policy["revision"] },
                { "claimToken", live["leaseToken"] }, { "claimAttempt", live["attempts"] }, { "checkedAt", clock.UtcNow.UtcDateTime }, { "claimUntil", Deadline(live).UtcDateTime },
                { "auditId", (await ids.NextAsync("evidenceValidationAudits", token)).Value }, { "status", failure is null ? "passed" : "rejected" },
                { "scanStatus", scan.Status }, { "decoderStatus", decode.Status == "decoded" && failure is null ? "decoded" : decode.Status == "invalid_media" ? "invalid" : "unavailable" },
                { "safeFailure", Nullable(failure) }, { "mime", failure is null ? Nullable(decode.Mime) : BsonNull.Value },
                { "width", failure is null && decode.Width is { } w ? new BsonInt32(w) : BsonNull.Value }, { "height", failure is null && decode.Height is { } h ? new BsonInt32(h) : BsonNull.Value },
                { "scannerEngine", Nullable(scan.EngineVersion) }, { "scannerDefinitions", scan.SignatureVersion is { } v ? new BsonInt64(v) : BsonNull.Value },
                { "scannerDefinitionsAt", scan.SignaturesAt is { } at ? new BsonDateTime(at.UtcDateTime) : BsonNull.Value } };
            row.Add("resultHash", EvidenceValidationRecords.Hash(row));
            await Rows("evidenceValidationResults").InsertOneAsync(tx, row, cancellationToken: token);
            await Rows("evidenceValidationAudits").InsertOneAsync(tx, EvidenceValidationRecords.Audit(row), cancellationToken: token);
            live = await queue.FenceProcessingAsync(tx, claim, EvidenceValidationRecords.Receipt(row), token);
            await new EvidenceLifecycleStore(database, ids, clock).EnsureAsync(tx, row, original.Publication, token);
            CheckDeadline(live, policy, token); return true;
        }, ct);
    }
    private async Task<VerifiedOriginalHandle> OpenAsync(ValidationOriginal original, CancellationToken ct) =>
        await registration.Store!.OpenVerifiedAsync(original.Publication, original.Budget, ct) is OpenOutcome.Opened opened
            ? opened.Handle : throw new ProcessingUnavailable("processing_error");
    private BsonDocument PolicyFilter() => new("$and", new BsonArray { EvidenceValidationSchemas.Validator("evidenceValidationPolicies"), new BsonDocument {
        { "consumerId", consumer }, { "storeId", registration.StoreId }, { "pipelineId", PipelineId }, { "active", true },
        { "approvedAt", new BsonDocument("$lte", clock.UtcNow.UtcDateTime) }, { "expiresAt", new BsonDocument("$gt", clock.UtcNow.UtcDateTime) },
        { "fence", new BsonDocument("$lt", long.MaxValue) } } });
    private async Task<BsonDocument> PolicyAsync(IClientSessionHandle tx, CancellationToken ct) =>
        await Rows("evidenceValidationPolicies").FindOneAndUpdateAsync(tx, PolicyFilter(), new BsonDocument("$inc", new BsonDocument("fence", 1L)),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct) ?? throw Unavailable();
    private void RequirePolicyIdentity(BsonDocument row, BsonDocument policy) {
        if (row["pipelineId"] != PipelineId || row["consumerId"] != consumer || row["policyId"] != policy["_id"] || row["policyRevision"] != policy["revision"]) throw Unavailable();
    }
    private void CheckDeadline(BsonDocument job, BsonDocument policy, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        if (clock.UtcNow >= Deadline(job)) throw Lost();
        if (clock.UtcNow >= new DateTimeOffset(policy["expiresAt"].ToUniversalTime())) throw Unavailable();
    }
    private static DateTimeOffset Deadline(BsonDocument job) => new(job["leaseUntil"].ToUniversalTime() < job["claimDeadline"].ToUniversalTime() ? job["leaseUntil"].ToUniversalTime() : job["claimDeadline"].ToUniversalTime());
    private static BsonValue Nullable(string? text) => text is null ? BsonNull.Value : new BsonString(text);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static PersistenceConflictException Lost() => new("evidence_validation_claim_lost", "Evidence processing lease is no longer current");
    private static PersistenceConflictException Unavailable() => new("evidence_validation_policy_unavailable", "Evidence processor approval is not current");
    internal sealed class ProcessingUnavailable(string code) : Exception { internal string Code { get; } = code; }
}
