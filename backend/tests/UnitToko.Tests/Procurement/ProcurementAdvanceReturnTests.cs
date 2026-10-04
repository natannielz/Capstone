using System.Runtime.Versioning;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Api.Procurement;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;
using Xunit.Abstractions;
using B = UnitToko.Platform.Procurement.ProcurementAdvanceReturnBson;
using P = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;
using D = UnitToko.Tests.Procurement.ProcurementAdvanceReturnTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ProcurementAdvanceReturnTests(MongoFixture fixture, ITestOutputHelper output)
{
    [WindowsStorageFact]
    public async Task Whole_genuine_credit_returns_two_same_order_advances_once_without_payable_or_cash_changes()
    {
        await using var data = await D.CreateAsync(fixture, receiptAmount: 7000, multiplePayouts: true, diagnostics: output.WriteLine);
        var second = data.SecondSettlement!["id"].AsInt64;
        var input = data.Input() with { Lines = [new(data.SettlementId, new(4000)), new(second, new(3000))] };
        await CurrentStorageAdmissionAsync(data);
        var preserved = await data.PreservedAsync(); var journals = await JournalsAsync(data);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReceiptsAsync(data.Reader, ProcurementRequestTestData.Location));
        Assert.False(await data.Rows(P.Payables).Find(new BsonDocument()).AnyAsync());
        await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, input with { Lines = [new(data.SettlementId, new(6000))] }));
        await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, input with { Lines = [new(data.SettlementId, new(7000))] }));
        var proposal = await data.ProposeAsync("return-two-proposal", input); var id = proposal["proposalId"].AsInt64;
        var otherProposal = await data.ProposeAsync("return-two-pending-proposal", input);
        Assert.Equal(journals, await JournalsAsync(data));
        Assert.False(await data.Rows(P.AdvanceHeads).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows(PaymentVerificationBson.Parties).Find(new BsonDocument("_id", data.ReceiptId)).AnyAsync());
        var rejectPreview = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementAdvanceReturnDecision.Reject);
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementAdvanceReturnDecision.Approve);
        await RollbackAsync(data, id, preview["binding"].AsString);
        var result = await data.Service.DecideAsync(data.Checker, id, 1, ProcurementAdvanceReturnDecision.Approve,
            preview["binding"].AsString, D.Meta("return-two-approved"));
        Assert.Equal("returned", result["status"]); Assert.Equal(data.ReceiptId, result["receiptId"].AsInt64);
        Assert.Equal(2, result["returnLineIds"].AsBsonArray.Count);
        await ProcurementSupplierPayableTests.JournalAsync(data.Payables, result["journalId"].AsInt64, 7000, "unidentified_cash", "supplier_advance");
        Assert.Equal(journals.Count + 1, (await JournalsAsync(data)).Count);
        foreach (var (settlement, returned, remaining) in new[] { (data.SettlementId, "4000.00", "2000.00"), (second, "3000.00", "1000.00") }) {
            var head = await data.Rows(P.AdvanceHeads).Find(new BsonDocument("_id", settlement)).SingleAsync();
            Assert.Equal(2, head["stateVersion"].AsInt32); Assert.Equal("0.00", head["appliedAmount"]);
            Assert.Equal(returned, head["returnedAmount"]); Assert.Equal(remaining, head["remainingAmount"]); Assert.Equal("return", head["lastEffectKind"]);
        }
        var party = await data.Rows(PaymentVerificationBson.Parties).Find(new BsonDocument("_id", data.ReceiptId)).SingleAsync();
        Assert.Equal(2, party["schemaVersion"].AsInt32); Assert.Equal("supplier_advance_return", party["kind"]);
        Assert.Equal(new BsonArray(new[] { data.SettlementId, second }.Order().Select(x => new BsonInt64(x))), party["settlementIds"]);
        Assert.Equal(result["decisionId"], party["firstReturnId"]);
        await CacheScopeAndLineOwnerAsync(data, result, otherProposal["proposalId"].AsInt64);
        var saved = await data.StateAsync();
        var recovered = await data.Service.LookupAsync(data.Checker, B.DecideOperation, "return-two-approved");
        Assert.Equal("confirmed", recovered["state"]); Assert.Equal(result, recovered["result"]);
        Assert.Equal(result, (await data.Service.ReadAsync(data.Reader, id))["decision"]);
        var wire = JsonSerializer.SerializeToElement(ProcurementAdvanceReturnDtos.Recovery(recovered), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(data.ReceiptId.ToString(System.Globalization.CultureInfo.InvariantCulture), wire.GetProperty("result").GetProperty("receiptId").GetString());
        await DeniedAsync(() => data.Service.DecideAsync(data.Checker, id, 1, ProcurementAdvanceReturnDecision.Approve, preview["binding"].AsString, D.Meta("return-two-approved")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.LookupAsync(data.Maker, B.DecideOperation, "return-two-approved"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Outsider, id));
        Assert.Equal(saved, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());

        var decision = await data.Rows(B.Decisions).Find(new BsonDocument("_id", result["decisionId"])).SingleAsync();
        var checkerGrant = await data.Rows("authorizationGrants").Find(new BsonDocument("_id", data.Checker.UserId)).SingleAsync();
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId),
            new BsonDocument("$set", new BsonDocument("permissions", new BsonArray { Permissions.ProcurementAdvanceReturnDecide, Permissions.JournalPost })));
        try {
            Assert.Equal(result, (await data.Service.LookupAsync(data.Checker, B.DecideOperation, "return-two-approved"))["result"]);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Checker, id));
        } finally { await data.Rows("authorizationGrants").ReplaceOneAsync(new BsonDocument("_id", data.Checker.UserId), checkerGrant); }
        foreach (var (table, filter) in new[] {
            (PaymentVerificationBson.Parties, new BsonDocument("_id", data.ReceiptId)),
            (P.AdvanceHeads, new BsonDocument("_id", data.SettlementId)),
            (B.Lines, new BsonDocument("_id", result["returnLineIds"][0])),
            (B.Decisions, new BsonDocument("_id", result["decisionId"])),
            ("journalEntries", new BsonDocument("_id", result["journalId"])),
            ("accountingAuditEvents", new BsonDocument { { "journalId", result["journalId"] }, { "kind", "journal.posted" } }),
            ("auditEvents", new BsonDocument("_id", decision["auditId"])),
            ("outboxEvents", new BsonDocument("sourceKey", B.SourceKey(decision))),
            ("idempotencyRecords", new BsonDocument { { "actorId", data.Checker.UserId }, { "operation", B.DecideOperation }, { "key", "return-two-approved" } }) }) {
            var row = await data.Rows(table).Find(filter).SingleAsync(); var exact = new BsonDocument("_id", row["_id"]);
            await data.Rows(table).DeleteOneAsync(exact);
            try { var broken = await data.StateAsync(); await DeniedAsync(() => data.Service.LookupAsync(data.Checker, B.DecideOperation, "return-two-approved"), table);
                if (table == B.Decisions) {
                    await DeniedAsync(() => data.Service.ReadAsync(data.Reader, id), "approved decision deleted");
                    await DeniedAsync(() => data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementAdvanceReturnDecision.Reject), "deleted approval cannot become rejection");
                    await DeniedAsync(() => data.Service.DecideAsync(data.Checker, id, 1, ProcurementAdvanceReturnDecision.Reject,
                        rejectPreview["binding"].AsString, D.Meta("return-deleted-approval-reject")), "deleted approval cannot be overwritten");
                }
                Assert.Equal(broken, await data.StateAsync()); }
            finally { await data.Rows(table).InsertOneAsync(row); }
        }
        var missingTogether = new List<(string Table, BsonDocument Row)>();
        foreach (var (table, filter) in new[] { (P.AdvanceHeads, new BsonDocument("_id", data.SettlementId)),
            (B.Lines, new BsonDocument { { "decisionId", result["decisionId"] }, { "settlementId", data.SettlementId } }),
            (B.Decisions, new BsonDocument("_id", result["decisionId"])) }) {
            var row = await data.Rows(table).Find(filter).SingleAsync(); missingTogether.Add((table, row));
            await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", row["_id"]));
        }
        try { var broken = await data.StateAsync();
            await DeniedAsync(() => data.Payables.Service.AdvanceAsync(data.Payables.Reader, data.SettlementId), "retained purpose cannot forget deleted return owners");
            Assert.Equal(broken, await data.StateAsync());
        } finally { foreach (var (table, row) in missingTogether) await data.Rows(table).InsertOneAsync(row); }
        var removedOwners = new List<(string Table, BsonDocument Row)>();
        foreach (var (table, filter) in new[] {
            (P.AdvanceHeads, new BsonDocument("_id", new BsonDocument("$in", new BsonArray { data.SettlementId, second }))),
            (B.Lines, new BsonDocument("decisionId", result["decisionId"])),
            (B.Decisions, new BsonDocument("_id", result["decisionId"])),
            (PaymentVerificationBson.Parties, new BsonDocument("_id", data.ReceiptId)),
            ("journalEntries", new BsonDocument("_id", result["journalId"])) }) {
            var rows = await data.Rows(table).Find(filter).ToListAsync(); Assert.NotEmpty(rows);
            removedOwners.AddRange(rows.Select(row => (table, row))); await data.Rows(table).DeleteManyAsync(filter);
        }
        try {
            Assert.True(await data.Rows(B.Proposals).Find(new BsonDocument("_id", id)).AnyAsync());
            Assert.True(await data.Rows("auditEvents").Find(new BsonDocument("_id", decision["auditId"])).AnyAsync());
            Assert.True(await data.Rows("outboxEvents").Find(new BsonDocument("sourceKey", B.SourceKey(decision))).AnyAsync());
            Assert.True(await data.Rows("idempotencyRecords").Find(new BsonDocument { { "operation", B.DecideOperation }, { "key", "return-two-approved" } }).AnyAsync());
            var broken = await data.StateAsync();
            await DeniedAsync(() => data.Payables.Service.AdvanceAsync(data.Payables.Reader, data.SettlementId), "retained operation evidence forbids reopening deleted return capacity");
            Assert.Equal(broken, await data.StateAsync());
        } finally { foreach (var (table, row) in removedOwners) await data.Rows(table).InsertOneAsync(row); }
        Assert.Equal(saved, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());
        // Later M75 application consumes only its explicit amount from the same v2 head.
        var recognized = await data.Payables.DecideAsync((await data.Payables.ProposeAsync("return-then-recognize"))["proposalId"].AsInt64, "return-then-recognized");
        var subsequent = await data.Payables.ProposeAsync("return-then-apply", data.Payables.Apply(recognized["payableId"].AsInt64, 1000));
        await data.Payables.DecideAsync(subsequent["proposalId"].AsInt64, "return-then-applied");
        var current = await data.Rows(P.AdvanceHeads).Find(new BsonDocument("_id", data.SettlementId)).SingleAsync();
        Assert.Equal(2, current["stateVersion"].AsInt32); Assert.Equal("4000.00", current["returnedAmount"]);
        Assert.Equal("1000.00", current["appliedAmount"]); Assert.Equal("1000.00", current["remainingAmount"]);
        var debt = await data.Rows(P.PayableHeads).Find(new BsonDocument("_id", recognized["payableId"])).SingleAsync();
        Assert.Equal("10250.00", debt["remainingAmount"]);
    }

    [WindowsStorageFact]
    public async Task Shared_remaining_advance_serializes_payable_application_and_return_and_preserves_legacy_snapshots()
    {
        await using var data = await D.CreateAsync(fixture, receiptAmount: 4000);
        var payable = await data.Payables.DecideAsync((await data.Payables.ProposeAsync("return-race-recognize"))["proposalId"].AsInt64, "return-race-recognized");
        var payableId = payable["payableId"].AsInt64;
        var application = await data.Payables.ProposeAsync("return-race-apply", data.Payables.Apply(payableId, 4000));
        var returned = await data.ProposeAsync("return-race-propose", data.Input(4000));
        var appPreview = await data.Payables.Service.PreviewDecisionAsync(data.Payables.Checker, application["proposalId"].AsInt64, ProcurementSupplierPayableDecision.Approve);
        var retPreview = await data.Service.PreviewDecisionAsync(data.Checker, returned["proposalId"].AsInt64, ProcurementAdvanceReturnDecision.Approve);
        var originals = await data.ProtectedAsync(); var journals = await JournalsAsync(data);
        var oldProposals = new BsonArray(await data.Rows(P.Proposals).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        var outcomes = await Task.WhenAll(
            Record.ExceptionAsync(() => data.Payables.Service.DecideAsync(data.Payables.Checker, application["proposalId"].AsInt64, 1,
                ProcurementSupplierPayableDecision.Approve, appPreview["binding"].AsString, ProcurementSupplierPayableTestData.Meta("return-race-applied"))),
            Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, returned["proposalId"].AsInt64, 1,
                ProcurementAdvanceReturnDecision.Approve, retPreview["binding"].AsString, D.Meta("return-race-returned"))));
        Assert.Single(outcomes, error => error is null); Assert.IsType<PersistenceConflictException>(Assert.Single(outcomes, error => error is not null));
        var head = await data.Rows(P.AdvanceHeads).Find(new BsonDocument("_id", data.SettlementId)).SingleAsync();
        Assert.Equal("2000.00", head["remainingAmount"]);
        Assert.Equal(4000m, decimal.Parse(head["appliedAmount"].AsString, System.Globalization.CultureInfo.InvariantCulture) +
            decimal.Parse(head.GetValue("returnedAmount", "0.00").AsString, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(journals.Count + 1, (await JournalsAsync(data)).Count);
        Assert.Equal(oldProposals, new BsonArray(await data.Rows(P.Proposals).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.Equal(originals, await data.ProtectedAsync());
        // The v2 writer must also support a later genuine payable application.
        if (outcomes[1] is null) {
            var next = await data.Payables.ProposeAsync("return-following-application", data.Payables.Apply(payableId, 2000));
            await data.Payables.DecideAsync(next["proposalId"].AsInt64, "return-following-applied");
            head = await data.Rows(P.AdvanceHeads).Find(new BsonDocument("_id", data.SettlementId)).SingleAsync();
            Assert.Equal(2, head["stateVersion"].AsInt32); Assert.Equal("4000.00", head["returnedAmount"]);
            Assert.Equal("2000.00", head["appliedAmount"]); Assert.Equal("0.00", head["remainingAmount"]); Assert.Equal("apply_advance", head["lastEffectKind"]);
            var debt = await data.Rows(P.PayableHeads).Find(new BsonDocument("_id", payableId)).SingleAsync();
            Assert.Equal("9250.00", debt["remainingAmount"]); // Return itself did not reduce the payable.
        } else {
            var legacy = head.DeepClone().AsBsonDocument;
            Assert.False(legacy.Contains("stateVersion"));
            var credit = await data.CreateCreditAsync("RETURN-RACE-REMAINING-CREDIT", 2000);
            var input = data.Input(2000) with { ReceiptId = credit["receiptId"].AsInt64 };
            var next = await data.ProposeAsync("application-following-return", input);
            await data.DecideAsync(next["proposalId"].AsInt64, "application-following-returned");
            head = await data.Rows(P.AdvanceHeads).Find(new BsonDocument("_id", data.SettlementId)).SingleAsync();
            Assert.Equal(2, head["stateVersion"].AsInt32); Assert.Equal("4000.00", head["appliedAmount"]);
            Assert.Equal("2000.00", head["returnedAmount"]); Assert.Equal("0.00", head["remainingAmount"]);
            var originalApplication = await data.Rows(P.Applications).Find(new BsonDocument("settlementId", data.SettlementId)).SingleAsync();
            Assert.False(originalApplication["advanceAfter"].AsBsonDocument.Contains("stateVersion"));
            Assert.Equal(legacy["version"], originalApplication["advanceAfter"]["version"]);
        }
    }

    [WindowsStorageFact]
    public async Task Whole_credit_cannot_mix_genuine_supplier_or_order_sources_or_use_another_approved_book()
    {
        await using var data = await D.CreateAsync(fixture);
        var original = await data.Payables.Service.AdvanceAsync(data.Payables.Reader, data.SettlementId);
        var originalChart = await data.Rows("accountingChartVersions").Find(new BsonDocument("_id", data.Payables.Valuation.Book["chartId"])).SingleAsync();
        var otherOrder = await ProcurementAdvanceReturnAlternateSources.AdvanceAsync(data, "return-other-order", differentSupplier: false);
        var otherSupplier = await ProcurementAdvanceReturnAlternateSources.AdvanceAsync(data, "return-other-supplier", differentSupplier: true);
        Assert.Equal(original["advance"]["supplierId"], otherOrder["advance"]["supplierId"]);
        Assert.NotEqual(original["advance"]["orderId"], otherOrder["advance"]["orderId"]);
        Assert.NotEqual(original["advance"]["supplierId"], otherSupplier["advance"]["supplierId"]);
        Assert.NotEqual(original["advance"]["orderId"], otherSupplier["advance"]["orderId"]);
        foreach (var alternate in new[] { otherOrder, otherSupplier }) {
            var settlementId = alternate["advance"]["settlementId"].AsInt64;
            // Each complete source is independently eligible; only the mixed credit is forbidden.
            var single = await data.Service.PreviewAsync(data.Maker, data.Input() with { Lines = [new(settlementId, new(3000))] });
            Assert.Equal(alternate["advance"]["orderId"], single["source"]["orderId"]);
            Assert.Equal(alternate["advance"]["supplierId"], single["source"]["supplierId"]);
            var before = await data.StateAsync();
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewAsync(data.Maker,
                data.Input() with { Lines = [new(data.SettlementId, new(1500)), new(settlementId, new(1500))] }));
            Assert.Equal("procurement_advance_return_source_mismatch", error.Code);
            Assert.Equal(before, await data.StateAsync());
        }
        var foreignPolicy = await ProcurementAdvanceReturnAlternateSources.ForeignBookPolicyAsync(data);
        Assert.NotEqual(data.Policy["bookId"], foreignPolicy["bookId"]);
        var state = await data.StateAsync(); var sources = await data.PreservedAsync();
        var denied = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Maker,
            data.Input() with { PolicyId = foreignPolicy["_id"].AsInt64 }));
        Assert.Equal("procurement_advance_return_integrity_unavailable", denied.Code);
        Assert.Equal(state, await data.StateAsync()); Assert.Equal(sources, await data.PreservedAsync());
        Assert.Equal(originalChart, await data.Rows("accountingChartVersions").Find(new BsonDocument("_id", originalChart["_id"])).SingleAsync());
        Assert.False(await data.Rows(B.Proposals).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows(P.AdvanceHeads).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows(PaymentVerificationBson.Parties).Find(new BsonDocument("_id", data.ReceiptId)).AnyAsync());
    }

    [WindowsStorageFact]
    public async Task Independent_current_authority_and_policy_are_required_at_the_atomic_final_boundary()
    {
        await using var data = await D.CreateAsync(fixture); var input = data.Input(); var preserved = await data.PreservedAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Reader, input));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Outsider, input));
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(
            data.Maker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, input))).Code);
        var proposal = await data.ProposeAsync("return-guards"); var id = proposal["proposalId"].AsInt64;
        var sourceFilter = new BsonDocument("_id", data.SettlementId);
        var source = await data.Rows(ProcurementSettlementBson.Events).Find(sourceFilter).SingleAsync();
        var beforeBrokenRead = await data.StateAsync();
        Assert.Equal(1L, (await data.Rows(ProcurementSettlementBson.Events).DeleteOneAsync(sourceFilter)).DeletedCount);
        try {
            var brokenSources = await data.ProtectedAsync();
            Assert.Equal("scope_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Outsider, id))).Code);
            Assert.Equal("procurement_settlement_integrity_unavailable", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Reader, id))).Code);
            Assert.Equal(beforeBrokenRead, await data.StateAsync()); Assert.Equal(brokenSources, await data.ProtectedAsync());
        } finally { await data.Rows(ProcurementSettlementBson.Events).InsertOneAsync(source); }
        Assert.Equal(preserved, await data.PreservedAsync());
        await data.Base.AddGrantAsync(data.Maker, 76002, ProcurementRequestTestData.Scope,
            [Permissions.ProcurementAdvanceReturnDecide, Permissions.JournalPost]);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(data.Maker, id, ProcurementAdvanceReturnDecision.Approve));
        foreach (var (table, selected, field, value) in new[] {
            (B.Policies, (BsonValue)data.PolicyId, "active", (BsonValue)false),
            ("authorizationGrants", (BsonValue)data.Maker.UserId, "status", (BsonValue)"revoked"),
            ("authorizationGrants", (BsonValue)data.Checker.UserId, "status", (BsonValue)"revoked"),
            ("personAccountBindings", (BsonValue)data.Maker.UserId, "status", (BsonValue)"revoked") }) {
            var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementAdvanceReturnDecision.Approve);
            var filter = new BsonDocument("_id", selected); var row = await data.Rows(table).Find(filter).SingleAsync();
            await data.Rows(table).UpdateOneAsync(filter, new BsonDocument("$set", new BsonDocument(field, value)));
            try { var before = await data.StateAsync(); await DeniedAsync(() => data.Service.DecideAsync(data.Checker, id, 1,
                ProcurementAdvanceReturnDecision.Approve, preview["binding"].AsString, D.Meta("return-denied-" + table + "-" + selected)), table);
                Assert.Equal(before, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync()); }
            finally { await data.Rows(table).ReplaceOneAsync(filter, row); }
        }
        var review = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementAdvanceReturnDecision.Approve);
        var at = data.Clock.UtcNow; var beforeFinal = await data.StateAsync(); var changed = 0; var request = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(eventData => {
                if (request != 0 || eventData.DatabaseNamespace.DatabaseName != data.Database.DatabaseNamespace.DatabaseName ||
                    eventData.Command.GetValue("insert", BsonNull.Value) != "outboxEvents") return;
                if (eventData.Command["documents"].AsBsonArray.Any(row => row.AsBsonDocument.GetValue("kind", BsonNull.Value) == B.DecideOperation)) request = eventData.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(eventData => {
                if (request != 0 && eventData.RequestId == request && Interlocked.CompareExchange(ref changed, 1, 0) == 0) data.Clock.UtcNow = at.AddMinutes(6);
            });
        };
        try {
            var error = await Record.ExceptionAsync(() => data.NewService(new MongoClient(settings)).DecideAsync(data.Checker, id, 1,
                ProcurementAdvanceReturnDecision.Approve, review["binding"].AsString, D.Meta("return-final-mfa")));
            Assert.Equal(1, changed); Assert.Equal("step_up_required", Assert.IsType<AuthorizationDeniedException>(error).Code);
            Assert.Equal(beforeFinal, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());
        } finally { data.Clock.UtcNow = at; }
        var absent = await data.Service.LookupAsync(data.Checker, B.DecideOperation, "return-final-mfa");
        Assert.Equal("not_confirmed", absent["state"]); Assert.True(absent["result"].IsBsonNull);
        var policy = await data.Rows(B.Policies).Find(new BsonDocument("_id", data.PolicyId)).SingleAsync();
        await data.Rows(B.Policies).UpdateOneAsync(new BsonDocument("_id", data.PolicyId),
            new BsonDocument("$set", new BsonDocument("expiresAt", at.AddSeconds(1).UtcDateTime)));
        try {
            var expiring = await data.ProposeAsync("return-expiring-policy");
            var expiryPreview = await data.Service.PreviewDecisionAsync(data.Checker, expiring["proposalId"].AsInt64, ProcurementAdvanceReturnDecision.Approve);
            var beforeExpiry = await data.StateAsync(); var deadlineRequest = 0; var crossed = 0;
            var expirySettings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
            expirySettings.ClusterConfigurator = builder => {
                builder.Subscribe<CommandStartedEvent>(eventData => {
                    if (deadlineRequest == 0 && eventData.DatabaseNamespace.DatabaseName == data.Database.DatabaseNamespace.DatabaseName &&
                        eventData.Command.GetValue("insert", BsonNull.Value) == "outboxEvents" &&
                        eventData.Command["documents"].AsBsonArray.Any(row => row.AsBsonDocument.GetValue("kind", BsonNull.Value) == B.DecideOperation)) deadlineRequest = eventData.RequestId;
                });
                builder.Subscribe<CommandSucceededEvent>(eventData => {
                    if (deadlineRequest != 0 && eventData.RequestId == deadlineRequest && Interlocked.CompareExchange(ref crossed, 1, 0) == 0) data.Clock.UtcNow = at.AddSeconds(2);
                });
            };
            var error = await Record.ExceptionAsync(() => data.NewService(new MongoClient(expirySettings)).DecideAsync(data.Checker,
                expiring["proposalId"].AsInt64, 1, ProcurementAdvanceReturnDecision.Approve, expiryPreview["binding"].AsString, D.Meta("return-final-policy")));
            Assert.Equal(1, crossed); Assert.Equal("procurement_advance_return_policy_unavailable", Assert.IsType<PersistenceConflictException>(error).Code);
            Assert.Equal(beforeExpiry, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());
        } finally { data.Clock.UtcNow = at; await data.Rows(B.Policies).ReplaceOneAsync(new BsonDocument("_id", data.PolicyId), policy); }
        var rejected = await data.DecideAsync(id, "return-rejected", ProcurementAdvanceReturnDecision.Reject);
        Assert.Equal("rejected", rejected["status"]); Assert.Empty(rejected["returnLineIds"].AsBsonArray); Assert.True(rejected["journalId"].IsBsonNull);
        Assert.False(await data.Rows(P.AdvanceHeads).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows(PaymentVerificationBson.Parties).Find(new BsonDocument("_id", data.ReceiptId)).AnyAsync());
        Assert.Equal(preserved, await data.PreservedAsync());
    }

    private static async Task CurrentStorageAdmissionAsync(D data)
    {
        Assert.False(await data.Rows(B.Lines).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows(P.AdvanceHeads).Find(new BsonDocument()).AnyAsync());
        var input = data.Payables.Recognize();
        var preview = await data.Payables.Service.PreviewAsync(data.Payables.Maker, input);
        var payableState = await data.Payables.StateAsync(); var returnState = await data.StateAsync(); var sources = await data.ProtectedAsync();
        async Task DeniesWithoutEffects(string fault) {
            await DeniedAsync(() => data.Payables.Service.AdvanceAsync(data.Payables.Reader, data.SettlementId), fault + ": current advance");
            await DeniedAsync(() => data.Payables.Service.PreviewAsync(data.Payables.Maker, input), fault + ": proposal preview");
            await DeniedAsync(() => data.Payables.Service.ProposeAsync(data.Payables.Maker, input, preview["binding"].AsString,
                ProcurementSupplierPayableTestData.Meta("missing-return-storage-" + fault)), fault + ": command admission");
            Assert.Equal(payableState, await data.Payables.StateAsync()); Assert.Equal(returnState, await data.StateAsync());
            Assert.Equal(sources, await data.ProtectedAsync());
        }
        async Task<BsonDocument> Definition(string table) => await (await data.Database.ListCollectionsAsync(
            new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
        var definition = await Definition(B.Lines);
        var indexes = (await (await data.Rows(B.Lines).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToArray();
        var retainedName = B.Lines + "_retained_storage_probe";
        Assert.DoesNotContain(retainedName, await (await data.Database.ListCollectionNamesAsync()).ToListAsync());
        await data.Database.RenameCollectionAsync(B.Lines, retainedName);
        try { await DeniesWithoutEffects("collection"); }
        finally { await data.Database.RenameCollectionAsync(retainedName, B.Lines); }
        Assert.Equal(definition, await Definition(B.Lines));

        var key = ProcurementAdvanceReturnSchemas.Keys(B.Lines)[0];
        var index = Assert.Single(indexes, row => row["key"].Equals(key));
        Assert.True(index["unique"].AsBoolean);
        await data.Rows(B.Lines).Indexes.DropOneAsync(index["name"].AsString);
        try { await DeniesWithoutEffects("index"); }
        finally { await data.Rows(B.Lines).Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(key,
            new CreateIndexOptions { Name = index["name"].AsString, Unique = true })); }
        Assert.Equal(indexes, (await (await data.Rows(B.Lines).Indexes.ListAsync()).ToListAsync()).OrderBy(row => row["name"].AsString).ToArray());

        var purpose = await Definition(PaymentVerificationBson.Parties);
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", PaymentVerificationBson.Parties },
            { "validator", PaymentVerificationSchemas.Validator(PaymentVerificationBson.Parties) } });
        try { await DeniesWithoutEffects("purpose-validator"); }
        finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", PaymentVerificationBson.Parties },
            { "validator", purpose["options"]["validator"] } }); }
        Assert.Equal(purpose, await Definition(PaymentVerificationBson.Parties));
        var advance = await data.Payables.Service.AdvanceAsync(data.Payables.Reader, data.SettlementId);
        Assert.Equal("6000.00", advance["capacity"]["remainingAmount"]);
        Assert.False(await data.Rows(P.AdvanceHeads).Find(new BsonDocument()).AnyAsync());
    }

    private static async Task CacheScopeAndLineOwnerAsync(D data, BsonDocument result, long otherProposalId)
    {
        var lines = await data.Rows(B.Lines).Find(new BsonDocument("decisionId", result["decisionId"]))
            .Sort(new BsonDocument("settlementId", 1)).ToListAsync();
        Assert.Equal(2, lines.Count); Assert.NotEqual(result["proposalId"].AsInt64, otherProposalId);
        using var tx = await data.Database.Client.StartSessionAsync();
        var options = new TransactionOptions(readConcern: ReadConcern.Snapshot, writeConcern: WriteConcern.WMajority);
        tx.StartTransaction(options);
        var transactionNumber = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        var cache = new ProcurementAdvanceReturnRecords.OwnerProofCache(tx);
        try {
            var firstSource = await data.Payables.Settlements.ReadPayableSourceAsync(data.Database, tx, lines[0]["settlementId"].AsInt64, default);
            await ProcurementAdvanceReturnRecords.ProveLineAsync(data.Database, tx, lines[0], firstSource, data.Payables.Settlements, default, cache);
            Assert.True(cache.TryGet(tx, result["decisionId"].AsInt64, out var proof));
            Assert.Equal(result["proposalId"], proof.Proposal["_id"]);
            using (var other = await data.Database.Client.StartSessionAsync()) {
                other.StartTransaction(options);
                try {
                    Assert.Throws<AuthorizationDeniedException>(() => { cache.TryGet(other, result["decisionId"].AsInt64, out _); });
                } finally { await other.AbortTransactionAsync(); }
            }
            var secondSource = await data.Payables.Settlements.ReadPayableSourceAsync(data.Database, tx, lines[1]["settlementId"].AsInt64, default);
            var changed = lines[1].DeepClone().AsBsonDocument;
            changed["proposalId"] = otherProposalId; changed["snapshotHash"] = B.Hash(changed);
            Assert.Equal(1, (await data.Rows(B.Lines).ReplaceOneAsync(tx, new BsonDocument("_id", changed["_id"]), changed)).ModifiedCount);
            Assert.True(cache.TryGet(tx, result["decisionId"].AsInt64, out _));
            var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => ProcurementAdvanceReturnRecords.ProveLineAsync(
                data.Database, tx, changed, secondSource, data.Payables.Settlements, default, cache));
            Assert.Equal("procurement_advance_return_integrity_unavailable", error.Code);
        } finally { await tx.AbortTransactionAsync(); }
        tx.StartTransaction(options);
        try {
            Assert.NotEqual(transactionNumber, tx.WrappedCoreSession.CurrentTransaction.TransactionNumber);
            Assert.Throws<AuthorizationDeniedException>(() => { cache.TryGet(tx, result["decisionId"].AsInt64, out _); });
        } finally { await tx.AbortTransactionAsync(); }
        Assert.Equal(lines, await data.Rows(B.Lines).Find(new BsonDocument("decisionId", result["decisionId"]))
            .Sort(new BsonDocument("settlementId", 1)).ToListAsync());
    }

    private static async Task RollbackAsync(D data, long id, string binding)
    {
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "outboxEvents") })).SingleAsync();
        var validator = definition["options"]["validator"].DeepClone(); var before = await data.StateAsync();
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" },
            { "validator", new BsonDocument("$and", new BsonArray { validator, new BsonDocument("kind", new BsonDocument("$ne", B.DecideOperation)) }) } });
        try { Assert.IsAssignableFrom<MongoException>(await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1,
            ProcurementAdvanceReturnDecision.Approve, binding, D.Meta("return-outbox-rollback")))); Assert.Equal(before, await data.StateAsync()); }
        finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" }, { "validator", validator } }); }
    }
    private static async Task<BsonArray> JournalsAsync(D data) => new(await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
    private static async Task DeniedAsync(Func<Task> action, string? context = null)
    {
        var error = await Record.ExceptionAsync(action);
        Assert.True(error is DomainValidationException or AuthorizationDeniedException or PersistenceConflictException, context + ": " + (error?.ToString() ?? "Unexpected success"));
    }
}
