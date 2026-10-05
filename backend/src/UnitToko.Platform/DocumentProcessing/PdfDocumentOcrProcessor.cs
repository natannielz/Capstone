using System.Diagnostics;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.DocumentProcessing;

internal sealed record PdfDocumentOcrProcessorObserver(Func<Task>? BeforeOriginal = null,
    Func<Task>? AfterOriginal = null, Func<Task>? AfterComputation = null);

/// <summary>One exact PDF namespace, immutable whole-original computation and a separately approved finite policy.</summary>
[SupportedOSPlatform("windows")]
internal sealed class PdfDocumentOcrProcessor
{
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly EvidenceStoreRegistration registration;
    private readonly string consumer, hostReference, modelReference, rasterReference;
    private readonly PdfDocumentOcr engine;
    private readonly EvidenceService evidence;
    private readonly OutboxStore queue;
    private readonly PdfDocumentExtractionRecords records;
    private readonly ISupplierDocumentOcrSource? supplier;
    private readonly Billing.IPaymentSupplementOcrSource? paymentSupplement;
    internal string SourceKind { get; }
    internal string PipelineId { get; }
    internal PdfRasterProvenance RasterProvenance { get; }
    internal ExtractionProvenance OcrProvenance => engine.OcrProvenance;
    internal OutboxClaimPolicy ClaimPolicy { get; }
    internal PdfDocumentOcrProcessorObserver? Observer { get; set; }

