using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Accounting;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;

namespace UnitToko.Tests.Ordering;
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ReturnFinancialApprovalTests(MongoFixture fixture)
{
    [WindowsStorageTheory]
    [InlineData("credit")] [InlineData("refund")]
    public async Task Exact_independent_approval_credits_original_components_and_reserves_only_explicit_paid_funding_without_paying_cash(string action) {
        await using var data = await Data.CreateAsync(fixture, action);
        var original = await data.OriginalAsync(); var preview = await data.PreviewAsync(); var metadata = Data.Meta("fa-original");
        Assert.Equal(1000.02m, preview.Result.Credit.Value); Assert.Equal(action == "refund" ? 1000m : 0m, preview.Result.ReservedRefund.Value);
        var saved = await data.ApproveAsync(preview, metadata);
        Assert.Equal(action == "refund" ? "refund_reserved" : "invoice_credited", saved["state"].AsString);
        Assert.Equal(saved, await data.ApproveAsync(preview, metadata));
        Assert.Equal(saved, await data.Service.RecoverAsync(data.Actor, data.Source.CaseId, data.Source.ReturnId, action, metadata.IdempotencyKey));
        Assert.Null(await data.Service.RecoverAsync(data.Actor, data.Source.CaseId, data.Source.ReturnId, action, "fa-unknown"));
        var head = await data.Rows(B.Invoices).Find(new BsonDocument("_id", data.InvoiceId)).SingleAsync();
        Assert.Equal(2L, head["version"].AsInt64); Assert.Equal(action == "refund" ? 0m : 1000m, head["paid"].AsDecimal); Assert.Equal(1000.02m, head["credited"].AsDecimal);
        var receipt = await data.Rows(B.Receipts).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(action == "refund" ? 0m : 1000m, receipt["allocated"].AsDecimal); Assert.Equal(action == "refund" ? 1000m : 0m, receipt["reservedForRefund"].AsDecimal);
        Assert.Equal(3000m, receipt["total"].AsDecimal - receipt["allocated"].AsDecimal - receipt["reservedForRefund"].AsDecimal);
        var journal = await data.Rows("journalEntries").Find(new BsonDocument("source.namespace", "billing.return-financial")).SingleAsync();
        Assert.Null(journal["reversalOf"].IsBsonNull ? null : journal["reversalOf"]);
        Assert.Equal(1000.02m, journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == "invoice_revenue")["debit"].AsDecimal);
        Assert.Equal(action == "refund" ? .02m : 1000.02m, journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == "invoice_ar")["credit"].AsDecimal);
        if (action == "refund") Assert.Equal(1000m, journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == "refund_liability")["credit"].AsDecimal);
        Assert.Equal(1, await data.Rows(A.Events).CountDocumentsAsync(new BsonDocument()));
        foreach (var (table, kind) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") }) Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument(kind, A.Operation)));
        await data.AssertOriginalAsync(original);
        var read = await data.Service.ReadAsync(data.Actor, data.Source.CaseId, data.Source.ReturnId, action); Assert.Equal(saved, read.Approval);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.ApproveAsync(preview, metadata with { Reason = "Changed original approval body" }));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.ApproveAsync(preview, Data.Meta("fa-no-second-approval")));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.PreviewAsync());
    }
    [WindowsStorageFact]
    public async Task Approval_requires_actual_request_maker_independence_current_grants_five_minute_MFA_and_explicit_policy() {
        await using var data = await Data.CreateAsync(fixture, "refund", policy: false);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.PreviewAsync()); await data.PolicyAsync(); var preview = await data.PreviewAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Actor with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, data.Source.CaseId, data.Source.ReturnId, data.Source.Action, data.Input));
        await data.Source.Payment.Invoice.Order.Prices.Catalog.Base.AddGrantAsync(data.Source.Actor, 99881, CatalogAuthoringTestData.Shop,
            [Permissions.CreditApprove, Permissions.RefundApprove, Permissions.PaymentReviewRead, Permissions.BankReceiptRead, Permissions.BankStatementRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead]);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Source.Actor, data.Source.CaseId, data.Source.ReturnId, data.Source.Action, data.Input));
        var revoked = await data.Rows("authorizationGrants").UpdateManyAsync(new BsonDocument("userId", data.Actor.UserId), Builders<BsonDocument>.Update.Pull("permissions", Permissions.RefundApprove));
        Assert.True(revoked.ModifiedCount > 0);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.ApproveAsync(preview, Data.Meta("fa-revoked")));
        Assert.Equal(0, await data.Rows(A.Events).CountDocumentsAsync(new BsonDocument()));
    }
    [WindowsStorageFact]
    public async Task Concurrent_approval_attempts_have_one_credit_one_reservation_and_one_posted_financial_event() {
        await using var data = await Data.CreateAsync(fixture, "refund"); var preview = await data.PreviewAsync();
        var results = await Task.WhenAll(Record.ExceptionAsync(() => data.ApproveAsync(preview, Data.Meta("fa-race-a"))), Record.ExceptionAsync(() => data.ApproveAsync(preview, Data.Meta("fa-race-b"))));
        Assert.Single(results, value => value is null); Assert.Single(results, value => value is not null);
        Assert.Equal(1, await data.Rows(A.Events).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.namespace", "billing.return-financial")));
        var head = await data.Rows(B.Invoices).Find(new BsonDocument("_id", data.InvoiceId)).SingleAsync(); Assert.Equal(1000.02m, head["credited"].AsDecimal);
        var receipt = await data.Rows(B.Receipts).Find(new BsonDocument()).SingleAsync(); Assert.Equal(1000m, receipt["reservedForRefund"].AsDecimal);
    }
    [WindowsStorageTheory]
    [InlineData("auditEvents")] [InlineData("outboxEvents")]
    public async Task Required_approval_effect_failure_rolls_back_journal_credit_reservation_and_heads_and_original_key_can_retry(string table) {
        await using var data = await Data.CreateAsync(fixture, "refund"); var preview = await data.PreviewAsync(); var before = await data.MoneyAsync(); var original = await data.OriginalAsync();
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
        var validator = definition["options"].AsBsonDocument.GetValue("validator", new BsonDocument()).DeepClone().AsBsonDocument;
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", new BsonDocument("$and", new BsonArray { validator, new BsonDocument("kind", new BsonDocument("$ne", A.Operation)) }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        var metadata = Data.Meta("fa-rollback"); Assert.NotNull(await Record.ExceptionAsync(() => data.ApproveAsync(preview, metadata)));
        Assert.Equal(before, await data.MoneyAsync()); Assert.Equal(0, await data.Rows(A.Events).CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.namespace", "billing.return-financial")));
        await data.AssertOriginalAsync(original);
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } });
        var saved = await data.ApproveAsync(preview, metadata); Assert.Equal(saved, await data.Service.RecoverAsync(data.Actor, data.Source.CaseId, data.Source.ReturnId, data.Source.Action, metadata.IdempotencyKey));
    }
    [WindowsStorageTheory]
    [InlineData("original-account")] [InlineData("liability-account")] [InlineData("closed-period")]
    public async Task Changed_original_or_approved_liability_mapping_and_closed_accounting_period_abort_the_whole_approval(string fault) {
        await using var data = await Data.CreateAsync(fixture, "refund"); var preview = await data.PreviewAsync(); var before = await data.MoneyAsync();
        if (fault == "closed-period") await data.Rows("accountingBooks").UpdateOneAsync(new BsonDocument("_id", 1L), Builders<BsonDocument>.Update.Set("closedThrough", DateOnly.FromDateTime(data.Clock.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)));
        else {
            var chart = await data.Rows("accountingChartVersions").Find(new BsonDocument("_id", 20L)).SingleAsync();
            var map = chart["mappings"].AsBsonArray.Single(line => line["mappingKey"] == (fault == "original-account" ? "invoice_revenue" : "refund_liability")); map["accountId"] = 991L;
            await data.Rows("accountingChartVersions").ReplaceOneAsync(new BsonDocument("_id", 20L), chart);
        }
        Assert.NotNull(await Record.ExceptionAsync(() => data.ApproveAsync(preview, Data.Meta("fa-accounting-denied"))));
        Assert.Equal(before, await data.MoneyAsync()); Assert.Equal(0, await data.Rows(A.Events).CountDocumentsAsync(new BsonDocument())); Assert.Equal(0, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.namespace", "billing.return-financial")));
    }
    internal sealed class Data(ReturnEntitlementTests.Data source, ActorContext actor) : IAsyncDisposable
    {
        internal ReturnEntitlementTests.Data Source { get; } = source;
        internal ActorContext Actor { get; } = actor;
        internal IMongoDatabase Database => Source.Database;
        internal UnitToko.Platform.IClock Clock => Source.Clock;
        internal long InvoiceId => Source.Payment.Issued.Id.Value;
        internal PaymentAllocationService Allocations => Source.Allocation!.Service;
        internal ReturnFinancialApprovalService Service => new(Database, new(Source.Payment.Invoice.Order.Prices.Catalog.Client), new(Database), Clock, Source.Service, Allocations);
        internal ReturnFinancialApprovalInput Input { get; private set; } = null!;
        internal IMongoCollection<BsonDocument> Rows(string table) => Database.GetCollection<BsonDocument>(table);
        internal Task<ReturnFinancialApprovalPreview> PreviewAsync() => Service.PreviewAsync(Actor, Source.CaseId, Source.ReturnId, Source.Action, Input);
        internal Task<BsonDocument> ApproveAsync(ReturnFinancialApprovalPreview preview, AuthorizationCommandMetadata metadata) => Service.ApproveAsync(Actor, Source.CaseId, Source.ReturnId, Source.Action, preview.Binding, Input, metadata);
        internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic independently reviewed original financial request and explicit funding; no transfer", "synthetic-financial-approval");
        internal static async Task<Data> CreateAsync(MongoFixture fixture, string action = "refund", bool policy = true) {
            var source = await ReturnEntitlementTests.Data.CreateAsync(fixture, action, paid: true);
            try {
                await source.RequestAsync(await source.PreviewAsync(), ReturnEntitlementTests.Data.Meta("fa-maker-original"));
                var actor = await source.Payment.Invoice.Order.Prices.Catalog.ActorAsync(7625, CatalogAuthoringTestData.Shop, Permissions.CaseRead, Permissions.InvoiceRead, Permissions.CreditApprove, Permissions.RefundApprove,
                    Permissions.PaymentReviewRead, Permissions.PaymentAllocate, Permissions.BankReceiptRead, Permissions.BankStatementRead, Permissions.ReceivingAccountRead, Permissions.ReceivingAccountDetailsRead);
                await PersonTestData.SeedDistinctPeopleAsync(source.Database, source.Clock, actor.UserId);
                var data = new Data(source, actor); var original = await data.Rows(B.Events).Find(new BsonDocument()).SingleAsync();
                data.Input = new(1L, action == "refund" ? [new(new(original["_id"].AsInt64), 1L, new Money(1000m))] : []);
                await data.Rows("accountingChartVersions").UpdateOneAsync(new BsonDocument("_id", 20L), Builders<BsonDocument>.Update.Push("mappings", AccountingTestDocuments.Mapping("refund_liability", 901, "A2109")));
                if (policy) await data.PolicyAsync(); return data;
            } catch { await source.DisposeAsync(); throw; }
        }
        internal async Task PolicyAsync() {
            var shop = CatalogAuthoringTestData.Shop;
            var policy = new BsonDocument { { "_id", 99880L }, { "schemaVersion", 1 }, { "revision", 1L }, { "fence", 0L },
                { "scope", new BsonDocument { { "fulfillmentOrganizationId", shop.OrganizationId!.Value }, { "shopId", shop.ShopId!.Value }, { "channelId", shop.ChannelId!.Value }, { "warehouseId", BsonNull.Value } } },
                { "creditRule", "credit_original_components" }, { "reservationRule", "reserve_released_allocations" }, { "independenceRule", "request_and_funding_makers" },
                { "arMapping", "invoice_ar" }, { "refundLiabilityMapping", "refund_liability" }, { "refundLiabilityAccountId", 901L }, { "refundLiabilityAccountCode", "A2109" },
                { "active", true }, { "approvedAt", Clock.UtcNow.AddDays(-1).UtcDateTime }, { "expiresAt", Clock.UtcNow.AddDays(1).UtcDateTime } };
            foreach (var field in new[] { "creditNoteProcedureReference", "taxTreatmentReference", "chargeTreatmentReference", "refundReservationProcedureReference", "operatingOwnerReference", "approvalReference" }) policy[field] = "synthetic-approved-" + field;
            await Rows(A.Policies).InsertOneAsync(policy);
        }
        internal async Task<BsonDocument> MoneyAsync() {
            var output = new BsonDocument(); foreach (var table in new[] { B.Invoices, B.Receipts }) output.Add(table, new BsonArray((await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).Select(row => row.DeepClone()))); return output;
        }
        internal async Task<BsonDocument> OriginalAsync() {
            var output = new BsonDocument();
            foreach (var table in new[] { InvoiceBson.Invoices, B.Events, BankReceiptBson.Receipts, "returnQuarantineReceipts", "returnInspections", "returnResolutionRequests", "returnEntitlementRequests", "invoiceReturnEntitlementPools" })
                output.Add(table, new BsonArray((await Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()).Select(row => B.Snapshot(row))));
            output.Add("journals", new BsonArray((await Rows("journalEntries").Find(new BsonDocument("source.namespace", new BsonDocument("$ne", "billing.return-financial"))).Sort(new BsonDocument("_id", 1)).ToListAsync()).Select(row => row.DeepClone()))); return output;
        }
        internal async Task AssertOriginalAsync(BsonDocument before) => Assert.Equal(before, await OriginalAsync());
        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }
}
