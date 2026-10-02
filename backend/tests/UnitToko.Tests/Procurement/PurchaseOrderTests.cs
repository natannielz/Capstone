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
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Organizations;
using D = UnitToko.Tests.Procurement.PurchaseOrderTestData;
using S = UnitToko.Platform.Procurement.ProcurementRequestPurchaseOrderSchemas;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class PurchaseOrderTests(MongoFixture fixture)
{
    [Fact]
    public async Task Commercial_validity_uses_the_Jakarta_business_day_even_before_UTC_midnight()
    {
        var input = new PurchaseOrderInput(new(100, 101, 102, 103), 1, 1, 2, 3, 4, 1, new(2026, 10, 3), "IDR",
            new("quote", "WIB-DATE-CHECK", new(2026, 10, 3), new(2026, 10, 2, 23, 0, 0, TimeSpan.Zero)), null,
            new("on_receipt", "Explicit synthetic pay-on-receipt terms", new Money(0), "on_receipt", null),
            [new(1, 1, "supplier", "000001", "000012", 1, 1, new Money(10000), new Money(1100), new Money(500))]);
        Assert.Equal(input.CommercialReference, PurchaseOrderRules.Copy(input).CommercialReference);
        Assert.Throws<DomainValidationException>(() => PurchaseOrderRules.Copy(input with {
            CommercialReference = input.CommercialReference with { ValidUntil = new(2026, 10, 2, 16, 59, 0, TimeSpan.Zero) } }));
    }

    [Fact]
    public async Task Genuine_request_supplier_and_explicit_commercial_terms_create_one_reviewed_order_and_quantity_commitment_only()
    {
        await using var data = await D.CreateAsync(fixture); var input = data.Input();
        var request = await data.Rows(ProcurementRequestTestData.Requests).Find(new BsonDocument()).SingleAsync();
        var preview = await data.Service.PreviewAsync(data.Maker, input);
        Assert.Equal("30000.00", preview["totals"]["net"].AsString); Assert.Equal("3300.00", preview["totals"]["tax"].AsString);
        Assert.Equal("700.00", preview["totals"]["supplierCharges"].AsString); Assert.Equal("34000.00", preview["totals"]["total"].AsString);
        Assert.Equal("Synthetic approved logo specification", preview["lines"][0]["specification"].AsString);
        Assert.Equal("Dus", preview["lines"][0]["packageLabel"].AsString); Assert.Equal(12, preview["lines"][0]["baseUnitsPerPackage"].ToInt32());
        Assert.Equal(36, preview["lines"][0]["baseUnits"].ToInt64()); Assert.Equal(6, preview["capacity"][0]["availablePackages"].AsInt32);
        var proposed = await data.Service.ProposeAsync(data.Maker, input, preview["binding"].AsString, D.Meta("po-original"));
        Assert.False(await data.Rows(S.Commitments).Find(new BsonDocument()).AnyAsync()); Assert.False(await data.Rows(S.Allocations).Find(new BsonDocument()).AnyAsync());
        var decisionPreview = await data.Service.PreviewDecisionAsync(data.Checker, proposed["proposalId"].AsInt64, PurchaseOrderDecision.Approve);
        var approved = await data.Service.DecideAsync(data.Checker, proposed["proposalId"].AsInt64, 1, PurchaseOrderDecision.Approve, decisionPreview["binding"].AsString, D.Meta("po-original-approved"));
        Assert.Equal("approved", approved["status"]); var state = await data.StateAsync();
        Assert.Equal(proposed, await data.Service.ProposeAsync(data.Maker, input, preview["binding"].AsString, D.Meta("po-original")));
        Assert.Equal(approved, await data.Service.DecideAsync(data.Checker, proposed["proposalId"].AsInt64, 1, PurchaseOrderDecision.Approve, decisionPreview["binding"].AsString, D.Meta("po-original-approved")));
        Assert.Equal(approved, await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "po-original-approved"));
        Assert.Equal(state, await data.StateAsync()); Assert.Equal(request, await data.Rows(ProcurementRequestTestData.Requests).Find(new BsonDocument()).SingleAsync());
        var allocation = await data.Rows(S.Allocations).Find(new BsonDocument()).SingleAsync(); var head = await data.Rows(S.Commitments).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(approved["orderId"], allocation["orderId"]); Assert.Equal(approved["decisionId"], allocation["approvalId"]);
        Assert.Equal(3, head["committedPackages"].AsInt32); Assert.Equal(6, head["requestedPackages"].AsInt32); Assert.Equal(1L, head["version"].AsInt64);
        Assert.Equal(1L, allocation["sequence"].AsInt64); Assert.True(allocation["previousAllocationId"].IsBsonNull);
        Assert.Equal(3, (await data.Service.PreviewAsync(data.Maker, input))["capacity"][0]["availablePackages"].AsInt32);
        Assert.Equal("approved", (await data.Service.ReadAsync(data.Reader, proposed["proposalId"].AsInt64))["state"]);
        await data.NoFinancialOrReceiptEffectsAsync();
    }

    [Fact]
    public async Task Omi_order_requires_explicit_external_reference_and_approves_only_against_the_real_approved_omi_request()
    {
        await using var data = await D.CreateAsync(fixture);
        var source = data.Procurement.Input(ProcurementRoute.OmiRegular, ProcurementRequestKind.StockNeed) with {
            Lines = [ProcurementRequestTestData.Line(data.Procurement.Sku, 6)] };
        var request = await data.Procurement.SubmitAsync("po-omi-request", source);
        var requestDecision = await data.Procurement.DecideAsync(request, "po-omi-request-approved");
        var input = data.Input() with { RequestId = request["requestId"].AsInt64, RequestDecisionId = requestDecision["decisionId"].AsInt64 };
        var original = await data.Rows(ProcurementRequestTestData.Requests).Find(new BsonDocument("_id", input.RequestId)).SingleAsync();
        var before = await data.StateAsync();
        Assert.Equal("invalid_purchase_order", (await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.PreviewAsync(data.Maker, input))).Code);
        Assert.Equal(before, await data.StateAsync());
        const string externalReference = "SYNTHETIC-OMI-ORDER-0001";
        var proposed = await data.ProposeAsync("po-omi-original", input with { ExternalOrderReference = externalReference });
        var approved = await data.DecideAsync(proposed, "po-omi-approved");
        var view = await data.Service.ReadAsync(data.Reader, proposed["proposalId"].AsInt64);
        Assert.Equal("approved", view["state"]); Assert.Equal("omi_regular", view["route"]); Assert.Equal(externalReference, view["input"]["externalOrderReference"]);
        Assert.Equal(input.RequestId, view["request"]["id"].AsInt64); Assert.Equal(input.RequestDecisionId, view["request"]["decisionId"].AsInt64);
        var allocation = await data.Rows(S.Allocations).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(input.RequestId, allocation["requestId"].AsInt64); Assert.Equal(approved["orderId"], allocation["orderId"]); Assert.Equal(3, allocation["packages"].AsInt32);
        Assert.Equal(original, await data.Rows(ProcurementRequestTestData.Requests).Find(new BsonDocument("_id", input.RequestId)).SingleAsync());
        await data.NoFinancialOrReceiptEffectsAsync();
    }

    [Fact]
    public async Task Parallel_approvals_cannot_overcommit_and_exact_remaining_packages_form_a_linked_immutable_allocation()
    {
        await using var data = await D.CreateAsync(fixture); var proposals = new[] { await data.ProposeAsync("po-race-a", data.Input(4)), await data.ProposeAsync("po-race-b", data.Input(4)) };
        var previews = new List<BsonDocument>();
        foreach (var row in proposals) previews.Add(await data.Service.PreviewDecisionAsync(data.Checker, row["proposalId"].AsInt64, PurchaseOrderDecision.Approve));
        var outcomes = await Task.WhenAll(proposals.Select(async (row, index) => await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, row["proposalId"].AsInt64,
            1, PurchaseOrderDecision.Approve, previews[index]["binding"].AsString, D.Meta("po-race-approve-" + index)))));
        Assert.Single(outcomes, value => value is null); Assert.IsType<PersistenceConflictException>(Assert.Single(outcomes, value => value is not null));
        Assert.Equal(1, await data.Rows(D.Orders).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(4, (await data.Rows(S.Commitments).Find(new BsonDocument()).SingleAsync())["committedPackages"].AsInt32);
        var loser = Array.FindIndex(outcomes, value => value is not null); await data.DecideAsync(proposals[loser], "po-overage-rejected", PurchaseOrderDecision.Reject);
        var remainder = await data.ProposeAsync("po-remainder", data.Input(2)); await data.DecideAsync(remainder, "po-remainder-approved");
        var allocations = await data.Rows(S.Allocations).Find(new BsonDocument()).Sort(new BsonDocument("sequence", 1)).ToListAsync(); Assert.Equal(2, allocations.Count);
        Assert.Equal(allocations[0]["_id"], allocations[1]["previousAllocationId"]); Assert.Equal(allocations[0]["snapshotHash"], allocations[1]["previousHash"]);
        Assert.Equal(6, (await data.Rows(S.Commitments).Find(new BsonDocument()).SingleAsync())["committedPackages"].AsInt32);
        Assert.Equal("procurement_request_capacity_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewAsync(data.Maker, data.Input(1)))).Code);
        await data.NoFinancialOrReceiptEffectsAsync();
    }

    [Fact]
    public async Task Foreign_scope_wrong_sources_and_other_actor_recovery_never_expose_or_reuse_order_facts()
    {
        await using var data = await D.CreateAsync(fixture); var proposed = await data.ProposeAsync("po-scope"); var before = await data.StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Outsider, proposed["proposalId"].AsInt64));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Outsider, data.Input()));
        Assert.Null(await data.Service.LookupAsync(data.Procurement.Maker, D.Prefix + "propose", "po-scope"));
        Assert.Null(await data.Service.LookupAsync(data.Maker, D.Prefix + "propose", "other-key"));
        foreach (var input in new[] { data.Input() with { RequestDecisionId = data.RequestDecision["decisionId"].AsInt64 + 999 },
            data.Input() with { SupplierVersionId = data.Supplier["versionId"].AsInt64 + 999 },
            data.Input() with { Lines = [data.Line() with { ExternalItemCode = "12" }] },
            data.Input() with { Lines = [data.Line() with { DefinitionId = data.Procurement.SecondSku.CurrentDefinitionId!.Value.Value }] } }) {
            var error = await Record.ExceptionAsync(() => data.Service.PreviewAsync(data.Maker, input));
            Assert.True(error is AuthorizationDeniedException or PersistenceConflictException, error?.ToString());
        }
        Assert.Equal(before, await data.StateAsync()); await data.NoFinancialOrReceiptEffectsAsync();
    }

    [Theory]
    [InlineData("catalog")] [InlineData("policy")] [InlineData("quote_expiry")]
    public async Task Changed_catalog_trust_or_expired_commercial_document_blocks_approval_but_preserves_independent_rejection(string fault)
    {
        await using var data = await D.CreateAsync(fixture); var proposal = await data.ProposeAsync("po-stale"); var id = proposal["proposalId"].AsInt64;
        var original = await data.Rows(D.Proposals).Find(new BsonDocument("_id", id)).SingleAsync();
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, PurchaseOrderDecision.Approve);
        if (fault == "catalog") await data.Procurement.Catalog.ReviseDefinitionAsync(data.Procurement.Author,
            new(data.Procurement.Sku.Id, data.Procurement.Sku.Version, CatalogAuthoringTestData.Definition(12, "Revised procurement carton")), D.Meta("po-source-revision"));
        if (fault == "policy") await data.Rows(SupplierTestData.Policies).UpdateOneAsync(new BsonDocument("_id", SupplierTestData.PolicyId), Builders<BsonDocument>.Update.Set("active", false));
        if (fault == "quote_expiry") { data.Clock.UtcNow = data.Clock.UtcNow.AddDays(2); data.Checker = data.Checker with { AuthenticatedAt = data.Clock.UtcNow }; }
        var before = await data.StateAsync(); var error = await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1, PurchaseOrderDecision.Approve, preview["binding"].AsString, D.Meta("po-stale-approve")));
        Assert.True(error is AuthorizationDeniedException or DomainValidationException or PersistenceConflictException, error?.ToString()); Assert.Equal(before, await data.StateAsync());
        await data.DecideAsync(proposal, "po-stale-rejected", PurchaseOrderDecision.Reject);
        Assert.Equal("rejected", (await data.Service.ReadAsync(data.Reader, id))["state"]); Assert.Equal(original, await data.Rows(D.Proposals).Find(new BsonDocument("_id", id)).SingleAsync());
        Assert.False(await data.Rows(D.Orders).Find(new BsonDocument()).AnyAsync()); Assert.False(await data.Rows(S.Allocations).Find(new BsonDocument()).AnyAsync());
        await data.NoFinancialOrReceiptEffectsAsync();
    }

    [Fact]
    public async Task Fresh_factor_and_distinct_canonical_people_are_required_even_for_distinct_reviewer_accounts()
    {
        await using var data = await D.CreateAsync(fixture); var proposal = await data.ProposeAsync("po-person"); var id = proposal["proposalId"].AsInt64;
        foreach (var actor in new[] { data.Maker, data.Reader, data.Checker with { Assurance = FactorAssurance.SingleFactor }, data.Checker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) } })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(actor, id, PurchaseOrderDecision.Approve));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Maker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, data.Input()));
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, PurchaseOrderDecision.Approve);
        await OrganizationAddressTests.MergePeopleAsync(data.Procurement.Organizations, data.Maker.UserId, data.Checker.UserId); var before = await data.StateAsync();
        var denied = await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1, PurchaseOrderDecision.Approve, preview["binding"].AsString, D.Meta("po-same-person")));
        Assert.True(denied is AuthorizationDeniedException or DomainValidationException, denied?.ToString()); Assert.Equal(before, await data.StateAsync());
    }

    [Fact]
    public async Task Allocation_or_required_effect_failure_rolls_back_the_order_approval_capacity_and_receipt_together()
    {
        await using var data = await D.CreateAsync(fixture); var proposal = await data.ProposeAsync("po-atomic"); var id = proposal["proposalId"].AsInt64;
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, PurchaseOrderDecision.Approve); var before = await data.StateAsync();
        foreach (var (table, field, denied) in new[] { (S.Allocations, "requestId", (BsonValue)data.Request["requestId"]), (S.Commitments, "requestId", data.Request["requestId"]),
            (D.Decisions, "decision", (BsonValue)"approve"), ("auditEvents", "kind", (BsonValue)(D.Prefix + "approve")),
            ("outboxEvents", "kind", (BsonValue)(D.Prefix + "approve")), ("idempotencyRecords", "operation", (BsonValue)(D.Prefix + "approve")) }) {
            var collection = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
            var validator = collection["options"]["validator"].AsBsonDocument.DeepClone().AsBsonDocument;
            try {
                await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", new BsonDocument("$and", new BsonArray { validator, new BsonDocument(field, new BsonDocument("$ne", denied)) }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
                Assert.NotNull(await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1, PurchaseOrderDecision.Approve, preview["binding"].AsString, D.Meta("po-atomic-approved"))));
                Assert.Equal(before, await data.StateAsync());
            } finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } }); }
        }
        await data.DecideAsync(proposal, "po-atomic-approved"); Assert.Equal(1, await data.Rows(S.Allocations).CountDocumentsAsync(new BsonDocument())); await data.NoFinancialOrReceiptEffectsAsync();
    }

    [Theory]
    [InlineData("head")] [InlineData("allocation")] [InlineData("both")] [InlineData("owner_receipt")]
    public async Task Missing_commitment_or_owner_proof_is_never_treated_as_zero_usage_or_valid_recovery(string fault)
    {
        await using var data = await D.CreateAsync(fixture); var proposal = await data.ProposeAsync("po-proof"); var approved = await data.DecideAsync(proposal, "po-proof-approved");
        if (fault is "head" or "both") Assert.Equal(1, (await data.Rows(S.Commitments).DeleteOneAsync(new BsonDocument())).DeletedCount);
        if (fault is "allocation" or "both") Assert.Equal(1, (await data.Rows(S.Allocations).DeleteOneAsync(new BsonDocument())).DeletedCount);
        if (fault == "owner_receipt") Assert.Equal(1, (await data.Rows("idempotencyRecords").DeleteOneAsync(new BsonDocument { { "operation", D.Prefix + "approve" }, { "key", "po-proof-approved" } })).DeletedCount);
        var before = await data.StateAsync();
        foreach (var action in new Func<Task>[] { async () => await data.Service.ReadAsync(data.Reader, proposal["proposalId"].AsInt64),
            async () => await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "po-proof-approved"), async () => await data.Service.PreviewAsync(data.Maker, data.Input(1)) })
            await Assert.ThrowsAsync<PersistenceConflictException>(action);
        Assert.Equal(before, await data.StateAsync()); Assert.Equal(approved["orderId"], (await data.Rows(D.Orders).Find(new BsonDocument()).SingleAsync())["_id"]);
    }

    [Fact]
    public async Task Explicit_commercial_inputs_do_not_silently_round_normalize_or_default_amounts_and_terms()
    {
        await using var data = await D.CreateAsync(fixture); var input = data.Input(); var before = await data.StateAsync();
        foreach (var invalid in new[] { input with { Currency = "USD" }, input with { RequestVersion = 2 }, input with { Lines = [] }, input with { Lines = [data.Line(), data.Line()] },
            input with { Lines = [data.Line() with { Packages = 0 }] }, input with { Lines = [data.Line() with { SourceAccountReference = " 000001" }] },
            input with { CommercialReference = input.CommercialReference with { Reference = " padded " } },
            input with { CommercialReference = input.CommercialReference with { ValidUntil = input.CommercialReference.ValidUntil.AddTicks(1) } },
            input with { CommercialReference = input.CommercialReference with { ValidUntil = input.CommercialReference.ValidUntil.ToOffset(TimeSpan.FromHours(7)) } },
            input with { PaymentTerms = input.PaymentTerms with { RequiredAdvance = new Money(1) } },
            input with { PaymentTerms = input.PaymentTerms with { DueDate = null } },
            input with { PaymentTerms = new("full_before_dispatch", "Synthetic full prepayment", new Money(30000), "before_dispatch", null) },
            input with { PaymentTerms = new("deposit", "Synthetic deposit", new Money(34000), "on_receipt", null) } })
            Assert.Equal("invalid_purchase_order", (await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.PreviewAsync(data.Maker, invalid))).Code);
        Assert.Equal(before, await data.StateAsync());
    }

    [Fact]
    public async Task Request_capture_is_bound_to_actual_actor_authority_database_transaction_and_unchanged_snapshot()
    {
        await using var data = await D.CreateAsync(fixture); var owner = data.Procurement.Service; ProcurementPurchaseOrderCapture? captured = null;
        await new AuthorizationStore(data.Database, data.Unit, data.Clock).ReadAsync(data.Maker, async (authority, tx, ct) => {
            captured = await owner.CapturePurchaseOrderAsync(data.Database, data.Maker, authority, tx, data.Request["requestId"].AsInt64, 1,
                data.RequestDecision["decisionId"].AsInt64, Permissions.ProcurementPrepare, [data.Line()], ct);
            captured.RequireContext(owner, data.Maker, authority, tx);
            Assert.Throws<PersistenceConflictException>(() => captured.RequireContext(owner, data.Checker, authority, tx));
            Assert.Throws<PersistenceConflictException>(() => captured.RequireContext(new object(), data.Maker, authority, tx));
            captured.Capacity[0]["availablePackages"] = 999;
            Assert.Throws<PersistenceConflictException>(captured.RequireCurrent); return true;
        }, CancellationToken.None);
        Assert.NotNull(captured); Assert.Throws<ObjectDisposedException>(captured.RequireCurrent); await data.NoFinancialOrReceiptEffectsAsync();
    }

    [Theory]
    [InlineData("policy")] [InlineData("grant")] [InlineData("session")] [InlineData("mfa")]
    public async Task Concurrent_trust_grant_session_or_factor_changes_cannot_escape_real_order_preview_fences(string change)
    {
        await using var data = await D.CreateAsync(fixture);
        await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", data.Maker.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture-connection", "fixture-policy"), SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var proof = new ProviderIdentityProof(data.Maker.ProviderIssuer, data.Maker.ProviderSubject, "po-race@example.invalid", true, "fixture-connection", data.Clock.UtcNow, data.Clock.UtcNow, "otp", "fixture-policy");
        var issued = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(data.Maker.UserId, 1, data.Maker.GrantVersion, data.Maker.ProviderIssuer, data.Maker.ProviderSubject), proof, "synthetic-po-race", ct));
        data.Maker = data.Maker with { Session = issued.Session.Reference };
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var requestId = 0; var observed = 0; var reads = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => { if (command.Command.GetValue("find", BsonNull.Value) == "skus" && Interlocked.Increment(ref reads) == 1) requestId = command.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(command => { if (requestId != 0 && command.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Purchase-order preview race did not complete");
            } });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName); var unit = new MongoUnitOfWork(client);
        var catalog = new CatalogAuthoringService(database, unit, new(database), data.Clock);
        var service = new PurchaseOrderService(database, unit, new(database), data.Clock, catalog, new(database, unit, new(database), data.Clock, catalog), new(database, unit, new(database), data.Clock, catalog));
        var input = data.Input(); var pending = Task.Run(() => service.PreviewAsync(data.Maker, input));
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (change == "policy") await data.Rows(SupplierTestData.Policies).UpdateOneAsync(new BsonDocument("_id", SupplierTestData.PolicyId), Builders<BsonDocument>.Update.Set("active", false));
            if (change == "grant") await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Maker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (change == "session") await sessions.RevokeAsync(issued.OpaqueToken, "Synthetic purchase-order revocation");
            if (change == "mfa") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
        } finally { release.Set(); }
        var error = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, observed); Assert.True(error is AuthorizationDeniedException or DomainValidationException or PersistenceConflictException, error?.ToString());
        Assert.False(await data.Rows(D.Proposals).Find(new BsonDocument()).AnyAsync()); await data.NoFinancialOrReceiptEffectsAsync();
    }
}
