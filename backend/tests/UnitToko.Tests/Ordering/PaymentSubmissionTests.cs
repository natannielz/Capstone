using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentSubmissionTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Checked_original_submission_pins_exact_content_and_waits_for_review_without_creating_money() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1,
            data.Reservation.ReservationId, data.Reference, EvidenceAdmissionTestData.Meta("submit-evidence")));
        Assert.Equal(0, await data.Rows("paymentSubmissions").CountDocumentsAsync(new BsonDocument()));
        await data.ValidateAsync();
        var result = await data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, data.Reservation.ReservationId, data.Reference, EvidenceAdmissionTestData.Meta("submit-evidence"));
        Assert.Equal("submitted", result["state"]); Assert.Equal(1L, result["reviewRevision"]);
        Assert.Equal("submitted", (await data.Payment.Service.ReadAsync(data.Actor, data.DeclarationId)).State);
        Assert.Equal(data.Reservation.ReservationId, (await data.Payment.Service.ReadAsync(data.Actor, data.DeclarationId)).Submission!.ReservationId.Value);
        Assert.Equal(data.Reservation.ReservationId, Assert.Single(await data.Service.ListAsync(data.Actor, data.DeclarationId)).ReservationId);
        var submission = await data.Rows("paymentSubmissions").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(data.Reference.Sha256, submission["evidence"]["sha256"]); Assert.Equal(data.Reservation.ReservationId, submission["reservationId"]);
        var pin = await data.Rows("evidencePins").Find(new BsonDocument()).SingleAsync(); Assert.Equal(submission["_id"], pin["associationId"]);
        Assert.Equal(data.Reference.Sha256, pin["reference"]["sha256"]);
        Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "payment.ocr.requested")));
        Assert.Equal(0, await data.Rows("payments").CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Payment.Save(data.DeclarationId, 1, data.Payment.Input, "edit-after-submission"));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.ReserveAsync(data.Actor, data.DeclarationId, 1, EvidenceAdmissionTestData.Meta("upload-after-submit")));
        await using var original = await data.OpenAsync(); using var bytes = new MemoryStream(); await original.Content.CopyToAsync(bytes); Assert.Equal(data.Bytes, bytes.ToArray());
    }
    [WindowsStorageFact]
    public async Task Exact_submission_retry_recovers_original_result_without_reopening_the_file_or_queuing_twice() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await data.ValidateAsync();
        var metadata = EvidenceAdmissionTestData.Meta("same-submission");
        var result = await data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, data.Reservation.ReservationId, data.Reference, metadata);
        data.Service.ReadObserver = new(() => throw new InvalidOperationException("Committed submission must not reopen its original"));
        Assert.Equal(result, await data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, data.Reservation.ReservationId, data.Reference, metadata));
        Assert.Equal(result, await data.Service.RecoverSubmissionAsync(data.Actor, data.DeclarationId, metadata.IdempotencyKey));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1,
            data.Reservation.ReservationId, new(data.Reference.Id, data.Reference.Version, new string('f', 64)), metadata));
        Assert.Equal(1, await data.Rows("paymentSubmissions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "payment.ocr.requested")));
    }
    [WindowsStorageFact]
    public async Task Draft_correction_during_actual_original_verification_cannot_submit_an_old_revision() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await data.ValidateAsync();
        data.Service.ReadObserver = new(async () => { await data.Payment.Save(data.DeclarationId, 1, data.Payment.Input, "correct-before-submit"); });
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1,
            data.Reservation.ReservationId, data.Reference, EvidenceAdmissionTestData.Meta("submit-old-draft")));
        Assert.Equal(0, await data.Rows("paymentSubmissions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal("draft", (await data.Payment.Service.ReadAsync(data.Actor, data.DeclarationId)).State);
    }
    [WindowsStorageFact]
    public async Task Security_policy_expiry_and_revoked_session_deny_submission_after_bytes_are_checked() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await data.ValidateAsync();
        var now = data.Catalog.Clock.UtcNow;
        async Task Submit() => await data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, data.Reservation.ReservationId, data.Reference, EvidenceAdmissionTestData.Meta("security-change"));
        foreach (var collection in new[] { "evidenceReadPolicies", "evidenceValidationPolicies" }) {
            data.Service.ReadObserver = new(async () => { await data.Rows(collection).UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("active", false))); });
            await Assert.ThrowsAsync<PersistenceConflictException>(Submit);
            await data.Rows(collection).UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("active", true)));
        }
        data.Service.ReadObserver = new(() => { data.Catalog.Clock.UtcNow = now.AddSeconds(61); return Task.CompletedTask; });
        await Assert.ThrowsAsync<PersistenceConflictException>(Submit); data.Catalog.Clock.UtcNow = now;
        data.Service.ReadObserver = new(async () => { await data.Catalog.Sessions.RevokeAsync(data.Catalog.Issued[data.Actor.UserId].OpaqueToken, "submission-logout"); });
        await Assert.ThrowsAsync<AuthorizationDeniedException>(Submit);
        Assert.Equal(0, await data.Rows("paymentSubmissions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "payment.ocr.requested")));
    }
    [WindowsStorageFact]
    public async Task Required_pin_audit_or_ocr_queue_failure_aborts_all_submission_effects_and_same_key_can_retry() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await data.ValidateAsync();
        async Task Submit() => await data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, data.Reservation.ReservationId, data.Reference, EvidenceAdmissionTestData.Meta("atomic-submission"));
        foreach (var name in new[] { "evidencePinAudits", "outboxEvents" }) {
            var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) })).SingleAsync();
            var original = definition["options"]["validator"];
            var restriction = name == "evidencePinAudits" ? new BsonDocument("pinId", new BsonDocument("$lt", 0L)) : new BsonDocument("kind", new BsonDocument("$ne", "payment.ocr.requested"));
            await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", new BsonDocument("$and", new BsonArray { original, restriction }) } });
            await Assert.ThrowsAnyAsync<MongoException>(Submit);
            Assert.Equal(0, await data.Rows("paymentSubmissions").CountDocumentsAsync(new BsonDocument()));
            Assert.Equal(0, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument()));
            Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", PaymentSubmissionBson.Operation)));
            Assert.Equal(0, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "payment.ocr.requested")));
            Assert.Equal("draft", (await data.Payment.Service.ReadAsync(data.Actor, data.DeclarationId)).State);
            await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", name }, { "validator", original } });
        }
        await Submit(); Assert.Equal("submitted", (await data.Payment.Service.ReadAsync(data.Actor, data.DeclarationId)).State);
    }
    [WindowsStorageFact]
    public async Task Two_submission_keys_race_for_one_immutable_image_association() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await data.ValidateAsync();
        async Task<Exception?> Attempt(string key) { try { await data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, data.Reservation.ReservationId, data.Reference, EvidenceAdmissionTestData.Meta(key)); return null; } catch (Exception error) { return error; } }
        var attempts = await Task.WhenAll(Attempt("submit-one"), Attempt("submit-two"));
        Assert.Single(attempts, value => value is null); Assert.Single(attempts, value => value is PersistenceConflictException);
        Assert.Equal(1, await data.Rows("paymentSubmissions").CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "payment.ocr.requested")));
        Assert.Equal(0, await data.Rows("payments").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Missing_required_submission_history_never_recovers_or_recreates_a_pin() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await data.ValidateAsync();
        var metadata = EvidenceAdmissionTestData.Meta("submission-history");
        await data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, data.Reservation.ReservationId, data.Reference, metadata);
        foreach (var (name, filter) in new (string, BsonDocument)[] {
            ("evidencePinAudits", new()), ("outboxEvents", new("kind", "payment.ocr.requested")), ("auditEvents", new("operation", PaymentSubmissionBson.Operation)),
            ("idempotencyRecords", new("operation", PaymentSubmissionBson.Operation)) }) {
            var original = await data.Rows(name).Find(filter).SingleAsync(); await data.Rows(name).DeleteOneAsync(new BsonDocument("_id", original["_id"]));
            await Assert.ThrowsAnyAsync<Exception>(() => data.Service.RecoverSubmissionAsync(data.Actor, data.DeclarationId, metadata.IdempotencyKey));
            await Assert.ThrowsAnyAsync<Exception>(() => data.Payment.Service.ReadAsync(data.Actor, data.DeclarationId));
            Assert.False(await data.Rows(name).Find(filter).AnyAsync());
            await data.Rows(name).InsertOneAsync(original);
        }
        Assert.NotNull(await data.Service.RecoverSubmissionAsync(data.Actor, data.DeclarationId, metadata.IdempotencyKey));
    }
}
