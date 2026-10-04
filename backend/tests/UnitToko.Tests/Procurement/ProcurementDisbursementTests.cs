using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Organizations;
using B = UnitToko.Platform.Procurement.ProcurementDisbursementBson;
using D = UnitToko.Tests.Procurement.ProcurementDisbursementTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ProcurementDisbursementTests(MongoFixture fixture) {
    [WindowsStorageFact]
    public async Task Genuine_advance_partial_recording_pins_original_consumes_capacity_and_recovers_without_cash_or_stock_effects() {
        await using var data = await D.CreateAsync(fixture); var preserved = await data.PreservedAsync();
        Assert.False(await data.Rows(D.Records).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows(D.Heads).Find(new BsonDocument()).AnyAsync());
        var options = await data.Service.OptionsAsync(data.Actor, data.Input().AuthorizationId);
        Assert.Equal("10000.00", options["capacity"]["remainingAmount"].AsString); Assert.Equal(0L, options["capacity"]["version"].AsInt64);
        await data.UploadAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Evidence.OpenReadAsync(data.Actor, data.DraftId, data.Reservation.ReservationId, 1, data.Reference, D.ReadMetadata));
        await data.ValidateAsync();
        await using (var original = await data.Evidence.OpenReadAsync(data.Actor, data.DraftId, data.Reservation.ReservationId, 1, data.Reference, D.ReadMetadata)) {
            using var bytes = new MemoryStream(); await original.Content.CopyToAsync(bytes); Assert.Equal(data.Bytes, bytes.ToArray());
        }
        var preview = await data.PreviewRecordAsync(); var metadata = D.Meta("record-first-part");
        var result = await data.RecordAsync(preview, metadata); Assert.Equal("recorded_unverified", result["status"].AsString);
        Assert.Equal("6000.00", result["amount"].AsString);
        Assert.Equal(result, await data.RecordAsync(preview, metadata));
        Assert.Equal(result, await data.Service.LookupAsync(data.Actor, D.Prefix + "record", metadata.IdempotencyKey));
        Assert.Equal(data.Draft, await data.Service.LookupAsync(data.Actor, D.Prefix + "draft", "disbursement-draft"));
        Assert.Equal(preserved, await data.PreservedAsync());
        options = await data.Service.OptionsAsync(data.Actor, data.Input().AuthorizationId);
        Assert.Equal("6000.00", options["capacity"]["recordedAmount"].AsString); Assert.Equal("4000.00", options["capacity"]["remainingAmount"].AsString);
        var pin = await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "ProcurementDisbursement")).SingleAsync();
        Assert.Equal(result["disbursementId"], pin["associationId"]); Assert.False(pin.Contains("refundPayoutDraftId")); Assert.False(pin.Contains("declarationId"));
        data.Draft = await data.CreateDraftAsync("second-part-draft", data.Input(4000, "SYNTHETIC-TRANSFER-0002"));
        (data.Reservation, data.Reference) = await data.UploadForAsync(data.DraftId, "second-part-image"); await data.ValidateAsync();
        var secondPreview = await data.PreviewRecordAsync();
        var beforeMutation = await data.StateAsync();
        var pinAudits = new BsonArray(await data.Rows("evidencePinAudits").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var prior = await data.Rows(D.Records).Find(new BsonDocument("_id", result["disbursementId"])).SingleAsync();
        var owner = data.Service; var metadataMutation = D.Meta("record-with-changed-prior-claim");
        AuthorizationStore.ValidateMetadata(metadataMutation);
        var bodyHash = AuthorizationStore.CommandHash(B.RecordBody(data.DraftId, 1, data.Reservation.ReservationId,
            data.Reference, secondPreview["binding"].AsString), metadataMutation.Reason);
        Assert.Null(await owner.RecoverRecordBodyAsync(data.Actor, data.DraftId, metadataMutation.IdempotencyKey, bodyHash, default));
        await owner.StorageAsync(default); await ProcurementDisbursementEvidenceSchemas.RequireStorageAsync(data.Database, default);
        var core = new EvidenceService(data.Database, data.Unit, new(data.Database), data.Clock, data.Files.Registration, procurementDisbursementTargets: owner);
        var pinned = 0; var mutated = 0;
        var corruption = await Record.ExceptionAsync(() => core.WithProcurementDisbursementPinAsync(data.Actor,
            new(EvidenceTargetKind.ProcurementDisbursement, data.DraftId), 1, data.Reservation.ReservationId, data.Reference, D.ReadMetadata,
            (claim, handle, started, ct) => {
                Assert.Equal(TimeSpan.FromSeconds(5), claim.InitialLeaseDuration); Assert.Equal(TimeSpan.FromSeconds(60), claim.RequestDuration);
                return owner.RecordWithProofAsync(data.Actor, data.DraftId, 1, data.Reservation.ReservationId, data.Reference,
                    secondPreview["binding"].AsString, metadataMutation, async (tx, id, target, inner) => {
                        var final = await new EvidenceService(data.Database, data.Unit, new(data.Database), data.Clock,
                            data.Files.Registration, procurementDisbursementTargets: target)
                            .PinProcurementDisbursementAsync(tx, data.Actor, id, claim, handle, started, inner);
                        pinned++;
                        // Corrupt only within this real command's transaction, after the
                        // historical proof and new pin. A cached proof cannot excuse changed bytes.
                        var changed = prior.DeepClone().AsBsonDocument;
                        changed["reason"] = "Synthetic prior claim changed after its complete source proof";
                        changed["snapshotHash"] = B.Hash(changed);
                        Assert.Equal(1L, (await data.Rows(D.Records).ReplaceOneAsync(tx, prior, changed, cancellationToken: inner)).ModifiedCount);
                        mutated++; return final;
                    }, ct);
            }));
        Assert.Equal(1, pinned); Assert.Equal(1, mutated);
        Assert.Equal("procurement_disbursement_integrity_unavailable", Assert.IsType<PersistenceConflictException>(corruption).Code);
        Assert.Equal(prior, await data.Rows(D.Records).Find(new BsonDocument("_id", prior["_id"])).SingleAsync());
        Assert.Equal(beforeMutation, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());
        Assert.Equal(pinAudits, new BsonArray(await data.Rows("evidencePinAudits").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.Null(await data.Service.LookupAsync(data.Actor, D.Prefix + "record", metadataMutation.IdempotencyKey));
        var second = await data.RecordAsync(secondPreview, D.Meta("record-second-part"));
        Assert.NotEqual(result["disbursementId"], second["disbursementId"]);
        var head = await data.Rows(D.Heads).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(2L, head["version"].AsInt64);
        Assert.Equal(2, await data.Rows(D.Records).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Rows(D.Allocations).CountDocumentsAsync(new BsonDocument()));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewAsync(data.Actor, data.Input(1, "SYNTHETIC-OVERSPEND")));
        Assert.Equal(preserved, await data.PreservedAsync());
    }

    [WindowsStorageFact]
    public async Task Competing_partial_recordings_cannot_overdraw_one_authorization_or_reuse_a_draft() {
        await using var data = await D.CreateAsync(fixture, createDraft: false); var preserved = await data.PreservedAsync();
        var first = await data.CreateDraftAsync("racing-draft-one", data.Input(7000, "RACING-ONE"));
        var second = await data.CreateDraftAsync("racing-draft-two", data.Input(7000, "RACING-TWO"));
        var drafts = new[] { first, second }; var uploads = new (ReservationResult Reservation, EvidenceRef Reference)[2];
        var previews = new BsonDocument[2];
        for (var i = 0; i < drafts.Length; i++) {
            uploads[i] = await data.UploadForAsync(drafts[i]["draftId"].AsInt64, "racing-image-" + i); await data.ValidateAsync();
            previews[i] = await data.Service.PreviewRecordAsync(data.Actor, drafts[i]["draftId"].AsInt64, 1, uploads[i].Reservation.ReservationId, uploads[i].Reference);
        }
        var results = await Task.WhenAll(drafts.Select(async (draft, i) => await Record.ExceptionAsync(() => data.Evidence.RecordAsync(data.Actor,
            draft["draftId"].AsInt64, 1, uploads[i].Reservation.ReservationId, uploads[i].Reference, previews[i]["binding"].AsString, D.Meta("race-record-" + i)))));
        Assert.Single(results, error => error is null); Assert.IsType<PersistenceConflictException>(Assert.Single(results, error => error is not null));
        var saved = await data.Rows(D.Records).Find(new BsonDocument()).SingleAsync(); Assert.Equal("7000.00", saved["input"]["amount"].AsString);
        Assert.Single(await data.Rows(D.Allocations).Find(new BsonDocument()).ToListAsync());
        Assert.Single(await data.Rows("evidencePins").Find(new BsonDocument("associationKind", "ProcurementDisbursement")).ToListAsync());
        var winner = Array.FindIndex(results, error => error is null);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Evidence.RecordAsync(data.Actor, drafts[winner]["draftId"].AsInt64, 1,
            uploads[winner].Reservation.ReservationId, uploads[winner].Reference, previews[winner]["binding"].AsString, D.Meta("another-key-same-draft")));
        Assert.Equal("3000.00", (await data.Service.OptionsAsync(data.Actor, data.Input().AuthorizationId))["capacity"]["remainingAmount"].AsString);
        Assert.Equal(preserved, await data.PreservedAsync());
    }

    [WindowsStorageFact]
    public async Task Every_record_capacity_pin_and_effect_write_rolls_back_and_missing_history_never_becomes_free_capacity() {
        await using var data = await D.CreateAsync(fixture); await data.UploadAsync(); await data.ValidateAsync();
        var preview = await data.PreviewRecordAsync(); var before = await data.StateAsync(); var preserved = await data.PreservedAsync();
        foreach (var (table, field, value) in new[] {
            (D.Records, "draftId", (BsonValue)data.DraftId), (D.Allocations, "authorizationId", (BsonValue)data.Input().AuthorizationId),
            (D.Heads, "version", (BsonValue)1L), ("evidencePins", "associationKind", (BsonValue)"ProcurementDisbursement"),
            ("auditEvents", "kind", (BsonValue)(D.Prefix + "record")), ("outboxEvents", "kind", (BsonValue)(D.Prefix + "record")),
            ("idempotencyRecords", "operation", (BsonValue)(D.Prefix + "record")) }) {
            var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
            var original = definition["options"]["validator"].DeepClone().AsBsonDocument;
            try {
                await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table },
                    { "validator", new BsonDocument("$and", new BsonArray { original, new BsonDocument(field, new BsonDocument("$ne", value)) }) },
                    { "validationLevel", "strict" }, { "validationAction", "error" } });
                Assert.NotNull(await Record.ExceptionAsync(() => data.RecordAsync(preview, D.Meta("atomic-record"))));
                Assert.Equal(before, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());
            } finally {
                await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", original }, { "validationLevel", "strict" }, { "validationAction", "error" } });
            }
        }
        var recorded = await data.RecordAsync(preview, D.Meta("atomic-record"));
        foreach (var (table, filter) in new[] { (D.Records, new BsonDocument()), (D.Heads, new BsonDocument()), (D.Allocations, new BsonDocument()),
            ("evidencePins", new BsonDocument("associationKind", "ProcurementDisbursement")),
            ("auditEvents", new BsonDocument("kind", D.Prefix + "record")), ("outboxEvents", new BsonDocument("kind", D.Prefix + "record")),
            ("idempotencyRecords", new BsonDocument("operation", D.Prefix + "record")) }) {
            var row = await data.Rows(table).Find(filter).SingleAsync(); Assert.Equal(1, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", row["_id"]))).DeletedCount);
            try {
                await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.ReadAsync(data.Reader, data.DraftId));
                await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.LookupAsync(data.Actor, D.Prefix + "record", "atomic-record"));
                await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.OptionsAsync(data.Actor, data.Input().AuthorizationId));
            } finally { await data.Rows(table).InsertOneAsync(row); }
        }
        Assert.Equal(recorded, await data.Service.LookupAsync(data.Actor, D.Prefix + "record", "atomic-record"));
        Assert.Equal(preserved, await data.PreservedAsync());
    }

    [WindowsStorageFact]
    public async Task Reads_are_masked_and_scoped_private_original_is_separate_and_recording_requires_independent_funding_approver() {
        await using var data = await D.CreateAsync(fixture); var view = await data.Service.ReadAsync(data.Reader, data.DraftId);
        Assert.DoesNotContain(SupplierBankTestData.Details.AccountNumber, view.ToJson()); Assert.DoesNotContain(SupplierBankTestData.Details.HolderName, view.ToJson());
        Assert.DoesNotContain(UnitToko.Tests.Ordering.ReceivingAccountTestData.Details.AccountNumber, view.ToJson());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Outsider, data.DraftId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Reader, data.Input()));
        await data.UploadAsync(); await data.ValidateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Evidence.OpenReadAsync(data.Reader, data.DraftId, data.Reservation.ReservationId, 1, data.Reference, D.ReadMetadata));
        var makerGrant = await data.Rows("authorizationGrants").Find(new BsonDocument("_id", data.Actor.UserId)).SingleAsync();
        await data.Base.AddGrantAsync(data.Funding.Checker, 889900, UnitToko.Tests.Catalog.CatalogAuthoringTestData.Shop,
            makerGrant["permissions"].AsBsonArray.Select(value => value.AsString).ToArray());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Funding.Checker, data.Input()));
        await OrganizationAddressTests.MergePeopleAsync(data.Funding.Procurement.Organizations, data.Actor.UserId, data.Funding.Checker.UserId);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Actor, data.Input()));
        Assert.False(await data.Rows(D.Records).Find(new BsonDocument()).AnyAsync());
    }

    [WindowsStorageFact]
    public async Task Changed_or_missing_current_sources_deny_new_record_but_retained_record_history_survives_policy_withdrawal() {
        await using var data = await D.CreateAsync(fixture); await data.UploadAsync(); await data.ValidateAsync();
        var preview = await data.PreviewRecordAsync(); var before = await data.StateAsync();
        foreach (var (table, id) in new[] { (D.Policies, D.PolicyId), (SupplierBankTestData.Policies, SupplierBankTestData.PolicyId),
            (ProcurementFundingTestData.Policies, ProcurementFundingTestData.PolicyId) }) {
            await data.Rows(table).UpdateOneAsync(new BsonDocument("_id", id), Builders<BsonDocument>.Update.Set("active", false));
            try {
                Assert.NotNull(await Record.ExceptionAsync(() => data.RecordAsync(preview, D.Meta("withdrawn-source-record"))));
                Assert.Equal(before, await data.StateAsync());
            } finally { await data.Rows(table).UpdateOneAsync(new BsonDocument("_id", id), Builders<BsonDocument>.Update.Set("active", true)); }
        }
        var result = await data.RecordAsync(preview, D.Meta("retained-record"));
        await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", D.PolicyId), Builders<BsonDocument>.Update.Set("active", false));
        await data.Rows(SupplierBankTestData.Policies).UpdateOneAsync(new BsonDocument("_id", SupplierBankTestData.PolicyId), Builders<BsonDocument>.Update.Set("active", false));
        Assert.Equal("recorded_unverified", (await data.Service.ReadAsync(data.Reader, data.DraftId))["state"].AsString);
        Assert.Equal(result, await data.Service.LookupAsync(data.Actor, D.Prefix + "record", "retained-record"));
    }

    [WindowsStorageTheory]
    [InlineData("funding-policy")] [InlineData("bank-policy")] [InlineData("mfa")] [InlineData("session")] [InlineData("person")]
    public async Task Current_source_or_identity_change_after_original_verification_cannot_commit_a_record(string change) {
        await using var data = await D.CreateAsync(fixture); await data.UploadAsync(); await data.ValidateAsync();
        var preview = await data.PreviewRecordAsync(); var before = await data.StateAsync(); var core = data.Core; var observed = 0;
        core.ProcurementDisbursementReadObserver = new(async () => {
            observed++;
            if (change == "funding-policy") await data.Rows(ProcurementFundingTestData.Policies).UpdateOneAsync(new BsonDocument("_id", ProcurementFundingTestData.PolicyId), Builders<BsonDocument>.Update.Set("active", false));
            if (change == "bank-policy") await data.Rows(SupplierBankTestData.Policies).UpdateOneAsync(new BsonDocument("_id", SupplierBankTestData.PolicyId), Builders<BsonDocument>.Update.Set("active", false));
            if (change == "mfa") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
            if (change == "session") await data.Catalog.Sessions.RevokeAsync(data.Catalog.Issued[data.Actor.UserId].OpaqueToken, "Synthetic session revoked after verified original");
            if (change == "person") await OrganizationAddressTests.MergePeopleAsync(data.Funding.Procurement.Organizations, data.Actor.UserId, data.Funding.Checker.UserId);
        });
        var error = await Record.ExceptionAsync(() => core.WithProcurementDisbursementPinAsync(data.Actor, new(EvidenceTargetKind.ProcurementDisbursement, data.DraftId),
            1, data.Reservation.ReservationId, data.Reference, D.ReadMetadata,
            (claim, handle, started, ct) => data.Service.RecordWithProofAsync(data.Actor, data.DraftId, 1, data.Reservation.ReservationId, data.Reference,
                preview["binding"].AsString, D.Meta("record-after-" + change), (tx, id, target, inner) =>
                    new EvidenceService(data.Database, data.Unit, new(data.Database), data.Clock, data.Files.Registration, procurementDisbursementTargets: target)
                        .PinProcurementDisbursementAsync(tx, data.Actor, id, claim, handle, started, inner), ct)));
        Assert.Equal(1, observed); Assert.True(error is AuthorizationDeniedException or PersistenceConflictException, error?.ToString());
        Assert.Equal(before, await data.StateAsync());
    }

    [WindowsStorageFact]
    public async Task Funding_capture_is_bound_to_its_actual_service_actor_transaction_and_immutable_source()
    {
        await using var data = await D.CreateAsync(fixture, createDraft: false); var service = data.Funding.Service;
        var authorization = new AuthorizationStore(data.Database, data.Unit, data.Clock); var input = data.Input();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => authorization.ReadAsync(data.Reader, (authority, tx, ct) =>
            service.CaptureDisbursementAsync(data.Database, data.Reader, authority, tx, input.FundingRequestId, input.FundingDecisionId, input.AuthorizationId, ct), default));
        var captured = await authorization.ReadAsync(data.Actor, async (authority, tx, ct) => {
            var proof = await service.CaptureDisbursementAsync(data.Database, data.Actor, authority, tx, input.FundingRequestId, input.FundingDecisionId, input.AuthorizationId, ct);
            await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Funding.Service.FenceDisbursementAsync(data.Database, data.Actor, authority, tx, proof, ct));
            await Assert.ThrowsAsync<PersistenceConflictException>(() => service.FenceDisbursementAsync(data.Database, data.Reader, authority, tx, proof, ct));
            proof.Source["authorization"]["amount"] = "999999.00";
            await Assert.ThrowsAsync<PersistenceConflictException>(() => service.FenceDisbursementAsync(data.Database, data.Actor, authority, tx, proof, ct));
            return proof;
        }, default);
        Assert.ThrowsAny<Exception>(() => { captured.RequireCurrent(); });
        Assert.False(await data.Rows(D.Drafts).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows(D.Records).Find(new BsonDocument()).AnyAsync());
    }

    [WindowsStorageFact]
    public async Task Exact_amount_date_policy_and_source_versions_are_validated_before_a_draft_reserves_anything() {
        await using var data = await D.CreateAsync(fixture, createDraft: false); var before = await data.StateAsync(); var input = data.Input();
        foreach (var changed in new[] { input with { Amount = new Money(0) }, input with { Amount = new Money(10000.01m) },
            input with { TransferReference = " padded" }, input with { TransferDate = input.TransferDate.AddDays(1) },
            input with { TransferDate = input.TransferDate.AddDays(-1) }, input with { PolicyRevision = 2 },
            input with { BeneficiaryVersion = input.BeneficiaryVersion + 1 }, input with { FundingDecisionId = input.FundingDecisionId + 1000 },
            input with { PayingAccountVersionId = input.PayingAccountVersionId + 1000 } }) {
            var error = await Record.ExceptionAsync(() => data.Service.PreviewAsync(data.Actor, changed));
            Assert.True(error is DomainValidationException or PersistenceConflictException, error?.ToString());
        }
        await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", D.PolicyId), Builders<BsonDocument>.Update.Set("allowPartialRecordings", false));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewAsync(data.Actor, input));
        _ = await data.Service.PreviewAsync(data.Actor, data.Input(10000));
        Assert.Equal(before, await data.StateAsync());
    }
}
