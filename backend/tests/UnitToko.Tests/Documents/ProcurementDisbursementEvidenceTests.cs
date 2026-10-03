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
using D = UnitToko.Tests.Procurement.ProcurementDisbursementTestData;

namespace UnitToko.Tests.Documents;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ProcurementDisbursementEvidenceTests(MongoFixture fixture)
{
    [Theory]
    [InlineData(EvidenceTargetKind.PaymentDeclaration, EvidencePurpose.ProcurementDisbursementImage)]
    [InlineData(EvidenceTargetKind.RefundPayout, EvidencePurpose.ProcurementDisbursementImage)]
    [InlineData(EvidenceTargetKind.CatalogSku, EvidencePurpose.ProcurementDisbursementImage)]
    [InlineData(EvidenceTargetKind.ProcurementDisbursement, EvidencePurpose.PaymentImage)]
    [InlineData(EvidenceTargetKind.ProcurementDisbursement, EvidencePurpose.RefundPayoutImage)]
    [InlineData(EvidenceTargetKind.ProcurementDisbursement, EvidencePurpose.CatalogImage)]
    public void Procurement_evidence_has_a_distinct_owner_and_never_impersonates_retained_kinds(EvidenceTargetKind target, EvidencePurpose purpose)
    {
        Assert.Throws<DomainValidationException>(() => new ReserveEvidenceImage(new(target, 1), purpose, 1));
        Assert.Throws<DomainValidationException>(() => new ReserveCatalogImage(new(EvidenceTargetKind.ProcurementDisbursement, 1)));
        var admitted = new ReserveEvidenceImage(new(EvidenceTargetKind.ProcurementDisbursement, 1), EvidencePurpose.ProcurementDisbursementImage, 1);
        Assert.Equal(EvidenceTargetKind.ProcurementDisbursement, admitted.Target.Kind);
        Assert.Equal(EvidencePurpose.ProcurementDisbursementImage, admitted.Purpose);
    }

    [Theory]
    [InlineData("evidenceLineages")] [InlineData("evidenceUploadReservations")]
    [InlineData("auditEvents")] [InlineData("idempotencyRecords")]
    [InlineData("evidenceReadLeases")] [InlineData("evidencePins")]
    public void Forward_extension_preserves_exact_v55_and_stronger_guards_but_rejects_unknown_widening(string name)
    {
        var previous = RefundPayoutEvidenceSchemas.Validator(name);
        var guard = new BsonDocument("fixtureGuard", new BsonDocument("$ne", true));
        var current = new BsonDocument("$and", new BsonArray { previous, guard });
        var next = ProcurementDisbursementEvidenceSchemas.Extend(name, current);
        Assert.Equal(guard, next["$and"][1]);
        Assert.Equal(previous, next["$and"][0]["$or"][0]);
        Assert.True(ProcurementDisbursementEvidenceSchemas.Supports(name, next, previous));
        Assert.True(ProcurementDisbursementEvidenceSchemas.Supports(name, next, RefundPayoutEvidenceSchemas.PreviousValidator(name)));
        Assert.Equal(next, ProcurementDisbursementEvidenceSchemas.Extend(name, next));
        if (PaymentEvidenceSchemas.Collections.Contains(name)) Assert.True(ProcurementDisbursementEvidenceSchemas.Supports(name, next, EvidenceSchemas.Validator(name)));
        var unknown = new BsonDocument("$or", new BsonArray { previous, new BsonDocument() });
        Assert.False(ProcurementDisbursementEvidenceSchemas.Supports(name, unknown, previous));
        Assert.Throws<InvalidOperationException>(() => ProcurementDisbursementEvidenceSchemas.Extend(name, unknown));
        var weaker = ProcurementDisbursementEvidenceSchemas.Validator(name); weaker["$or"].AsBsonArray.Add(new BsonDocument());
        Assert.False(ProcurementDisbursementEvidenceSchemas.Supports(name, weaker, previous));
        Assert.Throws<InvalidOperationException>(() => ProcurementDisbursementEvidenceSchemas.Extend(name, weaker));
    }

