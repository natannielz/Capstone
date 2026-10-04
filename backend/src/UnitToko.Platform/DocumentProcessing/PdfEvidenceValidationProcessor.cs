using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.DocumentProcessing;

internal sealed class PdfEvidenceValidationProcessor
{
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly EvidenceStoreRegistration registration;
    private readonly string consumer;
    private readonly OutboxClaimPolicy claimPolicy;
    private readonly IContentScanner scanner;
    private readonly WindowsPdfRaster raster;
    private readonly OutboxStore queue;
    private readonly EvidenceService evidence;
    private readonly PdfEvidenceValidationService approval;
    internal string PipelineId { get; }

    internal PdfEvidenceValidationProcessor(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        EvidenceStoreRegistration registration, string consumer, string pipelineId, OutboxClaimPolicy claimPolicy,
        IContentScanner scanner, WindowsPdfRaster raster, string hostAcceptanceReference, string clamdConfigurationAcceptanceReference)
    {
        ArgumentNullException.ThrowIfNull(scanner); ArgumentNullException.ThrowIfNull(raster); ArgumentNullException.ThrowIfNull(claimPolicy);
        StorageValidation.Token(pipelineId);
        if (!Reference(consumer, 128) || !Reference(hostAcceptanceReference, 256) || !Reference(clamdConfigurationAcceptanceReference, 256) ||
            !claimPolicy.Kinds.SequenceEqual([PdfEvidenceValidationRecords.Kind]) || claimPolicy.MaximumAttempts > 5)
            throw new ArgumentException("Invalid private PDF processor registration");
        this.database = database; this.unit = unit; this.ids = ids; this.clock = clock; this.registration = registration;
        this.consumer = consumer; PipelineId = pipelineId; this.claimPolicy = claimPolicy; this.scanner = scanner; this.raster = raster;
        queue = new(database, clock); evidence = new(database, unit, ids, clock, registration);
        approval = new(database, clock, consumer, pipelineId, registration.StoreId, hostAcceptanceReference, clamdConfigurationAcceptanceReference);
    }
    internal async Task<string> RunOnceAsync(CancellationToken ct = default)
    {
        if (registration.Store is null) return "unavailable";
        await approval.RequireStorageAsync(ct);
        if (!await approval.IsApprovedAsync(ct)) return "unavailable";
        var claim = await queue.ClaimAsync(consumer, claimPolicy, ct); if (claim is null) return "idle";
        try {
            await ProcessClaimAsync(claim, ct);
            return await queue.CompleteAsync(claim, ct) ? "completed" : "claim_lost";
        } catch (ProcessingUnavailable error) {
            var failed = await queue.FailAsync(claim, error.Code, true, TimeSpan.FromMinutes(1), ct);
            return !failed ? "claim_lost" : claim["attempts"].AsInt32 >= claim["attemptLimit"].AsInt32 ? "dead" : "retry";
        }
    }
    internal async Task ProcessClaimAsync(BsonDocument claim, CancellationToken ct = default)
    {
        if (registration.Store is null || claim.GetValue("kind", "") != PdfEvidenceValidationRecords.Kind ||
            claim.GetValue("leaseOwner", "") != consumer) throw PdfEvidenceValidationService.Unavailable();
        await approval.RequireStorageAsync(ct);
        var start = await unit.ExecuteAsync(async (tx, token) => {
            var live = await queue.FenceProcessingAsync(tx, claim, null, token);
            var policy = await approval.TouchPolicyAsync(tx, token);
            var original = await evidence.PdfValidationOriginalAsync(tx, live, token);
            if (original.Publication.Content.Length is < 1 or > PdfEvidenceValidationContract.MaximumOriginalBytes) throw PdfEvidenceValidationRecords.Broken();
            var existing = await PdfEvidenceValidationRecords.ReadAsync(database, tx, live, original.Publication, original.Source, token);
            if (existing is not null) {
                approval.RequireResultPolicy(existing, policy);
                await new EvidenceLifecycleStore(database, ids, clock).EnsureAsync(tx, existing, original.Publication, token);
            }
            CheckDeadline(live, policy, token);
            return (Live: live, Policy: policy, Original: original, Existing: existing);
        }, ct);
        if (start.Existing is not null) return;
        var until = new[] { Deadline(start.Live), new DateTimeOffset(start.Policy["expiresAt"].ToUniversalTime()), clock.UtcNow.AddSeconds(60) }.Min();
        using var window = new ProcessingWindow(clock, until, ct);
        ContentScanResult scan; PdfRasterResult? decoded = null; string? failure = null;
        try {
            window.Check();
            await using (var original = await OpenScanAsync(start.Original, window.Token)) scan = await scanner.ScanAsync(original.Stream, window.Token);
            window.Check();
            if (scan.Status is not ("clean" or "infected") || !ScannerFacts(scan, clock.UtcNow)) throw new ProcessingUnavailable("scanner_unavailable");
            if (scan.Length != start.Original.Publication.Content.Length || scan.Sha256 != start.Original.Publication.Content.Sha256) failure = "integrity_mismatch";
            else if (scan.Status == "infected") failure = "infected_media";
            else {
                if (!OperatingSystem.IsWindows()) throw new ProcessingUnavailable("decoder_unavailable");
                await using var original = await OpenRasterAsync(start.Original, window.Token);
                decoded = await raster.RasterizeAsync(original, start.Original.Publication, until, window.Token);
                window.Check();
                if (decoded.Status != "rasterized") {
                    failure = RasterFailure(decoded.FailureCode);
                    if (failure is null) throw new ProcessingUnavailable(decoded.FailureCode == "pdf_raster_deadline" ? "decode_timeout" : "decoder_unavailable");
                } else if (decoded.Original != start.Original.Publication) failure = "integrity_mismatch";
            }
            // Scanner/parser IO never runs in the transaction retry callback.
            await unit.ExecuteAsync(async (tx, token) => {
                window.Check();
                var live = await queue.FenceProcessingAsync(tx, claim, null, token);
                var policy = await approval.TouchPolicyAsync(tx, token);
                if (!PdfEvidenceValidationService.PolicyIdentity(policy).Equals(PdfEvidenceValidationService.PolicyIdentity(start.Policy)))
                    throw PdfEvidenceValidationService.Unavailable();
                var original = await evidence.PdfValidationOriginalAsync(tx, live, token);
                if (!original.Source.Equals(start.Original.Source) || original.Publication != start.Original.Publication) throw PdfEvidenceValidationRecords.Broken();
                var existing = await PdfEvidenceValidationRecords.ReadAsync(database, tx, live, original.Publication, original.Source, token);
                if (existing is not null) {
                    approval.RequireResultPolicy(existing, policy);
                    await new EvidenceLifecycleStore(database, ids, clock).EnsureAsync(tx, existing, original.Publication, token);
                    CheckDeadline(live, policy, token); window.Check(); return true;
                }
                var row = new BsonDocument {
                    { "_id", original.Source["versionRowId"] }, { "schemaVersion", 1 }, { "outboxId", live["_id"] }, { "source", original.Source },
                    { "original", PdfEvidenceValidationRecords.Original(original.Publication) }, { "storeId", registration.StoreId },
                    { "generation", original.Publication.Attempt.Generation }, { "length", original.Publication.Content.Length },
                    { "pipelineId", PipelineId }, { "consumerId", consumer }, { "policyId", policy["_id"] }, { "policyRevision", policy["revision"] },
                    { "claimToken", live["leaseToken"] }, { "claimAttempt", live["attempts"] }, { "checkedAt", clock.UtcNow.UtcDateTime }, { "claimUntil", until.UtcDateTime },
                    { "auditId", (await ids.NextAsync(PdfEvidenceValidationRecords.Audits, token)).Value }, { "status", failure is null ? "passed" : "rejected" },
                    { "scanStatus", scan.Status }, { "decoderStatus", failure is null ? "decoded" : failure == "invalid_media" ? "invalid" : "unavailable" },
                    { "safeFailure", failure is null ? BsonNull.Value : new BsonString(failure) },
                    { "mime", failure is null ? new BsonString(PdfEvidenceValidationContract.Mime) : BsonNull.Value },
                    { "pageCount", failure is null ? new BsonInt32(decoded!.Pages.Count) : BsonNull.Value },
                    { "rasterProvenance", failure is null ? PdfEvidenceValidationRecords.Provenance(decoded!.Provenance) : BsonNull.Value },
                    { "pages", failure is null ? new BsonArray(decoded!.Pages.Select(PdfEvidenceValidationRecords.Page)) : new BsonArray() },
                    { "scannerEngine", scan.EngineVersion! }, { "scannerDefinitions", scan.SignatureVersion!.Value }, { "scannerDefinitionsAt", scan.SignaturesAt!.Value.UtcDateTime } };
                if (!PdfEvidenceValidationRecords.CompletePages(row)) throw new ProcessingUnavailable("decoder_unavailable");
                row.Add("resultHash", PdfEvidenceValidationRecords.Hash(row));
                await Rows(PdfEvidenceValidationRecords.Results).InsertOneAsync(tx, row, cancellationToken: token);
                await Rows(PdfEvidenceValidationRecords.Audits).InsertOneAsync(tx, PdfEvidenceValidationRecords.Audit(row), cancellationToken: token);
                live = await queue.FenceProcessingAsync(tx, claim, PdfEvidenceValidationRecords.Receipt(row), token);
                await new EvidenceLifecycleStore(database, ids, clock).EnsureAsync(tx, row, original.Publication, token);
                CheckDeadline(live, policy, token); window.Check(); return true;
            }, window.Token);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProcessingUnavailable("decode_timeout"); }
    }
    private async Task<VerifiedOriginalHandle> OpenScanAsync(ValidationOriginal original, CancellationToken ct) =>
        await registration.Store!.OpenVerifiedAsync(original.Publication, original.Budget, ct) is OpenOutcome.Opened opened
            ? opened.Handle : throw new ProcessingUnavailable("processing_error");
    private async Task<VerifiedNativeOriginalHandle> OpenRasterAsync(ValidationOriginal original, CancellationToken ct) =>
        await registration.Store!.OpenVerifiedForNativeValidationAsync(original.Publication, original.Budget, ct) is NativeOpenOutcome.Opened opened
            ? opened.Handle : throw new ProcessingUnavailable("processing_error");
    internal static bool ScannerFacts(ContentScanResult row, DateTimeOffset now) => Reference(row.EngineVersion, 40) &&
        row.SignatureVersion is > 0 && row.SignaturesAt is { } at && at <= now;
    internal static string? RasterFailure(string? code) => code switch {
        "pdf_raster_source_mismatch" => "integrity_mismatch",
        "pdf_raster_source_limit" or "pdf_raster_malformed_pdf" or "pdf_raster_encrypted_pdf" or "pdf_raster_unsupported_forms" or
        "pdf_raster_unsupported_attachments" or "pdf_raster_unsupported_javascript" or "pdf_raster_page_limit" or
        "pdf_raster_geometry_limit" or "pdf_raster_total_pixel_limit" or "pdf_raster_output_limit" => "invalid_media",
        _ => null };
    private void CheckDeadline(BsonDocument job, BsonDocument policy, CancellationToken ct) {
        ct.ThrowIfCancellationRequested(); if (clock.UtcNow >= Deadline(job)) throw new PersistenceConflictException("evidence_validation_claim_lost", "PDF processing lease is no longer current");
        if (clock.UtcNow >= new DateTimeOffset(policy["expiresAt"].ToUniversalTime())) throw PdfEvidenceValidationService.Unavailable();
    }
    private static DateTimeOffset Deadline(BsonDocument job) => new(job["leaseUntil"].ToUniversalTime() < job["claimDeadline"].ToUniversalTime()
        ? job["leaseUntil"].ToUniversalTime() : job["claimDeadline"].ToUniversalTime());
    internal static bool Reference(string? text, int maximum) => !string.IsNullOrWhiteSpace(text) && text.Length <= maximum && text == text.Trim() && !text.Any(char.IsControl);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal sealed class ProcessingUnavailable(string code) : Exception { internal string Code { get; } = code; }
    private sealed class ProcessingWindow : IDisposable
    {
        private readonly IClock clock; private readonly DateTimeOffset until; private readonly TimeSpan duration;
        private readonly long began = Stopwatch.GetTimestamp(); private readonly CancellationTokenSource cancellation;
        internal CancellationToken Token => cancellation.Token;
        internal ProcessingWindow(IClock clock, DateTimeOffset until, CancellationToken ct) {
            this.clock = clock; this.until = until; duration = until - clock.UtcNow;
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(60)) throw new ProcessingUnavailable("decode_timeout");
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct); cancellation.CancelAfter(duration);
        }
        internal void Check() {
            Token.ThrowIfCancellationRequested();
            if (clock.UtcNow >= until || Stopwatch.GetElapsedTime(began) >= duration) throw new ProcessingUnavailable("decode_timeout");
        }
        public void Dispose() => cancellation.Dispose();
    }
}
