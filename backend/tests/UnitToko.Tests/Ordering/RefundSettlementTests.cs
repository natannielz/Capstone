using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using C = UnitToko.Platform.Billing.PaymentAllocationCorrectionBson;
using D = UnitToko.Platform.Billing.RefundSettlementBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed partial class RefundSettlementTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Actual_independent_debit_consumes_the_reserved_refund_once_and_recovers_without_changing_invoice_or_available_cash() {
        await using var data = await Data.CreateAsync(fixture);
        var before = await data.Payout.Source.ReadBalancesAsync(); var permanent = await data.PermanentAsync();
        var invoiceHead = await data.Rows(B.Invoices).Find(new BsonDocument()).SingleAsync(); invoiceHead.Remove("fence");
        var makerEvents = await data.Rows(B.Events).CountDocumentsAsync(new BsonDocument());
        var financialEvents = await data.Rows(A.Events).CountDocumentsAsync(new BsonDocument());
        var preview = await data.PreviewAsync(); var receiptPreview = Assert.Single(preview.View["receipts"].AsBsonArray);
        Assert.Equal(2L, receiptPreview["versionBefore"].AsInt64); Assert.Equal(3L, receiptPreview["versionAfter"].AsInt64);
        Assert.Equal(600m, receiptPreview["reservedForRefund"].AsDecimal); Assert.Equal(600m, receiptPreview["paidAfter"].AsDecimal);
        Assert.Equal(0m, receiptPreview["pending"].AsDecimal); Assert.Equal(2000m, receiptPreview["available"].AsDecimal);
        const string key = "settlement-lost-successful-reply";
        var saved = await data.VerifyAsync(preview, key); // Recovery below represents a discarded committed response.
        Assert.Equal(saved, await data.Service.RecoverSettlementAsync(data.Verifier, data.PayoutId, key));
        Assert.Equal(saved, await data.VerifyAsync(preview, key));
        Assert.Equal("bank_debit_verified", saved["state"]); Assert.Equal(600m, saved["amount"].AsDecimal);
        var after = await data.Payout.Source.ReadBalancesAsync();
        Assert.Equal(before.Invoices[0]["balance"], after.Invoices[0]["balance"]);
        Assert.Equal(before.ReceiptBalance["allocated"], after.ReceiptBalance["allocated"]);
        Assert.Equal(before.ReceiptBalance["reservedForRefund"], after.ReceiptBalance["reservedForRefund"]);
        Assert.Equal(3L, after.ReceiptBalance["version"].AsInt64);
        var row = await data.Rows(D.Events).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(row["_id"], after.ReceiptBalance["lastEventId"]); Assert.Equal(row["snapshotHash"], after.ReceiptBalance["lastEventHash"]);
        var liveInvoiceHead = await data.Rows(B.Invoices).Find(new BsonDocument()).SingleAsync(); liveInvoiceHead.Remove("fence");
        Assert.Equal(invoiceHead, liveInvoiceHead);
        var approval = await data.Rows(A.Events).Find(new BsonDocument("_id", data.Payout.ApprovalId)).SingleAsync();
        Assert.Equal(D.Releases(approval), row["releases"]);
        Assert.Equal(makerEvents, await data.Rows(B.Events).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(financialEvents, await data.Rows(A.Events).CountDocumentsAsync(new BsonDocument()));
        Assert.False(await data.Rows(C.Events).Find(new BsonDocument()).AnyAsync());
        Assert.Equal(1, await data.Rows(D.Events).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(BankTransactionClaims.Debit(row), await data.Rows(BankTransactionClaims.Collection).Find(new BsonDocument("direction", "debit")).SingleAsync());
        var journal = await data.Rows("journalEntries").Find(new BsonDocument("source.namespace", D.Operation)).SingleAsync();
        Assert.Equal("refund.bank-debit-settled", journal["source"]["eventKind"]);
        Assert.Equal(2, journal["lines"].AsBsonArray.Count);
        var liability = journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == row["liabilityAccount"]["mappingKey"]);
        var cash = journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == row["cashAccount"]["mappingKey"]);
        Assert.Equal(600m, liability["debit"].AsDecimal); Assert.Equal(0m, liability["credit"].AsDecimal);
        Assert.Equal(0m, cash["debit"].AsDecimal); Assert.Equal(600m, cash["credit"].AsDecimal);
        Assert.Equal(row["cashAccount"]["accountId"], cash["accountId"]); Assert.Equal(row["cashAccount"]["accountCode"], cash["accountCode"]);
        Assert.Equal(data.PayoutRecord["source"]["payingBank"]["cashAccountId"], cash["accountId"]);
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, D.Operation)));
        var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument("operation", D.Operation)).SingleAsync();
        Assert.Equal(PersonApprovalReceipt.Create(Permissions.DisbursementVerify, new(1, 1, RecordedBy: data.Payout.Actor.UserId, SubmittedBy: data.Importer.UserId)), receipt[PersonApprovalReceipt.Field]);
        var originalImport = await data.Rows(BankStatementBson.Imports).Find(new BsonDocument("_id", data.Input.BankImportId)).SingleAsync();
        Assert.Equal("Verified", originalImport["personAtImport"]["verification"].AsString);
        Assert.Equal(originalImport["personAtImport"]["personId"], row["people"].AsBsonArray.Single(person => person["userId"] == data.Importer.UserId)["personId"]);
        var grant = await data.Rows("authorizationGrants").Find(new BsonDocument("_id", data.Verifier.UserId)).SingleAsync();
        foreach (var permission in new[] { Permissions.DisbursementRecord, Permissions.RefundApprove, Permissions.CreditApprove, Permissions.BankReceiptConfirm })
            Assert.DoesNotContain(new BsonString(permission), grant["permissions"].AsBsonArray);
        Assert.Equal(permanent, await data.PermanentAsync());
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.VerifySettlementAsync(data.Verifier, data.PayoutId, data.Input, preview.Binding, true, Data.Meta(key) with { Reason = "Changed independent bank observation" }));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.VerifyAsync(preview, key, input: data.Input with { TransferVerificationReference = "different-proof" }));
        Assert.Null(await data.Service.RecoverSettlementAsync(data.Verifier, data.PayoutId, "never-committed-key"));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.VerifyAsync(preview, "different-key-after-confirmation"));
    }

    [WindowsStorageFact]
    public async Task Current_grants_fresh_factor_and_distinct_verified_people_cannot_be_replaced_by_role_or_account_labels() {
        await using var data = await Data.CreateAsync(fixture); var state = await data.SettlementStateAsync(); var permanent = await data.PermanentAsync();
        var stale = data.Verifier with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) };
        var factor = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.PreviewAsync(stale)); Assert.Equal("step_up_required", factor.Code);
        var grant = await data.Rows("authorizationGrants").Find(new BsonDocument("_id", data.Verifier.UserId)).SingleAsync();
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", grant["_id"]), Builders<BsonDocument>.Update.Pull("permissions", Permissions.DisbursementVerify));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.PreviewAsync());
        await data.Rows("authorizationGrants").ReplaceOneAsync(new BsonDocument("_id", grant["_id"]), grant);
        foreach (var actor in new[] { data.Payout.Actor, data.Importer }) {
            await data.Catalog.Base.AddGrantAsync(actor, 990000 + actor.UserId, CatalogAuthoringTestData.Shop, [Permissions.DisbursementVerify, Permissions.CaseRead, Permissions.InvoiceRead, Permissions.PaymentReviewRead,
                Permissions.BankReceiptRead, Permissions.BankStatementRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead]);
            var independence = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.PreviewAsync(actor)); Assert.Equal("independent_person_required", independence.Code);
        }
        await MergePeopleAsync(data, data.Payout.Actor.UserId, data.Verifier.UserId);
        var evidence = await new MongoUnitOfWork(data.Catalog.Client).ExecuteAsync((tx, ct) => new PersonRegistryService(data.Database, data.Clock).ResolveAsync(tx, [data.Verifier.UserId, data.Payout.Actor.UserId], ct));
        Assert.Equal(evidence[0].PersonId, evidence[1].PersonId);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.PreviewAsync());
        Assert.Equal(state, await data.SettlementStateAsync()); Assert.Equal(permanent, await data.PermanentAsync());
    }

    [WindowsStorageFact]
    public async Task A_current_valid_importer_binding_cannot_change_the_verified_person_recorded_on_the_actual_import() {
        await using var data = await Data.CreateAsync(fixture); const long replacement = 7692;
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, replacement);
        var originalImport = await data.Rows(BankStatementBson.Imports).Find(new BsonDocument("_id", data.Input.BankImportId)).SingleAsync();
        var previous = ReceivingAccountBson.Person(originalImport["personAtImport"]); Assert.Equal(PersonVerification.Verified, previous.Verification);
        await MergePeopleAsync(data, replacement, data.Importer.UserId);
        var proof = await new MongoUnitOfWork(data.Catalog.Client).ExecuteAsync((tx, ct) => new PersonRegistryService(data.Database, data.Clock).ResolveAsync(tx, [data.Importer.UserId], ct));
        Assert.Equal(PersonTestData.PersonId(replacement), proof[0].PersonId); Assert.NotEqual(previous.PersonId, proof[0].PersonId);
        var state = await data.SettlementStateAsync();
        var unavailable = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.PreviewAsync());
        Assert.Equal("refund_payout_integrity_unavailable", unavailable.Code);
        Assert.Equal(state, await data.SettlementStateAsync()); Assert.False(await data.Rows(D.Events).Find(new BsonDocument()).AnyAsync());
    }

    [WindowsStorageFact]
    public async Task Required_claim_journal_and_command_effects_fail_atomically_and_the_exact_failed_key_can_retry() {
        await using var data = await Data.CreateAsync(fixture); var preview = await data.PreviewAsync(); var state = await data.SettlementStateAsync(); var permanent = await data.PermanentAsync();
        const string key = "settlement-atomic-exact-retry";
        foreach (var (table, field, value) in new[] { (BankTransactionClaims.Collection, "direction", "debit"), ("auditEvents", "kind", D.Operation),
            ("idempotencyRecords", "operation", D.Operation), ("outboxEvents", "kind", D.Operation), ("accountingAuditEvents", "eventKey", "accounting:journal:") }) {
            var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
            var validator = definition["options"]["validator"].DeepClone().AsBsonDocument;
            var denial = table == "accountingAuditEvents" ? new BsonDocument(field, new BsonDocument("$not", new BsonRegularExpression("^" + value))) : new BsonDocument(field, new BsonDocument("$ne", value));
            try {
                await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", new BsonDocument("$and", new BsonArray { validator, denial }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
                Assert.NotNull(await Record.ExceptionAsync(() => data.VerifyAsync(preview, key)));
                Assert.Equal(state, await data.SettlementStateAsync()); Assert.Equal(permanent, await data.PermanentAsync());
            } finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } }); }
        }
        var saved = await data.VerifyAsync(preview, key); Assert.Equal(saved, await data.Service.RecoverSettlementAsync(data.Verifier, data.PayoutId, key));
        Assert.Equal(1, await data.Rows(D.Events).CountDocumentsAsync(new BsonDocument())); Assert.Equal(permanent, await data.PermanentAsync());
    }

    [WindowsStorageFact]
    public async Task Two_real_debit_candidates_for_one_recorded_payout_commit_exactly_one_shared_receipt_event_and_claim() {
        await using var data = await Data.CreateAsync(fixture); var permanent = await data.PermanentAsync(); var first = await data.PreviewAsync();
        var secondInput = data.Input with { BankImportId = await data.ImportDebitAsync("SETTLEMENT-0002", "settlement-race-second-import") };
        var second = await data.PreviewAsync(input: secondInput);
        var results = await Task.WhenAll(Record.ExceptionAsync(() => data.VerifyAsync(first, "settlement-race-one")), Record.ExceptionAsync(() => data.VerifyAsync(second, "settlement-race-two", input: secondInput)));
        Assert.Single(results, error => error is null); Assert.Single(results, error => error is not null);
        Assert.Equal(1, await data.Rows(D.Events).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows(BankTransactionClaims.Collection).CountDocumentsAsync(new BsonDocument("direction", "debit")));
        Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.namespace", D.Operation)));
        var balance = await data.Payout.Source.ReadBalancesAsync(); Assert.Equal(3L, balance.ReceiptBalance["version"].AsInt64);
        Assert.Equal(1400m, balance.ReceiptBalance["allocated"].AsDecimal); Assert.Equal(600m, balance.ReceiptBalance["reservedForRefund"].AsDecimal);
        Assert.Equal(permanent, await data.PermanentAsync());
    }

    [WindowsStorageFact]
    public async Task Missing_immutable_claim_source_or_journal_effect_is_never_healed_by_read_preview_or_recovery() {
        await using var data = await Data.CreateAsync(fixture); var preview = await data.PreviewAsync(); const string key = "settlement-integrity";
        var saved = await data.VerifyAsync(preview, key); var row = await data.Rows(D.Events).Find(new BsonDocument("_id", saved["settlementId"])).SingleAsync();
        var journalKey = "accounting:journal:" + row["journalId"].AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (table, filter) in new[] { (BankTransactionClaims.Collection, new BsonDocument("_id", row["canonicalIdentity"])),
            ("auditEvents", new BsonDocument("_id", row["auditId"])), ("outboxEvents", new BsonDocument("sourceKey", D.SourceKey(row))),
            ("idempotencyRecords", new BsonDocument { { "actorId", data.Verifier.UserId }, { "operation", D.Operation }, { "key", key } }),
            ("journalEntries", new BsonDocument("_id", row["journalId"])), ("accountingAuditEvents", new BsonDocument("eventKey", journalKey)),
            ("outboxEvents", new BsonDocument("sourceKey", journalKey)) }) {
            var original = await data.Rows(table).Find(filter).SingleAsync(); Assert.Equal(1, (await data.Rows(table).DeleteOneAsync(filter)).DeletedCount);
            try {
                await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Payout.Source.ReadBalancesAsync());
                await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadSettlementAsync(data.Verifier, data.PayoutId));
                await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverSettlementAsync(data.Verifier, data.PayoutId, key));
                Assert.False(await data.Rows(table).Find(filter).AnyAsync());
            } finally { await data.Rows(table).InsertOneAsync(original); }
        }
        Assert.Equal(saved, await data.Service.RecoverSettlementAsync(data.Verifier, data.PayoutId, key));
        Assert.Equal(3L, (await data.Payout.Source.ReadBalancesAsync()).ReceiptBalance["version"].AsInt64);
    }

    internal static Task<bool> MergePeopleAsync(Data data, long canonicalUser, long aliasUser) => new MongoUnitOfWork(data.Catalog.Client).ExecuteAsync(async (tx, ct) => {
        await data.Rows("personRecords").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.PersonId(aliasUser)),
            Builders<BsonDocument>.Update.Set("currentEquivalenceId", PersonTestData.GroupId(canonicalUser)).Inc("version", 1L), cancellationToken: ct);
        await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(canonicalUser)),
            Builders<BsonDocument>.Update.Set("memberPersonIds", new BsonArray(new[] { PersonTestData.PersonId(canonicalUser), PersonTestData.PersonId(aliasUser) }.Order()))
                .Set("memberUserIds", new BsonArray(new[] { canonicalUser, aliasUser }.Order())).Inc("version", 1L), cancellationToken: ct);
        await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(aliasUser)),
            Builders<BsonDocument>.Update.Set("status", "superseded").Inc("version", 1L), cancellationToken: ct); return true;
    });
}
