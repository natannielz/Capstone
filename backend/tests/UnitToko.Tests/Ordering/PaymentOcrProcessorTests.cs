using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentOcrProcessorTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Exact_submitted_original_produces_one_private_result_and_recovers_without_reprocessing_or_money() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await Submit(data);
        var engine = new FixtureEngine(); var processor = Processor(data, engine); await Policy(data, processor);
        var queue = new OutboxStore(data.Database, data.Catalog.Clock);
        var claim = await queue.ClaimAsync(Consumer, PaymentOcrProcessor.ClaimPolicy); Assert.NotNull(claim);
        Assert.Equal("succeeded", await processor.ProcessClaimAsync(claim)); Assert.Equal(1, engine.Calls);
        Assert.Equal(1, await data.Rows("documentExtractionResults").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("documentExtractionAudits").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("documentProcessingReadAudits").CountDocumentsAsync(new BsonDocument()));
        engine.Before = () => throw new InvalidOperationException("Committed extraction must recover without another original read/engine call");
        Assert.Equal("succeeded", await processor.ProcessClaimAsync(claim)); Assert.Equal(1, engine.Calls);
        data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddSeconds(31);
        var recovered = await queue.ClaimAsync(Consumer, PaymentOcrProcessor.ClaimPolicy); Assert.NotNull(recovered);
        Assert.Equal("succeeded", await processor.ProcessClaimAsync(recovered)); Assert.Equal(1, engine.Calls);
        Assert.False(await queue.CompleteAsync(claim)); Assert.True(await queue.CompleteAsync(recovered)); Assert.Equal("idle", await processor.RunOnceAsync());
        Assert.Equal(1, await data.Rows("documentProcessingReadAudits").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("payments").CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal("submitted", (await data.Payment.Service.ReadAsync(data.Actor, data.DeclarationId)).State);
    }

    [WindowsStorageFact]
    public async Task Disabled_policy_after_actual_bytes_cannot_commit_extraction() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await Submit(data);
        var engine = new FixtureEngine(); var processor = Processor(data, engine); await Policy(data, processor);
        var queue = new OutboxStore(data.Database, data.Catalog.Clock); var claim = await queue.ClaimAsync(Consumer, PaymentOcrProcessor.ClaimPolicy); Assert.NotNull(claim);
        engine.Before = async () => { await data.Rows("documentProcessingPolicies").UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("active", false))); };
        await Assert.ThrowsAnyAsync<Exception>(() => processor.ProcessClaimAsync(claim));
        Assert.Equal(0, await data.Rows("documentExtractionResults").CountDocumentsAsync(new BsonDocument()));
        Assert.False((await data.Rows("outboxEvents").Find(new BsonDocument("_id", claim["_id"])).SingleAsync()).Contains("processingReceipt"));
    }

    [WindowsStorageFact]
    public async Task Current_buyer_can_read_scoped_suggestions_but_another_person_missing_audit_or_revoked_membership_cannot() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await Submit(data);
        async Task<PaymentExtractionView> Read() => await data.Service.ReadExtractionAsync(data.Actor, data.DeclarationId, 1, data.Reference, new("payment-ocr-view", "synthetic-read"));
        Assert.Equal("queued", (await Read()).State);
        var processor = Processor(data, new FixtureEngine()); await Policy(data, processor); Assert.Equal("completed", await processor.RunOnceAsync());
        var view = await Read(); Assert.Equal("succeeded", view.State); Assert.Equal("100.00", view.Extraction!.Fields.Single(field => field.Field == "amount").Value);
        Assert.Equal(2, await data.Rows("documentExtractionReadAudits").CountDocumentsAsync(new BsonDocument()));
        await Assert.ThrowsAnyAsync<Exception>(() => data.Service.ReadExtractionAsync(data.Payment.Invoice.Finance, data.DeclarationId, 1, data.Reference, new("payment-ocr-view", "wrong-owner")));
        await Assert.ThrowsAnyAsync<Exception>(() => data.Service.ReadExtractionAsync(data.Actor, data.DeclarationId, 1, data.Reference, new("wrong-purpose", "wrong-purpose")));
        await Assert.ThrowsAnyAsync<Exception>(() => data.Service.ReadExtractionAsync(data.Actor, data.DeclarationId, 1, new(data.Reference.Id, data.Reference.Version, new string('f',64)), new("payment-ocr-view", "wrong-hash")));
        var audit = await data.Rows("documentExtractionAudits").Find(new BsonDocument()).SingleAsync();
        await data.Rows("documentExtractionAudits").DeleteOneAsync(new BsonDocument("_id", audit["_id"]));
        await Assert.ThrowsAnyAsync<Exception>(() => Read()); await data.Rows("documentExtractionAudits").InsertOneAsync(audit);
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "documentExtractionReadAudits") })).SingleAsync();
        var validator = definition["options"]["validator"];
        await data.Database.RunCommandAsync<MongoDB.Bson.BsonDocument>(new BsonDocument { { "collMod", "documentExtractionReadAudits" }, { "validator", new BsonDocument("$and", new BsonArray { validator, new BsonDocument("_id", new BsonDocument("$lt", 0L)) }) } });
        await Assert.ThrowsAnyAsync<MongoException>(() => Read());
        await data.Database.RunCommandAsync<MongoDB.Bson.BsonDocument>(new BsonDocument { { "collMod", "documentExtractionReadAudits" }, { "validator", validator } });
        await data.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.Actor.UserId), new BsonDocument("$set", new BsonDocument("status", "revoked")));
        await Assert.ThrowsAnyAsync<Exception>(() => Read());
        Assert.Equal(2, await data.Rows("documentExtractionReadAudits").CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Retry_retains_safe_failure_and_lost_claim_cannot_publish_late_output() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await Submit(data);
        var engine = new FixtureEngine { Failure = "ocr_unavailable" }; var processor = Processor(data, engine); await Policy(data, processor);
        Assert.Equal("retry", await processor.RunOnceAsync()); Assert.Equal(1, engine.Calls);
        Assert.Equal(1, await data.Rows("documentProcessingFailures").CountDocumentsAsync(new BsonDocument()));
        data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddMinutes(1);
        engine.Failure = null;
        engine.Before = async () => {
            data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddSeconds(31);
            var replacement = await new OutboxStore(data.Database, data.Catalog.Clock).ClaimAsync(Consumer, PaymentOcrProcessor.ClaimPolicy); Assert.NotNull(replacement);
        };
        await Assert.ThrowsAnyAsync<Exception>(() => processor.RunOnceAsync());
        Assert.Equal(0, await data.Rows("documentExtractionResults").CountDocumentsAsync(new BsonDocument()));
        engine.Before = null; data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddSeconds(31);
        Assert.Equal("completed", await processor.RunOnceAsync()); Assert.Equal(3, engine.Calls);
        Assert.Equal(1, await data.Rows("documentExtractionResults").CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Wrong_reference_or_missing_pin_is_never_processed_and_unapproved_policy_is_idle() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await Submit(data);
        var engine = new FixtureEngine(); var processor = Processor(data, engine);
        Assert.Equal("unavailable", await processor.RunOnceAsync()); Assert.Equal(0, engine.Calls);
        await Policy(data, processor);
        var mismatched = new PaymentOcrProcessor(data.Database, new(data.Catalog.Client), new(data.Database), data.Catalog.Clock, data.Files.Registration, Consumer, engine, "other-host", "other-model");
        Assert.Equal("unavailable", await mismatched.RunOnceAsync()); Assert.Equal(0, engine.Calls);
        var pin = await data.Rows("evidencePins").Find(new BsonDocument()).SingleAsync(); await data.Rows("evidencePins").DeleteOneAsync(new BsonDocument("_id", pin["_id"]));
        await Assert.ThrowsAnyAsync<Exception>(() => processor.RunOnceAsync()); Assert.Equal(0, engine.Calls);
        await data.Rows("evidencePins").InsertOneAsync(pin); data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddSeconds(31);
        engine.WrongReference = true; await Assert.ThrowsAnyAsync<Exception>(() => processor.RunOnceAsync());
        Assert.Equal(0, await data.Rows("documentExtractionResults").CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Expired_original_read_window_records_only_a_safe_retry_without_publishing_suggestions() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await Submit(data);
        var engine = new FixtureEngine { Before = () => Task.Delay(TimeSpan.FromSeconds(6)) };
        var processor = Processor(data, engine); await Policy(data, processor);
        Assert.Equal("retry", await processor.RunOnceAsync());
        var failure = await data.Rows("documentProcessingFailures").Find(new BsonDocument()).SingleAsync(); Assert.Equal("ocr_timeout", failure["safeFailure"]);
        Assert.False(failure.Contains("output")); Assert.Equal(0, await data.Rows("documentExtractionResults").CountDocumentsAsync(new BsonDocument()));
        Assert.False((await data.Rows("outboxEvents").Find(new BsonDocument("kind", "payment.ocr.requested")).SingleAsync()).Contains("processingReceipt"));
    }

    [LocalOcrFact]
    public async Task Actual_native_candidate_processes_the_pinned_original_through_Mongo_and_scoped_read() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await Submit(data);
        var options = LocalOcrTests.Options(); var processor = Processor(data, new WindowsLocalOcrEngine(options));
        var runtimeOptions = new PaymentOcrRuntimeOptions(data.Files.Root, data.Files.StoreId, ["S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"],
            Consumer, "synthetic-host-only", "synthetic-model-only", options);
        var runtime = PaymentOcrRuntime.Create(data.Database, new(data.Catalog.Client), new(data.Database), data.Catalog.Clock, runtimeOptions);
        Assert.Equal(processor.PipelineId, runtime.PipelineId); Assert.Equal("unavailable", await runtime.RunOnceAsync());
        await Policy(data, processor); Assert.Equal("completed", await runtime.RunOnceAsync());
        var result = await data.Service.ReadExtractionAsync(data.Actor, data.DeclarationId, 1, data.Reference, new("payment-ocr-view", "actual-native-empty-original"));
        Assert.Equal("succeeded", result.State); Assert.Contains("no_text", result.Extraction!.QualityFlags);
        Assert.All(result.Extraction.Fields, field => Assert.Null(field.Value)); Assert.Equal(WindowsLocalOcrEngine.Engine, result.Extraction.Provenance.EngineVersion);
        Assert.Equal(0, await data.Rows("payments").CountDocumentsAsync(new BsonDocument()));
    }

    [WindowsStorageFact]
    public async Task Expired_captured_original_deadline_refuses_before_result_persistence_and_preserves_money() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await Submit(data);
        var journal = await data.Rows("journalEntries").Find(new BsonDocument()).SingleAsync();
        var engine = new FixtureEngine(); var processor = Processor(data, engine); await Policy(data, processor);
        var queue = new OutboxStore(data.Database, data.Catalog.Clock);
        var claim = await queue.ClaimAsync(Consumer, PaymentOcrProcessor.ClaimPolicy); Assert.NotNull(claim);
        var beforeExpiry = data.Catalog.Clock.UtcNow;
        engine.Before = async () => {
            // FixtureEngine invokes this only after reading the actual protected original stream.
            var read = await data.Rows("documentProcessingReadAudits").Find(new BsonDocument()).SingleAsync();
            data.Catalog.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(read["until"].AsBsonDateTime.MillisecondsSinceEpoch + 1);
        };
        try {
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => processor.ProcessClaimAsync(claim));
            Assert.Equal("document_processing_lease_lost", error.Code);
            foreach (var table in new[] { "documentExtractionResults", "documentExtractionAudits", "documentProcessingFailures" })
                Assert.Equal(0, await data.Rows(table).CountDocumentsAsync(new BsonDocument()));
            Assert.False((await data.Rows("outboxEvents").Find(new BsonDocument("_id", claim["_id"])).SingleAsync()).Contains("processingReceipt"));
        } finally { data.Catalog.Clock.UtcNow = beforeExpiry; }
        engine.Before = null;
        Assert.Equal("succeeded", await processor.ProcessClaimAsync(claim)); Assert.Equal(2, engine.Calls);
        Assert.Equal(journal, await data.Rows("journalEntries").Find(new BsonDocument()).SingleAsync());
        Assert.Equal(0, await data.Rows("payments").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal("submitted", (await data.Payment.Service.ReadAsync(data.Actor, data.DeclarationId)).State);
    }

    [WindowsStorageFact]
    public async Task Required_result_audit_failure_rolls_back_result_receipt_and_money() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await Submit(data);
        var engine = new FixtureEngine(); var processor = Processor(data, engine); await Policy(data, processor);
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "documentExtractionAudits") })).SingleAsync();
        var original = definition["options"]["validator"];
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "documentExtractionAudits" }, { "validator", new BsonDocument("$and", new BsonArray { original, new BsonDocument("resultId", new BsonDocument("$lt", 0L)) }) } });
        await Assert.ThrowsAnyAsync<MongoException>(() => processor.RunOnceAsync());
        Assert.Equal(0, await data.Rows("documentExtractionResults").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("documentExtractionAudits").CountDocumentsAsync(new BsonDocument()));
        Assert.False((await data.Rows("outboxEvents").Find(new BsonDocument("kind", "payment.ocr.requested")).SingleAsync()).Contains("processingReceipt"));
    }

    internal const string Consumer = "synthetic-payment-ocr";
    internal static PaymentOcrProcessor Processor(PaymentEvidenceReadTests.ReadData data, ILocalOcrEngine engine) =>
        new(data.Database, new(data.Catalog.Client), new(data.Database), data.Catalog.Clock, data.Files.Registration, Consumer, engine);
    internal static async Task Submit(PaymentEvidenceReadTests.ReadData data) {
        await data.ValidateAsync(); await data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, data.Reservation.ReservationId, data.Reference, EvidenceAdmissionTestData.Meta("ocr-submission"));
    }
    internal static Task Policy(PaymentEvidenceReadTests.ReadData data, PaymentOcrProcessor processor) => data.Rows("documentProcessingPolicies").InsertOneAsync(new BsonDocument {
        { "_id", 1L }, { "schemaVersion", 1 }, { "consumerId", Consumer }, { "storeId", data.Files.StoreId }, { "pipelineId", processor.PipelineId },
        { "active", true }, { "revision", 1L }, { "fence", 0L }, { "approvedAt", data.Catalog.Clock.UtcNow.AddMinutes(-1).UtcDateTime },
        { "expiresAt", data.Catalog.Clock.UtcNow.AddHours(1).UtcDateTime }, { "hostAcceptanceRef", "synthetic-host-only" }, { "modelSelectionRef", "synthetic-model-only" } });
    internal sealed class FixtureEngine : ILocalOcrEngine {
        public ExtractionProvenance Provenance { get; } = new("synthetic-test-engine", "synthetic-test-model", new string('c',64), new string('d',64), "synthetic-test/" + PaymentFieldExtractor.Version);
        internal Func<Task>? Before; internal int Calls; internal string? Failure; internal bool WrongReference;
        public async Task<ExtractionResult> ExtractAsync(string jobId, EvidenceRef evidence, ExtractionPurpose purpose, Stream bytes, CancellationToken ct) {
            Calls++; using var copied = new MemoryStream(); await bytes.CopyToAsync(copied, ct);
            Assert.NotEmpty(copied.ToArray()); if (Before is not null) await Before();
            if (WrongReference) evidence = new(evidence.Id, evidence.Version, new string('f',64));
            if (Failure is not null) return new(jobId, evidence, purpose, Provenance, "failed", Failure, []);
            return new(jobId, evidence, purpose, Provenance, "succeeded", null, [new("Nominal: Rp 100,00", .98m, new(.1m,.1m,.8m,.1m))]);
        }
    }
}
