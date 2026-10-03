using System.Runtime.Versioning;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using D = UnitToko.Tests.Procurement.ProcurementInspectionTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ProcurementInspectionTests(MongoFixture fixture)
{
    [WindowsStorageTheory]
    [InlineData(ProcurementRoute.OmiRegular, false, "mp4", false)]
    [InlineData(ProcurementRoute.Ddo, false, "webm", false)]
    [InlineData(ProcurementRoute.Smart, true, "mp4", false)]
    [InlineData(ProcurementRoute.PasarKering, true, "mp4", true)]
    public async Task Actual_zero_advance_intake_video_worker_and_partial_inspection_preserve_quarantine_and_original_key(
        ProcurementRoute route, bool onReceipt, string media, bool aboveImageCeiling)
    {
        await using var data = await D.CreateAsync(fixture, route, onReceipt, media: media, aboveImageCeiling: aboveImageCeiling);
        var custody = await data.Receiving.StateAsync(); var orders = await data.Receiving.Orders.StateAsync();
        var before = await data.Service.ReadAsync(data.Reader, data.DraftId);
        Assert.Equal("draft", before["draft"]["state"].AsString); Assert.Equal(0L, before["capacity"]["inspectedContainers"].AsInt64);
        Assert.Equal(4L, before["capacity"]["remainingContainers"].AsInt64);
        Assert.Equal("0.00", before["draft"]["notice"]["order"]["requiredAdvance"].AsString);
        Assert.Equal(D.MaximumBytes, (await data.Evidence.UploadLimitsAsync(data.Actor, data.DraftId, 1)).MaximumBytes);
        await data.UploadAsync();
        var unvalidated = await data.PreviewSubmitAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.SubmitAsync(unvalidated));
        await data.ValidateAsync();
        var validation = await data.Rows(VideoValidationRecords.Results).Find(new BsonDocument()).SingleAsync();
        Assert.Equal("passed", validation["status"].AsString); Assert.Equal("decoded", validation["decoderStatus"].AsString);
        Assert.Equal(media == "webm" ? "video/webm" : "video/mp4", validation["mime"].AsString);
        Assert.Equal(5L, validation["videoFrames"].AsInt64); Assert.Equal(24000L, validation["audioSamples"].AsInt64);
        Assert.Equal(data.Bytes.LongLength, validation["length"].AsInt64);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data.Bytes)).ToLowerInvariant(), validation["original"]["sha256"].AsString);
        Assert.False(await data.Rows("evidenceValidationResults").Find(new BsonDocument()).AnyAsync());
        var job = await data.Rows("outboxEvents").Find(new BsonDocument("kind", VideoValidationRecords.Kind)).SingleAsync();
        Assert.Equal("completed", job["status"].AsString); Assert.True(job["processingReceipt"]["evidenceVideoValidation"].AsBoolean);
        var preview = await data.PreviewSubmitAsync(); var result = await data.SubmitAsync(preview);
        Assert.Equal("submitted_quarantine", result["state"].AsString); Assert.Equal(result, await data.SubmitAsync(preview));
        Assert.Equal(result, await data.Service.LookupAsync(data.Actor, D.SubmitOperation, "inspection-submit"));
        Assert.Equal(data.Draft, await data.Service.LookupAsync(data.Actor, D.DraftOperation, "inspection-draft"));
        var masked = await data.Service.ReadAsync(data.Reader, data.DraftId); Assert.True(masked["inspection"]["evidence"].IsBsonNull); Assert.True(masked["inspection"]["reservationId"].IsBsonNull);
        Assert.Equal(2L, masked["capacity"]["inspectedContainers"].AsInt64); Assert.Equal(2L, masked["capacity"]["remainingContainers"].AsInt64);
        var observed = masked["inspection"]["input"]["lines"][0];
        Assert.Equal(3, observed["receivedPackages"].AsInt32); Assert.Equal(1, observed["damagedPackages"].AsInt32);
        Assert.Equal(1, observed["expiredPackages"].AsInt32); Assert.Equal(2, observed["openedPackages"].AsInt32); // Sum exceeds received because flags overlap.
        Assert.DoesNotContain("healthyPackages", masked.ToJson()); Assert.DoesNotContain("releasable", masked.ToJson());
        var visible = await data.Service.ReadAsync(data.EvidenceReader, data.DraftId); Assert.Equal(data.Reference.Sha256, visible["inspection"]["evidence"]["sha256"].AsString);
        await using (var original = await data.Evidence.OpenReadAsync(data.EvidenceReader, data.DraftId, data.Reservation.ReservationId, 1, data.Reference, D.ReadMetadata)) {
            using var bytes = new MemoryStream(); await original.Content.CopyToAsync(bytes); Assert.Equal(data.Bytes, bytes.ToArray());
        }
        var pin = await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "ProcurementInspection")).SingleAsync();
        Assert.Equal(result["inspectionId"], pin["associationId"]); Assert.Equal(data.DraftId, pin["procurementInspectionDraftId"].AsInt64);
        Assert.False(pin.Contains("procurementDisbursementDraftId")); Assert.False(pin.Contains("refundPayoutDraftId"));
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, D.SubmitOperation)));
        var imagePolicy = await data.Rows("evidenceStorePolicies").Find(new BsonDocument("storeId", D.StoreId)).SingleAsync();
        Assert.Equal(10 * 1024 * 1024L, imagePolicy["maximumContentBytes"].AsInt64); Assert.Equal(0L, imagePolicy["chargedBytes"].AsInt64);
        if (aboveImageCeiling) Assert.True(data.Bytes.Length > imagePolicy["maximumContentBytes"].AsInt64);
        Assert.Equal(custody, await data.Receiving.StateAsync()); Assert.Equal(orders, await data.Receiving.Orders.StateAsync()); await data.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
        Assert.False(await data.Rows("evidenceReadLeases").Find(new BsonDocument("state", new BsonDocument("$ne", "released"))).AnyAsync());
    }

    [WindowsStorageFact]
    public async Task Inspection_authority_original_owner_private_scope_and_exact_package_reference_are_required()
    {
        await using var data = await D.CreateAsync(fixture); var state = await data.StateAsync(); var input = data.Input();
        foreach (var actor in new[] { data.Reader, data.EvidenceReader, data.Outsider })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(actor, input));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Outsider, data.DraftId));
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(
            data.Actor with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, input))).Code);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.ReserveAsync(data.OtherInspector, data.DraftId, 1, D.Meta("wrong-owner-reserve")));
        foreach (var changed in new[] { input with { Lines = [input.Lines[0] with { DefinitionVersion = input.Lines[0].DefinitionVersion + 1 }] },
            input with { Lines = [input.Lines[0] with { RequestSkuId = data.Receiving.Procurement.ForeignSku.Id.Value }] },
            input with { InspectionDate = input.InspectionDate.AddDays(1) }, input with { InspectedContainers = 5 } })
            await IntegrityDeniedAsync(() => data.Service.PreviewAsync(data.Actor, changed));
        Assert.Equal(state, await data.StateAsync());
        await data.UploadAsync(); await data.ValidateAsync(); var preview = await data.PreviewSubmitAsync(); state = await data.StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.OpenReadAsync(data.Reader, data.DraftId, data.Reservation.ReservationId, 1, data.Reference, D.ReadMetadata));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.OpenReadAsync(data.OtherInspector, data.DraftId, data.Reservation.ReservationId, 1, data.Reference, D.ReadMetadata));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewSubmitAsync(data.OtherInspector, data.DraftId, 1, data.Reservation.ReservationId, data.Reference));
        var wrong = new EvidenceRef(data.Reference.Id, data.Reference.Version, new string('0', 64));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Evidence.SubmitAsync(data.Actor, data.DraftId, 1, data.Reservation.ReservationId, wrong, preview["binding"].AsString, D.Meta("wrong-original")));
        var other = await data.DraftAsync("other-draft", input with { InspectionReference = "SYNTHETIC-OTHER-OBSERVATION" }); state = await data.StateAsync();
        Assert.Equal("evidence_actor_or_owner_denied", (await Assert.ThrowsAsync<DomainValidationException>(() => data.Evidence.SubmitAsync(data.Actor, other["draftId"].AsInt64, 1,
            data.Reservation.ReservationId, data.Reference, preview["binding"].AsString, D.Meta("wrong-target")))).Code);
        Assert.Equal(state, await data.StateAsync()); await data.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [WindowsStorageFact]
    public async Task Missing_or_truncated_original_and_real_truncated_video_validation_never_submit_an_inspection()
    {
        await using var data = await D.CreateAsync(fixture); await data.UploadAsync(); await data.ValidateAsync(); var preview = await data.PreviewSubmitAsync();
        var path = await data.OriginalPathAsync(); var state = await data.StateAsync();
        foreach (var missing in new[] { true, false }) {
            if (missing) File.Delete(path); else await File.WriteAllBytesAsync(path, data.Bytes[..(data.Bytes.Length / 2)]);
            try {
                Assert.Equal("evidence_read_integrity_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.SubmitAsync(preview, "unavailable-original"))).Code);
                Assert.Equal(state, await data.StateAsync());
            } finally { await File.WriteAllBytesAsync(path, data.Bytes); }
        }
        var rejectedDraft = await data.DraftAsync("truncated-draft", data.Input("SYNTHETIC-TRUNCATED-VIDEO"));
        var truncated = data.Bytes[..(data.Bytes.Length / 2)]; var upload = await data.UploadForAsync(rejectedDraft["draftId"].AsInt64, "truncated-video", truncated); await data.ValidateAsync(truncated);
        var result = await data.Rows(VideoValidationRecords.Results).Find(new BsonDocument("_id", upload.Reservation.VersionRowId)).SingleAsync();
        Assert.Equal("rejected", result["status"].AsString); Assert.Equal("invalid_media", result["safeFailure"].AsString);
        var rejectedPreview = await data.Service.PreviewSubmitAsync(data.Actor, rejectedDraft["draftId"].AsInt64, 1, upload.Reservation.ReservationId, upload.Reference);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Evidence.SubmitAsync(data.Actor, rejectedDraft["draftId"].AsInt64, 1, upload.Reservation.ReservationId, upload.Reference,
            rejectedPreview["binding"].AsString, D.Meta("reject-truncated-original")));
        Assert.False(await data.Rows(D.Submissions).Find(new BsonDocument()).AnyAsync()); await data.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [WindowsStorageFact]
    public async Task Competing_partial_inspections_consume_only_available_containers_and_original_key_races_return_one_submission()
    {
        await using var data = await D.CreateAsync(fixture, createDraft: false);
        var drafts = new[] { await data.DraftAsync("capacity-draft-one", data.Input("SYNTHETIC-CAPACITY-ONE", 3)),
            await data.DraftAsync("capacity-draft-two", data.Input("SYNTHETIC-CAPACITY-TWO", 3)) };
        var uploads = new (ReservationResult Reservation, EvidenceRef Reference)[2]; var previews = new BsonDocument[2];
        for (var i = 0; i < 2; i++) {
            uploads[i] = await data.UploadForAsync(drafts[i]["draftId"].AsInt64, "capacity-video-" + i); await data.ValidateAsync();
            previews[i] = await data.Service.PreviewSubmitAsync(data.Actor, drafts[i]["draftId"].AsInt64, 1, uploads[i].Reservation.ReservationId, uploads[i].Reference);
        }
        Task<BsonDocument> Submit(int index, string key) => data.Evidence.SubmitAsync(data.Actor, drafts[index]["draftId"].AsInt64, 1, uploads[index].Reservation.ReservationId,
            uploads[index].Reference, previews[index]["binding"].AsString, D.Meta(key));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async i => await Record.ExceptionAsync(() => Submit(i, "capacity-submit-" + i))));
        Assert.Single(outcomes, value => value is null); Assert.IsType<PersistenceConflictException>(Assert.Single(outcomes, value => value is not null));
        var winner = Array.FindIndex(outcomes, value => value is null); var replays = await Task.WhenAll(Submit(winner, "capacity-submit-" + winner), Submit(winner, "capacity-submit-" + winner));
        Assert.Equal(replays[0], replays[1]);
        Assert.Equal(1, await data.Rows(D.Submissions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument("associationKind", "ProcurementInspection")));
        var read = await data.Service.ReadAsync(data.Reader, drafts[winner]["draftId"].AsInt64);
        Assert.Equal(3L, read["capacity"]["inspectedContainers"].AsInt64); Assert.Equal(1L, read["capacity"]["remainingContainers"].AsInt64);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => Submit(winner, "different-key-same-draft")); await data.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [WindowsStorageFact]
    public async Task Late_owner_or_pin_audit_failure_rolls_back_all_claims_and_missing_history_cannot_reopen_capacity()
    {
        await using var data = await D.CreateAsync(fixture); await data.UploadAsync(); await data.ValidateAsync(); var preview = await data.PreviewSubmitAsync(); var state = await data.StateAsync();
        foreach (var (table, reject) in new[] { ("outboxEvents", new BsonDocument("kind", new BsonDocument("$ne", D.SubmitOperation))),
            ("evidencePinAudits", new BsonDocument("_id", new BsonDocument("$exists", false))) }) {
            var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
            var original = definition["options"]["validator"].DeepClone().AsBsonDocument;
            try {
                await GuardAsync(data, table, new BsonDocument("$and", new BsonArray { original, reject }));
                Assert.IsAssignableFrom<MongoException>(await Record.ExceptionAsync(() => data.SubmitAsync(preview, "atomic-inspection")));
                Assert.Equal(state, await data.StateAsync()); await data.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
            } finally { await GuardAsync(data, table, original); }
            Assert.Null(await data.Service.LookupAsync(data.Actor, D.SubmitOperation, "atomic-inspection"));
        }
        var submitted = await data.SubmitAsync(preview, "atomic-inspection");
        foreach (var (table, filter) in new[] { (D.Submissions, new BsonDocument()), (D.Heads, new BsonDocument()),
            (ProcurementReceivingTestData.Intakes, new BsonDocument("_id", data.IntakeId)),
            ("evidencePins", new BsonDocument("associationKind", "ProcurementInspection")), ("auditEvents", new BsonDocument("kind", D.SubmitOperation)),
            ("outboxEvents", new BsonDocument("kind", D.SubmitOperation)), ("idempotencyRecords", new BsonDocument("operation", D.SubmitOperation)) }) {
            var row = await data.Rows(table).Find(filter).SingleAsync(); await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", row["_id"]));
            try {
                await IntegrityDeniedAsync(() => data.Service.ReadAsync(data.Reader, data.DraftId));
                await IntegrityDeniedAsync(() => data.Service.LookupAsync(data.Actor, D.SubmitOperation, "atomic-inspection"));
                await IntegrityDeniedAsync(() => data.Service.PreviewAsync(data.Actor, data.Input("SYNTHETIC-AFTER-MISSING-HISTORY", 1)));
            } finally { await data.Rows(table).InsertOneAsync(row); }
        }
        Assert.Equal(submitted, await data.Service.LookupAsync(data.Actor, D.SubmitOperation, "atomic-inspection")); await data.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [WindowsStorageFact]
    public async Task Current_video_policy_or_inspector_grant_withdrawal_after_original_verification_cannot_commit()
    {
        await using var data = await D.CreateAsync(fixture); await data.UploadAsync(); await data.ValidateAsync(); var preview = await data.PreviewSubmitAsync(); var state = await data.StateAsync();
        var policy = await data.Rows(VideoValidationRecords.Policies).Find(new BsonDocument()).SingleAsync();
        var grants = await data.Rows("authorizationGrants").Find(new BsonDocument("userId", data.Actor.UserId)).ToListAsync();
        foreach (var change in new[] { "policy", "grant" }) {
            var service = data.Service; var evidence = new EvidenceService(data.Database, data.Unit, new(data.Database), data.Clock, data.Registration, procurementInspectionTargets: service);
            var observed = 0;
            evidence.ProcurementInspectionReadObserver = new(async () => {
                Interlocked.Increment(ref observed);
                if (change == "policy") await data.Rows(VideoValidationRecords.Policies).UpdateOneAsync(new BsonDocument("_id", policy["_id"]), Builders<BsonDocument>.Update.Set("active", false));
                else await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", data.Actor.UserId), Builders<BsonDocument>.Update.Pull("permissions", Permissions.ProcurementInspect));
            });
            try {
                var error = await Record.ExceptionAsync(() => evidence.WithProcurementInspectionPinAsync(data.Actor, new(EvidenceTargetKind.ProcurementInspection, data.DraftId), 1,
                    data.Reservation.ReservationId, data.Reference, D.ReadMetadata, (claim, original, started, token) => service.SubmitWithProofAsync(data.Actor, data.DraftId, 1,
                        data.Reservation.ReservationId, data.Reference, preview["binding"].AsString, D.Meta("after-original-" + change),
                        (tx, id, target, inner) => new EvidenceService(data.Database, data.Unit, new(data.Database), data.Clock, data.Registration, procurementInspectionTargets: target)
                            .PinProcurementInspectionAsync(tx, data.Actor, id, claim, original, started, inner), token)));
                Assert.Equal(1, observed); Assert.True(error is AuthorizationDeniedException or PersistenceConflictException, error?.ToString());
                Assert.Equal(state, await data.StateAsync());
            } finally {
                await data.Rows(VideoValidationRecords.Policies).ReplaceOneAsync(new BsonDocument("_id", policy["_id"]), policy);
                foreach (var grant in grants) await data.Rows("authorizationGrants").ReplaceOneAsync(new BsonDocument("_id", grant["_id"]), grant);
            }
        }
        var submitted = await data.SubmitAsync(preview, "after-restored-inputs");
        await data.Rows(VideoValidationRecords.Policies).UpdateOneAsync(new BsonDocument("_id", policy["_id"]), Builders<BsonDocument>.Update.Set("active", false));
        Assert.Equal(submitted, await data.Service.LookupAsync(data.Actor, D.SubmitOperation, "after-restored-inputs"));
        Assert.Equal("submitted_quarantine", (await data.Service.ReadAsync(data.Reader, data.DraftId))["inspection"]["state"].AsString);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Evidence.OpenReadAsync(data.EvidenceReader, data.DraftId, data.Reservation.ReservationId, 1, data.Reference, D.ReadMetadata));
        await data.Receiving.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [Fact]
    public void Physical_condition_flags_overlap_without_healthy_stock_inference_and_split_lot_rows_keep_exact_values()
    {
        var day = new DateOnly(2026, 10, 3);
        var line = new ProcurementInspectionLineInput(1, 2, 3, 3, 3, 3, 3, 3, "000LotA", day.AddDays(-1), "Q-01", "All three conditions overlap");
        var input = new ProcurementInspectionInput(ProcurementRequestTestData.Location, 1, 2, day, "000RefA", 1, [line], null);
        var copied = ProcurementInspectionRules.Copy(input); Assert.Equal(line, copied.Lines[0]);
        var split = ProcurementInspectionRules.Copy(input with { Lines = [line with { LotCode = "000LotB" }, line] }); Assert.Equal("000LotA", split.Lines[0].LotCode);
        Assert.Throws<DomainValidationException>(() => ProcurementInspectionRules.Copy(input with { Lines = [line, line] }));
        Assert.Throws<DomainValidationException>(() => ProcurementInspectionRules.Copy(input with { Lines = [line with { DamagedPackages = 4 }] }));
        Assert.Throws<DomainValidationException>(() => ProcurementInspectionRules.Copy(input with { Lines = [line with { Discrepancy = null }] }));
        Assert.Throws<DomainValidationException>(() => ProcurementInspectionRules.Copy(input with { InspectionReference = " leading" }));
    }

    private static async Task IntegrityDeniedAsync(Func<Task> action) {
        var error = await Record.ExceptionAsync(action); Assert.True(error is AuthorizationDeniedException or PersistenceConflictException, error?.ToString());
    }
    private static async Task GuardAsync(D data, string table, BsonDocument validator) => await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument {
        { "collMod", table }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } });
}
