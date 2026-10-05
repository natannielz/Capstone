using System.Collections.Immutable;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Billing;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentSupplementCorrectionTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Real_image_and_whole_PDF_reference_persist_beside_manual_correction_and_replay_original_key_after_restart()
    {
        await using var data = await PaymentSupplementCorrectionTestData.CreateAsync(fixture);
        var observations = ImmutableArray.Create(data.Observation()); var command = PaymentSupplementCorrectionTestData.Meta("accepted");
        var saved = await data.Data.Service.CorrectReviewAsync(data.Reviewer, data.Data.DeclarationId, 0, data.Review.Binding, data.Fields, observations, command);
        var version = await data.Rows(PaymentCorrectionBson.Versions).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(2, version["schemaVersion"].AsInt32); Assert.Equal(PaymentCorrectionBson.Binding(data.Review.Binding), version["binding"]);
        Assert.True(PaymentCorrectionBson.Observations(observations).Equals(version["supplementaryObservations"]), "Exact persisted supplementary page/field source changed");
        Assert.Equal(PaymentCorrectionBson.Hash(version), version["snapshotHash"]);
        var restarted = data.Restart();
        Assert.Equal(saved, await restarted.CorrectReviewAsync(data.Reviewer, data.Data.DeclarationId, 0, data.Review.Binding, data.Fields, observations, command));
        Assert.Equal(saved, await restarted.RecoverReviewCorrectionAsync(data.Reviewer, data.Data.DeclarationId, command.IdempotencyKey));
        var shown = await restarted.ReadCorrectionsAsync(data.Reviewer, data.Data.DeclarationId, data.Review.Binding, new("payment-ocr-view", "persisted-reference-after-restart"));
        Assert.Equal("1234567.89", Assert.Single(shown.OriginalFields, field => field.Field == "amount").Value);
        Assert.Equal("4000.00", Assert.Single(shown.Fields, field => field.Field == "amount").Value);
        var recent = Assert.Single(shown.Recent);
        var annotations = recent.SupplementaryObservations ?? throw new InvalidOperationException("The genuine supplementary reference was omitted");
        var reference = Assert.Single(annotations);
        Assert.Equal(data.PdfResult["_id"].AsInt64, reference.Candidate.ResultId); Assert.Equal(data.PdfResult["resultHash"].AsString, reference.Candidate.ResultHash);
        Assert.Equal(data.Pdf.Reference, reference.Candidate.Evidence); Assert.Equal(1, reference.Candidate.PageIndex); Assert.Equal("amount", reference.Candidate.Field);
        Assert.True(reference.OriginalReviewed); Assert.NotEqual(data.Review.Binding.Evidence, reference.Candidate.Evidence);
        var changed = await Assert.ThrowsAsync<PersistenceConflictException>(() => restarted.CorrectReviewAsync(data.Reviewer, data.Data.DeclarationId,
            0, data.Review.Binding, data.Fields, ImmutableArray.Create(data.Observation(0)), command));
        Assert.Equal("idempotency_key_reused", changed.Code);
        foreach (var name in new[] { PaymentCorrectionBson.Heads, PaymentCorrectionBson.Versions }) Assert.Equal(1L, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", PaymentCorrectionBson.Operation)));
        Assert.Equal(1L, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", PaymentCorrectionBson.Operation)));
        await data.UnchangedAsync();
    }

    [WindowsStorageFact]
    public async Task Wrong_primary_supplement_revision_result_hash_page_field_and_foreign_reviewer_cannot_create_a_correction()
    {
        await using var data = await PaymentSupplementCorrectionTestData.CreateAsync(fixture); var valid = data.Observation();
        // Even a genuine additional review grant cannot make the original uploader independent.
        await data.Data.Catalog.Base.AddGrantAsync(data.Data.Actor, 98554, CatalogAuthoringTestData.Shop,
            [Permissions.PaymentReviewRead, Permissions.InvoiceRead, Permissions.PaymentReviewCorrect]);
        var maker = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Data.Service.CorrectReviewAsync(data.Data.Actor,
            data.Data.DeclarationId, 0, data.Review.Binding, data.Fields, ImmutableArray.Create(valid), PaymentSupplementCorrectionTestData.Meta("primary-uploader")));
        Assert.Equal("independent_person_required", maker.Code);
        var foreign = await data.Data.Catalog.ActorAsync(98552, CatalogAuthoringTestData.OtherOwner,
            Permissions.PaymentReviewRead, Permissions.InvoiceRead, Permissions.PaymentReviewCorrect);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Data.Service.CorrectReviewAsync(foreign, data.Data.DeclarationId,
            0, data.Review.Binding, data.Fields, ImmutableArray.Create(valid), PaymentSupplementCorrectionTestData.Meta("foreign")));
        var candidates = new[] {
            (valid with { SupplementId = valid.SupplementId + 100 }, "payment_supplement_history_invalid"),
            (valid with { SupplementRevision = 1 }, "invalid_payment_correction"),
            (valid with { Candidate = new(valid.Candidate.ResultId + 100, valid.Candidate.ResultHash, valid.Candidate.Evidence, 1, "amount") }, "pdf_document_processing_history_invalid"),
            (valid with { Candidate = new(valid.Candidate.ResultId, new string('f', 64), valid.Candidate.Evidence, 1, "amount") }, "pdf_document_processing_history_invalid"),
            (valid with { Candidate = new(valid.Candidate.ResultId, valid.Candidate.ResultHash, data.Data.Reference, 1, "amount") }, "payment_draft_changed"),
            (valid with { Candidate = new(valid.Candidate.ResultId, valid.Candidate.ResultHash, valid.Candidate.Evidence, 2, "amount") }, "pdf_document_processing_history_invalid"),
            (valid with { Candidate = new(valid.Candidate.ResultId, valid.Candidate.ResultHash, valid.Candidate.Evidence, 1, "currency") }, "invalid_payment_correction"),
            (valid with { OriginalReviewed = false }, "invalid_payment_correction") };
        for (var index = 0; index < candidates.Length; index++) {
            var error = await Record.ExceptionAsync(() => data.Data.Service.CorrectReviewAsync(data.Reviewer, data.Data.DeclarationId, 0,
                data.Review.Binding, data.Fields, ImmutableArray.Create(candidates[index].Item1), PaymentSupplementCorrectionTestData.Meta("wrong-source-" + index)));
            Assert.True(error is DomainValidationException or PersistenceConflictException,
                "Unexpected supplementary source refusal type: " + error?.GetType().Name);
            Assert.Equal(candidates[index].Item2, error switch {
                DomainValidationException validation => validation.Code, PersistenceConflictException conflict => conflict.Code, _ => null });
        }
        var substituted = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Data.Service.CorrectReviewAsync(data.Reviewer,
            data.Data.DeclarationId, 0, data.Review.Binding with { Evidence = data.Pdf.Reference }, data.Fields,
            ImmutableArray.Create(valid), PaymentSupplementCorrectionTestData.Meta("PDF-cannot-be-primary")));
        Assert.Equal("payment_draft_changed", substituted.Code);
        var stale = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Data.Service.CorrectReviewAsync(data.Reviewer,
            data.Data.DeclarationId, 1, data.Review.Binding, data.Fields, ImmutableArray.Create(valid), PaymentSupplementCorrectionTestData.Meta("stale-review")));
        Assert.Equal("payment_draft_changed", stale.Code);
        Assert.False(await data.Rows(PaymentCorrectionBson.Versions).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", PaymentCorrectionBson.Operation)).AnyAsync());
        await data.UnchangedAsync();
    }

    [WindowsStorageFact]
    public async Task Current_D06_grants_and_expired_actual_originals_block_new_annotations_even_with_a_fresh_independent_reviewer()
    {
        await using var data = await PaymentSupplementCorrectionTestData.CreateAsync(fixture);
        var policy = await data.Rows(PaymentSupplementBson.Policies).Find(new BsonDocument()).SingleAsync();
        await data.Rows(PaymentSupplementBson.Policies).UpdateOneAsync(new BsonDocument("_id", policy["_id"]), Builders<BsonDocument>.Update.Set("active", false));
        var unavailable = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Data.Service.CorrectReviewAsync(data.Reviewer, data.Data.DeclarationId,
            0, data.Review.Binding, data.Fields, ImmutableArray.Create(data.Observation()), PaymentSupplementCorrectionTestData.Meta("D06-revoked")));
        Assert.Equal("payment_supplement_policy_unavailable", unavailable.Code);
        await data.Rows(PaymentSupplementBson.Policies).ReplaceOneAsync(new BsonDocument("_id", policy["_id"]), policy);
        await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", data.Reviewer.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Data.Service.CorrectReviewAsync(data.Reviewer, data.Data.DeclarationId,
            0, data.Review.Binding, data.Fields, ImmutableArray.Create(data.Observation()), PaymentSupplementCorrectionTestData.Meta("grant-revoked")));
        var afterNative = data.Data.Catalog.Clock.UtcNow;
        data.Data.Catalog.Clock.UseSystemClock = false; data.Data.Catalog.Clock.UtcNow = afterNative.AddMinutes(6);
        var fresh = await PaymentCorrectionTests.Corrector(data.Data, 98553);
        await PersonTestData.SeedDistinctPeopleAsync(data.Data.Database, data.Data.Catalog.Clock, fresh.UserId);
        var expired = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Data.Service.CorrectReviewAsync(fresh, data.Data.DeclarationId,
            0, data.Review.Binding, data.Fields, ImmutableArray.Create(data.Observation()), PaymentSupplementCorrectionTestData.Meta("original-expired")));
        Assert.Equal("evidence_read_not_eligible", expired.Code);
        Assert.False(await data.Rows(PaymentCorrectionBson.Versions).Find(new BsonDocument()).AnyAsync()); await data.UnchangedAsync();
    }

    [WindowsStorageFact]
    public async Task Required_audit_failure_rolls_back_reference_and_all_correction_effects_then_same_original_key_succeeds()
    {
        await using var data = await PaymentSupplementCorrectionTestData.CreateAsync(fixture);
        var definition = await (await data.Data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "auditEvents") })).SingleAsync();
        var validator = definition["options"]["validator"]; var command = PaymentSupplementCorrectionTestData.Meta("audit-rollback");
        await data.Data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", new BsonDocument("$and",
            new BsonArray { validator, new BsonDocument("kind", new BsonDocument("$ne", PaymentCorrectionBson.Operation)) }) } });
        try {
            var refused = await Assert.ThrowsAsync<MongoWriteException>(() => data.Data.Service.CorrectReviewAsync(data.Reviewer, data.Data.DeclarationId,
                0, data.Review.Binding, data.Fields, ImmutableArray.Create(data.Observation()), command)); Assert.Equal(121, refused.WriteError.Code);
            foreach (var name in new[] { PaymentCorrectionBson.Heads, PaymentCorrectionBson.Versions }) Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
            Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", PaymentCorrectionBson.Operation)).AnyAsync());
            Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", PaymentCorrectionBson.Operation)).AnyAsync());
            Assert.Null(await data.Restart().RecoverReviewCorrectionAsync(data.Reviewer, data.Data.DeclarationId, command.IdempotencyKey));
            await data.UnchangedAsync();
        } finally { await data.Data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", validator } }); }
        await data.Restart().CorrectReviewAsync(data.Reviewer, data.Data.DeclarationId, 0, data.Review.Binding, data.Fields, ImmutableArray.Create(data.Observation()), command);
        Assert.Equal(1L, await data.Rows(PaymentCorrectionBson.Versions).CountDocumentsAsync(new BsonDocument())); await data.UnchangedAsync();
    }
}
