using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Organizations;
using D = UnitToko.Tests.Procurement.ProcurementFundingTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class ProcurementFundingTests(MongoFixture fixture)
{
    [Fact]
    public async Task Genuine_fully_committed_order_creates_only_independent_partial_authorization_and_replays_original_keys()
    {
        await using var data = await D.CreateAsync(fixture); var input = data.Input();
        var source = await data.Orders.StateAsync(); var preview = await data.Service.PreviewAsync(data.Maker, input);
        Assert.Equal("64000.00", preview["order"]["total"]); Assert.Equal("20000.00", preview["capacity"]["requiredAdvance"]);
        Assert.Equal("0.00", preview["capacity"]["authorized"]); Assert.Equal("20000.00", preview["capacity"]["remaining"]);
        Assert.Equal(6, preview["order"]["lines"][0]["packages"].AsInt32); Assert.Equal("Synthetic approved logo specification", preview["order"]["lines"][0]["specification"]);
        var request = await data.Service.RequestAsync(data.Maker, input, preview["binding"].AsString, D.Meta("funding-original"));
        Assert.False(await data.Rows(D.Heads).Find(new BsonDocument()).AnyAsync()); Assert.False(await data.Rows(D.Allocations).Find(new BsonDocument()).AnyAsync());
        var decisionPreview = await data.Service.PreviewDecisionAsync(data.Checker, request["requestId"].AsInt64, ProcurementFundingDecision.Approve);
        var approved = await data.Service.DecideAsync(data.Checker, request["requestId"].AsInt64, 1, ProcurementFundingDecision.Approve, decisionPreview["binding"].AsString, D.Meta("funding-approved"));
        Assert.Equal("approved", approved["status"]); Assert.Equal("10000.00", approved["authorizedAmount"]);
        var state = await data.StateAsync();
        Assert.Equal(request, await data.Service.RequestAsync(data.Maker, input, preview["binding"].AsString, D.Meta("funding-original")));
        Assert.Equal(approved, await data.Service.DecideAsync(data.Checker, request["requestId"].AsInt64, 1, ProcurementFundingDecision.Approve, decisionPreview["binding"].AsString, D.Meta("funding-approved")));
        Assert.Equal(approved, await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "funding-approved"));
        Assert.Equal(state, await data.StateAsync()); Assert.Equal(source, await data.Orders.StateAsync());
        var choices = await data.Service.OrdersAsync(data.Reader, ProcurementRequestTestData.Location);
        Assert.Equal("10000.00", choices["items"][0]["capacity"]["remaining"]); Assert.Equal("10000.00", choices["items"][0]["capacity"]["authorized"]);
        Assert.Equal("approved", (await data.Service.ReadAsync(data.Reader, request["requestId"].AsInt64))["state"]);
        Assert.Equal(approved["authorizationId"], (await data.Rows(D.Allocations).Find(new BsonDocument()).SingleAsync())["_id"]);
        await data.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [Fact]
    public async Task Historical_admitted_order_remains_fundable_after_quote_expiry_and_catalog_revision_but_not_current_trust_withdrawal()
    {
        await using var data = await D.CreateAsync(fixture);
        await data.Procurement.Catalog.ReviseDefinitionAsync(data.Procurement.Author,
            new(data.Procurement.Sku.Id, data.Procurement.Sku.Version, CatalogAuthoringTestData.Definition(12, "Later revised carton")), D.Meta("funding-later-catalog"));
        data.Clock.UtcNow = data.Clock.UtcNow.AddDays(2); data.Maker = data.Maker with { AuthenticatedAt = data.Clock.UtcNow }; data.Checker = data.Checker with { AuthenticatedAt = data.Clock.UtcNow };
        var preview = await data.Service.PreviewAsync(data.Maker, data.Input());
        Assert.Equal("Dus", preview["order"]["lines"][0]["packageLabel"]);
        var request = await data.RequestAsync("funding-historical-order"); await data.DecideAsync(request, "funding-historical-approved");
        await data.Rows(SupplierTestData.Policies).UpdateOneAsync(new BsonDocument("_id", SupplierTestData.PolicyId), Builders<BsonDocument>.Update.Set("active", false));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewAsync(data.Maker, data.Input(1)));
        Assert.Equal("approved", (await data.Service.ReadAsync(data.Reader, request["requestId"].AsInt64))["state"]);
        Assert.NotNull(await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "funding-historical-approved"));
        await data.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [Fact]
    public async Task Parallel_approvals_cannot_exceed_advance_and_rejection_does_not_release_another_authorization()
    {
        await using var data = await D.CreateAsync(fixture);
        var requests = new[] { await data.RequestAsync("funding-race-a", data.Input(15000)), await data.RequestAsync("funding-race-b", data.Input(15000)) };
        var previews = new List<BsonDocument>(); foreach (var request in requests) previews.Add(await data.Service.PreviewDecisionAsync(data.Checker, request["requestId"].AsInt64, ProcurementFundingDecision.Approve));
        var outcomes = await Task.WhenAll(requests.Select(async (request, index) => await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, request["requestId"].AsInt64, 1,
            ProcurementFundingDecision.Approve, previews[index]["binding"].AsString, D.Meta("funding-race-approval-" + index)))));
        Assert.Single(outcomes, error => error is null); Assert.IsType<PersistenceConflictException>(Assert.Single(outcomes, error => error is not null));
        var loser = Array.FindIndex(outcomes, error => error is not null); await data.DecideAsync(requests[loser], "funding-race-rejected", ProcurementFundingDecision.Reject);
        var choices = await data.Service.OrdersAsync(data.Reader, ProcurementRequestTestData.Location); Assert.Equal("5000.00", choices["items"][0]["capacity"]["remaining"]);
        var remainder = await data.RequestAsync("funding-remainder", data.Input(5000)); await data.DecideAsync(remainder, "funding-remainder-approved");
        Assert.Equal(2, await data.Rows(D.Allocations).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal("20000.00", (await data.Rows(D.Heads).Find(new BsonDocument()).SingleAsync())["authorized"]);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewAsync(data.Maker, data.Input(1)));
        await data.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [Fact]
    public async Task Changed_capacity_invalidates_old_binding_even_when_requested_amount_still_fits()
    {
        await using var data = await D.CreateAsync(fixture); var first = await data.RequestAsync("funding-small-a", data.Input(5000)); var second = await data.RequestAsync("funding-small-b", data.Input(5000));
        var stale = await data.Service.PreviewDecisionAsync(data.Checker, second["requestId"].AsInt64, ProcurementFundingDecision.Approve);
        await data.DecideAsync(first, "funding-small-a-approved"); var before = await data.StateAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.DecideAsync(data.Checker, second["requestId"].AsInt64, 1, ProcurementFundingDecision.Approve, stale["binding"].AsString, D.Meta("funding-small-b-approved")));
        Assert.Equal(before, await data.StateAsync()); await data.DecideAsync(second, "funding-small-b-approved");
        Assert.Equal("10000.00", (await data.Rows(D.Heads).Find(new BsonDocument()).SingleAsync())["authorized"]);
    }

    [Fact]
    public async Task Funding_read_is_private_exact_scoped_and_does_not_borrow_procurement_authority_or_another_actors_recovery()
    {
        await using var data = await D.CreateAsync(fixture); var request = await data.RequestAsync("funding-private"); var before = await data.StateAsync();
        foreach (var actor in new[] { data.Outsider, data.Orders.Reader }) await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(actor, request["requestId"].AsInt64));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Outsider, data.Input()));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Reader, data.Input()));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.LookupAsync(data.Checker, D.Prefix + "request", "funding-private"));
        var anotherRequester = await data.Base.AddActorAsync(644, ProcurementRequestTestData.Scope, Permissions.FundingRead, Permissions.FundingRequest);
        Assert.Null(await data.Service.LookupAsync(anotherRequester, D.Prefix + "request", "funding-private"));
        Assert.Equal(before, await data.StateAsync());
    }

    [Fact]
    public async Task A_second_login_for_the_requester_person_cannot_approve_and_expired_factor_cannot_mutate()
    {
        await using var data = await D.CreateAsync(fixture); var request = await data.RequestAsync("funding-independent"); var before = await data.StateAsync();
        var old = data.Checker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) };
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(old, request["requestId"].AsInt64, ProcurementFundingDecision.Approve))).Code);
        Assert.Equal("step_up_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Maker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, data.Input()))).Code);
        await OrganizationAddressTests.MergePeopleAsync(data.Procurement.Organizations, data.Maker.UserId, data.Checker.UserId);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(data.Checker, request["requestId"].AsInt64, ProcurementFundingDecision.Approve));
        Assert.Equal(before, await data.StateAsync());
    }

    [Theory]
    [InlineData("withdrawn")] [InlineData("expired")] [InlineData("revision")] [InlineData("route")] [InlineData("budget")] [InlineData("limit")]
    public async Task Policy_changes_block_approval_but_preserve_immutable_request_and_independent_rejection(string fault)
    {
        await using var data = await D.CreateAsync(fixture); var request = await data.RequestAsync("funding-policy");
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, request["requestId"].AsInt64, ProcurementFundingDecision.Approve);
        var update = fault switch {
            "withdrawn" => Builders<BsonDocument>.Update.Set("active", false), "expired" => Builders<BsonDocument>.Update.Set("expiresAt", data.Clock.UtcNow.AddSeconds(-1).UtcDateTime),
            "revision" => Builders<BsonDocument>.Update.Set("revision", 2L), "route" => Builders<BsonDocument>.Update.Set("eligibleRoutes", new BsonArray { "smart" }),
            "budget" => Builders<BsonDocument>.Update.Set("requiresAdditionalBudgetSource", true), _ => Builders<BsonDocument>.Update.Set("requiresAdditionalLimitSource", true) };
        await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", D.PolicyId), update); var before = await data.StateAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.DecideAsync(data.Checker, request["requestId"].AsInt64, 1, ProcurementFundingDecision.Approve, preview["binding"].AsString, D.Meta("funding-policy-approve")));
        Assert.Equal(before, await data.StateAsync()); Assert.Equal("requested", (await data.Service.ReadAsync(data.Reader, request["requestId"].AsInt64))["state"]);
        Assert.Equal("rejected", (await data.DecideAsync(request, "funding-policy-rejected", ProcurementFundingDecision.Reject))["status"]);
        Assert.False(await data.Rows(D.Allocations).Find(new BsonDocument()).AnyAsync());
    }

    [Fact]
    public async Task Explicit_non_installment_policy_requires_the_whole_original_advance_and_zero_advance_is_ineligible()
    {
        await using (var data = await D.CreateAsync(fixture)) {
            await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", D.PolicyId), Builders<BsonDocument>.Update.Set("allowInstallments", false));
            await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewAsync(data.Maker, data.Input(10000)));
            var request = await data.RequestAsync("funding-whole", data.Input(20000)); await data.DecideAsync(request, "funding-whole-approved");
            await data.Orders.NoFinancialOrReceiptEffectsAsync();
        }
        await using (var data = await D.CreateAsync(fixture, positiveAdvance: false)) {
            Assert.Empty((await data.Service.OrdersAsync(data.Reader, ProcurementRequestTestData.Location))["items"].AsBsonArray);
            await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewAsync(data.Maker, data.Input(1)));
            Assert.False(await data.Rows(D.Requests).Find(new BsonDocument()).AnyAsync());
        }
    }

    [Theory]
    [InlineData("head")] [InlineData("allocation")] [InlineData("both")] [InlineData("decision")] [InlineData("receipt")] [InlineData("audit")] [InlineData("outbox")]
    public async Task Missing_capacity_or_owner_history_is_never_zero_or_successful_recovery(string fault)
    {
        await using var data = await D.CreateAsync(fixture); var request = await data.RequestAsync("funding-proof"); await data.DecideAsync(request, "funding-proof-approved");
        if (fault is "head" or "both") Assert.Equal(1, (await data.Rows(D.Heads).DeleteOneAsync(new BsonDocument())).DeletedCount);
        if (fault is "allocation" or "both") Assert.Equal(1, (await data.Rows(D.Allocations).DeleteOneAsync(new BsonDocument())).DeletedCount);
        if (fault == "decision") Assert.Equal(1, (await data.Rows(D.Decisions).DeleteOneAsync(new BsonDocument())).DeletedCount);
        if (fault is "receipt" or "audit" or "outbox") {
            var table = fault == "receipt" ? "idempotencyRecords" : fault == "audit" ? "auditEvents" : "outboxEvents"; var field = fault == "receipt" ? "operation" : "kind";
            Assert.Equal(1, (await data.Rows(table).DeleteOneAsync(new BsonDocument(field, D.Prefix + "approve"))).DeletedCount);
        }
        var before = await data.StateAsync();
        foreach (var action in new Func<Task>[] { async () => await data.Service.PreviewAsync(data.Maker, data.Input(1)),
            async () => await data.Service.ReadAsync(data.Reader, request["requestId"].AsInt64), async () => await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "funding-proof-approved") })
            await Assert.ThrowsAsync<PersistenceConflictException>(action);
        Assert.Equal(before, await data.StateAsync());
    }

    [Fact]
    public async Task Each_authorization_capacity_and_effect_write_rolls_back_together_on_storage_failure()
    {
        await using var data = await D.CreateAsync(fixture); var request = await data.RequestAsync("funding-atomic"); var id = request["requestId"].AsInt64;
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, ProcurementFundingDecision.Approve); var before = await data.StateAsync();
        foreach (var (table, field, denied) in new[] { (D.Decisions, "requestId", (BsonValue)id), (D.Heads, "orderId", data.Order["orderId"]), (D.Allocations, "orderId", data.Order["orderId"]),
            ("auditEvents", "kind", (BsonValue)(D.Prefix + "approve")), ("outboxEvents", "kind", (BsonValue)(D.Prefix + "approve")), ("idempotencyRecords", "operation", (BsonValue)(D.Prefix + "approve")) }) {
            var collection = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
            var validator = collection["options"]["validator"].AsBsonDocument.DeepClone().AsBsonDocument;
            try {
                await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", new BsonDocument("$and", new BsonArray { validator, new BsonDocument(field, new BsonDocument("$ne", denied)) }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
                Assert.NotNull(await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1, ProcurementFundingDecision.Approve, preview["binding"].AsString, D.Meta("funding-atomic-approved"))));
                Assert.Equal(before, await data.StateAsync());
            } finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } }); }
        }
        await data.DecideAsync(request, "funding-atomic-approved"); Assert.Equal(1, await data.Rows(D.Allocations).CountDocumentsAsync(new BsonDocument())); await data.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [Fact]
    public async Task Input_requires_explicit_positive_advance_and_order_policy_versions_with_ordered_dates()
    {
        await using var data = await D.CreateAsync(fixture); var input = data.Input(); var before = await data.StateAsync();
        foreach (var invalid in new[] { input with { Amount = new Money(0) }, input with { OrderVersion = 2 }, input with { OrderApprovalId = 0 }, input with { PolicyRevision = 0 },
            input with { BusinessDate = DateOnly.MinValue }, input with { NeededBy = input.BusinessDate.AddDays(-1) } })
            Assert.Equal("invalid_procurement_funding", (await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.PreviewAsync(data.Maker, invalid))).Code);
        Assert.Equal(before, await data.StateAsync());
    }

    [Theory]
    [InlineData("policy")] [InlineData("grant")] [InlineData("session")] [InlineData("mfa")] [InlineData("person")]
    public async Task Concurrent_policy_grant_session_factor_or_person_changes_cannot_escape_active_approval_preview_fences(string change)
    {
        await using var data = await D.CreateAsync(fixture); var request = await data.RequestAsync("funding-active-race");
        await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture-connection", "fixture-policy"), SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var proof = new ProviderIdentityProof(data.Checker.ProviderIssuer, data.Checker.ProviderSubject, "funding-race@example.invalid", true, "fixture-connection", data.Clock.UtcNow, data.Clock.UtcNow, "otp", "fixture-policy");
        var issued = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(data.Checker.UserId, 1, data.Checker.GrantVersion, data.Checker.ProviderIssuer, data.Checker.ProviderSubject), proof, "synthetic-funding-race", ct));
        data.Checker = data.Checker with { Session = issued.Session.Reference };
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var requestId = 0; var observed = 0; var reads = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => { if (command.Command.GetValue("find", BsonNull.Value) == D.Requests && Interlocked.Increment(ref reads) == 1) requestId = command.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(command => { if (requestId != 0 && command.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Funding approval preview race did not complete");
            } });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName); var unit = new MongoUnitOfWork(client);
        var catalog = new CatalogAuthoringService(database, unit, new(database), data.Clock);
        var orders = new PurchaseOrderService(database, unit, new(database), data.Clock, catalog, new(database, unit, new(database), data.Clock, catalog), new(database, unit, new(database), data.Clock, catalog));
        var service = new ProcurementFundingService(database, unit, new(database), data.Clock, catalog, orders);
        var pending = Task.Run(() => service.PreviewDecisionAsync(data.Checker, request["requestId"].AsInt64, ProcurementFundingDecision.Approve));
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (change == "policy") await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", D.PolicyId), Builders<BsonDocument>.Update.Set("active", false));
            if (change == "grant") await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (change == "session") await sessions.RevokeAsync(issued.OpaqueToken, "Synthetic funding revocation");
            if (change == "mfa") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
            if (change == "person") await OrganizationAddressTests.MergePeopleAsync(data.Procurement.Organizations, data.Maker.UserId, data.Checker.UserId);
        } finally { release.Set(); }
        var error = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(45)));
        Assert.Equal(1, observed); Assert.True(error is AuthorizationDeniedException or PersistenceConflictException, error?.ToString());
        Assert.False(await data.Rows(D.Decisions).Find(new BsonDocument()).AnyAsync()); Assert.False(await data.Rows(D.Allocations).Find(new BsonDocument()).AnyAsync());
        await data.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [Theory]
    [InlineData("allocation")] [InlineData("approval_receipt")]
    public async Task Missing_original_order_quantity_or_approval_proof_never_admits_a_funding_request(string fault)
    {
        await using var data = await D.CreateAsync(fixture);
        if (fault == "allocation") Assert.Equal(1, (await data.Rows(ProcurementRequestPurchaseOrderSchemas.Allocations).DeleteOneAsync(new BsonDocument())).DeletedCount);
        else Assert.Equal(1, (await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "operation", PurchaseOrderTestData.Prefix + "approve" }, { "key", "funding-fixture-po-approved" } })).DeletedCount);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewAsync(data.Maker, data.Input()));
        Assert.False(await data.Rows(D.Requests).Find(new BsonDocument()).AnyAsync());
    }

    [Fact]
    public async Task Approved_order_capture_cannot_be_reused_by_foreign_actor_owner_or_after_its_caller_transaction()
    {
        await using var data = await D.CreateAsync(fixture); var owner = data.Orders.Service; PurchaseOrderFundingCapture? capture = null;
        await new AuthorizationStore(data.Database, data.Unit, data.Clock).ReadAsync(data.Maker, async (authority, tx, ct) => {
            capture = await owner.CaptureFundingAsync(data.Database, data.Maker, authority, tx, data.Order["orderId"].AsInt64, 1, data.Order["decisionId"].AsInt64, Permissions.FundingRequest, ct);
            capture.RequireContext(owner, data.Maker, authority, tx);
            Assert.Throws<PersistenceConflictException>(() => capture.RequireContext(owner, data.Checker, authority, tx));
            Assert.Throws<PersistenceConflictException>(() => capture.RequireContext(new object(), data.Maker, authority, tx));
            capture.Source["order"]["version"] = 2L; Assert.Throws<PersistenceConflictException>(capture.RequireCurrent); return true;
        }, CancellationToken.None);
        Assert.NotNull(capture); Assert.Throws<ObjectDisposedException>(capture.RequireCurrent);
    }
}