    [WindowsStorageFact]
    public async Task Existing_payment_original_cannot_be_relabelled_as_procurement_or_acquire_a_new_owner_lease()
    {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); await data.ValidateAsync();
        var unit = new MongoUnitOfWork(data.Catalog.Client);
        var documents = new EvidenceService(data.Database, unit, new(data.Database), data.Catalog.Clock, data.Files.Registration, data.Payment.Service);
        var before = await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument());
        foreach (var (target, purpose) in new[] {
            (EvidenceTargetKind.PaymentDeclaration, "procurement-disbursement-evidence-view"),
            (EvidenceTargetKind.ProcurementDisbursement, "payment-evidence-view"),
            (EvidenceTargetKind.ProcurementDisbursement, "procurement-disbursement-evidence-view") })
            await Assert.ThrowsAsync<DomainValidationException>(() => unit.ExecuteAsync((tx, ct) =>
                documents.AcquireProcurementDisbursementReadAsync(tx, data.Actor, new(target, data.DeclarationId), 1,
                    data.Reservation.ReservationId, data.Reference, new(purpose, "distinct-procurement-source"), ct)));
        Assert.Equal(before, await data.Rows("evidenceReadLeases").CountDocumentsAsync(new BsonDocument()));
        Assert.False(await data.Rows("evidencePins").Find(new BsonDocument()).AnyAsync());
        await using var original = await data.OpenAsync();
        using var received = new MemoryStream(); await original.Content.CopyToAsync(received);
        Assert.Equal(data.Bytes, received.ToArray());
    }

    [WindowsStorageFact]
    public async Task Revoking_actual_recording_grant_after_original_verification_denies_pin_and_rolls_back_the_owner_command()
    {
        await using var data = await D.CreateAsync(fixture); await data.UploadAsync(); await data.ValidateAsync();
        var preserved = await data.PreservedAsync(); var state = await data.StateAsync(); var preview = await data.PreviewRecordAsync();
        var core = data.Core; var verified = 0;
        core.ProcurementDisbursementReadObserver = new(async () => {
            verified++;
            Assert.True((await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument {
                { "userId", data.Actor.UserId }, { "permissions", Permissions.DisbursementRecord } },
                Builders<BsonDocument>.Update.Pull("permissions", Permissions.DisbursementRecord))).ModifiedCount > 0);
        });
        var metadata = D.Meta("disbursement-revoked-after-verified-handle");
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => core.WithProcurementDisbursementPinAsync(data.Actor,
            new(EvidenceTargetKind.ProcurementDisbursement, data.DraftId), 1, data.Reservation.ReservationId, data.Reference, D.ReadMetadata,
            (claim, handle, started, ct) => data.Service.RecordWithProofAsync(data.Actor, data.DraftId, 1, data.Reservation.ReservationId,
                data.Reference, preview["binding"].AsString, metadata,
                (tx, id, target, inner) => new EvidenceService(data.Database, data.Unit, new(data.Database), data.Clock, data.Files.Registration,
                    procurementDisbursementTargets: target).PinProcurementDisbursementAsync(tx, data.Actor, id, claim, handle, started, inner), ct)));
        Assert.Equal(1, verified); Assert.Equal(state, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());
        var lease = await data.Rows("evidenceReadLeases").Find(new BsonDocument("target.kind", "ProcurementDisbursement")).SingleAsync();
        Assert.Equal("released", lease["state"]); Assert.Equal(data.DraftId, lease["target"]["id"].AsInt64);
    }

    [WindowsStorageFact]
    public async Task Infected_original_never_returns_private_bytes_or_pins_a_procurement_record()
    {
        await using var data = await D.CreateAsync(fixture); var preserved = await data.PreservedAsync(); var state = await data.StateAsync();
        await data.UploadAsync(); await data.ValidateAsync("infected");
        var status = await data.Evidence.StatusAsync(data.Actor, data.DraftId, data.Reservation.ReservationId);
        Assert.Equal("infected", status.ScanStatus); Assert.Equal("infected_media", status.SafeFailure);
        var preview = await data.PreviewRecordAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.RecordAsync(preview, D.Meta("procurement-rejected-original")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Evidence.OpenReadAsync(data.Actor, data.DraftId,
            data.Reservation.ReservationId, 1, data.Reference, D.ReadMetadata));
        Assert.False(await data.Rows("evidenceReadLeases").Find(new BsonDocument("target.kind", "ProcurementDisbursement")).AnyAsync());
        Assert.Equal(state, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());
    }
}
