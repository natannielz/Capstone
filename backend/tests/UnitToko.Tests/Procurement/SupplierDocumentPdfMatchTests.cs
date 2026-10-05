using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using Xunit.Abstractions;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class SupplierDocumentPdfMatchTests(MongoFixture fixture, ITestOutputHelper output)
{
    private const string ProfileSource = "UnitToko.SupplierDocumentMatching";
    private static readonly ImmutableHashSet<string> ProfileStages = ImmutableHashSet.Create(StringComparer.Ordinal,
        "matching.storage", "matching.context", "matching.verify-sources", "matching.pdf-candidates", "matching.resolve",
        "matching.quality-heads", "matching.source-capacities", "matching.fence", "source.inspection-history", "source.quality",
        "quality.receipt", "quality.capacity", "quality.policy", "quality.configuration", "quality.effects", "quality.disposition",
        "receipt.history", "receipt.order", "receipt.inspection", "receipt.configuration", "receipt.stock-effects");

    [Fact]
    public void Absent_PDF_reference_preserves_old_plain_and_quality_input_bytes_and_malformed_annotation_is_refused() {
        var input = new SupplierDocumentMatchInput(1, 1, new(ProcurementRequestTestData.Location, 2, 1, 3, SupplierDocumentKind.Invoice,
            "SYNTHETIC", new(2026, 10, 5), "IDR", new Money(100), [new("001", "Sealed unit", "supplier", "0001", "0002", 1,
                new Money(100), new Money(0), new Money(0), null, null)], null), null,
            [new(1, 1, 4, 1, null, null, new Money(100), new Money(0), new Money(0), null, null)]);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.DoesNotContain("pdfObservations", JsonSerializer.Serialize(input, json), StringComparison.Ordinal);
        Assert.False(SupplierDocumentBson.Input(input).Contains("pdfObservations"));
        var quality = input with { Lines = [input.Lines[0] with { QualityAllocationId = 5 }] };
        Assert.DoesNotContain("pdfObservations", JsonSerializer.Serialize(quality, json), StringComparison.Ordinal);
        Assert.Equal(5L, SupplierDocumentBson.Input(quality)["lines"][0]["qualityAllocationId"]);
        foreach (var legacy in new[] { input, quality }) {
            var originalBody = new { input = new { documentId = legacy.DocumentId, documentRevision = legacy.DocumentRevision,
                reviewedInput = legacy.ReviewedInput, ocrResultId = legacy.OcrResultId, lines = legacy.Lines }, binding = new string('a', 64) };
            Assert.Equal(JsonSerializer.Serialize(originalBody, json), JsonSerializer.Serialize(SupplierDocumentBson.MatchBody(legacy, new string('a', 64)), json));
        }
        var observation = new SupplierDocumentPdfMatchObservation("total_amount", new(6, new string('a', 64), new(7, 1, new string('b', 64)), 1, "total_amount"), true);
        var copied = SupplierDocumentRules.Copy(input with { PdfObservations = ImmutableArray.Create(observation) });
        Assert.NotNull(copied.PdfObservations); Assert.Equal(observation, Assert.Single(copied.PdfObservations.Value));
        foreach (var wrong in new[] {
            input with { PdfObservations = ImmutableArray<SupplierDocumentPdfMatchObservation>.Empty },
            input with { PdfObservations = ImmutableArray.Create(observation, observation) },
            input with { PdfObservations = ImmutableArray.Create(observation with { OriginalReviewed = false }) },
            input with { PdfObservations = ImmutableArray.Create(observation with { Field = "supplier_name" }) },
            input with { OcrResultId = 8, PdfObservations = ImmutableArray.Create(observation) } })
            Assert.Equal("invalid_supplier_document", Assert.Throws<DomainValidationException>(() => SupplierDocumentRules.Copy(wrong)).Code);
    }

    [WindowsStorageFact]
    public async Task Actual_all_page_references_persist_beside_manual_normal_and_quality_matching_and_original_key_replays_after_restart() {
        foreach (var quality in new[] { false, true }) await RunPositiveAsync(quality);
    }

    [WindowsStorageFact]
    public Task Actual_all_page_references_persist_beside_manual_quality_matching_and_original_key_replays_after_restart() =>
        RunPositiveAsync(quality: true);

    private async Task RunPositiveAsync(bool quality) {
        var elapsed = Stopwatch.StartNew();
        using var listener = new ActivityListener {
            ShouldListenTo = source => source.Name == ProfileSource,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ProfileStages.Contains(options.Name)
                ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            SampleUsingParentId = (ref ActivityCreationOptions<string> options) => ProfileStages.Contains(options.Name)
                ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStopped = activity => {
                if (activity.Source.Name == ProfileSource && ProfileStages.Contains(activity.OperationName))
                    output.WriteLine($"M89 duration mode={(quality ? "quality" : "normal")} stage={activity.OperationName} durationMs=" +
                        activity.Duration.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            }
        };
        ActivitySource.AddActivityListener(listener);
        void Stage(string stage) => output.WriteLine($"M89 positive mode={(quality ? "quality" : "normal")} stage={stage} elapsedMs={elapsed.ElapsedMilliseconds}");
        Stage("create-start");
        await using var fixtureData = await SupplierDocumentPdfMatchTestData.CreateAsync(fixture, quality);
        Stage("create-complete");
        var data = fixtureData.Data; var input = fixtureData.AnnotatedInput();
        // The existing uploader already owns Match+EvidenceRead and a verified person.
        // Supplier matching adds no new independent-uploader rule.
        var actor = data.Actor; Stage("preview-start");
        var preview = await data.Evidence.PreviewMatchAsync(actor, input);
        Stage("preview-complete");
        var metadata = SupplierDocumentTestData.Meta("real-pdf-human-match-" + quality);
        Stage("save-start");
        var saved = await data.Evidence.MatchAsync(actor, input, preview["binding"].AsString, metadata);
        Stage("save-complete");
        var row = await data.Rows(SupplierDocumentBson.Matches).Find(new BsonDocument("_id", saved["matchId"])).SingleAsync();
        Assert.Equal(quality ? 4 : 3, row["schemaVersion"].AsInt32); Assert.True(row["input"]["ocrResultId"].IsBsonNull);
        Assert.Equal(SupplierDocumentBson.Input(input), row["input"]);
        var refRow = Assert.Single(row["input"]["pdfObservations"].AsBsonArray);
        Assert.Equal(1, refRow["candidate"]["pageIndex"]); Assert.Equal(fixtureData.Result["_id"], refRow["candidate"]["resultId"]);
        Assert.Equal(fixtureData.Result["resultHash"], refRow["candidate"]["resultHash"]);
        Assert.Equal(fixtureData.Result["binding"]["reference"], refRow["candidate"]["evidence"]);
        Assert.True(refRow["originalReviewed"].AsBoolean);
        // The selected PDF document total is 34000; the human allocation stays 20000+2000+500.
        Assert.Equal(2, row["input"]["lines"][0]["packages"]); Assert.Equal("20000.00", row["input"]["lines"][0]["netAmount"]);
        Assert.Equal("2000.00", row["input"]["lines"][0]["taxAmount"]); Assert.Equal("500.00", row["input"]["lines"][0]["chargeAmount"]);
        Assert.Equal(1L, row["after"]["lines"][0]["remainingPackages"]);
        if (quality) Assert.Equal(fixtureData.Allocation!["allocationId"], row["lines"][0]["qualityAllocationId"]);
        var audit = await data.Rows("auditEvents").Find(new BsonDocument("_id", row["auditId"])).SingleAsync();
        Assert.Equal(SupplierDocumentBson.Audit(row), audit); Assert.Equal(row["input"]["pdfObservations"], audit["pdfObservations"]);
        var read = await data.Rows(PdfDocumentExtractionRecords.Reads).Find(new BsonDocument {
            { "actorId", actor.UserId }, { "correlationId", metadata.CorrelationId }, { "resultId", fixtureData.Result["_id"] } }).SingleAsync();
        Assert.Equal("SupplierInvoice", read["sourceKind"]); Assert.Equal(data.DraftId, read["sourceId"]);
        Assert.Equal(fixtureData.Result["resultHash"], read["resultHash"]); Assert.Equal(data.DocumentId, read["submissionId"]);
        Assert.Equal("supplier-document-pdf-ocr-view", read["purpose"]);
        Stage("replay-start");
        Assert.Equal(saved, await data.Evidence.MatchAsync(actor, input, row["binding"].AsString, metadata));
        Assert.Equal(saved, await data.Service.LookupAsync(actor, SupplierDocumentTestData.MatchOperation, metadata.IdempotencyKey));
        Stage("replay-and-recovery-complete");
        Stage("history-start");
        var loaded = await data.Service.MatchesAsync(actor, data.DocumentId);
        Assert.Equal(row["input"]["pdfObservations"], loaded["items"][0]["input"]["pdfObservations"]);
        var limited = await data.Service.MatchesAsync(data.Reader, data.DocumentId);
        Assert.False(limited["items"][0]["input"].AsBsonDocument.Contains("pdfObservations"));
        Assert.Equal(row["input"]["lines"], limited["items"][0]["input"]["lines"]);
        var detail = await data.Service.ReadAsync(data.Reader, data.DraftId);
        Assert.False(detail["matches"][0]["input"].AsBsonDocument.Contains("pdfObservations"));
        var privateHistory = await data.Service.MatchesAsync(data.EvidenceReader, data.DocumentId);
        Assert.Equal(row["input"]["pdfObservations"], privateHistory["items"][0]["input"]["pdfObservations"]);
        Stage("history-complete");
        foreach (var (table, field) in new[] { (SupplierDocumentBson.Matches, "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            Assert.Equal(1L, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, SupplierDocumentTestData.MatchOperation)));
        var changed = input with { PdfObservations = ImmutableArray.Create(fixtureData.Observation(page: 0)) };
        Assert.Equal("idempotency_key_reused", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Evidence.MatchAsync(actor, changed, row["binding"].AsString, metadata))).Code);
        Stage("changed-body-refused");
        await fixtureData.UnchangedAsync();
        Stage("preservation-complete");
    }

    [WindowsStorageFact]
    public async Task Foreign_scope_missing_person_and_wrong_result_page_hash_reference_or_source_refuse_without_match_or_read_audit_commit() {
        await using var fixtureData = await SupplierDocumentPdfMatchTestData.CreateAsync(fixture);
        var data = fixtureData.Data; var input = fixtureData.AnnotatedInput();
        var reads = await data.Rows(PdfDocumentExtractionRecords.Reads).CountDocumentsAsync(new BsonDocument());
        foreach (var actor in new[] { data.Reader, data.EvidenceReader, data.Outsider })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.PreviewMatchAsync(actor, input));
        var binding = await data.Rows("personAccountBindings").Find(new BsonDocument("_id", data.Matcher.UserId)).SingleAsync();
        await data.Rows("personAccountBindings").DeleteOneAsync(new BsonDocument("_id", data.Matcher.UserId));
        try { await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.PreviewMatchAsync(data.Matcher, input)); }
        finally { await data.Rows("personAccountBindings").InsertOneAsync(binding); }
        var observation = fixtureData.Observation();
        foreach (var candidate in new[] {
            new PdfDocumentCandidateReference(observation.Candidate.ResultId + 1, observation.Candidate.ResultHash, data.Reference, 1, observation.Field),
            new PdfDocumentCandidateReference(observation.Candidate.ResultId, new string('f', 64), data.Reference, 1, observation.Field),
            new PdfDocumentCandidateReference(observation.Candidate.ResultId, observation.Candidate.ResultHash, data.Reference, 2, observation.Field) }) {
            var changed = input with { PdfObservations = ImmutableArray.Create(observation with { Candidate = candidate }) };
            Assert.Equal("pdf_document_processing_history_invalid", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
                data.Evidence.PreviewMatchAsync(data.Matcher, changed))).Code);
        }
        var wrongRef = new PdfDocumentCandidateReference(observation.Candidate.ResultId, observation.Candidate.ResultHash,
            new(data.Reference.Id + 1, 1, data.Reference.Sha256), 1, observation.Field);
        Assert.Equal("supplier_document_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Evidence.PreviewMatchAsync(data.Matcher, input with { PdfObservations = ImmutableArray.Create(observation with { Candidate = wrongRef }) }))).Code);
        var secondDraft = await data.DraftAsync("foreign-native-pdf-source", data.Input("OTHER-PDF-INVOICE"));
        var wrongSource = input with { DocumentId = secondDraft["draftId"].AsInt64 };
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.PreviewMatchAsync(data.Matcher, wrongSource));
        Assert.Equal(reads, await data.Rows(PdfDocumentExtractionRecords.Reads).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await data.Rows(SupplierDocumentBson.Matches).CountDocumentsAsync(new BsonDocument()));
        await fixtureData.UnchangedAsync();
    }

    [WindowsStorageFact]
    public async Task Current_policy_and_original_expiry_and_exact_audit_failure_prevent_commit_then_same_key_commits_once() {
        await using var fixtureData = await SupplierDocumentPdfMatchTestData.CreateAsync(fixture);
        var data = fixtureData.Data; var input = fixtureData.AnnotatedInput();
        var preview = await data.Evidence.PreviewMatchAsync(data.Matcher, input); var metadata = SupplierDocumentTestData.Meta("pdf-match-atomic");
        var auditDefinition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "auditEvents") })).SingleAsync();
        var auditValidator = auditDefinition["options"]["validator"].AsBsonDocument;
        var readCount = await data.Rows(PdfDocumentExtractionRecords.Reads).CountDocumentsAsync(new BsonDocument());
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator",
            new BsonDocument("$and", new BsonArray { auditValidator, new BsonDocument("kind", new BsonDocument("$ne", SupplierDocumentTestData.MatchOperation)) }) } });
        try {
            var failure = await Assert.ThrowsAsync<MongoWriteException>(() => data.Evidence.MatchAsync(data.Matcher, input, preview["binding"].AsString, metadata));
            Assert.Equal(121, failure.WriteError.Code);
            Assert.Equal(0L, await data.Rows(SupplierDocumentBson.Matches).CountDocumentsAsync(new BsonDocument()));
            Assert.Equal(readCount, await data.Rows(PdfDocumentExtractionRecords.Reads).CountDocumentsAsync(new BsonDocument()));
            Assert.Null(await data.Service.LookupAsync(data.Matcher, SupplierDocumentTestData.MatchOperation, metadata.IdempotencyKey));
        } finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", auditValidator } }); }
        var saved = await data.Evidence.MatchAsync(data.Matcher, input, preview["binding"].AsString, metadata);
        Assert.Equal(saved, await data.Service.LookupAsync(data.Matcher, SupplierDocumentTestData.MatchOperation, metadata.IdempotencyKey));
        var remaining = input with { Lines = [input.Lines[0] with { Packages = 1, NetAmount = new Money(10000), TaxAmount = new Money(1300), ChargeAmount = new Money(200) }] };
        var policy = await data.Rows("evidenceReadPolicies").Find(new BsonDocument("storeId", ProcurementInspectionTestData.StoreId)).SingleAsync();
        await data.Rows("evidenceReadPolicies").UpdateOneAsync(new BsonDocument("_id", policy["_id"]), Builders<BsonDocument>.Update.Set("active", false));
        try { Assert.Equal("evidence_read_policy_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Evidence.PreviewMatchAsync(data.Matcher, remaining))).Code); }
        finally { await data.Rows("evidenceReadPolicies").ReplaceOneAsync(new BsonDocument("_id", policy["_id"]), policy); }
        data.Clock.UseSystemClock = false; data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
        var fresh = data.Matcher with { AuthenticatedAt = data.Clock.UtcNow };
        Assert.Equal("evidence_read_not_eligible", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Evidence.PreviewMatchAsync(fresh, remaining))).Code);
        // Historical recovery keeps exact proof; it never renews an expired private read.
        Assert.Equal(saved, await data.Service.LookupAsync(fresh, SupplierDocumentTestData.MatchOperation, metadata.IdempotencyKey));
        Assert.Equal(1L, await data.Rows(SupplierDocumentBson.Matches).CountDocumentsAsync(new BsonDocument()));
        await fixtureData.UnchangedAsync();
    }
}