    internal PdfDocumentOcrProcessor(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        EvidenceStoreRegistration registration, string sourceKind, string consumer, PdfRasterOptions raster,
        LocalOcrOptions ocr, string hostReference, string modelReference, string rasterReference)
    {
        if (!Reference(consumer, 128) || !Reference(hostReference, 256) || !Reference(modelReference, 256) || !Reference(rasterReference, 256))
            throw new ArgumentException("Explicit whole-PDF processor approvals are required");
        var purpose = sourceKind switch { "SupplierInvoice" => ExtractionPurpose.SupplierInvoice,
            Billing.PaymentSupplementBson.SourceKind => ExtractionPurpose.Payment,
            "Payment" => throw new NotSupportedException("Payment PDF processing requires a separate supplementary evidence owner; primary payment evidence must remain an eligible image"),
            _ => throw new ArgumentException("Unsupported PDF source namespace", nameof(sourceKind)) };
        this.database = database; this.unit = unit; this.ids = ids; this.clock = clock; this.registration = registration;
        this.consumer = consumer; this.hostReference = hostReference; this.modelReference = modelReference; this.rasterReference = rasterReference;
        SourceKind = sourceKind; engine = new(raster, ocr, purpose); RasterProvenance = new WindowsPdfRaster(raster).Provenance;
        PipelineId = PdfDocumentExtractionRecords.PipelineId(sourceKind, RasterProvenance, engine.OcrProvenance);
        ClaimPolicy = new([PdfDocumentExtractionRecords.Kind(sourceKind)], 5, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        evidence = new(database, unit, ids, clock, registration); queue = new(database, clock); records = new(database);
        if (sourceKind == Billing.PaymentSupplementBson.SourceKind)
            paymentSupplement = new Billing.PaymentSupplementOcrSource(database, new Billing.PaymentSupplementEvidenceHistory(database, evidence));
        {
            var catalog = new Catalog.CatalogAuthoringService(database, unit, ids, clock);
            var suppliers = new Procurement.SupplierService(database, unit, ids, clock, catalog);
            var requests = new Procurement.ProcurementRequestService(database, unit, ids, clock, catalog);
            var orders = new Procurement.PurchaseOrderService(database, unit, ids, clock, catalog, requests, suppliers);
            var receiving = new Procurement.ProcurementReceivingService(database, unit, ids, clock, catalog, orders, requests);
            supplier = new Procurement.SupplierDocumentOcrSource(new Procurement.SupplierDocumentService(database, unit, ids, clock, catalog, orders, receiving));
        }
    }

    internal async Task<string> RunOnceAsync(CancellationToken ct = default)
    {
        await PdfDocumentExtractionRecords.RequireStorageAsync(database, ct);
        if (registration.Store is null || !await Rows(PdfDocumentExtractionRecords.Policies).Find(PolicyFilter()).AnyAsync(ct)) return "unavailable";
        var claim = await queue.ClaimAsync(consumer, ClaimPolicy, ct); if (claim is null) return "idle";
        string outcome;
        try { outcome = await ProcessClaimAsync(claim, ct); }
        catch (ProcessingUnavailable error) { outcome = error.Code; }
        catch (PersistenceConflictException error) when (error.Code is "pdf_document_processing_history_invalid" or "evidence_processing_reference_changed") {
            outcome = "ocr_integrity_mismatch";
        }
        catch (PersistenceConflictException) { outcome = "ocr_unavailable"; }
        catch (Authorization.AuthorizationDeniedException) { outcome = "ocr_original_unavailable"; }
        if (outcome == "succeeded") return await queue.CompleteAsync(claim, ct) ? "completed" : "claim_lost";
        if (!Allowed(outcome)) throw PdfDocumentExtractionRecords.Broken();
        var retryable = Retryable(outcome);
        // The queue keeps its established bounded public failure vocabulary. The distinct PDF owner retains the detailed failure.
        var failed = await queue.FailAsync(claim, QueueFailure(outcome), retryable, TimeSpan.FromMinutes(1), ct);
        return !failed ? "claim_lost" : !retryable || claim["attempts"].AsInt32 >= claim["attemptLimit"].AsInt32 ? "dead" : "retry";
    }

    internal async Task<string> ProcessClaimAsync(BsonDocument claim, CancellationToken ct = default)
    {
        await PdfDocumentExtractionRecords.RequireStorageAsync(database, ct);
        if (registration.Store is null || claim.GetValue("kind", "") != PdfDocumentExtractionRecords.Kind(SourceKind) ||
            claim.GetValue("leaseOwner", "") != consumer) throw Unavailable();
        var started = Stopwatch.GetTimestamp();
        var start = await unit.ExecuteAsync(async (tx, token) => {
            var live = await queue.FenceProcessingAsync(tx, claim, null, token); var policy = await PolicyAsync(tx, token);
            var original = await OriginalAsync(tx, live, Deadline(live, policy), token);
            var existing = await records.ResultAsync(tx, live, token);
            if (existing is not null) {
                RequireExisting(existing, policy, original); Final(live, policy, original.Until, null, started, token);
                return new Start(live, policy, original, null, "succeeded");
            }
            var failed = await records.FailureAsync(tx, live, token);
            if (failed is not null) {
                RequireExisting(failed, policy, original); Final(live, policy, Minimum(original.Until, Date(failed["until"])), null, started, token);
                return new Start(live, policy, original, null, failed["safeFailure"].AsString);
            }
            var previous = await Rows(PdfDocumentExtractionRecords.ProcessingReads).Find(tx,
                new BsonDocument { { "outboxId", live["_id"] }, { "claimToken", live["leaseToken"] } }).SingleOrDefaultAsync(token);
            BsonDocument read;
            if (previous is null) {
                read = Common((await ids.NextAsync(PdfDocumentExtractionRecords.ProcessingReads, token)).Value, live, policy, original, original.Until);
                read.Add("recordHash", PdfDocumentExtractionRecords.Hash(read));
                await Rows(PdfDocumentExtractionRecords.ProcessingReads).InsertOneAsync(tx, read, cancellationToken: token);
            } else read = await records.ReadAuditAsync(tx, previous["_id"].AsInt64, token);
            RequireRead(read, live, policy, original); Final(live, policy, Minimum(original.Until, Date(read["until"])), null, started, token);
            return new Start(live, policy, original, read, null);
        }, ct);
        if (start.Outcome is not null) return start.Outcome;
        // Every later check uses this same persisted private window. Current state cannot restart it.
        var until = Minimum(start.Original.Until, Date(start.Read!["until"]));
        using var window = new Window(clock, until, ct);
        VerifiedNativeOriginalHandle? handle = null;
        PdfDocumentOcrResult? computation = null; BsonDocument? output = null;
        (string Code, int? PageIndex)? failure = null;
        try {
            if (Observer?.BeforeOriginal is { } before) await before();
            await unit.ExecuteAsync(async (tx, token) => {
                var (live, policy, original) = await Current(tx, token);
                Final(live, policy, Minimum(until, original.Until), null, started, token); window.Check(); return true;
            }, window.Token);
            var opened = await registration.Store.OpenVerifiedForNativeValidationAsync(start.Original.Publication, start.Original.Budget, window.Token);
            if (opened is not NativeOpenOutcome.Opened ok) failure = ("ocr_original_unavailable", null);
            else {
                handle = ok.Handle;
                if (Observer?.AfterOriginal is { } afterOriginal) await afterOriginal();
                await unit.ExecuteAsync(async (tx, token) => {
                    var (live, policy, original) = await Current(tx, token);
                    RequireHandle(handle, original.Publication); Final(live, policy, Minimum(until, original.Until), handle, started, token); window.Check(); return true;
                }, window.Token);
                computation = await engine.ExtractAsync(start.Live["_id"].AsObjectId.ToString(), start.Original.Reference, handle,
                    start.Original.Publication, until, window.Token);
                window.Check();
                if (computation.JobId != start.Live["_id"].AsObjectId.ToString() || computation.OriginalEvidence != start.Original.Reference ||
                    computation.Publication != start.Original.Publication || computation.Purpose != Purpose ||
                    computation.RasterProvenance != RasterProvenance || computation.OcrProvenance != OcrProvenance)
                    failure = ("ocr_integrity_mismatch", null);
                else if (computation.Status == "failed") {
                    try { failure = PdfDocumentExtractionRecords.Failure(computation); }
                    catch (PersistenceConflictException) { failure = ("ocr_invalid_output", null); }
                } else {
                    try { output = PdfDocumentExtractionRecords.Output(computation); }
                    catch (Exception error) when (error is PersistenceConflictException or UnitToko.Modules.Contracts.DomainValidationException or
                        ArgumentException or InvalidOperationException or InvalidDataException) { failure = ("ocr_invalid_output", null); }
                }
            }
            if (Observer?.AfterComputation is { } afterComputation) await afterComputation();
            return await unit.ExecuteAsync(async (tx, token) => {
                window.Check(); var (live, policy, original) = await Current(tx, token);
                var deadline = Minimum(until, original.Until);
                if (handle is not null) RequireHandle(handle, original.Publication);
                Final(live, policy, deadline, handle, started, token);
                var prior = await records.ResultAsync(tx, live, token);
                if (prior is not null) {
                    RequireExisting(prior, policy, original); Final(live, policy, deadline, handle, started, token); window.Check(); return "succeeded";
                }
                var oldFailure = await records.FailureAsync(tx, live, token);
                if (oldFailure is not null) {
                    RequireExisting(oldFailure, policy, original); Final(live, policy, deadline, handle, started, token); window.Check(); return oldFailure["safeFailure"].AsString;
                }
                if (failure is { } bad) {
                    if (!Allowed(bad.Code)) throw PdfDocumentExtractionRecords.Broken();
                    var row = Common((await ids.NextAsync(PdfDocumentExtractionRecords.Failures, token)).Value, live, policy, original, deadline);
                    row.Add("readAuditId", start.Read["_id"]).Add("bindingHash", PdfDocumentExtractionRecords.Hash(original.Binding))
                        .Add("originalHash", PdfDocumentExtractionRecords.Hash(PdfDocumentExtractionRecords.Original(original.Publication)))
                        .Add("safeFailure", bad.Code).Add("failurePageIndex", bad.PageIndex is { } page ? new BsonInt32(page) : BsonNull.Value);
                    row.Add("recordHash", PdfDocumentExtractionRecords.Hash(row));
                    await Rows(PdfDocumentExtractionRecords.Failures).InsertOneAsync(tx, row, cancellationToken: token);
                    _ = await records.FailureAsync(tx, live, token);
                    Final(live, policy, deadline, handle, started, token); window.Check(); return bad.Code;
                }
                if (handle is null || computation is null || output is null || computation.Status != "succeeded") throw PdfDocumentExtractionRecords.Broken();
                var result = Common((await ids.NextAsync(PdfDocumentExtractionRecords.Results, token)).Value, live, policy, original, deadline);
                result.Add("readAuditId", start.Read["_id"]).Add("auditId", (await ids.NextAsync(PdfDocumentExtractionRecords.Audits, token)).Value)
                    .Add("pageCount", computation.Pages.Count).Add("requiresHumanReview", true).Add("output", output);
                result.Add("resultHash", PdfDocumentExtractionRecords.Hash(result));
                await Rows(PdfDocumentExtractionRecords.Results).InsertOneAsync(tx, result, cancellationToken: token);
                await Rows(PdfDocumentExtractionRecords.Audits).InsertOneAsync(tx, PdfDocumentExtractionRecords.Audit(result), cancellationToken: token);
                live = await queue.FenceProcessingAsync(tx, claim, PdfDocumentExtractionRecords.Receipt(result), token);
                RequireExisting(await records.ResultAsync(tx, live, token) ?? throw PdfDocumentExtractionRecords.Broken(), policy, original);
                Final(live, policy, deadline, handle, started, token); window.Check(); return "succeeded";
            }, window.Token);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProcessingUnavailable("pdf_document_deadline"); }
        finally { if (handle is not null) await handle.DisposeAsync(); }

        async Task<(BsonDocument Live, BsonDocument Policy, Original Original)> Current(IClientSessionHandle tx, CancellationToken token) {
            window.Check(); var live = await queue.FenceProcessingAsync(tx, claim, null, token); var policy = await PolicyAsync(tx, token);
            if (!PolicyIdentity(policy).Equals(PolicyIdentity(start.Policy))) throw Unavailable();
            var original = await OriginalAsync(tx, live, Minimum(until, Deadline(live, policy)), token);
            if (original.Publication != start.Original.Publication || original.Reference != start.Original.Reference || !original.Binding.Equals(start.Original.Binding))
                throw PdfDocumentExtractionRecords.Broken();
            var read = await records.ReadAuditAsync(tx, start.Read["_id"].AsInt64, token);
            if (!read.Equals(start.Read)) throw PdfDocumentExtractionRecords.Broken(); RequireRead(read, live, policy, original);
            window.Check(); return (live, policy, original);
        }
    }

    private ExtractionPurpose Purpose => PdfDocumentExtractionRecords.PurposeForSource(SourceKind);
    private async Task<Original> OriginalAsync(IClientSessionHandle tx, BsonDocument live, DateTimeOffset until, CancellationToken ct) {
        Original original;
        if (SourceKind == Billing.PaymentSupplementBson.SourceKind) {
            var value = await evidence.PaymentSupplementPdfDocumentProcessingOriginalAsync(tx, paymentSupplement!, live, until, ct);
            original = new(value.Source.PdfEvidence, value.Publication, value.Budget, value.Binding, value.Until);
        } else {
            var value = await evidence.SupplierPdfDocumentProcessingOriginalAsync(tx, supplier!, live, until, ct);
            original = new(value.Source.Evidence, value.Publication, value.Budget, value.Binding, value.Until);
        }
        if (original.Publication.Content.Length is < 1 or > PdfDocumentExtractionContract.MaximumOriginalBytes ||
            !live["payload"].Equals(PdfDocumentExtractionRecords.Payload(SourceKind, original.Binding, original.Publication)) ||
            live["sourceKey"] != PdfDocumentExtractionRecords.SourceKey(SourceKind, original.Binding["submissionId"].AsInt64))
            throw PdfDocumentExtractionRecords.Broken();
        return original;
    }
    private void RequireRead(BsonDocument read, BsonDocument live, BsonDocument policy, Original original) {
        foreach (var (name, value) in new (string, BsonValue)[] { ("outboxId", live["_id"]), ("claimToken", live["leaseToken"]), ("claimAttempt", live["attempts"]),
            ("consumerId", consumer), ("pipelineId", PipelineId), ("policyId", policy["_id"]), ("policyRevision", policy["revision"]) })
            if (read[name] != value) throw PdfDocumentExtractionRecords.Broken();
        RequireExisting(read, policy, original);
    }
    private void RequireExisting(BsonDocument row, BsonDocument policy, Original original) {
        if (row["sourceKind"] != SourceKind || row["purpose"] != Purpose.ToString() || row["inputMime"] != PdfDocumentExtractionContract.InputMime ||
            row["consumerId"] != consumer || row["pipelineId"] != PipelineId || row["policyId"] != policy["_id"] || row["policyRevision"] != policy["revision"] ||
            !row["binding"].Equals(original.Binding) || !row["original"].Equals(PdfDocumentExtractionRecords.Original(original.Publication)) ||
            !row["rasterProvenance"].Equals(PdfDocumentExtractionRecords.RasterProvenance(RasterProvenance)) ||
            !row["ocrProvenance"].Equals(PdfDocumentExtractionRecords.OcrProvenance(OcrProvenance))) throw PdfDocumentExtractionRecords.Broken();
    }
    private BsonDocument Common(long id, BsonDocument live, BsonDocument policy, Original original, DateTimeOffset until) {
        var now = clock.UtcNow; if (now >= until || until - now > TimeSpan.FromMinutes(1)) throw new ProcessingUnavailable("pdf_document_deadline");
        return new() { { "_id", id }, { "schemaVersion", 1 }, { "outboxId", live["_id"] }, { "consumerId", consumer }, { "pipelineId", PipelineId },
            { "policyId", policy["_id"] }, { "policyRevision", policy["revision"] }, { "claimToken", live["leaseToken"] }, { "claimAttempt", live["attempts"] },
            { "createdAt", now.UtcDateTime }, { "until", until.UtcDateTime }, { "sourceKind", SourceKind }, { "purpose", Purpose.ToString() },
            { "inputMime", PdfDocumentExtractionContract.InputMime }, { "binding", original.Binding }, { "original", PdfDocumentExtractionRecords.Original(original.Publication) },
            { "rasterProvenance", PdfDocumentExtractionRecords.RasterProvenance(RasterProvenance) }, { "ocrProvenance", PdfDocumentExtractionRecords.OcrProvenance(OcrProvenance) } };
    }
    private BsonDocument PolicyFilter() => PdfDocumentExtractionRecords.Shape(PdfDocumentExtractionRecords.Policies, new() {
        { "consumerId", consumer }, { "storeId", registration.StoreId }, { "pipelineId", PipelineId }, { "sourceKind", SourceKind }, { "purpose", Purpose.ToString() },
        { "inputMime", PdfDocumentExtractionContract.InputMime }, { "rasterProvenance", PdfDocumentExtractionRecords.RasterProvenance(RasterProvenance) },
        { "ocrProvenance", PdfDocumentExtractionRecords.OcrProvenance(OcrProvenance) }, { "active", true },
        { "approvedAt", new BsonDocument("$lte", clock.UtcNow.UtcDateTime) }, { "expiresAt", new BsonDocument("$gt", clock.UtcNow.UtcDateTime) },
        { "fence", new BsonDocument("$lt", long.MaxValue) }, { "hostAcceptanceRef", hostReference }, { "modelSelectionRef", modelReference }, { "rasterAcceptanceRef", rasterReference } });
    private async Task<BsonDocument> PolicyAsync(IClientSessionHandle tx, CancellationToken ct) =>
        await Rows(PdfDocumentExtractionRecords.Policies).FindOneAndUpdateAsync(tx, PolicyFilter(), new BsonDocument("$inc", new BsonDocument("fence", 1L)),
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct) ?? throw Unavailable();
    private static BsonDocument PolicyIdentity(BsonDocument row) { var copy = row.DeepClone().AsBsonDocument; copy.Remove("fence"); return copy; }
    private void Final(BsonDocument live, BsonDocument policy, DateTimeOffset until, VerifiedNativeOriginalHandle? handle, long started, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        if (clock.UtcNow >= until || clock.UtcNow >= Deadline(live, policy) || Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMinutes(1))
            throw new ProcessingUnavailable("pdf_document_deadline");
        if (handle is not null) { using var retained = handle.AcquireInput(handle.Publication, PdfDocumentExtractionContract.MaximumOriginalBytes); retained.RequireUnchanged(); }
    }
    private static void RequireHandle(VerifiedNativeOriginalHandle handle, ExpectedPublication expected) {
        if (handle.Publication != expected) throw PdfDocumentExtractionRecords.Broken();
        using var retained = handle.AcquireInput(expected, PdfDocumentExtractionContract.MaximumOriginalBytes); retained.RequireUnchanged();
    }
    private static DateTimeOffset Deadline(BsonDocument live, BsonDocument policy) => new[] { Date(live["leaseUntil"]), Date(live["claimDeadline"]), Date(policy["expiresAt"]) }.Min();
    private static DateTimeOffset Minimum(DateTimeOffset one, DateTimeOffset two) => one < two ? one : two;
    private static DateTimeOffset Date(BsonValue value) => DateTimeOffset.FromUnixTimeMilliseconds(value.AsBsonDateTime.MillisecondsSinceEpoch);
    internal static bool Reference(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && value == value.Trim() && !value.Any(char.IsControl);
    internal static bool Allowed(string code) => PdfDocumentOcrFailures.Allowed(code);
    internal static bool Retryable(string code) => PdfDocumentOcrFailures.Retryable(code);
    internal static string QueueFailure(string code) => PdfDocumentOcrFailures.QueueFailure(code);
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static PersistenceConflictException Unavailable() => new("pdf_document_processing_policy_unavailable", "Persetujuan pemroses seluruh PDF tidak tersedia; dokumen asli tetap dipertahankan.");
    private sealed record Original(EvidenceRef Reference, ExpectedPublication Publication, StorageIoBudget Budget, BsonDocument Binding, DateTimeOffset Until);
    private sealed record Start(BsonDocument Live, BsonDocument Policy, Original Original, BsonDocument? Read, string? Outcome);
    internal sealed class ProcessingUnavailable(string code) : Exception { internal string Code { get; } = code; }
    private sealed class Window : IDisposable {
        private readonly IClock clock; private readonly DateTimeOffset until; private readonly TimeSpan duration;
        private readonly long began = Stopwatch.GetTimestamp(); private readonly CancellationTokenSource limit;
        internal CancellationToken Token => limit.Token;
        internal Window(IClock clock, DateTimeOffset until, CancellationToken ct) {
            this.clock = clock; this.until = until; duration = until - clock.UtcNow;
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(1)) throw new ProcessingUnavailable("pdf_document_deadline");
            limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(duration);
        }
        internal void Check() { Token.ThrowIfCancellationRequested(); if (clock.UtcNow >= until || Stopwatch.GetElapsedTime(began) >= duration) throw new ProcessingUnavailable("pdf_document_deadline"); }
        public void Dispose() => limit.Dispose();
    }
}
