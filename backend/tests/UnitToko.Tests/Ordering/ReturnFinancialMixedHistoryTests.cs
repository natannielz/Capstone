using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Accounting;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using C = UnitToko.Platform.Billing.PaymentAllocationCorrectionBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ReturnFinancialMixedHistoryTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Linked_refund_and_corrections_share_original_capacity_and_reservation_never_becomes_available_cash() {
        await using var data = await Data.CreateAsync(fixture);
        var cash = await data.CashAsync(); var original = await data.OriginalAsync();
        var correctionInput = data.Correction(1, 300m); var correction = await data.Allocation.Service.PreviewCorrectionAsync(data.Checker, data.AllocationId, correctionInput);
        await data.Allocation.Service.CorrectAsync(data.Checker, data.AllocationId, correction.Binding, correctionInput, Data.Meta("mixed-prior-correction"));
        var input = data.Input(2, 600m); var preview = await data.Service.PreviewAsync(data.Approver, data.CaseId, data.ReturnId, data.Action, input);
        Assert.Equal(300m, preview.View.Links[0]["previouslyReleased"].AsDecimal);
        var saved = await data.Service.ApproveAsync(data.Approver, data.CaseId, data.ReturnId, data.Action, preview.Binding, input, Data.Meta("mixed-refund"));
        var balances = await data.ReadBalancesAsync(); Assert.Equal(1100m, balances.ReceiptBalance["allocated"].AsDecimal); Assert.Equal(600m, balances.ReceiptBalance["reservedForRefund"].AsDecimal);
        Assert.Equal(1100m, balances.Invoices[0]["balance"]["paid"].AsDecimal); Assert.Equal(1000.02m, balances.Invoices[0]["balance"]["credited"].AsDecimal);
        Assert.Equal(2300m, 4000m - balances.ReceiptBalance["allocated"].AsDecimal - balances.ReceiptBalance["reservedForRefund"].AsDecimal);
        var capacities = await data.Allocation.Service.ReadCorrectionAsync(data.Checker, data.AllocationId);
        Assert.Equal(900m, capacities.Capacities[0]["previouslyReversed"].AsDecimal);
        correctionInput = data.Correction(3, 400m); correction = await data.Allocation.Service.PreviewCorrectionAsync(data.Checker, data.AllocationId, correctionInput);
        await data.Allocation.Service.CorrectAsync(data.Checker, data.AllocationId, correction.Binding, correctionInput, Data.Meta("mixed-next-correction"));
        var allocate = data.Allocation.Input(4, 200m); var allocation = await data.Allocation.Service.PreviewAsync(data.Allocation.Actor, data.Allocation.VerificationId, allocate);
        await data.Allocation.Service.AllocateAsync(data.Allocation.Actor, data.Allocation.VerificationId, allocation.Binding, allocate, Data.Meta("mixed-later-allocation"));
        balances = await data.ReadBalancesAsync(); Assert.Equal(900m, balances.ReceiptBalance["allocated"].AsDecimal); Assert.Equal(600m, balances.ReceiptBalance["reservedForRefund"].AsDecimal);
        Assert.Equal(900m, balances.Invoices[0]["balance"]["paid"].AsDecimal); Assert.Equal(1000.02m, balances.Invoices[0]["balance"]["credited"].AsDecimal);
        await Assert.ThrowsAsync<UnitToko.Modules.Contracts.DomainValidationException>(() => data.Allocation.Service.PreviewCorrectionAsync(data.Checker, data.AllocationId, data.Correction(5, 700.01m)));
        Assert.Equal(saved, await data.Service.RecoverAsync(data.Approver, data.CaseId, data.ReturnId, data.Action, "mixed-refund"));
        Assert.Equal(original, await data.OriginalAsync()); Assert.Equal(cash, await data.CashAsync());
        var journal = await data.Rows("journalEntries").Find(new BsonDocument("source.namespace", "billing.return-financial")).SingleAsync();
        Assert.Equal(600m, journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == "refund_liability")["credit"].AsDecimal);
        Assert.DoesNotContain(journal["lines"].AsBsonArray, line => line["mappingKey"] == "bank_cash" || line["mappingKey"] == "unidentified_cash");
    }

    [WindowsStorageFact]
    public async Task Credit_advances_only_invoice_history_and_later_allocation_uses_distinct_invoice_and_receipt_versions() {
        await using var data = await Data.CreateAsync(fixture, "credit"); var cash = await data.CashAsync();
        var input = data.Input(1); var preview = await data.Service.PreviewAsync(data.Approver, data.CaseId, data.ReturnId, data.Action, input);
        await data.Service.ApproveAsync(data.Approver, data.CaseId, data.ReturnId, data.Action, preview.Binding, input, Data.Meta("mixed-credit"));
        var balances = await data.ReadBalancesAsync(); Assert.Equal(1L, balances.ReceiptBalance["version"].AsInt64); Assert.Equal(2L, balances.Invoices[0]["balance"]["version"].AsInt64);
        Assert.Equal(2000m, balances.Invoices[0]["balance"]["paid"].AsDecimal); Assert.Equal(1000.02m, balances.Invoices[0]["balance"]["credited"].AsDecimal); Assert.Equal(0m, balances.ReceiptBalance["reservedForRefund"].AsDecimal);
        var allocate = new PaymentAllocationInput(1, [new(new(data.Allocation.InvoiceId), 2, new(500m))]);
        var allocation = await data.Allocation.Service.PreviewAsync(data.Allocation.Actor, data.Allocation.VerificationId, allocate);
        await data.Allocation.Service.AllocateAsync(data.Allocation.Actor, data.Allocation.VerificationId, allocation.Binding, allocate, Data.Meta("mixed-after-credit"));
        balances = await data.ReadBalancesAsync(); Assert.Equal(2L, balances.ReceiptBalance["version"].AsInt64); Assert.Equal(3L, balances.Invoices[0]["balance"]["version"].AsInt64);
        Assert.Equal(2500m, balances.Invoices[0]["balance"]["paid"].AsDecimal); Assert.Equal(1000.02m, balances.Invoices[0]["balance"]["credited"].AsDecimal);
        // This reader has no maker or approval grant; historical money still needs actual source read rights.
        Assert.Equal(cash, await data.CashAsync());
        Assert.Equal(0m, balances.ReceiptBalance["reservedForRefund"].AsDecimal);
    }

    [WindowsStorageTheory]
    [InlineData("event")] [InlineData("request")] [InlineData("effect")]
    public async Task Missing_approval_source_or_effect_never_reopens_original_release_capacity_or_money(string fault) {
        await using var data = await Data.CreateAsync(fixture); var input = data.Input(1, 600m);
        var preview = await data.Service.PreviewAsync(data.Approver, data.CaseId, data.ReturnId, data.Action, input);
        var saved = await data.Service.ApproveAsync(data.Approver, data.CaseId, data.ReturnId, data.Action, preview.Binding, input, Data.Meta("mixed-integrity"));
        var row = await data.Rows(A.Events).Find(new BsonDocument("_id", saved["financialApprovalId"])).SingleAsync();
        var (table, filter) = fault switch {
            "event" => (A.Events, new BsonDocument("_id", row["_id"])), "request" => (ReturnEntitlementBson.Requests, new BsonDocument("_id", data.ReturnId)),
            _ => ("outboxEvents", new BsonDocument("sourceKey", A.SourceKey(row))) };
        Assert.Equal(1, (await data.Rows(table).DeleteOneAsync(filter)).DeletedCount);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.ReadBalancesAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => new MongoUnitOfWork(data.Allocation.Source.Read.Catalog.Client).ExecuteAsync((tx, ct) =>
            new PaymentAllocationRecords(data.Database, data.Clock).CorrectionTotalsAsync(tx, data.AllocationId, ct)));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Allocation.Service.RecoverAsync(data.Allocation.Actor, "mixed-original-allocation"));
    }

    [WindowsStorageFact]
    public async Task Historical_balances_preserve_past_approval_proof_after_staff_authority_changes_but_recovery_requires_current_people() {
        await using var data = await Data.CreateAsync(fixture); var input = data.Input(1, 600m);
        var preview = await data.Service.PreviewAsync(data.Approver, data.CaseId, data.ReturnId, data.Action, input);
        await data.Service.ApproveAsync(data.Approver, data.CaseId, data.ReturnId, data.Action, preview.Binding, input, Data.Meta("mixed-historical-proof"));
        Assert.Equal(1, (await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Approver.UserId), Builders<BsonDocument>.Update.Pull("permissions", Permissions.RefundApprove))).ModifiedCount);
        var history = await data.ReadBalancesAsync(); Assert.Equal(1400m, history.Invoices[0]["balance"]["paid"].AsDecimal); Assert.Equal(1000.02m, history.Invoices[0]["balance"]["credited"].AsDecimal);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(data.Approver, data.CaseId, data.ReturnId, data.Action, "mixed-historical-proof"));
        Assert.Equal(1, (await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Approver.UserId), Builders<BsonDocument>.Update.AddToSet("permissions", Permissions.RefundApprove))).ModifiedCount);
        var maker = data.Allocation.Source.Read.Payment.Invoice.Finance.UserId;
        Assert.Equal(1, (await data.Rows("personAccountBindings").UpdateOneAsync(new BsonDocument("_id", maker), Builders<BsonDocument>.Update.Set("expiresAt", data.Clock.UtcNow.UtcDateTime))).ModifiedCount);
        history = await data.ReadBalancesAsync(); Assert.Equal(600m, history.ReceiptBalance["reservedForRefund"].AsDecimal); Assert.Equal(1000.02m, history.Invoices[0]["balance"]["credited"].AsDecimal);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.RecoverAsync(data.Approver, data.CaseId, data.ReturnId, data.Action, "mixed-historical-proof"));
    }

    [WindowsStorageFact]
    public async Task Approval_and_correction_race_on_the_same_money_heads_and_exactly_one_commits() {
        await using var data = await Data.CreateAsync(fixture); var input = data.Input(1, 400m);
        var preview = await data.Service.PreviewAsync(data.Approver, data.CaseId, data.ReturnId, data.Action, input);
        var correctionInput = data.Correction(1, 400m); var correction = await data.Allocation.Service.PreviewCorrectionAsync(data.Checker, data.AllocationId, correctionInput);
        var results = await Task.WhenAll(Record.ExceptionAsync(() => data.Service.ApproveAsync(data.Approver, data.CaseId, data.ReturnId, data.Action, preview.Binding, input, Data.Meta("mixed-race-approval"))),
            Record.ExceptionAsync(() => data.Allocation.Service.CorrectAsync(data.Checker, data.AllocationId, correction.Binding, correctionInput, Data.Meta("mixed-race-correction"))));
        Assert.Single(results, error => error is null); Assert.Single(results, error => error is not null);
        Assert.Equal(1, await data.Rows(A.Events).CountDocumentsAsync(new BsonDocument()) + await data.Rows(C.Events).CountDocumentsAsync(new BsonDocument()));
        var balances = await data.ReadBalancesAsync(); Assert.Equal(2L, balances.ReceiptBalance["version"].AsInt64); Assert.Equal(2L, balances.Invoices[0]["balance"]["version"].AsInt64);
        Assert.Equal(1600m, balances.ReceiptBalance["allocated"].AsDecimal); Assert.Equal(1600m, balances.Invoices[0]["balance"]["paid"].AsDecimal);
        var approved = await data.Rows(A.Events).Find(new BsonDocument()).AnyAsync(); Assert.Equal(approved ? 400m : 0m, balances.ReceiptBalance["reservedForRefund"].AsDecimal); Assert.Equal(approved ? 1000.02m : 0m, balances.Invoices[0]["balance"]["credited"].AsDecimal);
    }

    internal sealed class Data(PaymentAllocationTests.Data allocation, AfterSalesCaseService cases, long caseId, long returnId, string action, long allocationId,
        ActorContext approver, ActorContext checker) : IAsyncDisposable
    {
        internal PaymentAllocationTests.Data Allocation { get; } = allocation;
        internal AfterSalesCaseService Cases => cases;
        internal IMongoDatabase Database => Allocation.Source.Read.Database;
        internal UnitToko.Platform.IClock Clock => Allocation.Source.Read.Catalog.Clock;
        internal long CaseId { get; } = caseId;
        internal long ReturnId { get; } = returnId;
        internal string Action { get; } = action;
        internal long AllocationId { get; } = allocationId;
        internal ActorContext Approver { get; } = approver;
        internal ActorContext Checker { get; } = checker;
        internal ReturnEntitlementService Entitlements => new(Database, new(Allocation.Source.Read.Catalog.Client), new(Database), Clock, cases, Allocation.Source.Read.Payment.Invoice.Service);
        internal ReturnFinancialApprovalService Service => new(Database, new(Allocation.Source.Read.Catalog.Client), new(Database), Clock, Entitlements, Allocation.Service);
        internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
        internal ReturnFinancialApprovalInput Input(long version, decimal release = 0m) => new(version, Action == "credit" ? [] : [new(new(AllocationId), version, new(release))]);
        internal PaymentAllocationInput Correction(long version, decimal amount) => Allocation.Input(version, amount);
        internal Task<PaymentAllocationView> ReadBalancesAsync() => Allocation.Service.ReadAsync(Allocation.Source.Reader, Allocation.VerificationId, [Allocation.InvoiceId]);
        internal async Task<BsonDocument> CashAsync() => BankReceiptBson.Snapshot(await Rows(BankReceiptBson.Receipts).Find(new BsonDocument()).SingleAsync());
        internal async Task<BsonDocument> OriginalAsync() => B.Snapshot(await Rows(B.Events).Find(new BsonDocument("_id", AllocationId)).SingleAsync());
        internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic independent financial history review", "synthetic-mixed-financial-history");
        internal static async Task<Data> CreateAsync(MongoFixture fixture, string action = "refund") {
            var allocation = await PaymentAllocationTests.Data.CreateAsync(fixture);
            try {
                var originalInput = allocation.Input(0, 2000m); var originalPreview = await allocation.Service.PreviewAsync(allocation.Actor, allocation.VerificationId, originalInput);
                var original = await allocation.Service.AllocateAsync(allocation.Actor, allocation.VerificationId, originalPreview.Binding, originalInput, Meta("mixed-original-allocation"));
                var payment = allocation.Source.Read.Payment; var catalog = allocation.Source.Read.Catalog; var (cases, caseId, returnId) = await ReturnResolutionTests.Setup(payment);
                var maker = payment.Invoice.Finance; await catalog.Base.AddGrantAsync(maker, 99870, CatalogAuthoringTestData.Shop, [ReturnResolutionPolicy.Permission(action)]);
                var resolution = await cases.ReadResolutionAsync(maker, caseId, returnId, action);
                await cases.RequestResolutionAsync(maker, caseId, returnId, 0, resolution.Binding, new(action, "Pemeriksaan keuangan hasil retur"), Meta("mixed-resolution"));
                await ReturnEntitlementTests.SeedPolicyAsync(payment.Invoice);
                var approver = await catalog.ActorAsync(7540, CatalogAuthoringTestData.Shop, Permissions.CaseRead, Permissions.InvoiceRead, Permissions.CreditApprove, Permissions.RefundApprove,
                    Permissions.PaymentReviewRead, Permissions.BankReceiptRead, Permissions.BankStatementRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead);
                await PersonTestData.SeedDistinctPeopleAsync(allocation.Source.Read.Database, catalog.Clock, approver.UserId);
                var checker = await PaymentAllocationCorrectionTests.Checker(allocation);
                foreach (var actor in new[] { allocation.Actor, allocation.Source.Reader, checker }) await catalog.Base.AddGrantAsync(actor, 99900 + actor.UserId, CatalogAuthoringTestData.Shop, [Permissions.CaseRead]);
                var data = new Data(allocation, cases, caseId, returnId, action, original["allocationId"].AsInt64, approver, checker);
                var inspection = await data.Rows("returnInspections").Find(new BsonDocument("_id", returnId)).SingleAsync(); var line = inspection["input"]["lines"][0];
                var entitlementInput = new ReturnEntitlementInput([new(line["receiptId"].AsInt64, line["skuId"].AsInt64, new Money(0m), new Money(0m))]);
                var entitlement = await data.Entitlements.PreviewAsync(maker, caseId, returnId, action, entitlementInput);
                await data.Entitlements.RequestAsync(maker, caseId, returnId, action, entitlement.View.Pool["version"].AsInt64, entitlement.Binding, entitlementInput, Meta("mixed-entitlement"));
                var scope = (await data.Rows(B.Policies).Find(new BsonDocument()).SingleAsync())["scope"];
                var policy = new BsonDocument { { "_id", 9540L }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L }, { "scope", scope },
                    { "creditRule", "credit_original_components" }, { "reservationRule", "reserve_released_allocations" }, { "independenceRule", "request_and_funding_makers" },
                    { "arMapping", "invoice_ar" }, { "refundLiabilityMapping", "refund_liability" }, { "refundLiabilityAccountId", 807L }, { "refundLiabilityAccountCode", "A2103" },
                    { "active", true }, { "approvedAt", catalog.Clock.UtcNow.AddDays(-1).UtcDateTime }, { "expiresAt", catalog.Clock.UtcNow.AddDays(1).UtcDateTime } };
                foreach (var reference in new[] { "creditNoteProcedureReference", "taxTreatmentReference", "chargeTreatmentReference", "refundReservationProcedureReference", "operatingOwnerReference", "approvalReference" }) policy[reference] = "synthetic-approved-" + reference;
                await data.Rows(A.Policies).InsertOneAsync(policy);
                await data.Rows("accountingChartVersions").UpdateOneAsync(new BsonDocument("_id", 20L), Builders<BsonDocument>.Update.Push("mappings", AccountingTestDocuments.Mapping("refund_liability", 807, "A2103")));
                return data;
            } catch { await allocation.DisposeAsync(); throw; }
        }
        public ValueTask DisposeAsync() => Allocation.DisposeAsync();
    }
}
