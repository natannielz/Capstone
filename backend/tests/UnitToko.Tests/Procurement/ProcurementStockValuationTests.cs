using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Organizations;
using D = UnitToko.Tests.Procurement.ProcurementStockValuationTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ProcurementStockValuationTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Real_scanned_invoice_partial_approval_posts_exact_receipt_accrual_once_without_releasing_held_goods()
    {
        await using var data = await D.CreateAsync(fixture); var preserved = await data.PreservedAsync(); var input = data.Input();
        var unvalued = await CostAsync(data, "unvalued", 0, "0.00", "0.00", "0.00", "0.00");
        Assert.Empty(unvalued["items"].AsBsonArray);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.CostAsync(data.Outsider, data.ReceiptId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.CostAsync(data.SourceMaker, data.ReceiptId));
        foreach (var invalid in new[] { input with { NetAmount = new Money(9999), CapitalizedAmount = new Money(11249) },
            input with { CapitalizedAmount = new Money(10000) }, input with { TaxAmount = new Money(2001), CapitalizedAmount = new Money(12251) },
            data.Input(2) with { TaxAmount = new Money(1000), CapitalizedAmount = new Money(21500) } })
            await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, invalid));
        var proposal = await data.ProposeAsync("valuation-first"); var id = proposal["proposalId"].AsInt64;
        Assert.Equal("proposed", proposal["status"].AsString);
        await NoAllocationAsync(data); Assert.Equal(preserved, await data.PreservedAsync());
        var rejected = await data.ProposeAsync("valuation-rejected");
        var rejection = await data.DecideAsync(rejected["proposalId"].AsInt64, "valuation-rejected-decision", ProcurementStockValuationDecision.Reject);
        Assert.Equal("rejected", rejection["status"].AsString); Assert.True(rejection["allocationId"].IsBsonNull); Assert.True(rejection["journalId"].IsBsonNull);
        await NoAllocationAsync(data);
        Assert.Equal(unvalued, await CostAsync(data, "unvalued", 0, "0.00", "0.00", "0.00", "0.00"));
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementStockValuationDecision.Approve);
        var approved = await data.Service.DecideAsync(data.Checker, id, 1, ProcurementStockValuationDecision.Approve, preview["binding"].AsString, D.Meta("valuation-first-approved"));
        Assert.Equal("approved", approved["status"].AsString); var after = await data.StateAsync();
        Assert.Equal("procurement_stock_valuation_lookup_required", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.DecideAsync(data.Checker, id, 1, ProcurementStockValuationDecision.Approve, preview["binding"].AsString, D.Meta("valuation-first-approved")))).Code);
        Assert.Equal(approved, await data.Service.LookupAsync(data.Checker, D.DecideOperation, "valuation-first-approved"));
        Assert.Equal(proposal, await data.Service.LookupAsync(data.Maker, D.ProposeOperation, "valuation-first"));
        Assert.Null(await data.Service.LookupAsync(data.Maker, D.DecideOperation, "valuation-first-approved"));
        Assert.Null(await data.Service.LookupAsync(data.Checker, D.ProposeOperation, "valuation-first"));
        Assert.Equal(after, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());
        await JournalAsync(data, approved["journalId"].AsInt64, 11250m);
        var partial = await CostAsync(data, "partially_valued", 1, "10000.00", "1000.00", "250.00", "11250.00");
        var firstCost = Assert.Single(partial["items"].AsBsonArray);
        Assert.Equal(approved["allocationId"], firstCost["id"]); Assert.Equal(approved["journalId"], firstCost["journalId"]);
        Assert.Equal(data.Documents.DocumentId, firstCost["invoiceDocumentId"].AsInt64); Assert.Equal(data.MatchId, firstCost["invoiceMatchId"].AsInt64);
        var receiptHead = await data.Rows(D.ReceiptHeads).Find(new BsonDocument()).SingleAsync();
        var matchHead = await data.Rows(D.MatchHeads).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(1L, receiptHead["allocatedPackages"].AsInt64); Assert.Equal(2L, receiptHead["remainingPackages"].AsInt64);
        Assert.Equal(1L, matchHead["allocatedPackages"].AsInt64); Assert.Equal(1L, matchHead["remainingPackages"].AsInt64);
        Assert.Equal("10000.00", matchHead["remainingNetAmount"].AsString); Assert.Equal("1000.00", matchHead["remainingTaxAmount"].AsString);
        Assert.Equal("250.00", matchHead["remainingChargeAmount"].AsString);
        var second = await data.ProposeAsync("valuation-final"); var final = await data.DecideAsync(second["proposalId"].AsInt64, "valuation-final-approved");
        await JournalAsync(data, final["journalId"].AsInt64, 11250m);
        Assert.Equal(2L, await data.Rows(D.Allocations).CountDocumentsAsync(new BsonDocument()));
        matchHead = await data.Rows(D.MatchHeads).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(0L, matchHead["remainingPackages"].AsInt64); Assert.Equal("0.00", matchHead["remainingTaxAmount"].AsString); Assert.Equal("0.00", matchHead["remainingChargeAmount"].AsString);
        await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, input));
        Assert.Equal(preserved, await data.PreservedAsync()); await data.NoPaymentOrReleaseAsync();
        await CostAsync(data, "partially_valued", 2, "20000.00", "2000.00", "500.00", "22500.00");
        // A second actual human match accounts for the remaining invoice package and its exact remaining components.
        var remainingMatch = await data.Documents.MatchAsync("valuation-last-human-match", data.Documents.MatchInput(1, new Money(1300), new Money(200)));
        var fullyMatchedSources = await data.PreservedAsync();
        var lastInput = data.Input() with { InvoiceMatchId = remainingMatch["matchId"].AsInt64, TaxAmount = new Money(1300), ChargeAmount = new Money(200), CapitalizedAmount = new Money(11500) };
        var lastProposal = await data.ProposeAsync("valuation-last-receipt-package", lastInput);
        var lastApproval = await data.DecideAsync(lastProposal["proposalId"].AsInt64, "valuation-last-receipt-package-approved");
        await JournalAsync(data, lastApproval["journalId"].AsInt64, 11500m);
        var full = await CostAsync(data, "fully_valued", 3, "30000.00", "3300.00", "700.00", "34000.00");
        Assert.Equal(3, full["items"].AsBsonArray.Count); Assert.Equal(lastApproval["allocationId"], full["items"][0]["id"]);
        Assert.Equal(fullyMatchedSources, await data.PreservedAsync()); await data.NoPaymentOrReleaseAsync();
        var beforeHistoricalRead = data.Clock.UtcNow; var bookFilter = new BsonDocument("_id", data.Book["bookId"]);
        var openBook = await data.Rows("accountingBooks").Find(bookFilter).SingleAsync();
        // Explicit synthetic current booking restriction: this does not claim an approved period-close workflow.
        Assert.Equal(1L, (await data.Rows("accountingBooks").UpdateOneAsync(bookFilter,
            new BsonDocument("$set", new BsonDocument("closedThrough", input.PostingDate.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture))))).ModifiedCount);
        data.Clock.UtcNow = beforeHistoricalRead.AddDays(2);
        try { Assert.Equal(full, await CostAsync(data, "fully_valued", 3, "30000.00", "3300.00", "700.00", "34000.00")); }
        finally { data.Clock.UtcNow = beforeHistoricalRead; await data.Rows("accountingBooks").ReplaceOneAsync(bookFilter, openBook); }
    }

    [WindowsStorageFact]
    public async Task Failed_posting_rolls_back_both_capacities_and_competing_approvals_cannot_consume_the_same_invoice_value()
    {
        await using var data = await D.CreateAsync(fixture); var preserved = await data.PreservedAsync();
        var first = await data.ProposeAsync("valuation-race-one", data.Input(2)); var second = await data.ProposeAsync("valuation-race-two", data.Input(2));
        var one = await data.Service.PreviewDecisionAsync(data.Checker, first["proposalId"].AsInt64, ProcurementStockValuationDecision.Approve);
        var two = await data.Service.PreviewDecisionAsync(data.Checker, second["proposalId"].AsInt64, ProcurementStockValuationDecision.Approve);
        var journalIndexes = await (await data.Rows("journalEntries").Indexes.ListAsync()).ToListAsync();
        var sourceIndex = Assert.Single(journalIndexes, row => row["key"].Equals(new BsonDocument("sourceEvent", 1)));
        Assert.True(sourceIndex["unique"].AsBoolean);
        var withoutIndexState = await data.StateAsync();
        await data.Rows("journalEntries").Indexes.DropOneAsync(sourceIndex["name"].AsString);
        try {
            Assert.Equal("accounting_storage_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() =>
                data.Service.DecideAsync(data.Checker, first["proposalId"].AsInt64, 1, ProcurementStockValuationDecision.Approve,
                    one["binding"].AsString, D.Meta("valuation-missing-journal-uniqueness")))).Code);
            Assert.Equal(withoutIndexState, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync()); await NoAllocationAsync(data);
        } finally {
            var options = new CreateIndexOptions<BsonDocument> { Name = sourceIndex["name"].AsString, Unique = true };
            if (sourceIndex.TryGetValue("partialFilterExpression", out var partial)) options.PartialFilterExpression = partial.AsBsonDocument;
            await data.Rows("journalEntries").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(sourceIndex["key"].AsBsonDocument, options));
        }
        Assert.Equal(new BsonArray(journalIndexes.OrderBy(row => row["name"].AsString, StringComparer.Ordinal)),
            new BsonArray((await (await data.Rows("journalEntries").Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString, StringComparer.Ordinal)));
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "outboxEvents") })).SingleAsync();
        var validator = definition["options"]["validator"].DeepClone(); var before = await data.StateAsync();
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" },
            { "validator", new BsonDocument("$and", new BsonArray { validator, new BsonDocument("kind", new BsonDocument("$ne", D.DecideOperation)) }) } });
        try {
            Assert.IsAssignableFrom<MongoException>(await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, first["proposalId"].AsInt64, 1,
                ProcurementStockValuationDecision.Approve, one["binding"].AsString, D.Meta("valuation-rolled-back"))));
            Assert.Equal(before, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());
            Assert.Null(await data.Service.LookupAsync(data.Checker, D.DecideOperation, "valuation-rolled-back")); await NoAllocationAsync(data);
        } finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" }, { "validator", validator } }); }
        var outcomes = await Task.WhenAll(
            Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, first["proposalId"].AsInt64, 1, ProcurementStockValuationDecision.Approve, one["binding"].AsString, D.Meta("valuation-race-one-approved"))),
            Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, second["proposalId"].AsInt64, 1, ProcurementStockValuationDecision.Approve, two["binding"].AsString, D.Meta("valuation-race-two-approved"))));
        Assert.Single(outcomes, value => value is null); Assert.IsType<PersistenceConflictException>(Assert.Single(outcomes, value => value is not null));
        Assert.Equal(1L, await data.Rows(D.Allocations).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1L, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        var decision = await data.Rows(D.Decisions).Find(new BsonDocument("decision", "approve")).SingleAsync();
        await JournalAsync(data, decision["journalId"].AsInt64, 22500m);
        Assert.Equal(2L, (await data.Rows(D.ReceiptHeads).Find(new BsonDocument()).SingleAsync())["allocatedPackages"].AsInt64);
        Assert.Equal(0L, (await data.Rows(D.MatchHeads).Find(new BsonDocument()).SingleAsync())["remainingPackages"].AsInt64);
        Assert.Equal(preserved, await data.PreservedAsync()); await data.NoPaymentOrReleaseAsync();
    }

    [WindowsStorageFact]
    public async Task Current_policy_people_grants_and_period_are_required_and_excluded_components_are_never_invented()
    {
        await using var data = await D.CreateAsync(fixture); var input = data.Input();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Reader, input));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Outsider, input));
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Maker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, input))).Code);
        await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", data.PolicyId), new BsonDocument("$set", new BsonDocument("capitalizeTax", false)));
        try {
            var excluded = await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.PreviewAsync(data.Maker, input));
            Assert.Equal("procurement_stock_valuation_noncapitalized_component", excluded.Code);
        } finally { await data.Rows(D.Policies).ReplaceOneAsync(new BsonDocument("_id", data.PolicyId), data.Policy); }
        var proposed = await data.ProposeAsync("valuation-revalidate"); var id = proposed["proposalId"].AsInt64;
        foreach (var (table, selected, field, replacement) in new[] {
            (D.Policies, (BsonValue)data.PolicyId, "active", (BsonValue)false),
            ("authorizationGrants", (BsonValue)data.Checker.UserId, "status", (BsonValue)"revoked"),
            ("personAccountBindings", (BsonValue)data.Checker.UserId, "status", (BsonValue)"revoked"),
            ("accountingBooks", data.Book["bookId"], "closedThrough", (BsonValue)input.PostingDate.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)) }) {
            var review = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementStockValuationDecision.Approve);
            var filter = new BsonDocument("_id", selected); var original = await data.Rows(table).Find(filter).SingleAsync();
            var before = await data.StateAsync();
            Assert.Equal(1L, (await data.Rows(table).UpdateOneAsync(filter, new BsonDocument("$set", new BsonDocument(field, replacement)))).ModifiedCount);
            try { await DeniedAsync(() => data.Service.DecideAsync(data.Checker, id, 1, ProcurementStockValuationDecision.Approve, review["binding"].AsString, D.Meta("denied-" + table))); }
            finally { await data.Rows(table).ReplaceOneAsync(filter, original); }
            Assert.Equal(before, await data.StateAsync()); await NoAllocationAsync(data);
        }
        var chartReview = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementStockValuationDecision.Approve);
        var chartFilter = new BsonDocument("_id", data.Book["chartId"]); var chart = await data.Rows("accountingChartVersions").Find(chartFilter).SingleAsync();
        var beforeChart = await data.StateAsync();
        Assert.Equal(1L, (await data.Rows("accountingChartVersions").DeleteOneAsync(chartFilter)).DeletedCount);
        try {
            await DeniedAsync(() => data.Service.DecideAsync(data.Checker, id, 1, ProcurementStockValuationDecision.Approve,
                chartReview["binding"].AsString, D.Meta("valuation-chart-withdrawn")));
            Assert.Equal(beforeChart, await data.StateAsync()); await NoAllocationAsync(data);
        } finally { await data.Rows("accountingChartVersions").InsertOneAsync(chart); }
        var at = data.Clock.UtcNow; var policyBeforeExpiry = await data.Rows(D.Policies).Find(new BsonDocument("_id", data.PolicyId)).SingleAsync();
        await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", data.PolicyId), new BsonDocument("$set", new BsonDocument("expiresAt", at.AddSeconds(1).UtcDateTime)));
        try {
            var expiring = await data.ProposeAsync("valuation-expiring-policy");
            var expiryReview = await data.Service.PreviewDecisionAsync(data.Checker, expiring["proposalId"].AsInt64, ProcurementStockValuationDecision.Approve);
            var beforeExpiry = await data.StateAsync(); data.Clock.UtcNow = at.AddSeconds(2);
            Assert.Equal("procurement_stock_valuation_policy_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
                data.Service.DecideAsync(data.Checker, expiring["proposalId"].AsInt64, 1, ProcurementStockValuationDecision.Approve,
                    expiryReview["binding"].AsString, D.Meta("valuation-expired-policy")))).Code);
            Assert.Equal(beforeExpiry, await data.StateAsync()); await NoAllocationAsync(data);
        } finally { data.Clock.UtcNow = at; await data.Rows(D.Policies).ReplaceOneAsync(new BsonDocument("_id", data.PolicyId), policyBeforeExpiry); }
        // Pause a real snapshot immediately after its policy read, before it can serialize that source.
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = 0; var observed = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = cluster => {
            cluster.Subscribe<CommandStartedEvent>(value => {
                if (requestId == 0 && value.DatabaseNamespace.DatabaseName == data.Database.DatabaseNamespace.DatabaseName &&
                    value.Command.GetValue("find", BsonNull.Value) == D.Policies && value.Command.TryGetValue("filter", out var filter) &&
                    filter.IsBsonDocument && filter.AsBsonDocument.GetValue("_id", BsonNull.Value) == data.PolicyId) requestId = value.RequestId;
            });
            cluster.Subscribe<CommandSucceededEvent>(value => {
                if (requestId != 0 && value.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                    entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Valuation policy withdrawal did not complete");
                }
            });
        };
        var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName); var unit = new MongoUnitOfWork(client);
        var catalog = new CatalogAuthoringService(db, unit, new(db), data.Clock); var requests = new ProcurementRequestService(db, unit, new(db), data.Clock, catalog);
        var orders = new PurchaseOrderService(db, unit, new(db), data.Clock, catalog, requests, new(db, unit, new(db), data.Clock, catalog));
        var service = new ProcurementStockValuationService(db, unit, new(db), data.Clock, catalog, orders,
            new(db, unit, new(db), data.Clock, catalog, orders, requests), new(db, unit, new(db), data.Clock));
        var originalPolicy = await data.Rows(D.Policies).Find(new BsonDocument("_id", data.PolicyId)).SingleAsync(); var beforeRace = await data.StateAsync();
        var pending = Task.Run(() => service.PreviewAsync(data.Maker, input));
        try {
            try {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(1L, (await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", data.PolicyId), new BsonDocument("$set", new BsonDocument("active", false)))).ModifiedCount);
                Assert.False((await data.Rows(D.Policies).Find(new BsonDocument("_id", data.PolicyId)).SingleAsync())["active"].AsBoolean);
            } finally { release.Set(); }
            await DeniedAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(90))); Assert.Equal(1, observed);
        } finally { await data.Rows(D.Policies).ReplaceOneAsync(new BsonDocument("_id", data.PolicyId), originalPolicy); }
        Assert.Equal(beforeRace, await data.StateAsync()); await NoAllocationAsync(data);
        var alias = await data.Base.AddActorAsync(738, ProcurementRequestTestData.Scope, Permissions.ProcurementRead, Permissions.StockRead,
            Permissions.ProcurementStockValuationRead, Permissions.ProcurementStockValuationDecide, Permissions.JournalPost);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, alias.UserId);
        await OrganizationAddressTests.MergePeopleAsync(data.Inspection.Receiving.Procurement.Organizations, data.Documents.Matcher.UserId, alias.UserId);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(alias, id, ProcurementStockValuationDecision.Approve));
        await NoAllocationAsync(data); await data.NoPaymentOrReleaseAsync();
    }

    [WindowsStorageFact]
    public async Task Verified_proposal_source_proof_cannot_cross_a_transaction_retry()
    {
        await using var data = await D.CreateAsync(fixture);
        var proposal = await data.ProposeAsync("valuation-transaction-proof");
        var input = data.Input();
        var records = new ProcurementStockValuationRecords(data.Database, data.Unit, data.Clock,
            data.Inspection.Receiving.Procurement.Catalog, data.Inspection.Receiving.Orders.Service, data.Sources);
        using var tx = await data.Database.Client.StartSessionAsync();
        tx.StartTransaction();
        var proof = await records.VerifiedProposalAsync(tx, proposal["proposalId"].AsInt64, CancellationToken.None);
        proof.Require(tx, input, null);
        await tx.AbortTransactionAsync();
        tx.StartTransaction();
        try {
            Assert.Equal("procurement_stock_valuation_integrity_unavailable",
                Assert.Throws<AuthorizationDeniedException>(() => proof.Require(tx, input, null)).Code);
        } finally { await tx.AbortTransactionAsync(); }
        await NoAllocationAsync(data);
    }

    [WindowsStorageFact]
    public async Task Cached_invoice_history_requires_exact_bytes_and_cannot_cross_a_new_transaction_on_the_same_handle()
    {
        await using var data = await D.CreateAsync(fixture);
        var invoice = await data.Rows(SupplierDocumentBson.Submissions)
            .Find(new BsonDocument("_id", data.Documents.DocumentId)).SingleAsync();
        var reader = new ProcurementStockValuationCapacityReader(data.Database, data.Unit, data.Clock,
            data.Inspection.Receiving.Procurement.Catalog, data.Inspection.Receiving.Orders.Service);
        using var tx = await data.Database.Client.StartSessionAsync();
        tx.StartTransaction();
        try {
            Assert.Equal(invoice, (await reader.InvoiceHistoryAsync(tx, invoice, CancellationToken.None)).Document);
            var changed = invoice.DeepClone().AsBsonDocument;
            changed["reason"] = "Synthetic changed source after an authenticated history read";
            changed["snapshotHash"] = SupplierDocumentBson.Hash(changed);
            Assert.Equal(1L, (await data.Rows(SupplierDocumentBson.Submissions).ReplaceOneAsync(tx,
                new BsonDocument("_id", invoice["_id"]), changed)).ModifiedCount);
            Assert.Equal("procurement_stock_valuation_integrity_unavailable",
                (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => reader.InvoiceHistoryAsync(tx, changed, CancellationToken.None))).Code);
        } finally { await tx.AbortTransactionAsync(); }

        var pinFilter = new BsonDocument("_id", invoice["pinId"]);
        var pin = await data.Rows("evidencePins").Find(pinFilter).SingleAsync();
        Assert.Equal(1L, (await data.Rows("evidencePins").DeleteOneAsync(pinFilter)).DeletedCount);
        try {
            tx.StartTransaction();
            try {
                var current = await data.Rows(SupplierDocumentBson.Submissions).Find(tx,
                    new BsonDocument("_id", invoice["_id"])).SingleAsync();
                Assert.Equal(invoice, current);
                Assert.Equal("evidence_validation_integrity", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
                    reader.InvoiceHistoryAsync(tx, current, CancellationToken.None))).Code);
            } finally { await tx.AbortTransactionAsync(); }
        } finally { await data.Rows("evidencePins").InsertOneAsync(pin); }
        await NoAllocationAsync(data);
    }

    [WindowsStorageFact]
    public async Task Invoice_head_and_identity_changes_after_proposal_proof_fail_final_decision_preview_fences()
    {
        await using var data = await D.CreateAsync(fixture);
        var proposal = await data.ProposeAsync("valuation-invoice-fence-proof");
        var proposalId = proposal["proposalId"].AsInt64;
        var submitted = await data.Rows("supplierDocumentSubmissions").Find(new BsonDocument("_id", data.Documents.DocumentId)).SingleAsync();
        foreach (var (table, filter, field, replacement) in new[] {
            ("supplierDocumentHeads", new BsonDocument("_id", submitted["draftId"]), "draftHash", (BsonValue)new string('0', 64)),
            ("supplierDocumentIdentities", new BsonDocument("_id", submitted["identityId"]), "firstDraftId", (BsonValue)(long.MaxValue - 100)) }) {
            var original = await data.Rows(table).Find(filter).SingleAsync();
            var before = await data.StateAsync();
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var paused = 0;
            var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(value => {
                if (value.DatabaseNamespace.DatabaseName == data.Database.DatabaseNamespace.DatabaseName &&
                    value.Command.GetValue("update", BsonNull.Value) == table &&
                    value.Command.TryGetValue("updates", out var updates) && updates.IsBsonArray && updates.AsBsonArray.Count == 1 &&
                    updates.AsBsonArray[0].IsBsonDocument && updates.AsBsonArray[0].AsBsonDocument.TryGetValue("q", out var query) &&
                    query.IsBsonDocument && query.AsBsonDocument.GetValue("_id", BsonNull.Value) == filter["_id"] &&
                    Interlocked.CompareExchange(ref paused, 1, 0) == 0) {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Invoice source fence did not resume");
                }
            });
            var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName); var unit = new MongoUnitOfWork(client);
            var catalog = new CatalogAuthoringService(db, unit, new(db), data.Clock); var requests = new ProcurementRequestService(db, unit, new(db), data.Clock, catalog);
            var orders = new PurchaseOrderService(db, unit, new(db), data.Clock, catalog, requests, new(db, unit, new(db), data.Clock, catalog));
            var service = new ProcurementStockValuationService(db, unit, new(db), data.Clock, catalog, orders,
                new(db, unit, new(db), data.Clock, catalog, orders, requests), new(db, unit, new(db), data.Clock));
            var pending = Task.Run(() => service.PreviewDecisionAsync(data.Checker, proposalId, ProcurementStockValuationDecision.Approve));
            try {
                try {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
                    Assert.Equal(1L, (await data.Rows(table).UpdateOneAsync(filter, new BsonDocument("$set", new BsonDocument(field, replacement)))).ModifiedCount);
                } finally { release.Set(); }
                var failure = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(90)));
                Assert.True(failure is AuthorizationDeniedException { Code: "procurement_stock_valuation_integrity_unavailable" or "supplier_document_integrity_unavailable" } or
                    PersistenceConflictException { Code: "version_conflict" or "procurement_stock_valuation_changed" }, failure?.ToString() ?? "Unexpected success");
                Assert.Equal(1, paused);
            } finally {
                release.Set();
                await data.Rows(table).ReplaceOneAsync(filter, original);
            }
            Assert.Equal(before, await data.StateAsync());
            await NoAllocationAsync(data);
        }
    }

    [WindowsStorageFact]
    public async Task Missing_allocation_heads_owner_or_posted_journal_never_becomes_unconsumed_value_or_replays_another_posting()
    {
        await using var data = await D.CreateAsync(fixture, receiptPackages: 2);
        var firstReceipt = await data.Rows("procurementStockReceipts").Find(new BsonDocument("_id", data.ReceiptId)).SingleAsync();
        var secondReceiptInput = ProcurementStockReceiptBson.Input(firstReceipt["input"]) with { Packages = 1, ReceiptReference = "SYNTHETIC-VALUATION-SECOND-RECEIPT" };
        var receiptPreview = await data.Receipts.PreviewAsync(data.Maker, secondReceiptInput);
        var secondReceipt = await data.Receipts.ReceiveAsync(data.Maker, secondReceiptInput, receiptPreview["binding"].AsString, D.Meta("valuation-second-held-receipt"));
        var secondReceiptId = secondReceipt["receiptId"].AsInt64;
        Assert.NotEqual(data.ReceiptId, secondReceiptId); Assert.NotEqual(data.Receipt["lotId"], secondReceipt["lotId"]);
        var secondBalanceFilter = new BsonDocument("_id", secondReceipt["balanceId"]);
        var secondBalance = await data.Rows("stockBalances").Find(secondBalanceFilter).SingleAsync();
        Assert.Equal(1L, secondBalance["onHand"].AsInt64); Assert.Equal(1L, secondBalance["held"].AsInt64); Assert.Equal(0L, secondBalance["reserved"].AsInt64);
        var proposal = await data.ProposeAsync("valuation-proof"); var id = proposal["proposalId"].AsInt64;
        var approved = await data.DecideAsync(id, "valuation-proof-approved");
        var secondInput = data.Input() with { ReceiptId = secondReceiptId };
        var validSecondPreview = await data.Service.PreviewAsync(data.Maker, secondInput);
        Assert.Equal(secondReceiptId, validSecondPreview["receipt"]["id"].AsInt64);
        var secondCost = await data.Service.CostAsync(data.Reader, secondReceiptId);
        Assert.Equal("unvalued", secondCost["state"]); Assert.Equal(1L, secondCost["packageCount"].AsInt64);
        Assert.Equal(0L, secondCost["valuedPackages"].AsInt64); Assert.Equal(1L, secondCost["unvaluedPackages"].AsInt64);
        Assert.Equal("0.00", secondCost["capitalizedAmount"]); Assert.Empty(secondCost["items"].AsBsonArray);
        var preserved = await data.PreservedAsync(); var state = await data.StateAsync();
        var invoice = await data.Rows("supplierDocumentSubmissions").Find(new BsonDocument("_id", data.Documents.DocumentId)).SingleAsync();
        var allocationFilter = new BsonDocument("_id", approved["allocationId"]);
        var allocated = await data.Rows(D.Allocations).Find(allocationFilter).SingleAsync();
        foreach (var (headTable, headFilter) in new[] { (D.ReceiptHeads, new BsonDocument("_id", data.ReceiptId)),
            (D.MatchHeads, new BsonDocument("_id", ProcurementStockValuationCapacityReader.MatchKey(data.MatchId, 1))) }) {
            var allocatedHead = await data.Rows(headTable).Find(headFilter).SingleAsync();
            Assert.Equal(1L, (await data.Rows(D.Allocations).DeleteOneAsync(allocationFilter)).DeletedCount);
            Assert.Equal(1L, (await data.Rows(headTable).DeleteOneAsync(headFilter)).DeletedCount);
            try {
                Assert.True(await data.Rows(D.Decisions).Find(new BsonDocument("_id", approved["decisionId"])).AnyAsync());
                var broken = await data.StateAsync(); await DeniedAsync(() => data.Service.CostAsync(data.Reader, data.ReceiptId));
                await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, data.Input()));
                if (headTable == D.MatchHeads) {
                    // This receipt has no approval/allocation, so its own receipt guard remains clear.
                    Assert.Equal(secondCost, await data.Service.CostAsync(data.Reader, secondReceiptId));
                    await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, secondInput));
                }
                Assert.Equal(broken, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());
            } finally { await data.Rows(D.Allocations).InsertOneAsync(allocated); await data.Rows(headTable).InsertOneAsync(allocatedHead); }
            Assert.Equal(state, await data.StateAsync());
        }
        foreach (var (table, sourceFilter) in new[] {
            (D.ReceiptHeads, new BsonDocument("_id", data.ReceiptId)),
            (D.MatchHeads, new BsonDocument("_id", ProcurementStockValuationCapacityReader.MatchKey(data.MatchId, 1))),
            (D.Allocations, new BsonDocument("_id", approved["allocationId"])),
            (D.Decisions, new BsonDocument("_id", approved["decisionId"])),
            ("journalEntries", new BsonDocument("_id", approved["journalId"])),
            ("accountingAuditEvents", new BsonDocument { { "journalId", approved["journalId"] }, { "kind", "journal.posted" } }),
            ("auditEvents", new BsonDocument("_id", invoice["auditId"])),
            ("supplierDocumentMatches", new BsonDocument("_id", data.MatchId)) }) {
            var row = await data.Rows(table).Find(sourceFilter).SingleAsync(); var filter = new BsonDocument("_id", row["_id"]);
            Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(filter)).DeletedCount);
            try {
                await DeniedAsync(() => data.Service.ReadAsync(data.Reader, id));
                if (table == "auditEvents") await DeniedAsync(() => data.Service.HistoryAsync(data.Reader, data.Input().Location));
                await DeniedAsync(() => data.Service.LookupAsync(data.Checker, D.DecideOperation, "valuation-proof-approved"));
                await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, data.Input()));
                await DeniedAsync(() => data.Service.CostAsync(data.Reader, data.ReceiptId));
            } finally { await data.Rows(table).InsertOneAsync(row); }
            Assert.Equal(state, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());
        }
        var restoredRead = await data.Service.ReadAsync(data.Reader, id);
        var restoredHistory = await data.Service.HistoryAsync(data.Reader, data.Input().Location);
        Assert.Equal(restoredRead["proposal"], restoredHistory["items"].AsBsonArray
            .Single(item => item["proposal"]["id"].AsInt64 == id)["proposal"]);
        foreach (var operation in new[] { D.ProposeOperation, D.DecideOperation })
            foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("idempotencyRecords", "operation"), ("outboxEvents", "kind") }) {
                var proof = await data.Rows(table).Find(new BsonDocument(field, operation)).SingleAsync(); var filter = new BsonDocument("_id", proof["_id"]);
                Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(filter)).DeletedCount);
                try {
                    var missingProofState = await data.StateAsync();
                    await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, data.Input()));
                    await DeniedAsync(() => data.Service.LookupAsync(data.Checker, D.DecideOperation, "valuation-proof-approved"));
                    await DeniedAsync(() => data.Service.CostAsync(data.Reader, data.ReceiptId));
                    Assert.Equal(missingProofState, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());
                } finally { await data.Rows(table).InsertOneAsync(proof); }
                Assert.Equal(state, await data.StateAsync());
            }
        var journalFilter = new BsonDocument("_id", approved["journalId"]); var originalJournal = await data.Rows("journalEntries").Find(journalFilter).SingleAsync();
        var originalDraft = AccountingBson.ReadDraft(originalJournal);
        var alteredDraft = JournalDraft.FromResolved(originalDraft.Source, originalDraft.SourceRevision, originalDraft.Book, originalDraft.Dimensions,
            originalDraft.Date, originalDraft.ChartId, originalDraft.ChartVersion, originalDraft.Lines.Select(line => new ResolvedJournalLine(
                line.MappingKey, line.AccountId, line.AccountCode, new Money(line.Debit.Value == 0 ? 0 : line.Debit.Value + 1),
                new Money(line.Credit.Value == 0 ? 0 : line.Credit.Value + 1))), originalDraft.ReversalOf);
        var alteredJournal = originalJournal.DeepClone().AsBsonDocument;
        alteredJournal["lines"] = new BsonArray(alteredDraft.Lines.Select(AccountingBson.Line)); alteredJournal["total"] = new BsonDecimal128(alteredDraft.Total.Value);
        alteredJournal["fingerprint"] = alteredDraft.Fingerprint;
        Assert.Equal(alteredDraft.Fingerprint, AccountingBson.ReadDraft(alteredJournal).Fingerprint);
        Assert.Equal(1L, (await data.Rows("journalEntries").ReplaceOneAsync(journalFilter, alteredJournal)).ModifiedCount);
        try {
            var alteredState = await data.StateAsync();
            await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, data.Input()));
            await DeniedAsync(() => data.Service.LookupAsync(data.Checker, D.DecideOperation, "valuation-proof-approved"));
            await DeniedAsync(() => data.Service.CostAsync(data.Reader, data.ReceiptId));
            Assert.Equal(alteredState, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());
        } finally { await data.Rows("journalEntries").ReplaceOneAsync(journalFilter, originalJournal); }
        Assert.Equal(state, await data.StateAsync());
        Assert.Equal(approved, await data.Service.LookupAsync(data.Checker, D.DecideOperation, "valuation-proof-approved"));
        var second = await data.ProposeAsync("valuation-proof-restored");
        var secondApproval = await data.DecideAsync(second["proposalId"].AsInt64, "valuation-proof-restored-approved");
        await JournalAsync(data, secondApproval["journalId"].AsInt64, 11250m);
        Assert.Equal(2L, await data.Rows(D.Allocations).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2L, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(secondCost, await data.Service.CostAsync(data.Reader, secondReceiptId));
        Assert.Equal(secondBalance, await data.Rows("stockBalances").Find(secondBalanceFilter).SingleAsync());
        Assert.Equal(preserved, await data.PreservedAsync()); await data.NoPaymentOrReleaseAsync();
    }

    [WindowsStorageFact]
    public async Task Another_invoice_cannot_use_receipt_capacity_when_a_prior_invoice_loses_its_private_original_proof()
    {
        await using var data = await D.CreateAsync(fixture);
        var first = await data.ProposeAsync("valuation-prior-invoice-proof");
        var firstApproval = await data.DecideAsync(first["proposalId"].AsInt64, "valuation-prior-invoice-proof-approved");
        await JournalAsync(data, firstApproval["journalId"].AsInt64, 11250m);

        // A distinct private original, draft, submission and human match consume the
        // remaining inspection capacity. The selected invoice's own proof stays valid.
        var secondOriginal = data.Documents.Input("SYNTHETIC-INVOICE-PRIOR-PROOF-0002");
        var secondDraft = await data.Documents.DraftAsync("valuation-second-invoice-draft", secondOriginal);
        var secondDraftId = secondDraft["draftId"].AsInt64;
        var (reservation, reference) = await data.Documents.UploadForAsync(secondDraftId, "valuation-second-invoice-image");
        Assert.Equal("completed", await data.Documents.Runtime.RunOnceAsync());
        var submitPreview = await data.Documents.Service.PreviewSubmitAsync(data.Documents.Actor, secondDraftId, 1,
            reservation.ReservationId, reference);
        var secondSubmission = await data.Documents.Evidence.SubmitAsync(data.Documents.Actor, secondDraftId, 1,
            reservation.ReservationId, reference, submitPreview["binding"].AsString,
            SupplierDocumentTestData.Meta("valuation-second-invoice-submit"));
        var secondDocumentId = secondSubmission["documentId"].AsInt64;
        // The first reviewed match used 2,000 tax and 500 charge; the approved
        // PO leaves exactly 1,300 tax and 200 charge for its final package.
        var secondMatchInput = data.Documents.MatchInput(1, new Money(1300), new Money(200)) with {
            DocumentId = secondDocumentId, ReviewedInput = secondOriginal, OcrResultId = null
        };
        var secondMatch = await data.Documents.MatchAsync("valuation-second-invoice-human-match", secondMatchInput);
        var secondInput = data.Input() with { InvoiceDocumentId = secondDocumentId, InvoiceMatchId = secondMatch["matchId"].AsInt64,
            TaxAmount = new Money(1300), ChargeAmount = new Money(200), CapitalizedAmount = new Money(11500) };
        _ = await data.Service.PreviewAsync(data.Maker, secondInput);

        var firstSubmission = await data.Rows("supplierDocumentSubmissions")
            .Find(new BsonDocument("_id", data.Documents.DocumentId)).SingleAsync();
        var firstPinFilter = new BsonDocument("_id", firstSubmission["pinId"]);
        var firstPin = await data.Rows("evidencePins").Find(firstPinFilter).SingleAsync();
        Assert.Equal(1L, (await data.Rows("evidencePins").DeleteOneAsync(firstPinFilter)).DeletedCount);
        try {
            var before = await data.StateAsync();
            await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, secondInput));
            Assert.Equal(before, await data.StateAsync());
        } finally { await data.Rows("evidencePins").InsertOneAsync(firstPin); }

        var secondProposal = await data.ProposeAsync("valuation-second-invoice-proof-restored", secondInput);
        var secondApproval = await data.DecideAsync(secondProposal["proposalId"].AsInt64, "valuation-second-invoice-proof-approved");
        await JournalAsync(data, secondApproval["journalId"].AsInt64, 11500m);
        Assert.Equal(2L, await data.Rows(D.Allocations).CountDocumentsAsync(new BsonDocument()));
        await data.NoPaymentOrReleaseAsync();
    }

    private static async Task<BsonDocument> CostAsync(D data, string state, long valued, string net, string tax, string charge, string capitalized)
    {
        var before = await data.StateAsync(); var held = await data.HeldAsync();
        var counters = new BsonArray(await data.Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var cost = await data.Service.CostAsync(data.Reader, data.ReceiptId);
        Assert.Equal(data.ReceiptId, cost["receiptId"].AsInt64); Assert.Equal(data.Receipt["lotId"], cost["lotId"]); Assert.Equal(data.Receipt["balanceId"], cost["balanceId"]);
        Assert.Equal(3L, cost["packageCount"].AsInt64); Assert.Equal(valued, cost["valuedPackages"].AsInt64); Assert.Equal(3L - valued, cost["unvaluedPackages"].AsInt64);
        Assert.Equal("IDR", cost["currency"]); Assert.Equal(state, cost["state"]); Assert.Equal(net, cost["netAmount"]); Assert.Equal(tax, cost["taxAmount"]);
        Assert.Equal(charge, cost["chargeAmount"]); Assert.Equal(capitalized, cost["capitalizedAmount"]); Assert.True(cost["nextAllocationId"].IsBsonNull);
        Assert.Equal(before, await data.StateAsync()); Assert.Equal(held, await data.HeldAsync());
        Assert.Equal(counters, new BsonArray(await data.Rows("idCounters").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        return cost;
    }
    internal static async Task JournalAsync(D data, long journalId, decimal total)
    {
        var journal = await data.Rows("journalEntries").Find(new BsonDocument("_id", journalId)).SingleAsync();
        Assert.Equal(total, journal["total"].AsDecimal); Assert.Equal(data.Book["bookId"], journal["bookId"]);
        var lines = journal["lines"].AsBsonArray; Assert.Equal(2, lines.Count);
        var inventory = Assert.Single(lines, row => row["mappingKey"] == "inventory");
        var accrual = Assert.Single(lines, row => row["mappingKey"] == "receipt_accrual");
        Assert.Equal(total, inventory["debit"].AsDecimal); Assert.Equal(0m, inventory["credit"].AsDecimal);
        Assert.Equal(0m, accrual["debit"].AsDecimal); Assert.Equal(total, accrual["credit"].AsDecimal);
        Assert.Equal(data.Policy["inventoryAccountId"], inventory["accountId"]); Assert.Equal(data.Policy["receiptAccrualAccountId"], accrual["accountId"]);
        Assert.Equal(lines.Sum(row => row["debit"].AsDecimal), lines.Sum(row => row["credit"].AsDecimal));
    }
    private static async Task NoAllocationAsync(D data)
    {
        foreach (var table in new[] { D.Allocations, D.ReceiptHeads, D.MatchHeads, "journalEntries", "accountingAuditEvents", "accountingPeriodHistory" })
            Assert.False(await data.Rows(table).Find(new BsonDocument()).AnyAsync());
    }
    private static async Task DeniedAsync(Func<Task> action)
    {
        var failure = await Record.ExceptionAsync(action);
        Assert.True(failure is DomainValidationException or AuthorizationDeniedException or PersistenceConflictException, failure?.ToString() ?? "Unexpected success");
    }
}
