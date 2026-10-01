using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using C = UnitToko.Platform.Billing.PaymentAllocationCorrectionBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PaymentAllocationCorrectionTests(MongoFixture fixture)
{
    internal static async Task<ActorContext> Checker(PaymentAllocationTests.Data data, bool grant = true, bool policy = true) {
        var catalog = data.Source.Read.Catalog;
        var actor = await catalog.ActorAsync(7520, CatalogAuthoringTestData.Shop, [Permissions.PaymentAllocate, Permissions.PaymentReviewRead, Permissions.InvoiceRead, Permissions.BankReceiptRead, Permissions.BankStatementRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead, .. grant ? new[] { Permissions.JournalReverse } : []]);
        await PersonTestData.SeedDistinctPeopleAsync(data.Source.Read.Database, catalog.Clock, actor.UserId);
        if (policy) { var approved = await data.Rows(B.Policies).Find(new BsonDocument()).SingleAsync(); approved["_id"] = 9520L; approved["correctionProcedureReference"] = "synthetic-independently-approved-linked-unallocation"; await data.Rows(C.Policies).InsertOneAsync(approved); }
        return actor;
    }
    private static PaymentAllocationInput Input(PaymentAllocationTests.Data data, long version, decimal amount) => new(version, [new(new(data.InvoiceId), version, new(amount))]);
    private static async Task<BsonDocument> Allocate(PaymentAllocationTests.Data data, long version, decimal amount, string key) { var input = data.Input(version, amount); var view = await data.Service.PreviewAsync(data.Actor, data.VerificationId, input); return await data.Service.AllocateAsync(data.Actor, data.VerificationId, view.Binding, input, PaymentAllocationTests.Data.Meta(key)); }
    [Fact]
    public void Partial_reversal_preserves_reservations_and_credits_and_cannot_reverse_other_allocations() {
        var party = new PaymentPartyKey(new(1), new(2), new(3), new(4)); var receipt = new ReceiptFunds(new(5), 3, party, "IDR", new(100), new(60), new(20));
        var invoice = new InvoiceFunds(new(6), 2, party, "IDR", new(100), new(60), new(10)); var input = new PaymentAllocationInput(3, [new(new(6), 2, new(15))]);
        var result = PaymentAllocationPolicy.ReviewReversal(receipt, [invoice], [new(new(6), new(30), new(10))], input); Assert.Equal(35m, result.RemainingReceipt.Value); Assert.Equal(45m, result.Invoices[0].Remaining.Value);
        Assert.Throws<DomainValidationException>(() => PaymentAllocationPolicy.ReviewReversal(receipt, [invoice], [new(new(6), new(30), new(25))], input));
        Assert.Throws<DomainValidationException>(() => PaymentAllocationPolicy.ReviewReversal(receipt, [invoice with { Paid = new(10) }], [new(new(6), new(30), new(10))], input));
    }
    [WindowsStorageFact]
    public async Task Two_linked_partial_corrections_preserve_original_cash_and_journal_and_recover_without_double_release() {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture); var original = await Allocate(data, 0, 1000m, "correction-original"); await Allocate(data, 1, 500m, "correction-unrelated"); var checker = await Checker(data);
        var cash = (await data.Rows(UnitToko.Platform.Billing.BankReceiptBson.Receipts).Find(new BsonDocument()).SingleAsync()).DeepClone().AsBsonDocument; cash.Remove("fence");
        var originalJournal = await data.Rows("journalEntries").Find(new BsonDocument { { "source.namespace", "billing.payment-allocation" }, { "source.documentId", original["allocationId"] } }).SingleAsync();
        var input = Input(data, 2, 400m); var preview = await data.Service.PreviewCorrectionAsync(checker, original["allocationId"].AsInt64, input); Assert.Equal(1000m, preview.Capacities[0]["originallyAllocated"].AsDecimal); Assert.Equal(0m, preview.Capacities[0]["previouslyReversed"].AsDecimal);
        var saved = await data.Service.CorrectAsync(checker, original["allocationId"].AsInt64, preview.Binding, input, PaymentAllocationTests.Data.Meta("correction-first"));
        Assert.Equal(saved, await data.Service.CorrectAsync(checker, original["allocationId"].AsInt64, preview.Binding, input, PaymentAllocationTests.Data.Meta("correction-first"))); Assert.Equal(saved, await data.Service.RecoverCorrectionAsync(checker, "correction-first"));
        var after = await data.Service.ReadAsync(data.Source.Reader, data.VerificationId, [data.InvoiceId]); Assert.Equal(1100m, after.ReceiptBalance["allocated"].AsDecimal); Assert.Equal(1100m, after.Invoices[0]["balance"]["paid"].AsDecimal);
        var second = Input(data, 3, 600m); var remaining = await data.Service.PreviewCorrectionAsync(checker, original["allocationId"].AsInt64, second); Assert.Equal(400m, remaining.Capacities[0]["previouslyReversed"].AsDecimal);
        await data.Service.CorrectAsync(checker, original["allocationId"].AsInt64, remaining.Binding, second, PaymentAllocationTests.Data.Meta("correction-second"));
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.PreviewCorrectionAsync(checker, original["allocationId"].AsInt64, Input(data, 4, .01m)));
        after = await data.Service.ReadAsync(data.Source.Reader, data.VerificationId, [data.InvoiceId]); Assert.Equal(500m, after.ReceiptBalance["allocated"].AsDecimal); Assert.Equal(500m, after.Invoices[0]["balance"]["paid"].AsDecimal); Assert.Equal(0m, after.Invoices[0]["balance"]["credited"].AsDecimal); Assert.Equal(0m, after.ReceiptBalance["reservedForRefund"].AsDecimal);
        Assert.Equal(originalJournal, await data.Rows("journalEntries").Find(new BsonDocument("_id", originalJournal["_id"])).SingleAsync()); var currentCash = await data.Rows(UnitToko.Platform.Billing.BankReceiptBson.Receipts).Find(new BsonDocument()).SingleAsync(); currentCash.Remove("fence"); Assert.Equal(cash, currentCash);
        var journals = await data.Rows("journalEntries").Find(new BsonDocument("source.eventKind", "unallocated")).ToListAsync(); Assert.Equal(2, journals.Count); foreach (var journal in journals) { Assert.True(journal["reversalOf"].IsBsonNull); Assert.Equal("unidentified_cash", journal["lines"][0]["mappingKey"]); Assert.Equal(0m, journal["lines"][0]["debit"].AsDecimal); Assert.Equal("invoice_ar", journal["lines"][1]["mappingKey"]); Assert.Equal(0m, journal["lines"][1]["credit"].AsDecimal); }
        Assert.Equal(original, await data.Service.RecoverAsync(data.Actor, "correction-original")); await Allocate(data, 4, 100m, "allocation-after-corrections");
        after = await data.Service.ReadAsync(data.Source.Reader, data.VerificationId, [data.InvoiceId]); Assert.Equal(600m, after.ReceiptBalance["allocated"].AsDecimal); Assert.Equal(saved, await data.Service.RecoverCorrectionAsync(checker, "correction-first"));
    }
    [WindowsStorageFact]
    public async Task Correction_requires_approved_procedure_and_independent_current_financial_authority_and_missing_effect_history_never_releases_funds() {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture); var original = await Allocate(data, 0, 1000m, "protected-correction-original"); var checker = await Checker(data, policy: false); var input = Input(data, 1, 400m);
        await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => data.Service.PreviewCorrectionAsync(checker, original["allocationId"].AsInt64, input));
        var approved = await data.Rows(B.Policies).Find(new BsonDocument()).SingleAsync(); approved["_id"] = 9520L; approved["correctionProcedureReference"] = "synthetic-approved-correction"; await data.Rows(C.Policies).InsertOneAsync(approved);
        await data.Source.Read.Catalog.Base.AddGrantAsync(data.Actor, 9521, CatalogAuthoringTestData.Shop, [Permissions.JournalReverse]);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewCorrectionAsync(data.Actor, original["allocationId"].AsInt64, input));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewCorrectionAsync(checker with { AuthenticatedAt = data.Source.Read.Catalog.Clock.UtcNow.AddMinutes(-6) }, original["allocationId"].AsInt64, input));
        var preview = await data.Service.PreviewCorrectionAsync(checker, original["allocationId"].AsInt64, input); var saved = await data.Service.CorrectAsync(checker, original["allocationId"].AsInt64, preview.Binding, input, PaymentAllocationTests.Data.Meta("protected-correction"));
        await data.Rows(C.Events).DeleteOneAsync(new BsonDocument("_id", saved["correctionId"])); await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Source.Reader, data.VerificationId, [data.InvoiceId]));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverCorrectionAsync(checker, "protected-correction")); await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewCorrectionAsync(checker, original["allocationId"].AsInt64, Input(data, 2, 100m)));
    }
    [WindowsStorageFact]
    public async Task Revoked_secondary_correction_grant_or_expired_reviewer_person_blocks_new_write_and_original_key_recovery() {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture); var original = await Allocate(data, 0, 1000m, "revoked-correction-original"); var checker = await Checker(data);
        var input = Input(data, 1, 400m); var preview = await data.Service.PreviewCorrectionAsync(checker, original["allocationId"].AsInt64, input);
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", checker.UserId), Builders<BsonDocument>.Update.Pull("permissions", Permissions.JournalReverse));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.CorrectAsync(checker, original["allocationId"].AsInt64, preview.Binding, input, PaymentAllocationTests.Data.Meta("revoked-correction")));
        Assert.Equal(0, await data.Rows(C.Events).CountDocumentsAsync(new BsonDocument()));
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", checker.UserId), Builders<BsonDocument>.Update.AddToSet("permissions", Permissions.JournalReverse));
        await data.Service.CorrectAsync(checker, original["allocationId"].AsInt64, preview.Binding, input, PaymentAllocationTests.Data.Meta("revoked-correction"));
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", checker.UserId), Builders<BsonDocument>.Update.Pull("permissions", Permissions.JournalReverse));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverCorrectionAsync(checker, "revoked-correction"));
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", checker.UserId), Builders<BsonDocument>.Update.AddToSet("permissions", Permissions.JournalReverse));
        await data.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", checker.UserId), Builders<BsonDocument>.Update.Set("expiresAt", data.Source.Read.Catalog.Clock.UtcNow.UtcDateTime));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverCorrectionAsync(checker, "revoked-correction"));
        var after = await data.Service.ReadAsync(data.Source.Reader, data.VerificationId, [data.InvoiceId]); Assert.Equal(600m, after.ReceiptBalance["allocated"].AsDecimal); Assert.Equal(1, await data.Rows(C.Events).CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageTheory]
    [InlineData("period")] [InlineData("audit")] [InlineData("outbox")]
    public async Task Correction_failure_rolls_back_compensating_journal_money_heads_event_and_operation_effects(string fault) {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture); var original = await Allocate(data, 0, 1000m, "rollback-correction-original"); var checker = await Checker(data);
        var input = Input(data, 1, 400m); var preview = await data.Service.PreviewCorrectionAsync(checker, original["allocationId"].AsInt64, input);
        if (fault == "period") {
            var month = AccountingCalendar.Month(DateOnly.FromDateTime(data.Source.Read.Catalog.Clock.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime));
            Assert.Equal(1, (await data.Rows("accountingBooks").UpdateOneAsync(new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Set("closedThrough", month))).ModifiedCount);
        } else await data.Source.Read.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", fault == "audit" ? "auditEvents" : "outboxEvents" }, { "validator", new BsonDocument("kind", new BsonDocument("$ne", C.Operation)) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        var error = await Record.ExceptionAsync(() => data.Service.CorrectAsync(checker, original["allocationId"].AsInt64, preview.Binding, input, PaymentAllocationTests.Data.Meta("rollback-correction"))); Assert.NotNull(error);
        if (fault == "period") Assert.Equal("accounting_period_closed", Assert.IsType<UnitToko.Platform.Mongo.PersistenceConflictException>(error).Code);
        Assert.Equal(0, await data.Rows(C.Events).CountDocumentsAsync(new BsonDocument()));
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") }) Assert.Equal(0, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, C.Operation)));
        var after = await data.Service.ReadAsync(data.Source.Reader, data.VerificationId, [data.InvoiceId]); Assert.Equal(1000m, after.ReceiptBalance["allocated"].AsDecimal); Assert.Equal(1L, after.ReceiptBalance["version"].AsInt64); Assert.Equal(1000m, after.Invoices[0]["balance"]["paid"].AsDecimal); Assert.Equal(3, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(original, await data.Service.RecoverAsync(data.Actor, "rollback-correction-original"));
    }
    [WindowsStorageFact]
    public async Task Allocation_and_linked_correction_race_on_the_same_canonical_heads_and_only_one_commits() {
        await using var data = await PaymentAllocationTests.Data.CreateAsync(fixture); var original = await Allocate(data, 0, 1000m, "race-correction-original"); var checker = await Checker(data);
        var input = Input(data, 1, 400m); var reversal = await data.Service.PreviewCorrectionAsync(checker, original["allocationId"].AsInt64, input); var allocationInput = data.Input(1, 500m); var allocation = await data.Service.PreviewAsync(data.Actor, data.VerificationId, allocationInput);
        var results = await Task.WhenAll(Record.ExceptionAsync(() => data.Service.CorrectAsync(checker, original["allocationId"].AsInt64, reversal.Binding, input, PaymentAllocationTests.Data.Meta("racing-correction"))), Record.ExceptionAsync(() => data.Service.AllocateAsync(data.Actor, data.VerificationId, allocation.Binding, allocationInput, PaymentAllocationTests.Data.Meta("racing-allocation"))));
        Assert.Single(results, error => error is null); var after = await data.Service.ReadAsync(data.Source.Reader, data.VerificationId, [data.InvoiceId]); Assert.Equal(2L, after.ReceiptBalance["version"].AsInt64); Assert.Contains(after.ReceiptBalance["allocated"].AsDecimal, new[] { 600m, 1500m }); Assert.Equal(after.ReceiptBalance["allocated"], after.Invoices[0]["balance"]["paid"]);
    }
}
