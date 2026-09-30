using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.DocumentProcessing;

internal sealed class PaymentOcrProcessor
{
    internal static readonly OutboxClaimPolicy ClaimPolicy = new(["payment.ocr.requested"], 5, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
    private readonly IMongoDatabase database; private readonly MongoUnitOfWork unit; private readonly IdAllocator ids; private readonly IClock clock;
    private readonly EvidenceStoreRegistration registration; private readonly string consumer; private readonly ILocalOcrEngine engine;
    private readonly OutboxStore queue; private readonly EvidenceService evidence; private readonly ExtractionRecords records; private readonly IPaymentOcrSource source;
    private readonly string? hostAcceptanceReference, modelSelectionReference;
    internal string PipelineId { get; }
    internal PaymentOcrProcessor(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, EvidenceStoreRegistration registration, string consumer, ILocalOcrEngine engine,
        string? hostAcceptanceReference = null, string? modelSelectionReference = null) {
        if (consumer.Length is < 1 or > 128 || consumer != consumer.Trim() || consumer.Any(char.IsControl)) throw new ArgumentException("Invalid OCR consumer");
        this.database = database; this.unit = unit; this.ids = ids; this.clock = clock; this.registration = registration; this.consumer = consumer; this.engine = engine;
        this.hostAcceptanceReference = hostAcceptanceReference; this.modelSelectionReference = modelSelectionReference;
        queue = new(database, clock); evidence = new(database, unit, ids, clock, registration); records = new(database); source = new Billing.PaymentOcrSource(database);
        PipelineId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("payment-ocr-pipeline-v1\n" + ExtractionRecords.Provenance(engine.Provenance).ToJson()))).ToLowerInvariant();
    }
    internal async Task<string> RunOnceAsync(CancellationToken ct = default) {
        await StorageAsync(ct);
        if (registration.Store is null || !await Rows("documentProcessingPolicies").Find(PolicyFilter()).AnyAsync(ct)) return "unavailable";
        var claim = await queue.ClaimAsync(consumer, ClaimPolicy, ct); if (claim is null) return "idle";
        var outcome = await ProcessClaimAsync(claim, ct);
        if (outcome == "succeeded") return await queue.CompleteAsync(claim, ct) ? "completed" : "claim_lost";
        var retryable = outcome is "ocr_unavailable" or "ocr_timeout" or "ocr_original_unavailable";
        var failed = await queue.FailAsync(claim, outcome, retryable, TimeSpan.FromMinutes(1), ct);
        return !failed ? "claim_lost" : !retryable || claim["attempts"].AsInt32 >= claim["attemptLimit"].AsInt32 ? "dead" : "retry";
    }
    internal async Task<string> ProcessClaimAsync(BsonDocument claim, CancellationToken ct = default) {
        await StorageAsync(ct);
        if (registration.Store is null || claim.GetValue("kind", "") != "payment.ocr.requested" || claim.GetValue("leaseOwner", "") != consumer) throw Unavailable();
        var started = Stopwatch.GetTimestamp();
        var start = await unit.ExecuteAsync<(BsonDocument Live, BsonDocument Policy, ProcessingOriginal Original, BsonDocument? Read, string? Outcome)>(async (tx, token) => {
            var live = await queue.FenceProcessingAsync(tx, claim, null, token); var policy = await PolicyAsync(tx, token);
            var original = await evidence.ProcessingOriginalAsync(tx, source, live, Deadline(live, policy), token);
            var existing = await records.ResultAsync(tx, live, token);
            if (existing is not null) {
                if (!existing["binding"].Equals(original.Binding) || existing["pipelineId"] != PipelineId || existing["policyId"] != policy["_id"] || existing["policyRevision"] != policy["revision"] ||
                    !existing["provenance"].Equals(ExtractionRecords.Provenance(engine.Provenance))) throw Unavailable();
                Final(live, policy, original.Until, null, started, token); return (Live: live, Policy: policy, Original: original, Read: (BsonDocument?)null, Outcome: "succeeded");
            }
            var failure = await records.FailureAsync(tx, live, token);
            if (failure is not null) { Final(live, policy, original.Until, null, started, token); return (live, policy, original, (BsonDocument?)null, failure["safeFailure"].AsString); }
            var previous = await Rows("documentProcessingReadAudits").Find(tx, new BsonDocument { { "outboxId", live["_id"] }, { "claimToken", live["leaseToken"] } }).SingleOrDefaultAsync(token);
            BsonDocument read;
            if (previous is null) {
                read = Common((await ids.NextAsync("documentProcessingReadAudits", token)).Value, live, policy, original.Until);
                read.Add("binding", original.Binding).Add("recordHash", ExtractionRecords.Hash(read));
                await Rows("documentProcessingReadAudits").InsertOneAsync(tx, read, cancellationToken: token);
            } else read = await records.ReadAuditAsync(tx, previous["_id"].AsInt64, token);
            RequireRead(read, live, policy, original); Final(live, policy, Minimum(original.Until, Date(read["until"])), null, started, token);
            return (live, policy, original, (BsonDocument?)read, (string?)null);
        }, ct);
        if (start.Outcome is not null) return start.Outcome;
        var until = Minimum(start.Original.Until, Date(start.Read!["until"])); var duration = until - clock.UtcNow;
        if (duration <= TimeSpan.Zero) throw Lost();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(duration);
        VerifiedOriginalHandle? handle = null; ExtractionResult? output = null; string? failureCode = null;
        try {
            var opened = await registration.Store.OpenVerifiedAsync(start.Original.Publication, start.Original.Budget, limit.Token);
            if (opened is not OpenOutcome.Opened ok) failureCode = "ocr_original_unavailable";
            else {
                handle = ok.Handle;
                await unit.ExecuteAsync(async (tx, token) => {
                    var (live, policy, original) = await Current(tx, token); RequireHandle(handle, original);
                    Final(live, policy, Minimum(until, original.Until), handle, started, token); return true;
                }, limit.Token);
                output = await engine.ExtractAsync(start.Live["_id"].AsObjectId.ToString(), start.Original.Source.Evidence, ExtractionPurpose.Payment, handle.Stream, limit.Token);
                if (output.JobId != start.Live["_id"].AsObjectId.ToString() || output.Evidence != start.Original.Source.Evidence || output.Purpose != ExtractionPurpose.Payment || output.Provenance != engine.Provenance) throw ExtractionRecords.Broken();
                if (output.Status != "succeeded") failureCode = output.FailureCode is "ocr_unsupported_purpose" ? "ocr_invalid_output" : output.FailureCode;
            }
            return await unit.ExecuteAsync(async (tx, token) => {
                var (live, policy, original) = await Current(tx, token);
                if (handle is not null) RequireHandle(handle, original);
                var prior = await records.ResultAsync(tx, live, token);
                if (prior is not null) { Final(live, policy, Minimum(until, original.Until), handle, started, token); return "succeeded"; }
                var deadline = Minimum(until, original.Until);
                if (failureCode is not null) {
                    var failed = Common((await ids.NextAsync("documentProcessingFailures", token)).Value, live, policy, deadline);
                    failed.Add("readAuditId", start.Read["_id"]).Add("bindingHash", ExtractionRecords.Hash(original.Binding)).Add("safeFailure", failureCode);
                    failed.Add("recordHash", ExtractionRecords.Hash(failed)); await Rows("documentProcessingFailures").InsertOneAsync(tx, failed, cancellationToken: token);
                    Final(live, policy, deadline, handle, started, token); return failureCode;
                }
                if (output is null || output.Status != "succeeded" || handle is null) throw ExtractionRecords.Broken();
                var row = Common((await ids.NextAsync("documentExtractionResults", token)).Value, live, policy, deadline);
                row.Add("binding", original.Binding).Add("readAuditId", start.Read["_id"]).Add("auditId", (await ids.NextAsync("documentExtractionAudits", token)).Value)
                    .Add("purpose", "Payment").Add("provenance", ExtractionRecords.Provenance(output.Provenance)).Add("output", ExtractionRecords.Output(output));
                row.Add("resultHash", ExtractionRecords.Hash(row));
                await Rows("documentExtractionResults").InsertOneAsync(tx, row, cancellationToken: token);
                await Rows("documentExtractionAudits").InsertOneAsync(tx, ExtractionRecords.Audit(row), cancellationToken: token);
                live = await queue.FenceProcessingAsync(tx, claim, ExtractionRecords.Receipt(row), token);
                _ = await records.ResultAsync(tx, live, token);
                Final(live, policy, deadline, handle, started, token); return "succeeded";
            }, limit.Token);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            // No private output survives an expired read window. A safe failure event may still be recorded
            // under the current job lease; this path never reopens evidence or grants additional read time.
            return await RecordFailure("ocr_timeout", ct);
        } finally { if (handle is not null) await handle.DisposeAsync(); }

        async Task<(BsonDocument Live, BsonDocument Policy, ProcessingOriginal Original)> Current(IClientSessionHandle tx, CancellationToken token) {
            var live = await queue.FenceProcessingAsync(tx, claim, null, token); var policy = await PolicyAsync(tx, token);
            if (policy["_id"] != start.Policy["_id"] || policy["revision"] != start.Policy["revision"]) throw Unavailable();
            var original = await evidence.ProcessingOriginalAsync(tx, source, live, Deadline(live, policy), token);
            if (!original.Binding.Equals(start.Original.Binding) || original.Publication != start.Original.Publication) throw ExtractionRecords.Broken();
            var read = await records.ReadAuditAsync(tx, start.Read["_id"].AsInt64, token); if (!read.Equals(start.Read)) throw ExtractionRecords.Broken();
            RequireRead(read, live, policy, original); return (live, policy, original);
        }
        async Task<string> RecordFailure(string code, CancellationToken token) => await unit.ExecuteAsync(async (tx, currentToken) => {
            var live = await queue.FenceProcessingAsync(tx, claim, null, currentToken); var policy = await PolicyAsync(tx, currentToken);
            if (policy["_id"] != start.Policy["_id"] || policy["revision"] != start.Policy["revision"]) throw Unavailable();
            var read = await records.ReadAuditAsync(tx, start.Read["_id"].AsInt64, currentToken); if (!read.Equals(start.Read)) throw ExtractionRecords.Broken();
            if (await records.ResultAsync(tx, live, currentToken) is not null) return "succeeded";
            var previous = await records.FailureAsync(tx, live, currentToken); if (previous is not null) return previous["safeFailure"].AsString;
            var deadline = Deadline(live, policy);
            var failed = Common((await ids.NextAsync("documentProcessingFailures", currentToken)).Value, live, policy, deadline);
            failed.Add("readAuditId", read["_id"]).Add("bindingHash", ExtractionRecords.Hash(read["binding"].AsBsonDocument)).Add("safeFailure", code);
            failed.Add("recordHash", ExtractionRecords.Hash(failed)); await Rows("documentProcessingFailures").InsertOneAsync(tx, failed, cancellationToken: currentToken);
            Final(live, policy, deadline, null, started, currentToken); return code;
        }, token);
    }
    private void RequireRead(BsonDocument read, BsonDocument live, BsonDocument policy, ProcessingOriginal original) {
        if (read["outboxId"] != live["_id"] || read["claimToken"] != live["leaseToken"] || read["claimAttempt"] != live["attempts"] || read["consumerId"] != consumer ||
            read["pipelineId"] != PipelineId || read["policyId"] != policy["_id"] || read["policyRevision"] != policy["revision"] || !read["binding"].Equals(original.Binding)) throw ExtractionRecords.Broken();
    }
    private static void RequireHandle(VerifiedOriginalHandle handle, ProcessingOriginal original) {
        if (!handle.Stream.CanRead || handle.Attempt != original.Publication.Attempt || handle.Content != original.Publication.Content) throw ExtractionRecords.Broken();
    }
    private BsonDocument Common(long id, BsonDocument live, BsonDocument policy, DateTimeOffset until) => new() { { "_id", id }, { "schemaVersion", 1 }, { "outboxId", live["_id"] },
        { "consumerId", consumer }, { "pipelineId", PipelineId }, { "policyId", policy["_id"] }, { "policyRevision", policy["revision"] }, { "claimToken", live["leaseToken"] },
        { "claimAttempt", live["attempts"] }, { "createdAt", clock.UtcNow.UtcDateTime }, { "until", until.UtcDateTime } };
    private BsonDocument PolicyFilter() {
        var filter = new BsonDocument { { "consumerId", consumer }, { "storeId", registration.StoreId }, { "pipelineId", PipelineId },
            { "active", true }, { "approvedAt", new BsonDocument("$lte", clock.UtcNow.UtcDateTime) }, { "expiresAt", new BsonDocument("$gt", clock.UtcNow.UtcDateTime) }, { "fence", new BsonDocument("$lt", long.MaxValue) } };
        if (hostAcceptanceReference is not null) filter.Add("hostAcceptanceRef", hostAcceptanceReference);
        if (modelSelectionReference is not null) filter.Add("modelSelectionRef", modelSelectionReference);
        return ExtractionRecords.Shape("documentProcessingPolicies", filter);
    }
    private async Task<BsonDocument> PolicyAsync(IClientSessionHandle tx, CancellationToken ct) => await Rows("documentProcessingPolicies").FindOneAndUpdateAsync(tx, PolicyFilter(),
        new BsonDocument("$inc", new BsonDocument("fence", 1L)), new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After }, ct) ?? throw Unavailable();
    private void Final(BsonDocument job, BsonDocument policy, DateTimeOffset until, VerifiedOriginalHandle? handle, long started, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        if (clock.UtcNow >= until || clock.UtcNow >= Deadline(job, policy) || Stopwatch.GetElapsedTime(started) >= ClaimPolicy.MaximumProcessingTime || handle is not null && !handle.Stream.CanRead) throw Lost();
    }
    private static DateTimeOffset Deadline(BsonDocument job, BsonDocument policy) => new[] { Date(job["leaseUntil"]), Date(job["claimDeadline"]), Date(policy["expiresAt"]) }.Min();
    private static DateTimeOffset Minimum(DateTimeOffset one, DateTimeOffset two) => one < two ? one : two;
    private static DateTimeOffset Date(BsonValue value) => DateTimeOffset.FromUnixTimeMilliseconds(value.AsBsonDateTime.MillisecondsSinceEpoch);
    private async Task StorageAsync(CancellationToken ct) {
        await DocumentProcessingStorage.RequireAsync(database, ct);
    }
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static PersistenceConflictException Unavailable() => new("document_processing_policy_unavailable", "Document processing approval is not current");
    private static PersistenceConflictException Lost() => new("document_processing_lease_lost", "Document processing lease is no longer current");
}
