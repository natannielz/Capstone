using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class RefundPayoutEvidenceTests(MongoFixture fixture)
{
    [Theory]
    [InlineData(EvidenceTargetKind.PaymentDeclaration, EvidencePurpose.RefundPayoutImage)]
    [InlineData(EvidenceTargetKind.RefundPayout, EvidencePurpose.PaymentImage)]
    [InlineData(EvidenceTargetKind.RefundPayout, EvidencePurpose.CatalogImage)]
    [InlineData(EvidenceTargetKind.CatalogSku, EvidencePurpose.RefundPayoutImage)]
    public void Outgoing_target_never_impersonates_incoming_payment_or_catalog(EvidenceTargetKind target, EvidencePurpose purpose) {
        Assert.Throws<DomainValidationException>(() => new ReserveEvidenceImage(new(target, 1), purpose, 1));
        Assert.Throws<DomainValidationException>(() => new ReserveCatalogImage(new(EvidenceTargetKind.RefundPayout, 1)));
        var admitted = new ReserveEvidenceImage(new(EvidenceTargetKind.RefundPayout, 1), EvidencePurpose.RefundPayoutImage, 1);
        Assert.Equal(EvidenceTargetKind.RefundPayout, admitted.Target.Kind);
        Assert.Equal(EvidencePurpose.RefundPayoutImage, admitted.Purpose);
    }
    [Theory]
    [InlineData("evidenceLineages")] [InlineData("evidenceUploadReservations")]
    [InlineData("auditEvents")] [InlineData("idempotencyRecords")]
    [InlineData("evidenceReadLeases")] [InlineData("evidencePins")]
    public void Forward_extension_preserves_complete_predecessor_and_extra_guards_and_rejects_unknown_widening(string name) {
        var previous = RefundPayoutEvidenceSchemas.PreviousValidator(name);
        var guard = new BsonDocument("fixtureGuard", new BsonDocument("$ne", true));
        var current = new BsonDocument("$and", new BsonArray { previous, guard });
        var next = RefundPayoutEvidenceSchemas.Extend(name, current);
        Assert.Equal(guard, next["$and"][1]);
        Assert.Equal(previous, next["$and"][0]["$or"][0]);
        Assert.True(RefundPayoutEvidenceSchemas.Supports(name, next, previous));
        Assert.Equal(next, RefundPayoutEvidenceSchemas.Extend(name, next));
        var unknown = new BsonDocument("$or", new BsonArray { previous, new BsonDocument() });
        Assert.False(RefundPayoutEvidenceSchemas.Supports(name, unknown, previous));
        Assert.Throws<InvalidOperationException>(() => RefundPayoutEvidenceSchemas.Extend(name, unknown));
        var weakerSuccessor = RefundPayoutEvidenceSchemas.Validator(name); weakerSuccessor["$or"].AsBsonArray.Add(new BsonDocument());
        Assert.False(RefundPayoutEvidenceSchemas.Supports(name, weakerSuccessor, previous));
    }
    [WindowsStorageFact]
    public async Task Incoming_payment_keeps_actual_private_read_validation_and_owner_pin_after_forward_extension() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture);
        await data.ValidateAsync();
        await using (var original = await data.OpenAsync()) {
            using var received = new MemoryStream(); await original.Content.CopyToAsync(received);
            Assert.Equal(data.Bytes, received.ToArray());
        }
        var submitted = await data.Service.SubmitAsync(data.Actor, data.DeclarationId, 1, data.Reservation.ReservationId, data.Reference,
            EvidenceAdmissionTestData.Meta("incoming-pin-after55"));
        var pin = await data.Rows("evidencePins").Find(new BsonDocument("associationId", submitted["submissionId"])).SingleAsync();
        Assert.Equal("PaymentSubmission", pin["associationKind"]);
        Assert.Equal(data.DeclarationId, pin["declarationId"].AsInt64);
        Assert.False(pin.Contains("refundPayoutDraftId"));
        Assert.Equal(0, await data.Rows("payments").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("refundPayouts").CountDocumentsAsync(new BsonDocument()));
        var disguised = pin.DeepClone().AsBsonDocument; disguised["associationKind"] = "RefundPayout";
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Rows("evidencePins").ReplaceOneAsync(new BsonDocument("_id", pin["_id"]), disguised));
    }
    [WindowsStorageFact]
    public async Task Incoming_private_reference_cannot_acquire_an_outgoing_lease_or_return_original_bytes() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await data.ValidateAsync();
        var owner = new EvidenceService(data.Database, new(data.Catalog.Client), new(data.Database), data.Catalog.Clock, data.Files.Registration, data.Payment.Service);
        var before = await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument());
        await Assert.ThrowsAsync<DomainValidationException>(() => new UnitToko.Platform.Mongo.MongoUnitOfWork(data.Catalog.Client).ExecuteAsync((tx, ct) =>
            owner.AcquireRefundPayoutReadAsync(tx, data.Actor, new(EvidenceTargetKind.PaymentDeclaration, data.DeclarationId), 1,
                data.Reservation.ReservationId, data.Reference, new("refund-payout-evidence-view", "wrong-kind-private-read"), ct)));
        await Assert.ThrowsAsync<DomainValidationException>(() => new UnitToko.Platform.Mongo.MongoUnitOfWork(data.Catalog.Client).ExecuteAsync((tx, ct) =>
            owner.AcquireRefundPayoutReadAsync(tx, data.Actor, new(EvidenceTargetKind.RefundPayout, data.DeclarationId), 1,
                data.Reservation.ReservationId, data.Reference, new("refund-payout-evidence-view", "no-outgoing-source"), ct)));
        Assert.Equal(before, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("refundPayouts").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Catalog_upload_and_exact_retry_remain_bound_to_actual_catalog_target_after_forward_extension() {
        await using var data = await EvidenceAdmissionTestData.CreateAsync(fixture, sku: true);
        var admitted = await data.ReserveAsync();
        var bytes = ImageDecoderTests.Png(4, 3);
        using var source = new MemoryStream(bytes);
        var uploaded = await data.Service.UploadAsync(data.Actor, admitted.ReservationId, admitted.Revision, source, EvidenceAdmissionTestData.Meta("catalog-after55"));
        Assert.Equal(uploaded, await data.Service.RecoverAsync(data.Actor, admitted.ReservationId, EvidenceAdmissionTestData.Meta("catalog-after55")));
        var reservation = await data.RowAsync("evidenceUploadReservations", admitted.ReservationId);
        Assert.Equal("CatalogSku", reservation["target"]["kind"]); Assert.Equal("CatalogImage", reservation["purpose"]);
        Assert.Equal("evidence.catalog-image.reserve", reservation["operation"]);
        Assert.Equal(1, await data.Rows("documentVersions").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("refundPayouts").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Actual_CaseRead_revocation_inside_document_read_target_denies_final_fence_and_rolls_back() {
        await using var data = await RefundPayoutTests.Data.CreateAsync(fixture); var preserved = await data.PreservedAsync();
        var grantFilter = new BsonDocument("userId", data.Actor.UserId);
        var grants = new BsonArray(await data.Rows("authorizationGrants").Find(grantFilter).Sort(new BsonDocument("_id", 1)).ToListAsync());
        Assert.Contains(grants, grant => grant["permissions"].AsBsonArray.Any(permission => permission == Permissions.CaseRead));
        var draft = await data.Rows("refundPayoutDrafts").Find(new BsonDocument("_id", data.DraftId)).SingleAsync();
        var targets = (IRefundPayoutEvidenceUploadTarget)data.Service; var captured = 0;
        var reads = new AuthorizationStore(data.Database, new(data.Catalog.Client), data.Clock);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => reads.ReadAsync(data.Actor, (_, tx, ct) =>
            targets.WithReadTargetAsync(tx, data.Actor, new(EvidenceTargetKind.RefundPayout, data.DraftId), 1, null, async (target, inner) => {
                captured++; Assert.Equal(EvidenceTargetKind.RefundPayout, target.Target.Kind); Assert.Equal(data.DraftId, target.Target.Id); Assert.Equal(1, target.Revision);
                var revoke = new BsonDocument { { "userId", data.Actor.UserId }, { "permissions", Permissions.CaseRead } };
                var changed = await data.Rows("authorizationGrants").UpdateManyAsync(tx, revoke,
                    Builders<BsonDocument>.Update.Pull("permissions", Permissions.CaseRead), cancellationToken: inner);
                Assert.True(changed.ModifiedCount > 0);
                Assert.Equal(0, await data.Rows("authorizationGrants").CountDocumentsAsync(tx, revoke, cancellationToken: inner));
                return true;
            }, ct), default));
        Assert.Equal(1, captured);
        Assert.Equal(grants, new BsonArray(await data.Rows("authorizationGrants").Find(grantFilter).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.Equal(draft, await data.Rows("refundPayoutDrafts").Find(new BsonDocument("_id", data.DraftId)).SingleAsync());
        Assert.Equal(preserved, await data.PreservedAsync());
        Assert.Equal(0, await data.Rows("refundPayouts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument("associationKind", "RefundPayout")));
        Assert.Equal(0, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument("target.kind", "RefundPayout")));
    }
    [WindowsStorageFact]
    public async Task Actual_outgoing_original_requires_validation_exact_kind_and_current_recorder_before_private_bytes() {
        await using var data = await RefundPayoutTests.Data.CreateAsync(fixture); var preserved = await data.PreservedAsync();
        await data.UploadAsync();
        var leases = await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument());
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Evidence.OpenReadAsync(data.Actor, data.DraftId,
            data.Reservation.ReservationId, 1, data.Reference, RefundPayoutTests.Data.ReadMetadata));
        Assert.Equal(leases, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument()));
        await data.ValidateAsync();
        await using (var opened = await data.Evidence.OpenReadAsync(data.Actor, data.DraftId, data.Reservation.ReservationId, 1,
            data.Reference, RefundPayoutTests.Data.ReadMetadata)) {
            using var bytes = new MemoryStream(); await opened.Content.CopyToAsync(bytes); Assert.Equal(data.Bytes, bytes.ToArray());
        }
        var lease = await data.Rows("evidenceReadLeases").Find(new BsonDocument("target.kind", "RefundPayout")).SingleAsync();
        Assert.Equal(data.DraftId, lease["target"]["id"].AsInt64); Assert.Equal("released", lease["state"]);
        Assert.Equal(data.Reference.Sha256, lease["reference"]["sha256"].AsString);
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Core.OpenRefundPayoutReadAsync(data.Actor,
            new(EvidenceTargetKind.PaymentDeclaration, data.DraftId), 1, data.Reservation.ReservationId, data.Reference, RefundPayoutTests.Data.ReadMetadata));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Evidence.OpenReadAsync(data.Actor, data.DraftId,
            data.Reservation.ReservationId, 1, data.Reference, new("payment-evidence-view", "wrong-purpose")));
        Assert.Equal(1, (await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 987420L),
            Builders<BsonDocument>.Update.Pull("permissions", Permissions.DisbursementRecord))).ModifiedCount);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.OpenReadAsync(data.Actor, data.DraftId,
            data.Reservation.ReservationId, 1, data.Reference, RefundPayoutTests.Data.ReadMetadata));
        Assert.Equal(leases + 1, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument("associationKind", "RefundPayout")));
        Assert.Equal(0, await data.Rows("refundPayouts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(preserved, await data.PreservedAsync());
    }
    [WindowsStorageFact]
    public async Task Actual_current_grant_revocation_after_verified_original_forbids_same_transaction_pin_and_payout() {
        await using var data = await RefundPayoutTests.Data.CreateAsync(fixture); await data.UploadAsync(); await data.ValidateAsync();
        var preserved = await data.PreservedAsync(); var preview = await data.PreviewRecordAsync(); var core = data.Core; var verified = 0;
        core.RefundPayoutReadObserver = new(async () => {
            verified++;
            Assert.Equal(1, (await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", 987420L),
                Builders<BsonDocument>.Update.Pull("permissions", Permissions.DisbursementRecord))).ModifiedCount);
        });
        var metadata = RefundPayoutTests.Data.Meta("outgoing-current-revoked-after-handle");
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => core.WithRefundPayoutPinAsync(data.Actor,
            new(EvidenceTargetKind.RefundPayout, data.DraftId), 1, data.Reservation.ReservationId, data.Reference, RefundPayoutTests.Data.ReadMetadata,
            (claim, handle, started, token) => data.Service.RecordVerifiedAsync(data.Actor, data.DraftId, 1, data.Reservation.ReservationId,
                data.Reference, preview.Binding, metadata, (tx, id, inner) => core.PinRefundPayoutAsync(tx, data.Actor, id, claim, handle, started, inner), token)));
        Assert.Equal(1, verified);
        Assert.Equal(0, await data.Rows("refundPayouts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument("associationKind", "RefundPayout")));
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", "billing.refund.payout.record")));
        Assert.Equal(0, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", "billing.refund.payout.record")));
        Assert.Equal(0, await data.Rows("auditEvents").CountDocumentsAsync(new BsonDocument("kind", "billing.refund.payout.record")));
        var lease = await data.Rows("evidenceReadLeases").Find(new BsonDocument("target.kind", "RefundPayout")).SingleAsync(); Assert.Equal("released", lease["state"]);
        Assert.Equal(preserved, await data.PreservedAsync());
    }
    [WindowsStorageFact]
    public async Task Actual_scanner_rejection_never_exposes_outgoing_bytes_or_pins_a_payout() {
        await using var data = await RefundPayoutTests.Data.CreateAsync(fixture); var preserved = await data.PreservedAsync();
        await data.UploadAsync(); await data.ValidateAsync("infected");
        var status = await data.Evidence.StatusAsync(data.Actor, data.DraftId, data.Reservation.ReservationId);
        Assert.Equal("infected", status.ScanStatus); Assert.Equal("infected_media", status.SafeFailure);
        var preview = await data.PreviewRecordAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.RecordAsync(preview, RefundPayoutTests.Data.Meta("outgoing-rejected-image")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Evidence.OpenReadAsync(data.Actor, data.DraftId, data.Reservation.ReservationId,
            1, data.Reference, RefundPayoutTests.Data.ReadMetadata));
        Assert.Equal(0, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument("target.kind", "RefundPayout")));
        Assert.Equal(0, await data.Rows("evidencePins").CountDocumentsAsync(new BsonDocument("associationKind", "RefundPayout")));
        Assert.Equal(0, await data.Rows("refundPayouts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(preserved, await data.PreservedAsync());
    }
}
