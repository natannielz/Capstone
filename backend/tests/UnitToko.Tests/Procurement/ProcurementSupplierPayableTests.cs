using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Api.Procurement;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Organizations;
using Xunit.Abstractions;
using B = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;
using D = UnitToko.Tests.Procurement.ProcurementSupplierPayableTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ProcurementSupplierPayableTests(MongoFixture fixture, ITestOutputHelper output)
{
    [WindowsStorageFact]
    public async Task Genuine_payables_apply_only_verified_advance_once_and_shared_prior_owners_cannot_reopen_capacity()
    {
        await using var data = await D.CreateAsync(fixture, diagnostics: output.WriteLine);
        var preserved = await data.ProtectedAsync(); var originalJournals = await JournalsAsync(data);
        Assert.Equal(3, originalJournals.Count); // valuation, actual incoming cash anchor, independently verified outgoing advance
        var input = data.Recognize(); var proposal = await data.ProposeAsync("payable-recognize");
        Assert.False(await data.Rows(B.Payables).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows(B.Applications).Find(new BsonDocument()).AnyAsync());
        Assert.Equal(originalJournals, await JournalsAsync(data)); Assert.Equal(preserved, await data.ProtectedAsync());
        var review = await data.Service.PreviewDecisionAsync(data.Checker, proposal["proposalId"].AsInt64, ProcurementSupplierPayableDecision.Approve);
        await RollbackAsync(data, proposal["proposalId"].AsInt64, review["binding"].AsString, "payable-recognition-rollback");
        var recognition = await data.Service.DecideAsync(data.Checker, proposal["proposalId"].AsInt64, 1, ProcurementSupplierPayableDecision.Approve,
            review["binding"].AsString, D.Meta("payable-recognize-approved"));
        var payableId = recognition["payableId"].AsInt64;
        Assert.True(recognition["applicationId"].IsBsonNull); await JournalAsync(data, recognition["journalId"].AsInt64, 11250, "receipt_accrual", "supplier_payable");
        await CapacityAsync(data, payableId, "11250.00", "0.00", "11250.00", "0.00", "6000.00");
        var recognizedState = await data.StateAsync();
        var originalProposal = await data.Rows(B.Proposals).Find(new BsonDocument("_id", proposal["proposalId"])).SingleAsync();
        Assert.Equal("procurement_supplier_payable_lookup_required", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.ProposeAsync(data.Maker, input, originalProposal["binding"].AsString, D.Meta("payable-recognize")))).Code);
        Assert.Equal("procurement_supplier_payable_lookup_required", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.DecideAsync(data.Checker, proposal["proposalId"].AsInt64, 1,
                ProcurementSupplierPayableDecision.Approve, review["binding"].AsString, D.Meta("payable-recognize-approved")))).Code);
        var recovered = await data.Service.LookupAsync(data.Checker, B.DecideOperation, "payable-recognize-approved");
        Assert.Equal("confirmed", recovered["state"]); Assert.Equal(recognition, recovered["result"]);
        recovered = await data.Service.LookupAsync(data.Maker, B.ProposeOperation, "payable-recognize");
        Assert.Equal("confirmed", recovered["state"]); Assert.Equal(proposal, recovered["result"]);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.LookupAsync(data.Maker, B.DecideOperation, "payable-recognize-approved"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.LookupAsync(data.Checker, B.ProposeOperation, "payable-recognize"));
        await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, input));
        Assert.Equal(recognizedState, await data.StateAsync());
        var reject = await data.ProposeAsync("payable-application-rejected", data.Apply(payableId));
        var beforeReject = await JournalsAsync(data);
        var rejected = await data.DecideAsync(reject["proposalId"].AsInt64, "payable-application-rejection", ProcurementSupplierPayableDecision.Reject);
        Assert.Equal("rejected", rejected["status"]); Assert.True(rejected["applicationId"].IsBsonNull); Assert.True(rejected["journalId"].IsBsonNull);
        Assert.Equal(beforeReject, await JournalsAsync(data)); Assert.False(await data.Rows(B.Applications).Find(new BsonDocument()).AnyAsync());
        var apply = await data.ProposeAsync("payable-apply-four-thousand", data.Apply(payableId));
        await CapacityAsync(data, payableId, "11250.00", "0.00", "11250.00", "0.00", "6000.00");
        var applyReview = await data.Service.PreviewDecisionAsync(data.Checker, apply["proposalId"].AsInt64, ProcurementSupplierPayableDecision.Approve);
        await RollbackAsync(data, apply["proposalId"].AsInt64, applyReview["binding"].AsString, "payable-application-rollback");
        var applied = await data.Service.DecideAsync(data.Checker, apply["proposalId"].AsInt64, 1, ProcurementSupplierPayableDecision.Approve,
            applyReview["binding"].AsString, D.Meta("payable-four-thousand-approved"));
        Assert.Equal(payableId, applied["payableId"].AsInt64);
        Assert.Equal(apply["proposalId"], applied["proposalId"]);
        var application = await data.Rows(B.Applications).Find(new BsonDocument("_id", applied["applicationId"])).SingleAsync();
        Assert.Equal(payableId, application["payableId"].AsInt64);
        Assert.Equal(applied["decisionId"], application["decisionId"]); Assert.Equal(applied["journalId"], application["journalId"]);
        await JournalAsync(data, applied["journalId"].AsInt64, 4000, "supplier_payable", "supplier_advance");
        await CapacityAsync(data, payableId, "11250.00", "4000.00", "7250.00", "4000.00", "2000.00");
        Assert.Equal(preserved, await data.ProtectedAsync());
        var state = await data.StateAsync();
        recovered = await data.Service.LookupAsync(data.Checker, B.DecideOperation, "payable-four-thousand-approved");
        Assert.Equal("confirmed", recovered["state"]); Assert.Equal(applied, recovered["result"]);
        Assert.Equal(payableId, recovered["result"]["payableId"].AsInt64);
        Assert.Equal(application["_id"], recovered["result"]["applicationId"]);
        Assert.Equal(application["journalId"], recovered["result"]["journalId"]);
        var wire = JsonSerializer.SerializeToElement(new ProcurementSupplierPayableRecoveryDto("confirmed",
            ProcurementSupplierPayableDtos.Result(recovered["result"])), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(payableId.ToString(CultureInfo.InvariantCulture), wire.GetProperty("result").GetProperty("payableId").GetString());
        Assert.Equal(application["_id"].AsInt64.ToString(CultureInfo.InvariantCulture), wire.GetProperty("result").GetProperty("applicationId").GetString());
        Assert.Equal(application["journalId"].AsInt64.ToString(CultureInfo.InvariantCulture), wire.GetProperty("result").GetProperty("journalId").GetString());
        Assert.Equal(applied, (await data.Service.ReadAsync(data.Reader, apply["proposalId"].AsInt64))["decision"]);
        await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, data.Apply(payableId, 2001)));
        Assert.Equal(state, await data.StateAsync());

        // Missing both a capacity head and its application must not turn a retained approved decision into zero usage.
        foreach (var table in new[] { B.PayableHeads, B.AdvanceHeads }) {
            var head = await data.Rows(table).Find(new BsonDocument("_id", table == B.PayableHeads ? payableId : data.SettlementId)).SingleAsync();
            Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", head["_id"]))).DeletedCount);
            Assert.Equal(1L, (await data.Rows(B.Applications).DeleteOneAsync(new BsonDocument("_id", application["_id"]))).DeletedCount);
            try {
                var broken = await data.StateAsync();
                await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, data.Apply(payableId, 1)), table);
                await DeniedAsync(() => data.Service.LookupAsync(data.Checker, B.DecideOperation, "payable-four-thousand-approved"), table);
                Assert.Equal(broken, await data.StateAsync()); Assert.Equal(preserved, await data.ProtectedAsync());
            } finally { await data.Rows(table).InsertOneAsync(head); await data.Rows(B.Applications).InsertOneAsync(application); }
        }
        var appliedDecision = await data.Rows(B.Decisions).Find(new BsonDocument("_id", applied["decisionId"])).SingleAsync();
        foreach (var wrongPayable in new BsonValue[] { BsonNull.Value, new BsonInt64(checked(payableId + 1)) }) {
            var exact = new BsonDocument("_id", appliedDecision["_id"]);
            Assert.Equal(1L, (await data.Rows(B.Decisions).UpdateOneAsync(exact,
                Builders<BsonDocument>.Update.Set("payableId", wrongPayable))).ModifiedCount);
            try {
                var broken = await data.StateAsync();
                await DeniedAsync(() => data.Service.ReadAsync(data.Reader, apply["proposalId"].AsInt64), "application decision payable association");
                Assert.Equal(broken, await data.StateAsync());
            } finally { await data.Rows(B.Decisions).ReplaceOneAsync(exact, appliedDecision); }
        }
        var sourceSettlement = await data.Rows("procurementSettlements").Find(new BsonDocument("_id", data.SettlementId)).SingleAsync();
        foreach (var (table, filter) in new[] {
            (B.Decisions, new BsonDocument("_id", applied["decisionId"])),
            ("journalEntries", new BsonDocument("_id", applied["journalId"])),
            ("accountingAuditEvents", new BsonDocument { { "journalId", applied["journalId"] }, { "kind", "journal.posted" } }),
            ("auditEvents", new BsonDocument("_id", appliedDecision["auditId"])),
            ("outboxEvents", new BsonDocument("sourceKey", B.SourceKey(appliedDecision))),
            ("idempotencyRecords", new BsonDocument { { "actorId", data.Checker.UserId }, { "operation", B.DecideOperation }, { "key", "payable-four-thousand-approved" } }),
            ("bankTransactionClaims", new BsonDocument("_id", sourceSettlement["canonicalIdentity"])) }) {
            var row = await data.Rows(table).Find(filter).SingleAsync(); var exact = new BsonDocument("_id", row["_id"]);
            Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(exact)).DeletedCount);
            try {
                var broken = await data.StateAsync(); var protectedBroken = await data.ProtectedAsync();
                await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, data.Apply(payableId, 1)), table);
                Assert.Equal(broken, await data.StateAsync()); Assert.Equal(protectedBroken, await data.ProtectedAsync());
            } finally { await data.Rows(table).InsertOneAsync(row); }
        }

        // A second genuine valuation/payable shares the same finite verified advance. Its preview must authenticate foreign prior payable A.
        var secondValuation = await data.Valuation.DecideAsync((await data.Valuation.ProposeAsync("payable-second-valuation"))["proposalId"].AsInt64,
            "payable-second-valuation-approved");
        var secondInput = data.Recognize() with { ValuationAllocationId = secondValuation["allocationId"].AsInt64 };
        var secondRecognition = await data.DecideAsync((await data.ProposeAsync("payable-second-recognition", secondInput))["proposalId"].AsInt64,
            "payable-second-recognition-approved");
        var secondPayable = secondRecognition["payableId"].AsInt64;
        var sharedProtected = await data.ProtectedAsync();
        foreach (var (table, filter) in new[] {
            (B.Payables, new BsonDocument("_id", payableId)),
            (B.Decisions, new BsonDocument("_id", recognition["decisionId"])),
            ("journalEntries", new BsonDocument("_id", recognition["journalId"])) }) {
            var row = await data.Rows(table).Find(filter).SingleAsync();
            Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", row["_id"]))).DeletedCount);
            try {
                var broken = await data.StateAsync();
                await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, data.Apply(secondPayable, 2000)), "foreign prior " + table);
                Assert.Equal(broken, await data.StateAsync()); Assert.Equal(sharedProtected, await data.ProtectedAsync());
            } finally { await data.Rows(table).InsertOneAsync(row); }
        }
        var one = await data.ProposeAsync("payable-race-A", data.Apply(payableId, 2000));
        var two = await data.ProposeAsync("payable-race-B", data.Apply(secondPayable, 2000));
        var oneReview = await data.Service.PreviewDecisionAsync(data.Checker, one["proposalId"].AsInt64, ProcurementSupplierPayableDecision.Approve);
        var twoReview = await data.Service.PreviewDecisionAsync(data.Checker, two["proposalId"].AsInt64, ProcurementSupplierPayableDecision.Approve);
        var outcomes = await Task.WhenAll(
            Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, one["proposalId"].AsInt64, 1, ProcurementSupplierPayableDecision.Approve, oneReview["binding"].AsString, D.Meta("payable-race-A-approved"))),
            Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, two["proposalId"].AsInt64, 1, ProcurementSupplierPayableDecision.Approve, twoReview["binding"].AsString, D.Meta("payable-race-B-approved"))));
        Assert.Single(outcomes, value => value is null); Assert.IsType<PersistenceConflictException>(Assert.Single(outcomes, value => value is not null));
        Assert.Equal(2L, await data.Rows(B.Applications).CountDocumentsAsync(new BsonDocument()));
        var exhausted = await data.Rows(B.AdvanceHeads).Find(new BsonDocument("_id", data.SettlementId)).SingleAsync();
        Assert.Equal("6000.00", exhausted["totalAmount"]); Assert.Equal("6000.00", exhausted["appliedAmount"]); Assert.Equal("0.00", exhausted["remainingAmount"]);
        Assert.Equal(sharedProtected, await data.ProtectedAsync());
        await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, data.Apply(secondPayable, 1)));
    }

    [WindowsStorageFact]
    public async Task Zero_advance_receipt_recognizes_full_payable_only_after_current_independent_finance_authority()
    {
        await using var data = await D.CreateAsync(fixture, withAdvance: false); var protectedState = await data.ProtectedAsync();
        var input = data.Recognize();
        Assert.Equal("0.00", (await data.Rows("purchaseOrderProposals").Find(new BsonDocument("_id", data.Receiving.Proposal["proposalId"])).SingleAsync())["input"]["paymentTerms"]["requiredAdvance"]);
        foreach (var table in new[] { "procurementFundingRequests", "procurementDisbursementRecords", "procurementSettlements", "bankTransactionClaims" })
            Assert.False(await data.Rows(table).Find(new BsonDocument()).AnyAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Reader, input));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Outsider, input));
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.PreviewAsync(data.Maker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, input))).Code);
        var proposal = await data.ProposeAsync("zero-advance-payable"); var id = proposal["proposalId"].AsInt64;
        foreach (var (table, selected, field, replacement) in new[] {
            (B.Policies, (BsonValue)data.PolicyId, "active", (BsonValue)false),
            ("identities", (BsonValue)data.Maker.UserId, "status", (BsonValue)"suspended"),
            ("authorizationGrants", (BsonValue)data.Maker.UserId, "status", (BsonValue)"revoked"),
            ("authorizationGrants", (BsonValue)data.Checker.UserId, "status", (BsonValue)"revoked"),
            ("personAccountBindings", (BsonValue)data.Maker.UserId, "status", (BsonValue)"revoked"),
            ("personAccountBindings", (BsonValue)data.Checker.UserId, "status", (BsonValue)"revoked"),
            ("accountingBooks", data.Valuation.Book["bookId"], "closedThrough", (BsonValue)input.PostingDate.ToString("yyyy-MM", CultureInfo.InvariantCulture)) }) {
            var review = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementSupplierPayableDecision.Approve);
            var filter = new BsonDocument("_id", selected); var row = await data.Rows(table).Find(filter).SingleAsync();
            Assert.Equal(1L, (await data.Rows(table).UpdateOneAsync(filter, new BsonDocument("$set", new BsonDocument(field, replacement)))).ModifiedCount);
            try {
                var before = await data.StateAsync();
                Func<Task> attempt = () => data.Service.DecideAsync(data.Checker, id, 1, ProcurementSupplierPayableDecision.Approve,
                    review["binding"].AsString, D.Meta("zero-denied-" + table + "-" + selected));
                if (selected == data.Maker.UserId && table is "identities" or "authorizationGrants")
                    Assert.Equal(table == "identities" ? "original_maker_authority_changed" : "original_maker_scope_changed",
                        (await Assert.ThrowsAsync<AuthorizationDeniedException>(attempt)).Code);
                else await DeniedAsync(attempt, table);
                Assert.Equal(before, await data.StateAsync()); Assert.Equal(protectedState, await data.ProtectedAsync());
            } finally { await data.Rows(table).ReplaceOneAsync(filter, row); }
        }
        var originalPolicy = await data.Rows(B.Policies).Find(new BsonDocument("_id", data.PolicyId)).SingleAsync(); var at = data.Clock.UtcNow;
        await data.Rows(B.Policies).UpdateOneAsync(new BsonDocument("_id", data.PolicyId), new BsonDocument("$set", new BsonDocument("expiresAt", at.AddSeconds(1).UtcDateTime)));
        try {
            var expiring = await data.ProposeAsync("payable-expiring-policy");
            var review = await data.Service.PreviewDecisionAsync(data.Checker, expiring["proposalId"].AsInt64, ProcurementSupplierPayableDecision.Approve);
            await CrossDeadlineAsync(expiring["proposalId"].AsInt64, review["binding"].AsString, "payable-expired-policy",
                TimeSpan.FromSeconds(2), "procurement_supplier_payable_policy_unavailable");
        } finally { data.Clock.UtcNow = at; await data.Rows(B.Policies).ReplaceOneAsync(new BsonDocument("_id", data.PolicyId), originalPolicy); }
        var factorReview = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementSupplierPayableDecision.Approve);
        await CrossDeadlineAsync(id, factorReview["binding"].AsString, "payable-expired-final-factor", TimeSpan.FromMinutes(6), "step_up_required");
        var personReview = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementSupplierPayableDecision.Approve);
        var people = new List<(string Table, BsonDocument Row)>();
        foreach (var (table, personId) in new[] { ("personRecords", PersonTestData.PersonId(data.Checker.UserId)),
            ("personEquivalenceGroups", PersonTestData.GroupId(data.Maker.UserId)), ("personEquivalenceGroups", PersonTestData.GroupId(data.Checker.UserId)) })
            people.Add((table, await data.Rows(table).Find(new BsonDocument("_id", personId)).SingleAsync()));
        await OrganizationAddressTests.MergePeopleAsync(data.Receiving.Procurement.Organizations, data.Maker.UserId, data.Checker.UserId);
        try {
            var before = await data.StateAsync();
            await DeniedAsync(() => data.Service.DecideAsync(data.Checker, id, 1, ProcurementSupplierPayableDecision.Approve,
                personReview["binding"].AsString, D.Meta("payable-same-canonical-person")));
            Assert.Equal(before, await data.StateAsync());
        } finally { foreach (var (table, row) in people) await data.Rows(table).ReplaceOneAsync(new BsonDocument("_id", row["_id"]), row); }
        var approved = await data.DecideAsync(id, "zero-advance-payable-approved");
        await JournalAsync(data, approved["journalId"].AsInt64, 11250, "receipt_accrual", "supplier_payable");
        var head = await data.Rows(B.PayableHeads).Find(new BsonDocument("_id", approved["payableId"])).SingleAsync();
        Assert.Equal("11250.00", head["totalAmount"]); Assert.Equal("0.00", head["appliedAmount"]); Assert.Equal("11250.00", head["remainingAmount"]);
        Assert.False(await data.Rows(B.AdvanceHeads).Find(new BsonDocument()).AnyAsync()); Assert.False(await data.Rows(B.Applications).Find(new BsonDocument()).AnyAsync());
        Assert.Equal(protectedState, await data.ProtectedAsync());

        async Task CrossDeadlineAsync(long proposalId, string binding, string key, TimeSpan elapsed, string expectedCode)
        {
            var before = await data.StateAsync(); var held = await data.ProtectedAsync();
            var requestId = 0; var changed = 0;
            var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
            settings.ClusterConfigurator = builder => {
                builder.Subscribe<CommandStartedEvent>(value => {
                    if (requestId != 0 || value.DatabaseNamespace.DatabaseName != data.Database.DatabaseNamespace.DatabaseName ||
                        value.Command.GetValue("insert", BsonNull.Value) != "outboxEvents") return;
                    if (value.Command["documents"].AsBsonArray.Any(row => row.AsBsonDocument.GetValue("kind", BsonNull.Value) == B.DecideOperation &&
                        row.AsBsonDocument.GetValue("requestKey", BsonNull.Value) == key)) requestId = value.RequestId;
                });
                builder.Subscribe<CommandSucceededEvent>(value => {
                    if (requestId != 0 && value.RequestId == requestId && Interlocked.CompareExchange(ref changed, 1, 0) == 0)
                        data.Clock.UtcNow = at.Add(elapsed);
                });
            };
            var client = new MongoClient(settings); var db = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName); var unit = new MongoUnitOfWork(client);
            var catalog = new CatalogAuthoringService(db, unit, new(db), data.Clock); var requests = new ProcurementRequestService(db, unit, new(db), data.Clock, catalog);
            var orders = new PurchaseOrderService(db, unit, new(db), data.Clock, catalog, requests, new(db, unit, new(db), data.Clock, catalog));
            var valuation = new ProcurementStockValuationService(db, unit, new(db), data.Clock, catalog, orders,
                new(db, unit, new(db), data.Clock, catalog, orders, requests), new(db, unit, new(db), data.Clock));
            var bank = new BankReceiptService(db, unit, new(db), data.Clock, new(db, unit, new(db), data.Clock, new(db, unit, new(db), data.Clock)));
            var disbursements = new ProcurementDisbursementService(db, unit, new(db), data.Clock, catalog,
                new(db, unit, new(db), data.Clock, catalog, orders), new(db, unit, new(db), data.Clock, catalog), bank);
            var service = new ProcurementSupplierPayableService(db, unit, new(db), data.Clock, valuation,
                new(db, unit, new(db), data.Clock, catalog, disbursements, bank));
            try {
                var failure = await Record.ExceptionAsync(() => service.DecideAsync(data.Checker, proposalId, 1,
                    ProcurementSupplierPayableDecision.Approve, binding, D.Meta(key)));
                Assert.Equal(1, changed);
                Assert.Equal(expectedCode, expectedCode == "step_up_required"
                    ? Assert.IsType<AuthorizationDeniedException>(failure).Code : Assert.IsType<PersistenceConflictException>(failure).Code);
                Assert.Equal(before, await data.StateAsync()); Assert.Equal(held, await data.ProtectedAsync());
            } finally { data.Clock.UtcNow = at; }
            var recovery = await data.Service.LookupAsync(data.Checker, B.DecideOperation, key);
            Assert.Equal("not_confirmed", recovery["state"]); Assert.True(recovery["result"].IsBsonNull);
        }
    }

    private static async Task RollbackAsync(D data, long proposalId, string binding, string key)
    {
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "outboxEvents") })).SingleAsync();
        var validator = definition["options"]["validator"].DeepClone(); var state = await data.StateAsync(); var originals = await data.ProtectedAsync();
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" },
            { "validator", new BsonDocument("$and", new BsonArray { validator, new BsonDocument("kind", new BsonDocument("$ne", B.DecideOperation)) }) } });
        try {
            Assert.IsAssignableFrom<MongoException>(await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, proposalId, 1,
                ProcurementSupplierPayableDecision.Approve, binding, D.Meta(key))));
            Assert.Equal(state, await data.StateAsync()); Assert.Equal(originals, await data.ProtectedAsync());
            var recovered = await data.Service.LookupAsync(data.Checker, B.DecideOperation, key);
            Assert.Equal("not_confirmed", recovered["state"]); Assert.True(recovered["result"].IsBsonNull);
        } finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" }, { "validator", validator } }); }
    }
    internal static async Task JournalAsync(D data, long journalId, decimal amount, string debit, string credit)
    {
        var row = await data.Rows("journalEntries").Find(new BsonDocument("_id", journalId)).SingleAsync();
        Assert.Equal(amount, row["total"].AsDecimal); Assert.Equal(data.Valuation.Book["bookId"], row["bookId"]);
        Assert.Equal(2, row["lines"].AsBsonArray.Count);
        var dr = Assert.Single(row["lines"].AsBsonArray, line => line["mappingKey"] == debit);
        var cr = Assert.Single(row["lines"].AsBsonArray, line => line["mappingKey"] == credit);
        Assert.Equal(amount, dr["debit"].AsDecimal); Assert.Equal(0, dr["credit"].AsDecimal);
        Assert.Equal(amount, cr["credit"].AsDecimal); Assert.Equal(0, cr["debit"].AsDecimal);
        Assert.Equal(row["lines"].AsBsonArray.Sum(line => line["debit"].AsDecimal), row["lines"].AsBsonArray.Sum(line => line["credit"].AsDecimal));
    }
    private static async Task CapacityAsync(D data, long payableId, string total, string applied, string remaining, string advanceApplied, string advanceRemaining)
    {
        var payable = await data.Rows(B.PayableHeads).Find(new BsonDocument("_id", payableId)).SingleAsync();
        Assert.Equal(total, payable["totalAmount"]); Assert.Equal(applied, payable["appliedAmount"]); Assert.Equal(remaining, payable["remainingAmount"]);
        var advance = await data.Rows(B.AdvanceHeads).Find(new BsonDocument("_id", data.SettlementId)).SingleOrDefaultAsync();
        if (advance is null) { Assert.Equal("0.00", advanceApplied); Assert.Equal("6000.00", advanceRemaining); }
        else { Assert.Equal("6000.00", advance["totalAmount"]); Assert.Equal(advanceApplied, advance["appliedAmount"]); Assert.Equal(advanceRemaining, advance["remainingAmount"]); }
    }
    private static async Task<BsonArray> JournalsAsync(D data) => new(await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task DeniedAsync(Func<Task> action, string? context = null)
    {
        var failure = await Record.ExceptionAsync(action);
        Assert.True(failure is DomainValidationException or AuthorizationDeniedException or PersistenceConflictException, context + ": " + (failure?.ToString() ?? "Unexpected success"));
    }
}
