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
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Organizations;
using B = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;
using D = UnitToko.Tests.Procurement.ProcurementQualityAcceptanceTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ProcurementQualityAcceptanceTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Explicit_mixed_inspection_subset_is_independently_accepted_once_and_all_stock_remains_held()
    {
        await using var data = await D.CreateAsync(fixture); var input = data.Input(); var preserved = await data.PreservedAsync();
        Assert.True(data.Stock.InspectionLine.DamagedPackages + data.Stock.InspectionLine.OpenedPackages > data.Stock.InspectionLine.ReceivedPackages);
        var proposal = await data.ProposeAsync("quality-subset", input); var id = proposal["proposalId"].AsInt64;
        Assert.False(await data.Rows(B.ReceiptHeads).Find(new BsonDocument()).AnyAsync());
        Assert.False(await data.Rows(B.Allocations).Find(new BsonDocument()).AnyAsync());
        Assert.Equal(preserved, await data.PreservedAsync());
        var observation = data.Observation(input);
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementQualityAcceptanceDecision.Approve, observation);
        await RollbackAsync(data, id, observation, preview["binding"].AsString);
        foreach (var wrong in new[] { observation with { AcceptedPackages = 2 }, observation with { LotReference = "OTHER-LOT" },
            observation with { PutawayReference = "OTHER-BAY" }, observation with { SegregationReference = "OTHER-SUBSET" },
            observation with { ExpiresOn = observation.ExpiresOn!.Value.AddDays(1) }, observation with { ObservedAt = data.Clock.UtcNow.AddSeconds(1) } })
            await DeniedAsync(() => data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementQualityAcceptanceDecision.Approve, wrong));
        await DeniedAsync(() => data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementQualityAcceptanceDecision.Approve, null));
        var result = await data.Service.DecideAsync(data.Checker, id, 1, ProcurementQualityAcceptanceDecision.Approve, observation,
            preview["binding"].AsString, D.Meta("quality-approved"));
        Assert.Equal("approved_held", result["status"]); Assert.False(result["allocationId"].IsBsonNull);
        var head = await data.Rows(B.ReceiptHeads).Find(new BsonDocument("_id", data.ReceiptId)).SingleAsync();
        Assert.Equal(1L, head["acceptedPackages"].AsInt64); Assert.Equal(2L, head["remainingPackages"].AsInt64);
        Assert.Equal(result["allocationId"], head["lastAllocationId"]);
        var saved = await data.StateAsync();
        Assert.Equal(result, (await data.Service.LookupAsync(data.Checker, B.DecideOperation, "quality-approved"))["result"]);
        var read = Assert.IsType<BsonDocument>(await data.Service.ReadAsync(data.Reader, id));
        Assert.Equal(result, read["decision"]);
        var actualRead = JsonSerializer.SerializeToElement(ProcurementQualityAcceptanceDtos.Read(read), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var actualChoices = JsonSerializer.SerializeToElement(ProcurementQualityAcceptanceDtos.Receipts(await data.Service.ReceiptsAsync(data.Reader, ProcurementRequestTestData.Location)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        PhysicalOnly(actualRead); PhysicalOnly(actualChoices);
        Assert.Equal(input.LotReference, actualRead.GetProperty("proposal").GetProperty("input").GetProperty("lotReference").GetString());
        Assert.Equal(input.AcceptedPackages, actualRead.GetProperty("proposal").GetProperty("input").GetProperty("acceptedPackages").GetInt32());
        await DeniedAsync(() => data.Service.DecideAsync(data.Checker, id, 1, ProcurementQualityAcceptanceDecision.Approve, observation,
            preview["binding"].AsString, D.Meta("quality-approved")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.LookupAsync(data.Maker, B.DecideOperation, "quality-approved"));
        Assert.Equal(saved, await data.StateAsync()); Assert.Equal(preserved, await data.PreservedAsync());
        // Recovery needs the original action grant only; it must not become a read-gated resend.
        var grant = await data.Rows("authorizationGrants").Find(new BsonDocument("_id", data.Checker.UserId)).SingleAsync();
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId),
            new BsonDocument("$set", new BsonDocument("permissions", new BsonArray { Permissions.ProcurementQualityAcceptanceDecide })));
        try {
            Assert.Equal(result, (await data.Service.LookupAsync(data.Checker, B.DecideOperation, "quality-approved"))["result"]);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Checker, id));
        } finally { await data.Rows("authorizationGrants").ReplaceOneAsync(new BsonDocument("_id", data.Checker.UserId), grant); }

        await BrokenOwnersAsync(data, result);
        var remainder = data.Input(packages: 2); var pending = await data.ProposeAsync("quality-remaining", remainder);
        var rejected = await data.DecideAsync(pending["proposalId"].AsInt64, "quality-rejected", remainder, ProcurementQualityAcceptanceDecision.Reject);
        Assert.Equal("rejected", rejected["status"]); Assert.True(rejected["allocationId"].IsBsonNull);
        Assert.Equal(head["acceptedPackages"], (await data.Rows(B.ReceiptHeads).Find(new BsonDocument("_id", data.ReceiptId)).SingleAsync())["acceptedPackages"]);
        Assert.Equal(preserved, await data.PreservedAsync());
        // Current freshness/expiry does not erase an immutable prior physical finding.
        var at = data.Clock.UtcNow; data.Clock.UtcNow = at.AddDays(40);
        try { Assert.Equal(result, Assert.IsType<BsonDocument>(await data.Service.ReadAsync(data.Reader, id))["decision"]); }
        finally { data.Clock.UtcNow = at; }
    }

    [WindowsStorageFact]
    public async Task Two_independent_pending_proposals_race_for_one_receipt_capacity_without_releasing_stock()
    {
        await using var data = await D.CreateAsync(fixture); var input = data.Input(packages: 2);
        var one = await data.ProposeAsync("quality-race-one", input); var two = await data.ProposeAsync("quality-race-two", input);
        var observation = data.Observation(input);
        var first = await data.Service.PreviewDecisionAsync(data.Checker, one["proposalId"].AsInt64, ProcurementQualityAcceptanceDecision.Approve, observation);
        var second = await data.Service.PreviewDecisionAsync(data.Checker, two["proposalId"].AsInt64, ProcurementQualityAcceptanceDecision.Approve, observation);
        var preserved = await data.PreservedAsync();
        var outcomes = await Task.WhenAll(Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, one["proposalId"].AsInt64, 1,
            ProcurementQualityAcceptanceDecision.Approve, observation, first["binding"].AsString, D.Meta("quality-race-first"))),
            Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, two["proposalId"].AsInt64, 1,
                ProcurementQualityAcceptanceDecision.Approve, observation, second["binding"].AsString, D.Meta("quality-race-second"))));
        Assert.Single(outcomes, error => error is null); Assert.IsType<PersistenceConflictException>(Assert.Single(outcomes, error => error is not null));
        Assert.Equal(1L, await data.Rows(B.Allocations).CountDocumentsAsync(new BsonDocument()));
        var head = await data.Rows(B.ReceiptHeads).Find(new BsonDocument("_id", data.ReceiptId)).SingleAsync();
        Assert.Equal(2L, head["acceptedPackages"].AsInt64); Assert.Equal(1L, head["remainingPackages"].AsInt64);
        await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, input)); Assert.Equal(preserved, await data.PreservedAsync());
    }

    [WindowsStorageFact]
    public async Task Current_independent_people_authority_and_final_MFA_policy_expiry_are_atomic()
    {
        await using var data = await D.CreateAsync(fixture); var input = data.Input();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Reader, input));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Outsider, input));
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(
            data.Maker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, input))).Code);
        await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, input with { AcceptedCondition = data.AcceptedCondition == "sealed_undamaged" ? "intact_as_defined" : "sealed_undamaged" }));
        var id = (await data.ProposeAsync("quality-authority", input))["proposalId"].AsInt64; var observation = data.Observation(input);
        var preserved = await data.PreservedAsync();
        await data.Base.AddGrantAsync(data.Maker, 77002, ProcurementRequestTestData.Scope, [Permissions.ProcurementQualityAcceptanceDecide]);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(data.Maker, id, ProcurementQualityAcceptanceDecision.Approve, observation));
        var alias = await data.Base.AddActorAsync(778, ProcurementRequestTestData.Scope,
            Permissions.ProcurementRead, Permissions.StockRead, Permissions.ProcurementQualityAcceptanceRead, Permissions.ProcurementQualityAcceptanceDecide);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, alias.UserId);
        await OrganizationAddressTests.MergePeopleAsync(data.Inspection.Receiving.Procurement.Organizations, data.Maker.UserId, alias.UserId);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(alias, id, ProcurementQualityAcceptanceDecision.Approve, observation));
        foreach (var (table, selected, field, value) in new[] {
            (B.Policies, (BsonValue)data.PolicyId, "active", (BsonValue)false),
            ("authorizationGrants", (BsonValue)data.Maker.UserId, "status", (BsonValue)"revoked"),
            ("authorizationGrants", (BsonValue)data.Checker.UserId, "status", (BsonValue)"revoked"),
            ("personAccountBindings", (BsonValue)data.Maker.UserId, "status", (BsonValue)"revoked") }) {
            var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementQualityAcceptanceDecision.Approve, observation);
            var filter = new BsonDocument("_id", selected); var original = await data.Rows(table).Find(filter).SingleAsync();
            await data.Rows(table).UpdateOneAsync(filter, new BsonDocument("$set", new BsonDocument(field, value)));
            try { var before = await data.StateAsync(); await DeniedAsync(() => data.Service.DecideAsync(data.Checker, id, 1,
                ProcurementQualityAcceptanceDecision.Approve, observation, preview["binding"].AsString, D.Meta("quality-revoked-" + table + "-" + selected)));
                Assert.Equal(before, await data.StateAsync()); }
            finally { await data.Rows(table).ReplaceOneAsync(filter, original); }
        }
        var at = data.Clock.UtcNow;
        var review = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementQualityAcceptanceDecision.Approve, observation);
        await FinalClockAsync(data, id, observation, review["binding"].AsString, at.AddMinutes(6), "quality-final-mfa", "step_up_required");
        var originalPolicy = data.Policy;
        await data.InstallPolicyAsync(originalPolicy["allowNonExpiring"].AsBoolean, TimeSpan.FromSeconds(1));
        var expiringInput = data.Input(); var expiringObservation = data.Observation(expiringInput);
        data.Policy = originalPolicy; // Restore only the helper's selection; both approved policy rows remain immutable.
        Assert.NotEqual(input.PolicyId, expiringInput.PolicyId);
        var expiringId = (await data.ProposeAsync("quality-expiring", expiringInput))["proposalId"].AsInt64;
        var expiringReview = await data.Service.PreviewDecisionAsync(data.Checker, expiringId, ProcurementQualityAcceptanceDecision.Approve, expiringObservation);
        await FinalClockAsync(data, expiringId, expiringObservation, expiringReview["binding"].AsString, at.AddSeconds(2), "quality-final-policy", "procurement_quality_acceptance_policy_unavailable");
        var afterDecision = at.AddSeconds(2); data.Clock.UtcNow = afterDecision;
        var rejected = await data.DecideAsync(expiringId, "quality-expired-policy-rejected", expiringInput, ProcurementQualityAcceptanceDecision.Reject);
        Assert.Equal("rejected", rejected["status"]); Assert.False(await data.Rows(B.Allocations).Find(new BsonDocument()).AnyAsync());
        Assert.Equal(rejected, (await data.Service.ReadAsync(data.Reader, expiringId))!["decision"]);
        data.Clock.UtcNow = at.AddMinutes(16);
        try {
            var freshChecker = data.Checker with { AuthenticatedAt = data.Clock.UtcNow };
            await DeniedAsync(() => data.Service.PreviewDecisionAsync(freshChecker, id, ProcurementQualityAcceptanceDecision.Approve, data.Observation(input)));
            var rejection = await data.Service.PreviewDecisionAsync(freshChecker, id, ProcurementQualityAcceptanceDecision.Reject, null);
            Assert.Equal("rejected", (await data.Service.DecideAsync(freshChecker, id, 1, ProcurementQualityAcceptanceDecision.Reject, null,
                rejection["binding"].AsString, D.Meta("quality-stale-observation-rejected")))["status"]);
            Assert.False(await data.Rows(B.ReceiptHeads).Find(new BsonDocument()).AnyAsync());
        } finally { data.Clock.UtcNow = at.AddMinutes(16); }
        Assert.Equal(rejected, (await data.Service.ReadAsync(data.Reader, expiringId))!["decision"]);
        Assert.Equal(preserved, await data.PreservedAsync());
    }

    [WindowsStorageFact]
    public async Task Unknown_expiry_requires_explicit_policy_and_agreed_nonexpiring_evidence()
    {
        await using var data = await D.CreateAsync(fixture, nonExpiring: true, allowNonExpiring: false); var input = data.Input();
        var before = await data.StateAsync(); await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, input)); Assert.Equal(before, await data.StateAsync());
        // A separate explicit local approval permits the observation; neither old policy nor receipt is rewritten.
        await data.InstallPolicyAsync(allowNonExpiring: true);
        input = data.Input(); var id = (await data.ProposeAsync("quality-nonexpiring", input))["proposalId"].AsInt64;
        var observation = data.Observation(input);
        await DeniedAsync(() => data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementQualityAcceptanceDecision.Approve,
            observation with { NonExpiringReason = "Different assessment" }));
        var preserved = await data.PreservedAsync(); var accepted = await data.DecideAsync(id, "quality-nonexpiring-approved", input);
        Assert.Equal("approved_held", accepted["status"]); Assert.Equal(preserved, await data.PreservedAsync());
    }

    [WindowsStorageFact]
    public async Task Actual_dated_receipt_cannot_be_accepted_on_expiry_day_or_relabeled_nonexpiring()
    {
        await using var data = await D.CreateAsync(fixture, expiryDays: 0); var input = data.Input(); var before = await data.StateAsync();
        await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, input));
        await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, input with { ExpiresOn = null, NonExpiringReason = "Cannot override known receipt expiry" }));
        await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, input with { ExpiresOn = input.ExpiresOn!.Value.AddDays(30) }));
        Assert.Equal(before, await data.StateAsync()); Assert.False(await data.Rows(B.Proposals).Find(new BsonDocument()).AnyAsync());
    }

    private static async Task BrokenOwnersAsync(D data, BsonDocument result) {
        var id = result["proposalId"].AsInt64;
        var decision = await data.Rows(B.Decisions).Find(new BsonDocument("_id", result["decisionId"])).SingleAsync();
        foreach (var (table, filter) in new[] { (B.ReceiptHeads, new BsonDocument("_id", data.ReceiptId)), (B.Allocations, new BsonDocument("_id", result["allocationId"])),
            (B.Decisions, new BsonDocument("_id", result["decisionId"])), (B.Proposals, new BsonDocument("_id", id)),
            ("auditEvents", new BsonDocument("_id", decision["auditId"])), ("outboxEvents", new BsonDocument("sourceKey", B.SourceKey(decision))),
            ("idempotencyRecords", new BsonDocument { { "actorId", data.Checker.UserId }, { "operation", B.DecideOperation }, { "key", "quality-approved" } }) }) {
            var row = await data.Rows(table).Find(filter).SingleAsync(); await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", row["_id"]));
            try { var broken = await data.StateAsync(); await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, data.Input()), table);
                if (table == B.Proposals) {
                    Assert.Null(await data.Service.ReadAsync(data.Reader, id)); // The endpoint maps missing resources to an unavailable response.
                    await DeniedAsync(() => data.Service.LookupAsync(data.Checker, B.DecideOperation, "quality-approved"), table);
                } else await DeniedAsync(() => data.Service.ReadAsync(data.Reader, id), table);
                Assert.Equal(broken, await data.StateAsync()); }
            finally { await data.Rows(table).InsertOneAsync(row); }
        }
        var removed = new List<(string Table, BsonDocument Row)>();
        foreach (var (table, selected) in new[] { (B.ReceiptHeads, (BsonValue)data.ReceiptId), (B.Allocations, result["allocationId"]), (B.Decisions, result["decisionId"]) }) {
            var row = await data.Rows(table).Find(new BsonDocument("_id", selected)).SingleAsync(); removed.Add((table, row)); await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", selected));
        }
        try { var broken = await data.StateAsync(); await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, data.Input()), "retained effects cannot reopen deleted acceptance capacity"); Assert.Equal(broken, await data.StateAsync()); }
        finally { foreach (var (table, row) in removed) await data.Rows(table).InsertOneAsync(row); }
        var allocation = await data.Rows(B.Allocations).Find(new BsonDocument("_id", result["allocationId"])).SingleAsync();
        var wrong = allocation.DeepClone().AsBsonDocument; wrong["receiptHash"] = new string('f', 64); wrong["snapshotHash"] = B.Hash(wrong);
        await data.Rows(B.Allocations).ReplaceOneAsync(new BsonDocument("_id", wrong["_id"]), wrong);
        try { await DeniedAsync(() => data.Service.PreviewAsync(data.Maker, data.Input()), "rehashed foreign receipt link"); }
        finally { await data.Rows(B.Allocations).ReplaceOneAsync(new BsonDocument("_id", allocation["_id"]), allocation); }
        var command = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "actorId", data.Checker.UserId },
            { "operation", B.DecideOperation }, { "key", "quality-approved" } }).SingleAsync();
        var foreignPerson = command.DeepClone().AsBsonDocument;
        foreignPerson["personApproval"]["approval"]["requestedBy"] = data.Reader.UserId;
        await data.Rows("idempotencyRecords").ReplaceOneAsync(new BsonDocument("_id", command["_id"]), foreignPerson);
        try { await DeniedAsync(() => data.Service.LookupAsync(data.Checker, B.DecideOperation, "quality-approved"), "foreign decision participant"); }
        finally { await data.Rows("idempotencyRecords").ReplaceOneAsync(new BsonDocument("_id", command["_id"]), command); }
    }
    private static async Task RollbackAsync(D data, long id, ProcurementQualityAcceptanceObservation observation, string binding) {
        var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "outboxEvents") })).SingleAsync();
        var validator = definition["options"]["validator"].DeepClone(); var before = await data.StateAsync();
        await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" },
            { "validator", new BsonDocument("$and", new BsonArray { validator, new BsonDocument("kind", new BsonDocument("$ne", B.DecideOperation)) }) } });
        try { var error = await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1,
            ProcurementQualityAcceptanceDecision.Approve, observation, binding, D.Meta("quality-outbox-rollback")));
            Assert.True(error is MongoException, $"{(error as AuthorizationDeniedException)?.Code}: {error}"); Assert.Equal(before, await data.StateAsync()); }
        finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", "outboxEvents" }, { "validator", validator } }); }
    }
    private static async Task FinalClockAsync(D data, long id, ProcurementQualityAcceptanceObservation observation, string binding, DateTimeOffset advanced, string key, string expectedCode) {
        var at = data.Clock.UtcNow; var before = await data.StateAsync(); var changed = 0; var request = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(value => {
                if (request != 0 || value.DatabaseNamespace.DatabaseName != data.Database.DatabaseNamespace.DatabaseName || value.Command.GetValue("insert", BsonNull.Value) != "outboxEvents") return;
                if (value.Command["documents"].AsBsonArray.Any(row => row.AsBsonDocument.GetValue("kind", BsonNull.Value) == B.DecideOperation)) request = value.RequestId;
            });
            builder.Subscribe<CommandSucceededEvent>(value => { if (request != 0 && value.RequestId == request && Interlocked.CompareExchange(ref changed, 1, 0) == 0) data.Clock.UtcNow = advanced; });
        };
        try {
            var error = await Record.ExceptionAsync(() => data.NewService(new MongoClient(settings)).DecideAsync(data.Checker, id, 1,
                ProcurementQualityAcceptanceDecision.Approve, observation, binding, D.Meta(key)));
            Assert.Equal(1, changed);
            Assert.Equal(expectedCode, error switch { AuthorizationDeniedException denied => denied.Code, PersistenceConflictException conflict => conflict.Code, _ => error?.ToString() });
            Assert.Equal(before, await data.StateAsync());
        } finally { data.Clock.UtcNow = at; }
        Assert.Equal("not_confirmed", (await data.Service.LookupAsync(data.Checker, B.DecideOperation, key))["state"]);
    }
    private static async Task DeniedAsync(Func<Task> action, string? context = null) {
        var error = await Record.ExceptionAsync(action);
        Assert.True(error is DomainValidationException or AuthorizationDeniedException or PersistenceConflictException, context + ": " + (error?.ToString() ?? "Unexpected success"));
    }
    private static void PhysicalOnly(JsonElement value) {
        if (value.ValueKind == JsonValueKind.Array) { foreach (var item in value.EnumerateArray()) PhysicalOnly(item); return; }
        if (value.ValueKind != JsonValueKind.Object) return;
        foreach (var field in value.EnumerateObject()) {
            Assert.DoesNotContain(field.Name, new[] { "netPricePerPackage", "netAmount", "taxAmount", "supplierChargeAmount", "chargeAmount", "totalAmount", "total",
                "requiredAdvance", "paymentTerms", "sourceAccountReference", "capitalizedAmount", "journalId" });
            PhysicalOnly(field.Value);
        }
    }
}
