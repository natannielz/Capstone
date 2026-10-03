using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.DocumentProcessing;

internal sealed class VideoValidationProcessor
{
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly EvidenceStoreRegistration registration;
    private readonly string consumer;
    private readonly OutboxClaimPolicy claimPolicy;
    private readonly VideoEvidenceLimits limits;
    private readonly IContentScanner scanner;
    private readonly WindowsVideoDecoder decoder;
    private readonly string hostAcceptanceReference;
    private readonly string clamdConfigurationAcceptanceReference;
    private readonly OutboxStore queue;
    private readonly EvidenceService evidence;
    internal string PipelineId { get; }
    internal VideoValidationProcessor(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        EvidenceStoreRegistration registration, string consumer, string pipelineId, OutboxClaimPolicy claimPolicy,
        VideoEvidenceLimits limits, IContentScanner scanner, WindowsVideoDecoder decoder, string hostAcceptanceReference,
        string clamdConfigurationAcceptanceReference) {
        StorageValidation.Token(pipelineId);
        ArgumentNullException.ThrowIfNull(limits); ArgumentNullException.ThrowIfNull(scanner); ArgumentNullException.ThrowIfNull(decoder);
        if (consumer.Length is < 1 or > 128 || consumer != consumer.Trim() || consumer.Any(char.IsControl) ||
            !claimPolicy.Kinds.SequenceEqual([VideoValidationRecords.Kind]) || !Reference(hostAcceptanceReference) || !Reference(clamdConfigurationAcceptanceReference))
            throw new ArgumentException("Invalid private video processor registration");
        this.database = database; this.unit = unit; this.ids = ids; this.clock = clock; this.registration = registration;
        this.consumer = consumer; PipelineId = pipelineId; this.claimPolicy = claimPolicy; this.limits = limits; this.scanner = scanner; this.decoder = decoder;
        this.hostAcceptanceReference = hostAcceptanceReference; this.clamdConfigurationAcceptanceReference = clamdConfigurationAcceptanceReference;
        queue = new(database, clock); evidence = new(database, unit, ids, clock, registration);
    }
    internal async Task<string> RunOnceAsync(CancellationToken ct = default) {
        if (registration.Store is null) return "unavailable";
        await RequireStorageAsync(ct);
        if (!await Rows(VideoValidationRecords.Policies).Find(PolicyFilter()).AnyAsync(ct)) return "unavailable";
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
        if (registration.Store is null || claim.GetValue("kind", "") != VideoValidationRecords.Kind || claim.GetValue("leaseOwner", "") != consumer) throw Unavailable();
        await RequireStorageAsync(ct);
        var start = await unit.ExecuteAsync(async (tx, token) => {
            var live = await queue.FenceProcessingAsync(tx, claim, null, token);
            var policy = await PolicyAsync(tx, token);
            var original = await evidence.VideoValidationOriginalAsync(tx, live, token);
            var existing = await VideoValidationRecords.ReadAsync(database, tx, live, original.Publication, original.Source, token);
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
        ContentScanResult scan; VideoDecodeResult decode = new("unavailable"); string? failure = null;
        try {
            await using (var original = await OpenScanAsync(start.Original, timeout.Token)) scan = await scanner.ScanAsync(original.Stream, timeout.Token);
            if (scan.Status is not ("clean" or "infected") || !ScannerFacts(scan)) throw new ProcessingUnavailable("scanner_unavailable");
            if (scan.Length != start.Original.Publication.Content.Length || scan.Sha256 != start.Original.Publication.Content.Sha256) failure = "integrity_mismatch";
            else if (scan.Status == "infected") failure = "infected_media";
            else {
                if (!OperatingSystem.IsWindows()) throw new ProcessingUnavailable("decoder_unavailable");
                await using var original = await OpenDecodeAsync(start.Original, timeout.Token);
                decode = await decoder.DecodeAsync(original, start.Original.Publication, limits, timeout.Token);
                if (decode.Status == "invalid_media") failure = "invalid_media";
                else if (decode.Status != "decoded") throw new ProcessingUnavailable(decode.Status == "decode_timeout" ? "decode_timeout" : "decoder_unavailable");
                else if (!DecodedFacts(decode, start.Original.Publication.Content)) failure = "integrity_mismatch";
            }
            // Scan/decode perform file IO outside retrying transactions. Only immutable facts are retried below.
            await unit.ExecuteAsync(async (tx, token) => {
                var live = await queue.FenceProcessingAsync(tx, claim, null, token);
                var policy = await PolicyAsync(tx, token);
                if (!PolicyIdentity(policy).Equals(PolicyIdentity(start.Policy))) throw Unavailable();
                var original = await evidence.VideoValidationOriginalAsync(tx, live, token);
                if (!original.Source.Equals(start.Original.Source) || original.Publication != start.Original.Publication) throw VideoValidationRecords.Broken();
                var existing = await VideoValidationRecords.ReadAsync(database, tx, live, original.Publication, original.Source, token);
                if (existing is not null) {
                    RequirePolicyIdentity(existing, policy); await new EvidenceLifecycleStore(database, ids, clock).EnsureAsync(tx, existing, original.Publication, token);
                    CheckDeadline(live, policy, token); return true;
                }
                var row = new BsonDocument {
                    { "_id", original.Source["versionRowId"] }, { "schemaVersion", 1 }, { "outboxId", live["_id"] }, { "source", original.Source },
                    { "original", VideoValidationRecords.Original(original.Publication) }, { "storeId", registration.StoreId },
                    { "generation", original.Publication.Attempt.Generation }, { "length", original.Publication.Content.Length },
                    { "pipelineId", PipelineId }, { "consumerId", consumer }, { "policyId", policy["_id"] }, { "policyRevision", policy["revision"] },
                    { "claimToken", live["leaseToken"] }, { "claimAttempt", live["attempts"] }, { "checkedAt", clock.UtcNow.UtcDateTime }, { "claimUntil", Deadline(live).UtcDateTime },
                    { "auditId", (await ids.NextAsync(VideoValidationRecords.Audits, token)).Value }, { "status", failure is null ? "passed" : "rejected" },
                    { "scanStatus", scan.Status }, { "decoderStatus", failure is null ? "decoded" : failure == "invalid_media" ? "invalid" : "unavailable" },
                    { "safeFailure", Nullable(failure) }, { "mime", failure is null ? Nullable(decode.Mime) : BsonNull.Value },
                    { "videoCodec", failure is null ? Nullable(decode.VideoCodec) : BsonNull.Value },
                    { "width", failure is null && decode.Width is { } w ? new BsonInt32(w) : BsonNull.Value },
                    { "height", failure is null && decode.Height is { } h ? new BsonInt32(h) : BsonNull.Value },
                    { "durationSeconds", failure is null && decode.DurationSeconds is { } duration ? new BsonDecimal128(duration) : BsonNull.Value },
                    { "videoFrames", failure is null && decode.VideoFrames is { } frames ? new BsonInt64(frames) : BsonNull.Value },
                    { "audioCodec", failure is null ? Nullable(decode.AudioCodec) : BsonNull.Value },
                    { "audioChannels", failure is null && decode.AudioChannels is { } channels ? new BsonInt32(channels) : BsonNull.Value },
                    { "audioSampleRate", failure is null && decode.AudioSampleRate is { } rate ? new BsonInt32(rate) : BsonNull.Value },
                    { "audioFrames", failure is null && decode.AudioFrames is { } audioFrames ? new BsonInt64(audioFrames) : BsonNull.Value },
                    { "audioSamples", failure is null && decode.AudioSamples is { } samples ? new BsonInt64(samples) : BsonNull.Value },
                    { "scannerEngine", scan.EngineVersion! }, { "scannerDefinitions", scan.SignatureVersion!.Value }, { "scannerDefinitionsAt", scan.SignaturesAt!.Value.UtcDateTime } };
                row.Add("resultHash", VideoValidationRecords.Hash(row));
                await Rows(VideoValidationRecords.Results).InsertOneAsync(tx, row, cancellationToken: token);
                await Rows(VideoValidationRecords.Audits).InsertOneAsync(tx, VideoValidationRecords.Audit(row), cancellationToken: token);
                live = await queue.FenceProcessingAsync(tx, claim, VideoValidationRecords.Receipt(row), token);
                await new EvidenceLifecycleStore(database, ids, clock).EnsureAsync(tx, row, original.Publication, token);
                CheckDeadline(live, policy, token); return true;
            }, timeout.Token);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProcessingUnavailable("decode_timeout"); }
    }
    private async Task RequireStorageAsync(CancellationToken ct) {
        foreach (var name in VideoValidationSchemas.Collections)
            await InventoryRepository.RequireStorageAsync(database, name, VideoValidationSchemas.Validator(name), VideoValidationSchemas.Key(name), ct);
        foreach (var name in new[] { "evidenceLifecycles", "evidenceLifecycleAudits" })
            await InventoryRepository.RequireStorageAsync(database, name, EvidenceAccessSchemas.Validator(name), EvidenceAccessSchemas.Key(name), ct);
    }
    private async Task<VerifiedOriginalHandle> OpenScanAsync(ValidationOriginal original, CancellationToken ct) =>
        await registration.Store!.OpenVerifiedAsync(original.Publication, original.Budget, ct) is OpenOutcome.Opened opened
            ? opened.Handle : throw new ProcessingUnavailable("processing_error");
    private async Task<VerifiedNativeOriginalHandle> OpenDecodeAsync(ValidationOriginal original, CancellationToken ct) =>
        await registration.Store!.OpenVerifiedForNativeValidationAsync(original.Publication, original.Budget, ct) is NativeOpenOutcome.Opened opened
            ? opened.Handle : throw new ProcessingUnavailable("processing_error");
    private bool DecodedFacts(VideoDecodeResult row, StorageContent original) => row.Length == original.Length && row.Sha256 == original.Sha256 &&
        original.Length <= limits.MaximumBytes && limits.AcceptsDimensions(row.Width ?? 0, row.Height ?? 0) &&
        row.DurationSeconds is > 0 && row.DurationSeconds <= limits.MaximumDuration.Ticks / (decimal)TimeSpan.TicksPerSecond &&
        row.VideoFrames is > 0 && row.VideoFrames <= limits.MaximumVideoFrames &&
        (row.Mime == "video/mp4" && row.VideoCodec is "h264" or "hevc" && row.AudioCodec is null or "aac" ||
         row.Mime == "video/webm" && row.VideoCodec is "vp8" or "vp9" && row.AudioCodec is null or "opus" or "vorbis") &&
        (row.AudioCodec is null ? row.AudioChannels is null && row.AudioSampleRate is null && row.AudioFrames == 0 && row.AudioSamples == 0 :
         row.AudioChannels is > 0 && row.AudioChannels <= limits.MaximumAudioChannels && row.AudioSampleRate is > 0 && row.AudioSampleRate <= limits.MaximumAudioSampleRate &&
         row.AudioFrames is > 0 && row.AudioFrames <= limits.MaximumAudioFrames && row.AudioSamples is > 0 && row.AudioSamples <= limits.MaximumAudioSamples);
    private bool ScannerFacts(ContentScanResult row) => !string.IsNullOrWhiteSpace(row.EngineVersion) && row.EngineVersion.Length <= 40 &&
        !row.EngineVersion.Any(char.IsControl) && row.SignatureVersion is > 0 && row.SignaturesAt is { } at && at <= clock.UtcNow;
    private BsonDocument PolicyFilter() => new("$and", new BsonArray { VideoValidationSchemas.Validator(VideoValidationRecords.Policies), new BsonDocument {
        { "consumerId", consumer }, { "storeId", registration.StoreId }, { "pipelineId", PipelineId }, { "active", true },
        { "hostAcceptanceRef", hostAcceptanceReference }, { "clamdConfigurationAcceptanceRef", clamdConfigurationAcceptanceReference },
        { "approvedAt", new BsonDocument("$lte", clock.UtcNow.UtcDateTime) }, { "expiresAt", new BsonDocument("$gt", clock.UtcNow.UtcDateTime) },
        { "fence", new BsonDocument("$lt", long.MaxValue) } } });
    private async Task<BsonDocument> PolicyAsync(IClientSessionHandle tx, CancellationToken ct) =>
        await Rows(VideoValidationRecords.Policies).FindOneAndUpdateAsync(tx, PolicyFilter(), new BsonDocument("$inc", new BsonDocument("fence", 1L)),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct) ?? throw Unavailable();
    private void RequirePolicyIdentity(BsonDocument row, BsonDocument policy) {
        if (row["pipelineId"] != PipelineId || row["consumerId"] != consumer || row["policyId"] != policy["_id"] || row["policyRevision"] != policy["revision"]) throw Unavailable();
    }
    private static BsonDocument PolicyIdentity(BsonDocument policy) { var copy = policy.DeepClone().AsBsonDocument; copy.Remove("fence"); return copy; }
    private void CheckDeadline(BsonDocument job, BsonDocument policy, CancellationToken ct) {
        ct.ThrowIfCancellationRequested(); if (clock.UtcNow >= Deadline(job)) throw Lost();
        if (clock.UtcNow >= new DateTimeOffset(policy["expiresAt"].ToUniversalTime())) throw Unavailable();
    }
    private static DateTimeOffset Deadline(BsonDocument job) => new(job["leaseUntil"].ToUniversalTime() < job["claimDeadline"].ToUniversalTime() ? job["leaseUntil"].ToUniversalTime() : job["claimDeadline"].ToUniversalTime());
    private static bool Reference(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value == value.Trim() && !value.Any(char.IsControl);
    private static BsonValue Nullable(string? value) => value is null ? BsonNull.Value : new BsonString(value);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static PersistenceConflictException Lost() => new("evidence_validation_claim_lost", "Video processing lease is no longer current");
    private static PersistenceConflictException Unavailable() => new("evidence_video_validation_policy_unavailable", "Video processor approval is not current");
    internal sealed class ProcessingUnavailable(string code) : Exception { internal string Code { get; } = code; }
}
