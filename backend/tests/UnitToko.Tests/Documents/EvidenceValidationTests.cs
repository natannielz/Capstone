using System.Runtime.Versioning;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class EvidenceValidationTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Real_original_is_decoded_and_result_commits_before_ack_without_changing_admission_or_money() {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reservation = await Upload(data); var processor = Processor(data); await Policy(data, processor.PipelineId);
        var before = await data.RowAsync("documentVersions", reservation.VersionRowId);
        var queue = new OutboxStore(data.DB, data.Catalog.Clock); var claim = Assert.IsType<BsonDocument>(await queue.ClaimAsync(Consumer, ClaimPolicy));
        await processor.ProcessClaimAsync(claim);
        var row = await data.RowAsync("evidenceValidationResults", reservation.VersionRowId);
        Assert.Equal("passed", row["status"]); Assert.Equal("clean", row["scanStatus"]); Assert.Equal("decoded", row["decoderStatus"]); Assert.Equal("image/png", row["mime"]);
        Assert.Equal(13, row["width"]); Assert.Equal(7, row["height"]); Assert.Equal(1, await data.Rows("evidenceValidationAudits").CountDocumentsAsync(new BsonDocument()));
        var pendingAck = await data.Rows("outboxEvents").Find(new BsonDocument("_id", claim["_id"])).SingleAsync();
        Assert.Equal("leased", pendingAck["status"]); Assert.Equal(row["resultHash"], pendingAck["processingReceipt"]["resultHash"]);
        await processor.ProcessClaimAsync(claim); Assert.Equal(1, await data.Rows("evidenceValidationResults").CountDocumentsAsync(new BsonDocument()));
        Assert.True(await queue.CompleteAsync(claim));
        var status = await data.StatusAsync(reservation.ReservationId); Assert.Equal("clean", status.ScanStatus); Assert.Equal("decoded", status.DecoderStatus); Assert.Equal("quarantined", status.State);
        var after = await data.RowAsync("documentVersions", reservation.VersionRowId); Assert.Equal(before["sha256"], after["sha256"]); Assert.Equal(before["lifecycleRevision"], after["lifecycleRevision"]);
        Assert.Equal("quarantined", after["state"]); Assert.Equal(0, await data.Rows("payments").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Crash_after_result_commit_reclaims_only_ack_without_another_scan_or_decode() {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture); var reservation = await Upload(data);
        var scanner = new FixtureScanner(); var processor = Processor(data, scanner); await Policy(data, processor.PipelineId);
        var queue = new OutboxStore(data.DB, data.Catalog.Clock); var first = Assert.IsType<BsonDocument>(await queue.ClaimAsync(Consumer, ClaimPolicy));
        await processor.ProcessClaimAsync(first); data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddSeconds(6);
        var second = Assert.IsType<BsonDocument>(await queue.ClaimAsync(Consumer, ClaimPolicy)); Assert.NotEqual(first["leaseToken"], second["leaseToken"]);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => processor.ProcessClaimAsync(first));
        await processor.ProcessClaimAsync(second); Assert.True(await queue.CompleteAsync(second)); Assert.Equal(1, scanner.Calls);
        Assert.Equal(1, await data.Rows("evidenceValidationResults").CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows("evidenceValidationAudits").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal("clean", (await data.StatusAsync(reservation.ReservationId)).ScanStatus);
    }
    [WindowsStorageFact]
    public async Task Missing_worker_policy_never_claims_a_job_or_reads_an_original() {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture); _ = await Upload(data); var scanner = new FixtureScanner(); var processor = Processor(data, scanner);
        Assert.Equal("unavailable", await processor.RunOnceAsync()); Assert.Equal(0, scanner.Calls);
        var row = await data.Rows("outboxEvents").Find(new BsonDocument("kind", "evidence.validation.requested")).SingleAsync(); Assert.Equal("pending", row["status"]);
    }
    [WindowsStorageFact]
    public async Task Unavailable_scanner_retries_without_a_terminal_result_or_clean_projection() {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture); var reservation = await Upload(data); var processor = Processor(data, new FixtureScanner("scanner_unavailable")); await Policy(data, processor.PipelineId);
        Assert.Equal("retry", await processor.RunOnceAsync()); Assert.Equal(0, await data.Rows("evidenceValidationResults").CountDocumentsAsync(new BsonDocument()));
        var status = await data.StatusAsync(reservation.ReservationId); Assert.Equal("unavailable", status.ScanStatus); Assert.Equal("unavailable", status.DecoderStatus);
        Assert.Equal("scanner_unavailable", status.SafeFailure);
    }
    [WindowsStorageFact]
    public async Task Expired_claim_and_revoked_worker_policy_cannot_commit_after_slow_scanning() {
        foreach (var revoke in new[] { false, true }) {
            await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture); _ = await Upload(data);
            var scanner = new FixtureScanner(after: async () => {
                if (revoke) await data.Rows("evidenceValidationPolicies").UpdateOneAsync(new BsonDocument("consumerId", Consumer), new BsonDocument("$set", new BsonDocument("active", false)));
                else data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddMinutes(2);
            });
            var processor = Processor(data, scanner); await Policy(data, processor.PipelineId);
            var claim = Assert.IsType<BsonDocument>(await new OutboxStore(data.DB, data.Catalog.Clock).ClaimAsync(Consumer, ClaimPolicy));
            await Assert.ThrowsAsync<PersistenceConflictException>(() => processor.ProcessClaimAsync(claim));
            Assert.Equal(0, await data.Rows("evidenceValidationResults").CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Rows("evidenceValidationAudits").CountDocumentsAsync(new BsonDocument()));
        }
    }
    internal const string Consumer = "synthetic-private-validator";
    [WindowsStorageFact]
    public async Task Production_composition_uses_actual_adapters_and_requires_durable_approval_before_streaming() {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture); var reservation = await Upload(data);
        var version = "ClamAV 1.5.1/30000/" + data.Catalog.Clock.UtcNow.AddHours(-1).UtcDateTime.ToString("ddd MMM d HH:mm:ss yyyy", System.Globalization.CultureInfo.InvariantCulture);
        // A loopback protocol simulator, not a real scanner or production host approval.
        await using var server = new LocalScannerTests.Server(version);
        var runtime = EvidenceValidationRuntime.Create(data.DB, data.Unit, new(data.DB), data.Catalog.Clock,
            new(data.Root, data.StoreId, ["S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"],
                Consumer, "synthetic-host-acceptance-fixture", new(1024, 100, 100, 10000), ImageDecoderTests.Options(), new(server.Port, "1.5.1", 30000, TimeSpan.FromDays(2), 1024, TimeSpan.FromSeconds(3))));
        Assert.Equal("unavailable", await runtime.RunOnceAsync()); Assert.Null(server.Bytes);
        await Policy(data, runtime.PipelineId); Assert.Equal("completed", await runtime.RunOnceAsync()); Assert.Equal(ImageDecoderTests.Png(13, 7), server.Bytes);
        Assert.Equal("decoded", (await data.StatusAsync(reservation.ReservationId)).DecoderStatus);
        await data.Rows("evidenceValidationPolicies").UpdateOneAsync(new BsonDocument("consumerId", Consumer), new BsonDocument("$set", new BsonDocument("active", false)));
        var revoked = await data.StatusAsync(reservation.ReservationId); Assert.Equal("unavailable", revoked.ScanStatus); Assert.Equal("processing_approval_unavailable", revoked.SafeFailure);
        Assert.Equal("unavailable", await runtime.RunOnceAsync());
    }
    [WindowsStorageFact]
    public async Task Rejected_and_unavailable_results_never_decode_infected_bytes_or_approve_money() {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture);
        var reservation = await Upload(data); var decoder = new CountingDecoder();
        var processor = new EvidenceValidationProcessor(data.DB, data.Unit, new(data.DB), data.Catalog.Clock, data.Registration, Consumer, new string('c', 64), ClaimPolicy,
            new(1024, 100, 100, 10000), new FixtureScanner("infected"), decoder); await Policy(data, processor.PipelineId);
        Assert.Equal("completed", await processor.RunOnceAsync()); Assert.Equal(0, decoder.Calls);
        var status = await data.StatusAsync(reservation.ReservationId); Assert.Equal("infected", status.ScanStatus); Assert.Equal("infected_media", status.SafeFailure);
        Assert.Equal("rejected", (await data.RowAsync("evidenceValidationResults", reservation.VersionRowId))["status"]);
    }
    [WindowsStorageFact]
    public async Task Corrupt_image_is_rejected_after_clean_scan_and_cannot_be_projected_as_decoded() {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture); var reservation = await Upload(data, "not an actual image"u8.ToArray());
        var processor = Processor(data); await Policy(data, processor.PipelineId); Assert.Equal("completed", await processor.RunOnceAsync());
        var status = await data.StatusAsync(reservation.ReservationId); Assert.Equal("clean", status.ScanStatus); Assert.Equal("invalid", status.DecoderStatus); Assert.Equal("invalid_media", status.SafeFailure);
        var row = await data.RowAsync("evidenceValidationResults", reservation.VersionRowId); row["status"] = "passed"; row["decoderStatus"] = "decoded"; row["safeFailure"] = BsonNull.Value;
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows("evidenceValidationResults").ReplaceOneAsync(new BsonDocument("_id", row["_id"]), row));
    }
    [WindowsStorageFact]
    public async Task Concurrent_execution_commits_one_result_and_damaged_receipts_audits_and_hashes_fail_closed() {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture); var reservation = await Upload(data);
        var processor = Processor(data); await Policy(data, processor.PipelineId); var queue = new OutboxStore(data.DB, data.Catalog.Clock);
        var claim = Assert.IsType<BsonDocument>(await queue.ClaimAsync(Consumer, new(["evidence.validation.requested"], 3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2))));
        var outcomes = await Task.WhenAll(Record.ExceptionAsync(() => processor.ProcessClaimAsync(claim)), Record.ExceptionAsync(() => processor.ProcessClaimAsync(claim)));
        Assert.Contains(outcomes, value => value is null);
        // Private originals retain exclusive read handles. A simultaneous loser may be temporarily
        // unavailable; retrying its exact live claim must recover the one committed result.
        foreach (var error in outcomes.Where(value => value is not null)) Assert.Equal("processing_error", Assert.IsType<EvidenceValidationProcessor.ProcessingUnavailable>(error).Code);
        await processor.ProcessClaimAsync(claim);
        Assert.Equal(1, await data.Rows("evidenceValidationResults").CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows("evidenceValidationAudits").CountDocumentsAsync(new BsonDocument()));
        var result = await data.RowAsync("evidenceValidationResults", reservation.VersionRowId);
        var audit = await data.Rows("evidenceValidationAudits").Find(new BsonDocument()).SingleAsync();
        var job = await data.Rows("outboxEvents").Find(new BsonDocument("_id", claim["_id"])).SingleAsync();
        await data.Rows("outboxEvents").UpdateOneAsync(new BsonDocument("_id", job["_id"]), new BsonDocument("$unset", new BsonDocument("processingReceipt", "")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.StatusAsync(reservation.ReservationId));
        await data.Rows("outboxEvents").ReplaceOneAsync(new BsonDocument("_id", job["_id"]), job);
        await data.Rows("evidenceValidationAudits").DeleteOneAsync(new BsonDocument("_id", audit["_id"]));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => processor.ProcessClaimAsync(claim));
        await data.Rows("evidenceValidationAudits").InsertOneAsync(audit);
        await data.Rows("evidenceValidationResults").UpdateOneAsync(new BsonDocument("_id", result["_id"]), new BsonDocument("$set", new BsonDocument("resultHash", new string('f', 64))));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.StatusAsync(reservation.ReservationId));
        await data.Rows("evidenceValidationResults").ReplaceOneAsync(new BsonDocument("_id", result["_id"]), result);
        await data.Rows("evidenceValidationResults").DeleteOneAsync(new BsonDocument("_id", result["_id"]));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.StatusAsync(reservation.ReservationId));
    }
    private sealed class CountingDecoder : IImageDecoder {
        internal int Calls;
        public Task<ImageDecodeResult> DecodeAsync(Stream original, EvidenceLimits limits, CancellationToken ct) { Calls++; throw new InvalidOperationException("Infected source must not reach decoder"); }
    }
    internal static OutboxClaimPolicy ClaimPolicy => new(["evidence.validation.requested"], 3, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2));
    internal static EvidenceValidationProcessor Processor(EvidenceAdmissionTestData data, IContentScanner? scanner = null) =>
        new(data.DB, data.Unit, new(data.DB), data.Catalog.Clock, data.Registration, Consumer, new string('c', 64), ClaimPolicy,
            new(1024, 100, 100, 10000), scanner ?? new FixtureScanner(), new WindowsImageDecoder(ImageDecoderTests.Options()));
    internal static Task Policy(EvidenceAdmissionTestData data, string pipeline) => Policy(data.DB, data.Catalog.Clock, data.StoreId, pipeline);
    internal static async Task Policy(IMongoDatabase db, IClock clock, string storeId, string pipeline) {
        var now = clock.UtcNow;
        await db.GetCollection<BsonDocument>("evidenceValidationPolicies").InsertOneAsync(new BsonDocument {
            { "_id", (await new IdAllocator(db).NextAsync("evidenceValidationPolicies")).Value }, { "schemaVersion", 1 }, { "consumerId", Consumer }, { "storeId", storeId },
            { "pipelineId", pipeline }, { "active", true }, { "revision", 1L }, { "fence", 0L }, { "approvedAt", now.AddMinutes(-1).UtcDateTime }, { "expiresAt", now.AddDays(1).UtcDateTime },
            { "hostAcceptanceRef", "synthetic-test-only-not-production-acceptance" } });
    }
    internal static async Task<ReservationResult> Upload(EvidenceAdmissionTestData data, byte[]? content = null) {
        var reservation = await data.ReserveAsync(); using var stream = new MemoryStream(content ?? ImageDecoderTests.Png(13, 7));
        var result = await data.Service.UploadAsync(data.Actor, reservation.ReservationId, reservation.Revision, stream, EvidenceAdmissionTestData.Meta()); Assert.Equal("quarantined", result.State); return reservation;
    }
    internal sealed class FixtureScanner(string state = "clean", Func<Task>? after = null) : IContentScanner {
        internal int Calls { get; private set; }
        public async Task<ContentScanResult> ScanAsync(Stream original, CancellationToken ct) {
            Calls++; using var bytes = new MemoryStream(); await original.CopyToAsync(bytes, ct); if (after is not null) await after();
            return state == "scanner_unavailable" ? new(state) : new(state, "synthetic-scanner", 1, DateTimeOffset.UnixEpoch, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant());
        }
    }
}
