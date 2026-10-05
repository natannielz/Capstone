using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentEvidenceReadTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Submitted_original_keeps_uploader_and_explicit_reviewer_access_without_accepting_foreign_or_mixed_associations() {
        await using var data = await ReadData.CreateAsync(fixture); await data.ValidateAsync();
        await data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, data.Reservation.ReservationId,
            data.Reference, EvidenceAdmissionTestData.Meta("submitted-original-authority"));
        var leasesAfterSubmission = await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument());
        var reviewer = await PaymentReviewTests.Reviewer(data);
        var foreign = await data.Catalog.ActorAsync(9953, CatalogAuthoringTestData.OtherOwner, Permissions.PaymentReviewRead, Permissions.InvoiceRead);
        var submitted = await data.Payment.Service.ReadAsync(data.Actor, data.DeclarationId);
        Assert.Equal("submitted", submitted.State); Assert.Equal(1, submitted.Revision);
        await using (var original = await data.OpenAsync()) {
            using var received = new MemoryStream(); await original.Content.CopyToAsync(received); Assert.Equal(data.Bytes, received.ToArray());
        }
        await using (var original = await data.Service.OpenReviewReadAsync(reviewer, data.DeclarationId, data.Reservation.ReservationId,
            submitted.Revision, data.Reference, ReadData.Metadata)) {
            using var received = new MemoryStream(); await original.Content.CopyToAsync(received); Assert.Equal(data.Bytes, received.ToArray());
        }
        var before = await StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.OpenAsync(reviewer));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.OpenReviewReadAsync(foreign, data.DeclarationId,
            data.Reservation.ReservationId, submitted.Revision, data.Reference, ReadData.Metadata));
        var unit = new MongoUnitOfWork(data.Catalog.Client);
        foreach (var change in new Func<EvidenceUploadTarget, EvidenceUploadTarget>[] {
            target => target with { ReviewUploaderId = reviewer.UserId },
            target => target with { SubmittedReservationId = data.Reservation.ReservationId + 1 },
            target => target with { SubmittedSubmissionId = null },
            target => target with { Owner = CatalogAuthoringTestData.OtherOwner },
            target => target with { PayoutReservationId = data.Reservation.ReservationId, PayoutId = 1, PayoutUploaderId = data.Actor.UserId },
            target => target with { DisbursementReservationId = data.Reservation.ReservationId, DisbursementId = 1, DisbursementUploaderId = data.Actor.UserId } }) {
            var documents = new EvidenceService(data.Database, unit, new(data.Database), data.Catalog.Clock, data.Files.Registration,
                new AlteredReadTarget(data.Payment.Service, change));
            var denied = await Assert.ThrowsAsync<DomainValidationException>(() => unit.ExecuteAsync((tx, ct) =>
                documents.AcquirePaymentReadAsync(tx, data.Actor, new(EvidenceTargetKind.PaymentDeclaration, data.DeclarationId), submitted.Revision,
                    data.Reservation.ReservationId, data.Reference, ReadData.Metadata, ct)));
            Assert.Equal("evidence_actor_or_owner_denied", denied.Code);
            Assert.Equal(before, await StateAsync());
        }
        Assert.Equal(leasesAfterSubmission + 2, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument()));
        Assert.False(await data.Rows("evidenceReadLeases").Find(new BsonDocument("state", new BsonDocument("$ne", "released"))).AnyAsync());
        Assert.Equal(1, await data.Rows("paymentSubmissions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("payments").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));

        async Task<BsonDocument> StateAsync() {
            var state = new BsonDocument();
            foreach (var name in new[] { "paymentSubmissions", "evidencePins", "evidencePinAudits", "evidenceReadLeases", "evidenceReadAudits",
                "payments", "bankReceipts", "journalEntries", "idempotencyRecords", "auditEvents", "outboxEvents" })
                state[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
            return state;
        }
    }

    [WindowsStorageFact]
    public async Task Checked_original_opens_under_a_bound_read_lease_and_preserves_upload_receipt_and_money() {
        await using var data = await ReadData.CreateAsync(fixture);
        await data.ValidateAsync();
        await using (var file = await data.OpenAsync()) {
            Assert.Equal("image/png", file.Mime); Assert.Equal(data.Bytes.Length, file.Length); Assert.Equal(data.Reference, file.Reference);
            using var bytes = new MemoryStream(); await file.Content.CopyToAsync(bytes); Assert.Equal(data.Bytes, bytes.ToArray());
        }
        var lease = await data.Rows("evidenceReadLeases").Find(new BsonDocument()).SingleAsync(); Assert.Equal("released", lease["state"]);
        Assert.Equal(3, await data.Rows("evidenceReadAudits").CountDocumentsAsync(new BsonDocument()));
        var lifecycle = await data.Rows("evidenceLifecycles").Find(new BsonDocument()).SingleAsync(); Assert.Equal("validated", lifecycle["state"]);
        var original = await data.Rows("documentVersions").Find(new BsonDocument()).SingleAsync(); Assert.Equal("quarantined", original["state"]);
        Assert.Equal(original["lifecycleRevision"].AsInt64 + 1, lifecycle["revision"].AsInt64);
        Assert.Equal("quarantined", (await data.Service.RecoverUploadAsync(data.Actor, data.DeclarationId, data.Reservation.ReservationId, EvidenceAdmissionTestData.Meta())).State);
        Assert.Equal("draft", (await data.Payment.Service.ReadAsync(data.Actor, data.DeclarationId)).State);
        Assert.Equal(0, await data.Rows("payments").CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Pending_or_rejected_security_check_cannot_issue_a_read_lease() {
        await using var data = await ReadData.CreateAsync(fixture);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.OpenAsync());
        await data.ValidateAsync("infected"); await Assert.ThrowsAsync<PersistenceConflictException>(() => data.OpenAsync());
        Assert.Equal(0, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Different_owner_wrong_reference_and_changed_draft_cannot_open_the_original() {
        await using var data = await ReadData.CreateAsync(fixture); await data.ValidateAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => data.OpenAsync(data.Payment.Invoice.Finance));
        await Assert.ThrowsAnyAsync<Exception>(() => data.Service.OpenReadAsync(data.Actor, data.DeclarationId, data.Reservation.ReservationId, 1,
            new(data.Reference.Id, data.Reference.Version, new string('f', 64)), ReadData.Metadata));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.OpenReadAsync(data.Actor, data.DeclarationId, data.Reservation.ReservationId, 2, data.Reference, ReadData.Metadata));
        Assert.Equal(0, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Membership_revoked_after_actual_file_verification_denies_finalization_and_releases_the_handle() {
        await using var data = await ReadData.CreateAsync(fixture); await data.ValidateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        data.Service.ReadObserver = new(async () => { entered.TrySetResult(); await resume.Task.WaitAsync(TimeSpan.FromSeconds(10)); });
        var pending = data.OpenAsync();
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await data.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.Actor.UserId), new BsonDocument("$set", new BsonDocument("status", "revoked")));
        } finally { resume.TrySetResult(); }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => pending);
        Assert.Equal("released", (await data.Rows("evidenceReadLeases").Find(new BsonDocument()).SingleAsync())["state"]);
        Assert.False(await data.Rows("evidenceReadAudits").Find(new BsonDocument("kind", "activated")).AnyAsync());
    }
    [WindowsStorageFact]
    public async Task Read_lease_renewal_rechecks_current_authority_before_another_chunk() {
        await using var data = await ReadData.CreateAsync(fixture); await data.ValidateAsync(); await using var file = await data.OpenAsync();
        var one = new byte[1]; Assert.Equal(1, await file.Content.ReadAsync(one));
        await data.Rows("memberships").UpdateManyAsync(new BsonDocument("userId", data.Actor.UserId), new BsonDocument("$set", new BsonDocument("status", "revoked")));
        data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddSeconds(3);
        await Assert.ThrowsAnyAsync<Exception>(() => file.Content.ReadAsync(one).AsTask());
    }
    [WindowsStorageFact]
    public async Task Policy_or_draft_change_after_verification_cannot_activate_access() {
        await using var data = await ReadData.CreateAsync(fixture); await data.ValidateAsync();
        foreach (var collection in new[] { "evidenceReadPolicies", "evidenceValidationPolicies" }) {
            data.Service.ReadObserver = new(async () => { await data.Rows(collection).UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("active", false))); });
            await Assert.ThrowsAsync<PersistenceConflictException>(() => data.OpenAsync());
            await data.Rows(collection).UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("active", true)));
        }
        data.Service.ReadObserver = new(async () => { await data.Payment.Save(data.DeclarationId, 1, data.Payment.Input, "changed-during-file-verification"); });
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.OpenAsync());
        Assert.False(await data.Rows("evidenceReadAudits").Find(new BsonDocument("kind", "activated")).AnyAsync());
        Assert.Equal(3, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument("state", "released")));
    }
    [WindowsStorageFact]
    public async Task Hard_deadline_stops_remaining_bytes_and_closes_the_file() {
        await using var data = await ReadData.CreateAsync(fixture); await data.ValidateAsync(); await using var file = await data.OpenAsync();
        data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddSeconds(61);
        await Assert.ThrowsAsync<IOException>(() => file.Content.ReadAsync(new byte[1]).AsTask());
        Assert.False(file.Content.CanRead); Assert.Equal("released", (await data.Rows("evidenceReadLeases").Find(new BsonDocument()).SingleAsync())["state"]);
    }
    [WindowsStorageFact]
    public async Task Missing_lifecycle_or_read_audit_denies_bytes_without_repairing_history() {
        await using var data = await ReadData.CreateAsync(fixture); await data.ValidateAsync();
        var lifecycleAudit = await data.Rows("evidenceLifecycleAudits").Find(new BsonDocument()).SingleAsync();
        await data.Rows("evidenceLifecycleAudits").DeleteOneAsync(new BsonDocument("_id", lifecycleAudit["_id"]));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.OpenAsync());
        Assert.Equal(0, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument()));
        await data.Rows("evidenceLifecycleAudits").InsertOneAsync(lifecycleAudit);
        BsonDocument? readAudit = null;
        data.Service.ReadObserver = new(async () => {
            readAudit = await data.Rows("evidenceReadAudits").Find(new BsonDocument()).SingleAsync();
            await data.Rows("evidenceReadAudits").DeleteOneAsync(new BsonDocument("_id", readAudit["_id"]));
        });
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.OpenAsync());
        Assert.Equal("provisional", (await data.Rows("evidenceReadLeases").Find(new BsonDocument()).SingleAsync())["state"]);
        Assert.False(await data.Rows("evidenceReadAudits").Find(new BsonDocument()).AnyAsync());
        // Only the test restores its deliberately removed audit; production does not recreate it.
        await data.Rows("evidenceReadAudits").InsertOneAsync(readAudit!); data.Service.ReadObserver = null;
        await using var file = await data.OpenAsync(); Assert.Equal(1, await file.Content.ReadAsync(new byte[1]));
        var active = await data.Rows("evidenceReadLeases").Find(new BsonDocument("state", "active")).SingleAsync();
        await data.Rows("evidenceReadAudits").DeleteOneAsync(new BsonDocument { { "leaseId", active["_id"] }, { "kind", "acquired" } });
        data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddSeconds(3);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => file.Content.ReadAsync(new byte[1]).AsTask()); Assert.False(file.Content.CanRead);
    }
    [WindowsStorageFact]
    public async Task Required_activation_audit_failure_rolls_back_activation_before_any_bytes() {
        await using var data = await ReadData.CreateAsync(fixture); await data.ValidateAsync();
        var stronger = new BsonDocument("$and", new BsonArray { EvidenceAccessSchemas.Validator("evidenceReadAudits"), new BsonDocument("kind", new BsonDocument("$ne", "activated")) });
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "evidenceReadAudits" }, { "validator", stronger } });
        await Assert.ThrowsAnyAsync<MongoException>(() => data.OpenAsync());
        var lease = await data.Rows("evidenceReadLeases").Find(new BsonDocument()).SingleAsync(); Assert.Equal("released", lease["state"]); Assert.Equal(2L, lease["revision"]);
        Assert.Equal(new[] { "acquired", "released" }, (await data.Rows("evidenceReadAudits").Find(new BsonDocument()).Sort(new BsonDocument("revision", 1)).ToListAsync()).Select(row => row["kind"].AsString));
        Assert.Equal(0, await data.Rows("payments").CountDocumentsAsync(new BsonDocument()));
    }

    // Corrupt only the adapter result after its genuine owner has authorized and
    // reconstructed the submission, to exercise the shared final association boundary.
    private sealed class AlteredReadTarget(IPaymentEvidenceUploadTarget actual, Func<EvidenceUploadTarget, EvidenceUploadTarget> change) : IPaymentEvidenceUploadTarget {
        public IMongoDatabase Database => actual.Database;
        public Task<T> WithReadTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? revision, EvidenceRef? reference,
            Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) =>
            actual.WithReadTargetAsync(tx, actor, target, revision, reference, (authority, token) => action(change(authority), token), ct);
        public Task<T> WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? revision,
            Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) => throw new NotSupportedException();
        public Task<PaymentEvidenceAssociation> RequireSubmissionAssociationAsync(IClientSessionHandle tx, ActorContext actor, long submissionId,
            EvidenceReadClaim claim, CancellationToken ct) => throw new NotSupportedException();
    }

    internal sealed class ReadData : IAsyncDisposable {
        internal EvidenceAdmissionTestData Files = null!;
        internal PaymentDraftData Payment = null!;
        internal PaymentEvidenceService Service = null!;
        internal ReservationResult Reservation = null!;
        internal EvidenceRef Reference = null!;
        internal byte[] Bytes { get; private set; } = ImageDecoderTests.Png(13, 7);
        private EvidenceLimits ImageLimits { get; set; } = new(1024, 100, 100, 10000);
        internal long DeclarationId;
        internal IMongoDatabase Database => Payment.Invoice.Order.Database;
        internal UnitToko.Tests.Catalog.CatalogAuthoringTestData Catalog => Payment.Invoice.Order.Prices.Catalog;
        internal ActorContext Actor => Payment.Invoice.Order.Buyer;
        internal static AuthorizationReadMetadata Metadata => new("payment-evidence-view", "synthetic-private-read");
        internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
        internal Task<ProtectedEvidenceRead> OpenAsync(ActorContext? actor = null) => Service.OpenReadAsync(actor ?? Actor, DeclarationId, Reservation.ReservationId, 1, Reference, Metadata);
        internal static async Task<ReadData> CreateAsync(MongoFixture fixture, PaymentDraftData? attachedPayment = null,
            long maximumContentBytes = 1024, byte[]? primaryImageBytes = null, EvidenceLimits? primaryImageLimits = null,
            long maximumReadBytes = 1024, long verificationDurationMs = 10000, long leaseDurationMs = 5000,
            long maximumValidationAgeMs = 86400000) {
            var data = new ReadData();
            try {
                if (primaryImageBytes is not null) data.Bytes = primaryImageBytes.ToArray();
                if (primaryImageLimits is not null) data.ImageLimits = primaryImageLimits;
                Assert.InRange(data.Bytes.LongLength, 1L, Math.Min(maximumContentBytes, Math.Min(maximumReadBytes, data.ImageLimits.MaximumBytes)));
                data.Files = await EvidenceAdmissionTestData.CreateAsync(fixture); data.Payment = attachedPayment ?? await PaymentDraftData.CreateAsync(fixture);
                var storage = EvidenceAdmissionSchemaTests.Policy(maximum: maximumContentBytes, bytes: checked(30 * maximumContentBytes));
                storage["_id"] = (await new IdAllocator(data.Database).NextAsync("evidenceStorePolicies")).Value;
                await data.Rows("evidenceStorePolicies").InsertOneAsync(storage);
                await PolicyAsync(data.Database, data.Files.StoreId, data.Catalog.Clock.UtcNow, maximumReadBytes, verificationDurationMs, leaseDurationMs, maximumValidationAgeMs);
                data.Service = new(data.Database, new(data.Catalog.Client), new(data.Database), data.Catalog.Clock, data.Payment.Service, data.Files.Registration);
                var saved = await data.Payment.Save(null, 0, data.Payment.Input, "private-read-draft"); data.DeclarationId = saved["declarationId"].AsInt64;
                data.Reservation = await data.Service.ReserveAsync(data.Actor, data.DeclarationId, 1, EvidenceAdmissionTestData.Meta());
                using var stream = new MemoryStream(data.Bytes); var uploaded = await data.Service.UploadAsync(data.Actor, data.DeclarationId, data.Reservation.ReservationId, 1, stream, EvidenceAdmissionTestData.Meta());
                data.Reference = uploaded.Evidence!.Reference; return data;
            } catch { await data.DisposeAsync(); throw; }
        }
        internal async Task ValidateAsync(string status = "clean") {
            var processor = new EvidenceValidationProcessor(Database, new(Catalog.Client), new(Database), Catalog.Clock, Files.Registration,
                EvidenceValidationTests.Consumer, new string('c', 64), EvidenceValidationTests.ClaimPolicy, ImageLimits,
                new EvidenceValidationTests.FixtureScanner(status, signaturesAt: Catalog.Clock.UtcNow.AddHours(-1)), new WindowsImageDecoder(ImageDecoderTests.Options()));
            await EvidenceValidationTests.Policy(Database, Catalog.Clock, Files.StoreId, processor.PipelineId);
            Assert.Equal("completed", await processor.RunOnceAsync());
        }
        internal static async Task PolicyAsync(IMongoDatabase database, string storeId, DateTimeOffset now,
            long maximumReadBytes = 1024, long verificationDurationMs = 10000, long leaseDurationMs = 5000,
            long maximumValidationAgeMs = 86400000) =>
            await database.GetCollection<BsonDocument>("evidenceReadPolicies").InsertOneAsync(new BsonDocument {
                { "_id", (await new IdAllocator(database).NextAsync("evidenceReadPolicies")).Value }, { "schemaVersion", 1 }, { "storeId", storeId },
                { "active", true }, { "revision", 1L }, { "fence", 0L }, { "createdAt", now.UtcDateTime }, { "updatedAt", now.UtcDateTime },
                { "maximumBytes", maximumReadBytes }, { "verificationDurationMs", verificationDurationMs }, { "leaseDurationMs", leaseDurationMs }, { "requestDurationMs", 60000L },
                { "maximumValidationAgeMs", maximumValidationAgeMs }, { "maximumSignatureAgeMs", 604800000L } });
        public async ValueTask DisposeAsync() { if (Payment is not null) await Payment.DisposeAsync(); if (Files is not null) await Files.DisposeAsync(); }
    }
}
