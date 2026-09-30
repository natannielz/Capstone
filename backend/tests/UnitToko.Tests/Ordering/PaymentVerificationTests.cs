using System.Collections.Immutable;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentVerificationTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Original_maker_unknown_person_other_store_and_expired_MFA_cannot_verify_and_late_equivalence_blocks_recovery() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture);
        string[] permissions = [Permissions.PaymentReviewRead, Permissions.InvoiceRead, Permissions.PaymentVerify, Permissions.BankReceiptRead, Permissions.BankStatementRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead];
        await data.Read.Catalog.Base.AddGrantAsync(data.Read.Actor, 74880, CatalogAuthoringTestData.Shop, permissions);
        var outside = await data.Read.Catalog.ActorAsync(74881, CatalogAuthoringTestData.OtherOwner, permissions);
        var unknown = await data.Read.Catalog.ActorAsync(74882, CatalogAuthoringTestData.Shop, permissions);
        foreach (var actor in new[] { data.Read.Actor, outside, unknown, data.Reviewer with { AuthenticatedAt = data.Read.Catalog.Clock.UtcNow.AddMinutes(-6) } })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(actor, data.Read.DeclarationId, data.ReceiptId));
        var shown = await data.PreviewAsync(); await data.VerifyAsync(shown);
        var maker = data.Read.Actor.UserId; var reviewer = data.Reviewer.UserId;
        await new MongoUnitOfWork(data.Read.Catalog.Client).ExecuteAsync(async (tx, ct) => {
            await data.Rows("personRecords").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.PersonId(reviewer)), Builders<BsonDocument>.Update.Set("currentEquivalenceId", PersonTestData.GroupId(maker)).Inc("version", 1L), cancellationToken: ct);
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(maker)), Builders<BsonDocument>.Update.Set("memberPersonIds", new BsonArray { PersonTestData.PersonId(maker), PersonTestData.PersonId(reviewer) }).Set("memberUserIds", new BsonArray { maker, reviewer }).Inc("version", 1L), cancellationToken: ct);
            await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(reviewer)), Builders<BsonDocument>.Update.Set("status", "superseded").Inc("version", 1L), cancellationToken: ct); return true;
        });
        var failure = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(data.Reviewer, "verify-payment")); Assert.Equal("independent_person_required", failure.Code);
        Assert.NotNull(await data.Service.ReadAsync(data.Reader, data.Read.DeclarationId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(outside, data.Read.DeclarationId));
    }
    [WindowsStorageFact]
    public async Task Policy_revocation_after_actual_original_verification_and_required_audit_failure_leave_no_match_or_payer_claim() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); var shown = await data.PreviewAsync();
        data.Read.Service.ReadObserver = new(async () => { await data.Rows("evidenceReadPolicies").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("active", false)); });
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.VerifyAsync(shown)); data.Read.Service.ReadObserver = null;
        await data.Rows("evidenceReadPolicies").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("active", true));
        var definition = await (await data.Read.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "auditEvents") })).SingleAsync();
        var validator = definition["options"]["validator"];
        await data.Read.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", new BsonDocument("$and", new BsonArray { validator, new BsonDocument("kind", new BsonDocument("$ne", B.Operation)) }) } });
        await Assert.ThrowsAsync<MongoWriteException>(() => data.VerifyAsync(shown));
        foreach (var name in new[] { B.Decisions, B.Parties }) Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", B.Operation)).AnyAsync());
        Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", B.Operation)).AnyAsync());
        Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Concurrent_same_key_recovers_one_verification_and_retained_evidence_without_more_cash() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); var shown = await data.PreviewAsync();
        var outcomes = await Task.WhenAll(Record.ExceptionAsync(() => data.VerifyAsync(shown)), Record.ExceptionAsync(() => data.VerifyAsync(shown)));
        Assert.Contains(outcomes, error => error is null);
        foreach (var error in outcomes.Where(error => error is not null)) Assert.Equal("version_conflict", Assert.IsType<PersistenceConflictException>(error).Code);
        var saved = await data.VerifyAsync(shown); Assert.Equal(saved, await data.Service.RecoverAsync(data.Reviewer, "verify-payment"));
        Assert.Equal(1, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows(B.Parties).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Missing_decision_anchor_approval_OCR_or_bank_journal_provenance_is_never_healed_by_replay() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); var shown = await data.PreviewAsync(); var result = await data.VerifyAsync(shown);
        foreach (var (name, filter) in new (string, BsonDocument)[] {
            (B.Decisions, new("_id", result["verificationId"])), (B.Parties, new("_id", data.ReceiptId)),
            ("idempotencyRecords", new() { { "operation", B.Operation }, { "key", "verify-payment" } }),
            ("documentExtractionAudits", new()), ("journalEntries", new("source.namespace", "billing.bank-receipt"))
        }) {
            var original = await data.Rows(name).Find(filter).SingleAsync(); await data.Rows(name).DeleteOneAsync(new BsonDocument("_id", original["_id"]));
            Assert.NotNull(await Record.ExceptionAsync(() => data.Service.RecoverAsync(data.Reviewer, "verify-payment")));
            Assert.False(await data.Rows(name).Find(new BsonDocument("_id", original["_id"])).AnyAsync());
            await data.Rows(name).InsertOneAsync(original); // Only the isolated test restores its deliberate corruption.
        }
        Assert.Equal(result, await data.Service.RecoverAsync(data.Reviewer, "verify-payment"));
    }
    [WindowsStorageFact]
    public async Task Reviewed_exact_image_and_receipt_bind_one_payer_without_a_second_cash_or_AR_posting() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); var preview = await data.PreviewAsync();
        Assert.Equal(1L, preview.Review["revision"].AsInt64); Assert.NotEmpty(preview.Discrepancies);
        var before = await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        var result = await data.VerifyAsync(preview);
        Assert.Equal(result, await data.VerifyAsync(preview));
        Assert.Equal(result, await data.Service.RecoverAsync(data.Reviewer, "verify-payment"));
        var saved = await data.Service.ReadAsync(data.Reader, data.Read.DeclarationId); Assert.NotNull(saved);
        Assert.Equal(data.Read.Actor.UserId, saved["source"]["recordedBy"].AsInt64);
        Assert.Equal(1, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows(B.Parties).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows(BankReceiptBson.Receipts).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(before, await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        Assert.False(await data.Rows("paymentAllocations").Find(new BsonDocument()).AnyAsync());
        await data.Rows(B.Policies).UpdateOneAsync(new BsonDocument(), new BsonDocument("$set", new BsonDocument("active", false)));
        await data.Rows("evidenceReadPolicies").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("active", false));
        Assert.Equal(result, await data.Service.RecoverAsync(data.Reviewer, "verify-payment"));
        await data.Rows("evidenceReadPolicies").UpdateOneAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("active", true));
        var changed = data.Corrections.Fields.Select(field => field.Field == "amount" ? field with { Value = "4001.00" } : field).ToImmutableArray();
        var locked = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Read.Service.CorrectReviewAsync(data.Reviewer, data.Read.DeclarationId, 1, data.Corrections.Binding, changed, PaymentVerificationTestData.Meta("after-verification")));
        Assert.Equal("payment_already_verified", locked.Code);
    }
    [WindowsStorageFact]
    public async Task Another_real_declaration_from_the_same_party_shares_cash_and_validates_the_original_payer_claim() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); var first = await data.VerifyAsync(await data.PreviewAsync());
        // A second actual declaration uploads and validates its own immutable original in the same database/store.
        var read = new PaymentEvidenceReadTests.ReadData { Files = data.Read.Files, Payment = data.Read.Payment, Service = data.Read.Service };
        read.DeclarationId = (await read.Payment.Save(null, 0, read.Payment.Input, "second-payer-declaration"))["declarationId"].AsInt64;
        read.Reservation = await read.Service.ReserveAsync(read.Actor, read.DeclarationId, 1, EvidenceAdmissionTestData.Meta("second-payer-reservation"));
        using var stream = new MemoryStream(read.Bytes);
        read.Reference = (await read.Service.UploadAsync(read.Actor, read.DeclarationId, read.Reservation.ReservationId, 1, stream, EvidenceAdmissionTestData.Meta("second-payer-upload"))).Evidence!.Reference;
        var validator = new UnitToko.Platform.DocumentProcessing.EvidenceValidationProcessor(read.Database, new(read.Catalog.Client), new(read.Database), read.Catalog.Clock, read.Files.Registration,
            EvidenceValidationTests.Consumer, new string('c', 64), EvidenceValidationTests.ClaimPolicy, new(1024, 100, 100, 10000),
            new EvidenceValidationTests.FixtureScanner("clean", signaturesAt: read.Catalog.Clock.UtcNow.AddHours(-1)), new UnitToko.Platform.DocumentProcessing.WindowsImageDecoder(ImageDecoderTests.Options()));
        Assert.Equal("completed", await validator.RunOnceAsync());
        await read.Service.SubmitAsync(read.Actor, read.DeclarationId, 1, read.Reservation.ReservationId, read.Reference, EvidenceAdmissionTestData.Meta("second-payer-submit"));
        Assert.Equal("completed", await PaymentOcrProcessorTests.Processor(read, new PaymentOcrProcessorTests.FixtureEngine()).RunOnceAsync());
        var extraction = await read.Service.ReadReviewExtractionAsync(data.Reviewer, read.DeclarationId, 1, read.Reference, new("payment-ocr-view", "second-payer-review"));
        var binding = new PaymentCorrectionBinding(1, extraction.SubmissionId, read.Reference, extraction.ResultId!.Value);
        await read.Service.CorrectReviewAsync(data.Reviewer, read.DeclarationId, 0, binding, data.Corrections.Fields, PaymentVerificationTestData.Meta("second-payer-correction"));
        var preview = await data.Service.PreviewAsync(data.Reviewer, read.DeclarationId, data.ReceiptId);
        var second = await data.Service.VerifyAsync(data.Reviewer, read.DeclarationId, data.ReceiptId, PaymentVerificationTestData.Input(preview), PaymentVerificationTestData.Meta("second-payer-verification"));
        Assert.NotEqual(first["verificationId"], second["verificationId"]); Assert.Equal(2, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows(B.Parties).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows("bankReceipts").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument())); Assert.False(await data.Rows("paymentAllocations").Find(new BsonDocument()).AnyAsync());
        Assert.Equal(second, await data.Service.RecoverAsync(data.Reviewer, "second-payer-verification"));
        var original = await data.Rows(B.Decisions).Find(new BsonDocument("_id", first["verificationId"])).SingleAsync();
        await data.Rows(B.Decisions).DeleteOneAsync(new BsonDocument("_id", first["verificationId"]));
        var missing = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(data.Reviewer, "second-payer-verification"));
        Assert.Equal("payment_verification_unavailable", missing.Code);
        Assert.False(await data.Rows(B.Decisions).Find(new BsonDocument("_id", first["verificationId"])).AnyAsync());
        await data.Rows(B.Decisions).InsertOneAsync(original); // Isolated test repair only.
        Assert.Equal(second, await data.Service.RecoverAsync(data.Reviewer, "second-payer-verification"));
    }
    [WindowsStorageFact]
    public async Task Stale_corrected_review_unknown_outcome_changed_body_and_reader_cannot_authorize_a_match() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture); var preview = await data.PreviewAsync();
        var changed = data.Corrections.Fields.Select(field => field.Field == "recipient" ? field with { Value = "Synthetic reviewed recipient" } : field).ToImmutableArray();
        await data.Read.Service.CorrectReviewAsync(data.Reviewer, data.Read.DeclarationId, 1, data.Corrections.Binding, changed, PaymentVerificationTestData.Meta("changed-review"));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.VerifyAsync(preview));
        preview = await data.PreviewAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.VerifyAsync(data.Reader, data.Read.DeclarationId, data.ReceiptId, PaymentVerificationTestData.Input(preview), PaymentVerificationTestData.Meta("reader")));
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => data.Service.VerifyAsync(data.Reviewer, data.Read.DeclarationId, data.ReceiptId,
            PaymentVerificationTestData.Input(preview) with { Resolutions = [] }, PaymentVerificationTestData.Meta("unresolved")));
        Assert.Null(await data.Service.RecoverAsync(data.Reviewer, "unknown"));
        await data.VerifyAsync(preview);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.VerifyAsync(data.Reviewer, data.Read.DeclarationId, data.ReceiptId,
            PaymentVerificationTestData.Input(preview) with { PayerVerificationReference = "changed" }, PaymentVerificationTestData.Meta("verify-payment")));
        Assert.Equal(1, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument()));
    }
}
