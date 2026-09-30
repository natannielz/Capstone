using System.Collections.Immutable;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentCorrectionTests(MongoFixture fixture)
{
    internal static Task<ActorContext> Corrector(PaymentEvidenceReadTests.ReadData data, long id = 9955) => data.Catalog.ActorAsync(id, CatalogAuthoringTestData.Shop,
        Permissions.PaymentReviewRead, Permissions.InvoiceRead, Permissions.PaymentReviewCorrect);
    internal static async Task<PaymentCorrectionView> Prepare(PaymentEvidenceReadTests.ReadData data, ActorContext actor) {
        await PaymentOcrProcessorTests.Submit(data); var processor = PaymentOcrProcessorTests.Processor(data, new PaymentOcrProcessorTests.FixtureEngine());
        await PaymentOcrProcessorTests.Policy(data, processor); Assert.Equal("completed", await processor.RunOnceAsync());
        var extraction = await data.Service.ReadReviewExtractionAsync(actor, data.DeclarationId, 1, data.Reference, new("payment-ocr-view", "synthetic-correction"));
        return await data.Service.ReadCorrectionsAsync(actor, data.DeclarationId, new(1, extraction.SubmissionId, data.Reference, extraction.ResultId!.Value), new("payment-ocr-view", "synthetic-correction"));
    }
    internal static ImmutableArray<PaymentReviewField> Changed(PaymentCorrectionView view, string value = "250.00") => [.. view.Fields.Select(field => field.Field == "amount" ? field with { Value = value } : field)];
    [WindowsStorageFact]
    public async Task Immutable_corrections_preserve_OCR_buyer_maker_history_and_recover_exact_commits_without_cash() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); var actor = await Corrector(data); var view = await Prepare(data, actor);
        var metadata = EvidenceAdmissionTestData.Meta("correction-first"); var original = await data.Rows("documentExtractionResults").Find(new BsonDocument()).SingleAsync();
        var submission = await data.Rows("paymentSubmissions").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(0, view.ReviewRevision); Assert.Empty(view.Recent);
        var first = await data.Service.CorrectReviewAsync(actor, data.DeclarationId, 0, view.Binding, Changed(view), metadata);
        Assert.Equal(first, await data.Service.CorrectReviewAsync(actor, data.DeclarationId, 0, view.Binding, Changed(view), metadata));
        Assert.Equal(first, await data.Service.RecoverReviewCorrectionAsync(actor, data.DeclarationId, metadata.IdempotencyKey));
        var current = await data.Service.ReadCorrectionsAsync(actor, data.DeclarationId, view.Binding, new("payment-ocr-view", "synthetic-correction"));
        Assert.Equal(1, current.ReviewRevision); Assert.Equal("100.00", current.OriginalFields.Single(f => f.Field == "amount").Value);
        Assert.Equal("250.00", current.Fields.Single(f => f.Field == "amount").Value); Assert.Equal(actor.UserId, Assert.Single(current.Recent).ActorId);
        var other = await Corrector(data, 9956); await data.Service.CorrectReviewAsync(other, data.DeclarationId, 1, view.Binding, Changed(current, "300.00"), EvidenceAdmissionTestData.Meta("correction-second"));
        current = await data.Service.ReadCorrectionsAsync(actor, data.DeclarationId, view.Binding, new("payment-ocr-view", "synthetic-correction"));
        Assert.Equal(new[] { other.UserId, actor.UserId }, current.Recent.Select(row => row.ActorId).ToArray());
        Assert.Equal(first, await data.Service.CorrectReviewAsync(actor, data.DeclarationId, 0, view.Binding, Changed(view), metadata));
        Assert.Equal(original, await data.Rows("documentExtractionResults").Find(new BsonDocument()).SingleAsync());
        Assert.Equal(submission, await data.Rows("paymentSubmissions").Find(new BsonDocument()).SingleAsync());
        Assert.Equal(2, await data.Rows(PaymentCorrectionBson.Versions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("payments").CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Changed_body_stale_version_source_and_missing_receipt_fail_closed() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); var actor = await Corrector(data); var view = await Prepare(data, actor);
        var metadata = EvidenceAdmissionTestData.Meta("correction-retry");
        await data.Service.CorrectReviewAsync(actor, data.DeclarationId, 0, view.Binding, Changed(view), metadata);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.CorrectReviewAsync(actor, data.DeclarationId, 0, view.Binding, Changed(view, "500.00"), metadata));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.CorrectReviewAsync(actor, data.DeclarationId, 0, view.Binding, Changed(view), EvidenceAdmissionTestData.Meta("stale-correction")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.CorrectReviewAsync(actor, data.DeclarationId, 1, view.Binding with { ExtractionId = view.Binding.ExtractionId + 1 }, Changed(view), EvidenceAdmissionTestData.Meta("wrong-extraction")));
        await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "operation", PaymentCorrectionBson.Operation }, { "key", metadata.IdempotencyKey } });
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.RecoverReviewCorrectionAsync(actor, data.DeclarationId, metadata.IdempotencyKey));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.CorrectReviewAsync(actor, data.DeclarationId, 1, view.Binding, Changed(view), EvidenceAdmissionTestData.Meta("missing-receipt")));
    }
    [WindowsStorageFact]
    public async Task Explicit_permission_scope_fresh_MFA_and_live_grants_are_required() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); var actor = await Corrector(data); var view = await Prepare(data, actor);
        var reader = await PaymentReviewTests.Reviewer(data);
        var outside = await data.Catalog.ActorAsync(9957, CatalogAuthoringTestData.OtherOwner, Permissions.PaymentReviewRead, Permissions.InvoiceRead, Permissions.PaymentReviewCorrect);
        foreach (var denied in new[] { reader, outside, data.Actor, actor with { AuthenticatedAt = data.Catalog.Clock.UtcNow.AddMinutes(-6) } }) {
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.CorrectReviewAsync(denied, data.DeclarationId, 0, view.Binding, Changed(view), EvidenceAdmissionTestData.Meta("denied-correction-" + denied.UserId)));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverReviewCorrectionAsync(denied, data.DeclarationId, "missing"));
        }
        await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", actor.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.CorrectReviewAsync(actor, data.DeclarationId, 0, view.Binding, Changed(view), EvidenceAdmissionTestData.Meta("revoked-correction")));
        Assert.Equal(0, await data.Rows(PaymentCorrectionBson.Versions).CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Concurrent_first_corrections_have_one_winner_and_required_audit_failure_rolls_back_every_effect() {
        await using var data = await PaymentEvidenceReadTests.ReadData.CreateAsync(fixture); var actor = await Corrector(data); var view = await Prepare(data, actor);
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "auditEvents") })).SingleAsync();
        var oldValidator = definition["options"]["validator"];
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", new BsonDocument("$and", new BsonArray {
            oldValidator, new BsonDocument("kind", new BsonDocument("$ne", PaymentCorrectionBson.Operation)) }) } });
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Service.CorrectReviewAsync(actor, data.DeclarationId, 0, view.Binding, Changed(view), EvidenceAdmissionTestData.Meta("failed-correction")));
        foreach (var name in new[] { PaymentCorrectionBson.Heads, PaymentCorrectionBson.Versions }) Assert.Equal(0, await data.Rows(name).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", PaymentCorrectionBson.Operation)));
        Assert.Equal(0, await data.Rows("outboxEvents").CountDocumentsAsync(new BsonDocument("kind", PaymentCorrectionBson.Operation)));
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", oldValidator } });
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Save(string key, string amount) { await start.Task; try {
            await data.Service.CorrectReviewAsync(actor, data.DeclarationId, 0, view.Binding, Changed(view, amount), EvidenceAdmissionTestData.Meta(key)); return true;
        } catch (PersistenceConflictException) { return false; } }
        var a = Save("race-correction-a", "200.00"); var b = Save("race-correction-b", "300.00"); start.SetResult(); Assert.Single(await Task.WhenAll(a, b), result => result);
        Assert.Equal(1, await data.Rows(PaymentCorrectionBson.Versions).CountDocumentsAsync(new BsonDocument()));
    }
}
