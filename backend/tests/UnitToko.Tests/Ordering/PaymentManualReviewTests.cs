using System.Collections.Immutable;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using M = UnitToko.Platform.Billing.PaymentManualReviewRecords;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentManualReviewTests(MongoFixture fixture)
{
    internal static async Task Policy(PaymentVerificationTestData data) {
        var receipt = await data.Rows(BankReceiptBson.Receipts).Find(new BsonDocument("_id", data.ReceiptId)).SingleAsync();
        await data.Rows(M.Policies).InsertOneAsync(new BsonDocument { { "_id", 980L }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L }, { "scope", receipt["scope"] },
            { "procedureReference", "synthetic-approved-OCR-outage-procedure" }, { "operatingOwnerReference", "synthetic-finance-owner" }, { "approvalReference", "synthetic-manual-policy-approval" },
            { "allowedFailures", new BsonArray { "ocr_unavailable", "ocr_timeout", "ocr_invalid_output" } }, { "active", true },
            { "approvedAt", data.Read.Catalog.Clock.UtcNow.AddDays(-1).UtcDateTime }, { "expiresAt", data.Read.Catalog.Clock.UtcNow.AddDays(1).UtcDateTime } });
    }
    internal static ImmutableArray<PaymentReviewField> Fields() => [.. PaymentFieldExtractor.Fields.Select(name => new PaymentReviewField(name, name switch {
        "amount" => "4000.00", "currency" => "IDR", "account" => "0000123456789", _ => null }))];
    [WindowsStorageFact]
    public async Task Actual_outage_records_versioned_manual_observations_then_independent_bank_verification_without_fabricated_OCR_or_cash() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture, "ocr_unavailable"); await Policy(data);
        var before = await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.PreviewAsync());
        var shown = await data.Service.PreviewManualReviewAsync(data.Reviewer, data.Read.DeclarationId);
        Assert.Equal(0, shown.ReviewRevision); Assert.Equal("ocr_unavailable", shown.Failure["safeFailure"]);
        var input = new PaymentManualReviewInput(shown.Binding, true, Fields()); var meta = PaymentVerificationTestData.Meta("manual-first");
        var result = await data.Service.RecordManualReviewAsync(data.Reviewer, data.Read.DeclarationId, 0, input, meta);
        Assert.Equal(result, await data.Service.RecordManualReviewAsync(data.Reviewer, data.Read.DeclarationId, 0, input, meta));
        Assert.Equal(result, await data.Service.RecoverManualReviewAsync(data.Reviewer, data.Read.DeclarationId, meta.IdempotencyKey));
        Assert.Equal(0, await data.Rows("documentExtractionResults").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0, await data.Rows(PaymentVerificationBson.Decisions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(before, await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var next = await data.Service.PreviewManualReviewAsync(data.Reviewer, data.Read.DeclarationId); Assert.Equal(1, next.ReviewRevision);
        var unknownAmount = Fields().Select(f => f.Field == "amount" ? f with { Value = null } : f).ToImmutableArray();
        await data.Service.RecordManualReviewAsync(data.Reviewer, data.Read.DeclarationId, 1, new(next.Binding, true, unknownAmount), PaymentVerificationTestData.Meta("manual-unknown"));
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => data.PreviewAsync());
        next = await data.Service.PreviewManualReviewAsync(data.Reviewer, data.Read.DeclarationId);
        await data.Service.RecordManualReviewAsync(data.Reviewer, data.Read.DeclarationId, 2, new(next.Binding, true, Fields()), PaymentVerificationTestData.Meta("manual-final"));
        var preview = await data.PreviewAsync(); Assert.Equal("manual", preview.Review["kind"]); Assert.Equal(3, preview.Review["revision"].AsInt64);
        var verified = await data.VerifyAsync(preview); var saved = await data.Service.ReadAsync(data.Reader, data.Read.DeclarationId);
        Assert.Equal(2, saved!["schemaVersion"].AsInt32); Assert.Equal("manual", saved["review"]["kind"]);
        Assert.Equal(verified, await data.Service.RecoverAsync(data.Reviewer, "verify-payment"));
        Assert.Equal(before, await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        data.FailedEngine!.Failure = null; data.Read.Catalog.Clock.UtcNow = data.Read.Catalog.Clock.UtcNow.AddMinutes(1);
        Assert.Equal("completed", await PaymentOcrProcessorTests.Processor(data.Read, data.FailedEngine).RunOnceAsync());
        Assert.Equal(verified, await data.Service.RecoverAsync(data.Reviewer, "verify-payment"));
        Assert.Equal(result, await data.Service.RecoverManualReviewAsync(data.Reviewer, data.Read.DeclarationId, meta.IdempotencyKey));
        var failure = await data.Rows("documentProcessingFailures").Find(new BsonDocument()).SingleAsync(); await data.Rows("documentProcessingFailures").DeleteOneAsync(new BsonDocument("_id", failure["_id"]));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.RecoverAsync(data.Reviewer, "verify-payment"));
        Assert.Equal(3, await data.Rows(M.Versions).CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Permission_policy_original_MFA_and_required_audit_are_not_bypassed_and_first_revision_races_have_one_winner() {
        await using var data = await PaymentVerificationTestData.CreateAsync(fixture, "ocr_timeout");
        var missing = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewManualReviewAsync(data.Reviewer, data.Read.DeclarationId)); Assert.Equal("payment_manual_review_policy_unavailable", missing.Code);
        await Policy(data);
        foreach (var actor in new[] { data.Reader, data.Read.Actor, data.Reviewer with { AuthenticatedAt = data.Read.Catalog.Clock.UtcNow.AddMinutes(-6) } })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewManualReviewAsync(actor, data.Read.DeclarationId));
        var shown = await data.Service.PreviewManualReviewAsync(data.Reviewer, data.Read.DeclarationId); var input = new PaymentManualReviewInput(shown.Binding, true, Fields());
        var definition = await (await data.Read.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "auditEvents") })).SingleAsync(); var old = definition["options"]["validator"];
        await data.Read.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", new BsonDocument("$and", new BsonArray { old, new BsonDocument("kind", new BsonDocument("$ne", M.Operation)) }) } });
        await Assert.ThrowsAsync<MongoWriteException>(() => data.Service.RecordManualReviewAsync(data.Reviewer, data.Read.DeclarationId, 0, input, PaymentVerificationTestData.Meta("manual-audit-denial")));
        Assert.Equal(0, await data.Rows(M.Versions).CountDocumentsAsync(new BsonDocument())); Assert.False(await data.Rows("outboxEvents").Find(new BsonDocument("kind", M.Operation)).AnyAsync());
        await data.Read.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "auditEvents" }, { "validator", old } });
        data.Read.Service.ReadObserver = new(async () => await data.Rows("evidenceValidationPolicies").UpdateManyAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("active", false)));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.RecordManualReviewAsync(data.Reviewer, data.Read.DeclarationId, 0, input, PaymentVerificationTestData.Meta("manual-security-denial")));
        data.Read.Service.ReadObserver = null; await data.Rows("evidenceValidationPolicies").UpdateManyAsync(new BsonDocument(), Builders<BsonDocument>.Update.Set("active", true));
        var a = Record.ExceptionAsync(() => data.Service.RecordManualReviewAsync(data.Reviewer, data.Read.DeclarationId, 0, input, PaymentVerificationTestData.Meta("manual-race-a")));
        var b = Record.ExceptionAsync(() => data.Service.RecordManualReviewAsync(data.Reviewer, data.Read.DeclarationId, 0, input, PaymentVerificationTestData.Meta("manual-race-b")));
        Assert.Single(await Task.WhenAll(a, b), error => error is null); Assert.Equal(1, await data.Rows(M.Versions).CountDocumentsAsync(new BsonDocument()));
        var one = await data.Rows(M.Versions).Find(new BsonDocument()).SingleAsync(); await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "operation", M.Operation }, { "key", one["requestKey"] } });
        var missingReceipt = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverManualReviewAsync(data.Reviewer, data.Read.DeclarationId, one["requestKey"].AsString));
        Assert.Equal("payment_verification_unavailable", missingReceipt.Code);
    }
}
