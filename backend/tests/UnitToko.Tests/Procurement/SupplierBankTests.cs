using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Organizations;
using D = UnitToko.Tests.Procurement.SupplierBankTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class SupplierBankTests(MongoFixture fixture)
{
    [Fact]
    public async Task Independent_exact_bank_versions_preserve_leading_zeroes_supplier_purchase_order_and_funding_history()
    {
        await using var funding = await ProcurementFundingTestData.CreateAsync(fixture);
        var data = await D.PrepareAsync(funding.Orders.Suppliers, funding.Orders.Supplier);
        var fundingRequest = await funding.RequestAsync("bank-retained-funding"); var fundingApproval = await funding.DecideAsync(fundingRequest, "bank-retained-funding-approved");
        var supplierBefore = await data.Suppliers.StateAsync(); var ordersBefore = await funding.Orders.StateAsync(); var fundingBefore = await funding.StateAsync();
        var input = data.Input; var preview = await data.Service.PreviewAsync(data.Maker, input);
        var proposal = await data.Service.ProposeAsync(data.Maker, input, preview["binding"].AsString, D.Meta("bank-first"));
        Assert.Equal(proposal, await data.Service.ProposeAsync(data.Maker, input, preview["binding"].AsString, D.Meta("bank-first")));
        Assert.Equal(proposal, await data.Service.LookupAsync(data.Maker, D.Prefix + "propose", "bank-first"));
        var original = await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync();
        var observationPreview = await data.Service.PreviewObservationAsync(data.Checker, proposal["proposalId"].AsInt64, D.Details, D.SourceReference);
        var observed = await data.Service.ObserveAsync(data.Checker, proposal["proposalId"].AsInt64, 1, D.Details, D.SourceReference, observationPreview["binding"].AsString, true, D.Meta("bank-first-observed"));
        Assert.Equal(observed, await data.Service.ObserveAsync(data.Checker, proposal["proposalId"].AsInt64, 1, D.Details, D.SourceReference, observationPreview["binding"].AsString, true, D.Meta("bank-first-observed")));
        var decisionPreview = await data.Service.PreviewDecisionAsync(data.Checker, proposal["proposalId"].AsInt64, SupplierBankDecision.Approve, observed["recordId"].AsInt64);
        var approved = await data.Service.DecideAsync(data.Checker, proposal["proposalId"].AsInt64, 1, SupplierBankDecision.Approve, observed["recordId"].AsInt64, decisionPreview["binding"].AsString, D.Meta("bank-first-approved"));
        Assert.Equal(approved, await data.Service.DecideAsync(data.Checker, proposal["proposalId"].AsInt64, 1, SupplierBankDecision.Approve, observed["recordId"].AsInt64, decisionPreview["binding"].AsString, D.Meta("bank-first-approved")));
        Assert.Equal(original, await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync());
        var firstVersion = await data.Rows(D.Versions).Find(new BsonDocument()).SingleAsync();
        var revisedDetails = D.Details with { AccountNumber = "000087654321" };
        var revised = await data.ProposeAsync("bank-second", input with { BaseVersion = 1, Details = revisedDetails });
        var secondObservation = await data.ObserveAsync(revised, "bank-second-observed", revisedDetails);
        var second = await data.DecideAsync(revised, "bank-second-approved", secondObservation["recordId"].AsInt64);
        Assert.Equal(2L, second["version"].AsInt64); Assert.Equal(firstVersion, await data.Rows(D.Versions).Find(new BsonDocument("_id", firstVersion["_id"])).SingleAsync());
        var consumer = data.Service;
        await Assert.ThrowsAsync<PersistenceConflictException>(() => new AuthorizationStore(data.Database, data.Unit, data.Clock).ReadAsync(data.DetailsReader,
            async (authority, tx, ct) => await consumer.CaptureBeneficiaryAsync(data.Database, data.DetailsReader, authority, tx, input.SupplierId, approved["versionId"].AsInt64, 1, ct), CancellationToken.None));
        var history = await data.Service.ReadAsync(data.DetailsReader, proposal["proposalId"].AsInt64);
        Assert.Equal("000012345678", history["input"]["details"]["accountNumber"]); Assert.Equal("approved", history["state"]);
        Assert.Equal(approved, await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "bank-first-approved"));
        Assert.Equal(supplierBefore, await data.Suppliers.StateAsync()); Assert.Equal(ordersBefore, await funding.Orders.StateAsync()); Assert.Equal(fundingBefore, await funding.StateAsync());
        Assert.Equal("approved", (await data.Suppliers.Service.ReadAsync(data.Suppliers.Checker, SupplierAccess.Approve, data.Supplier["proposalId"].AsInt64))["state"]);
        Assert.Equal("approved", (await funding.Orders.Service.ReadAsync(funding.Orders.Reader, funding.Proposal["proposalId"].AsInt64))["state"]);
        Assert.Equal(fundingApproval, await funding.Service.LookupAsync(funding.Checker, ProcurementFundingTestData.Prefix + "approve", "bank-retained-funding-approved"));
        var fresh = await funding.Service.PreviewAsync(funding.Maker, funding.Input(1000)); Assert.Equal("10000.00", fresh["capacity"]["remaining"]);
        await data.NoFinancialEffectsAsync(); await funding.Orders.NoFinancialOrReceiptEffectsAsync();
    }

    [Fact]
    public async Task Masked_shop_read_never_implies_details_or_mutation_and_warehouse_or_foreign_grants_are_not_promoted()
    {
        await using var data = await D.CreateAsync(fixture); var proposal = await data.ProposeAsync("bank-private");
        var observed = await data.ObserveAsync(proposal, "bank-private-observed"); await data.DecideAsync(proposal, "bank-private-approved", observed["recordId"].AsInt64);
        var current = await data.Service.CurrentAsync(data.Reader, data.Input.SupplierId);
        Assert.True(current["eligible"].AsBoolean); Assert.Equal(ReceivingAccountPolicy.Mask(D.Details), current["details"]["maskedAccount"]);
        var list = await data.Service.ListAsync(data.Reader, SupplierTestData.Owner, null);
        var narrow = current.ToJson() + list.ToJson() + (await data.Service.SuppliersAsync(data.Reader, SupplierTestData.Owner, null)).ToJson();
        foreach (var secret in new[] { D.Details.AccountNumber, D.TrustedChannel, D.SourceReference, "SYNTHETIC-BANK-AUTHORIZATION", "SYNTHETIC-BANK-POLICY-APPROVAL", "\"holderName\"", "\"accountNumber\"" }) Assert.DoesNotContain(secret, narrow, StringComparison.Ordinal);
        foreach (var actor in new[] { data.Reader, data.Outsider, data.Warehouse, data.Procurement.Reader }) {
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(actor, proposal["proposalId"].AsInt64));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(actor, data.Input));
        }
        foreach (var actor in new[] { data.Outsider, data.Warehouse }) await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.CurrentAsync(actor, data.Input.SupplierId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.DetailsReader, data.Input));
        Assert.Equal("approved", (await data.Service.ReadAsync(data.DetailsReader, proposal["proposalId"].AsInt64))["state"]);
        var version = await data.Rows(D.Versions).Find(new BsonDocument()).SingleAsync(); var consumer = data.Service; SupplierBankCapture? capture = null;
        await new AuthorizationStore(data.Database, data.Unit, data.Clock).ReadAsync(data.DetailsReader, async (authority, tx, ct) => {
            capture = await consumer.CaptureBeneficiaryAsync(data.Database, data.DetailsReader, authority, tx, data.Input.SupplierId, version["_id"].AsInt64, 1, ct);
            await consumer.FenceBeneficiaryAsync(data.Database, data.DetailsReader, authority, tx, capture, ct);
            Assert.Equal(D.Details.AccountNumber, capture.Source["details"]["accountNumber"]);
            Assert.Throws<PersistenceConflictException>(() => capture.RequireContext(new object(), data.DetailsReader, authority, tx));
            Assert.Throws<PersistenceConflictException>(() => capture.RequireContext(consumer, data.Reader, authority, tx));
            capture.Source["details"]["accountNumber"] = "000087654321"; Assert.Throws<PersistenceConflictException>(capture.RequireCurrent); return true;
        }, CancellationToken.None);
        Assert.NotNull(capture); Assert.Throws<ObjectDisposedException>(capture.RequireCurrent);
        var privateAudits = await data.Rows("auditEvents").Find(new BsonDocument("kind", D.Prefix + "details.read")).ToListAsync(); Assert.NotEmpty(privateAudits);
        foreach (var audit in privateAudits) {
            Assert.DoesNotContain(D.Details.AccountNumber, audit.ToJson(), StringComparison.Ordinal); Assert.DoesNotContain(D.TrustedChannel, audit.ToJson(), StringComparison.Ordinal);
            Assert.True(audit.Contains("purpose")); Assert.True(audit.Contains("selection"));
        }
        var before = await data.StateAsync();
        Assert.Null(await data.Service.LookupAsync(data.Outsider, D.Prefix + "approve", "bank-private-approved"));
        Assert.Null(await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "bank-private"));
        Assert.Null(await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "missing-key"));
        Assert.Equal(before, await data.StateAsync()); await data.NoFinancialEffectsAsync();
    }

    [Fact]
    public async Task Supplied_contact_or_unattested_details_cannot_establish_bank_trust_and_mismatches_remain_honest_rejectable_history()
    {
        await using var data = await D.CreateAsync(fixture); var proposal = await data.ProposeAsync("bank-observation"); var id = proposal["proposalId"].AsInt64;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewObservationAsync(data.Maker, id, D.Details, D.SourceReference));
        var mismatch = D.Details with { AccountNumber = "000012345679" };
        var preview = await data.Service.PreviewObservationAsync(data.Checker, id, mismatch, D.SourceReference);
        var before = await data.StateAsync();
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.ObserveAsync(data.Checker, id, 1, mismatch, D.SourceReference, preview["binding"].AsString, false, D.Meta("bank-unattested")));
        Assert.Equal(before, await data.StateAsync());
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.ObserveAsync(data.Checker, id, 1, D.Details, D.SourceReference, preview["binding"].AsString, true, D.Meta("bank-changed-observation")));
        var observed = await data.Service.ObserveAsync(data.Checker, id, 1, mismatch, D.SourceReference, preview["binding"].AsString, true, D.Meta("bank-mismatch-observed"));
        Assert.Equal(mismatch.AccountNumber, (await data.Service.ReadAsync(data.DetailsReader, id))["observation"]["observed"]["accountNumber"]);
        await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.PreviewDecisionAsync(data.Checker, id, SupplierBankDecision.Approve, observed["recordId"].AsInt64));
        await data.DecideAsync(proposal, "bank-mismatch-rejected", null, SupplierBankDecision.Reject);
        Assert.Equal("rejected", (await data.Service.ReadAsync(data.DetailsReader, id))["state"]); Assert.False(await data.Rows(D.Versions).Find(new BsonDocument()).AnyAsync());
        await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", D.PolicyId), Builders<BsonDocument>.Update.Set("allowReferenceOnly", false));
        var unsupported = await data.ProposeAsync("bank-reference-evidence-required");
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewObservationAsync(data.Checker, unsupported["proposalId"].AsInt64, D.Details, D.SourceReference));
        await data.NoFinancialEffectsAsync();
    }

    [Fact]
    public async Task Stale_policy_binding_or_observation_cannot_activate_but_rejection_and_historical_recovery_remain_possible()
    {
        await using var data = await D.CreateAsync(fixture); var policy = await data.Rows(D.Policies).Find(new BsonDocument()).SingleAsync();
        foreach (var fault in new[] { "revision", "withdrawn", "expired", "age" }) {
            var proposal = await data.ProposeAsync("bank-stale-" + fault); var observed = await data.ObserveAsync(proposal, "bank-stale-observe-" + fault);
            var preview = await data.Service.PreviewDecisionAsync(data.Checker, proposal["proposalId"].AsInt64, SupplierBankDecision.Approve, observed["recordId"].AsInt64);
            var now = data.Clock.UtcNow;
            var changed = policy.DeepClone().AsBsonDocument;
            if (fault == "revision") changed["revision"] = 2L;
            if (fault == "withdrawn") changed["active"] = false;
            if (fault == "expired") changed["expiresAt"] = now.AddSeconds(-1).UtcDateTime;
            if (fault == "age") { data.Clock.UtcNow = now.AddMinutes(16); data.Checker = data.Checker with { AuthenticatedAt = data.Clock.UtcNow }; }
            await data.Rows(D.Policies).ReplaceOneAsync(new BsonDocument("_id", D.PolicyId), changed);
            try {
                var before = await data.StateAsync();
                await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.DecideAsync(data.Checker, proposal["proposalId"].AsInt64, 1, SupplierBankDecision.Approve, observed["recordId"].AsInt64, preview["binding"].AsString, D.Meta("bank-stale-approve-" + fault)));
                Assert.Equal(before, await data.StateAsync()); await data.DecideAsync(proposal, "bank-stale-reject-" + fault, null, SupplierBankDecision.Reject);
            } finally { data.Maker = data.Maker with { AuthenticatedAt = data.Clock.UtcNow }; data.Checker = data.Checker with { AuthenticatedAt = data.Clock.UtcNow }; await data.Rows(D.Policies).ReplaceOneAsync(new BsonDocument("_id", D.PolicyId), policy); }
        }
        var good = await data.ProposeAsync("bank-policy-current"); var goodObservation = await data.ObserveAsync(good, "bank-policy-current-observed");
        var approved = await data.DecideAsync(good, "bank-policy-current-approved", goodObservation["recordId"].AsInt64);
        Assert.True((await data.Service.CurrentAsync(data.Reader, data.Input.SupplierId))["eligible"].AsBoolean);
        foreach (var fault in new[] { "withdrawn", "revision", "expired" }) {
            var changed = policy.DeepClone().AsBsonDocument;
            if (fault == "withdrawn") changed["active"] = false;
            if (fault == "revision") changed["revision"] = 2L;
            if (fault == "expired") changed["expiresAt"] = data.Clock.UtcNow.AddSeconds(-1).UtcDateTime;
            await data.Rows(D.Policies).ReplaceOneAsync(new BsonDocument("_id", D.PolicyId), changed);
            Assert.False((await data.Service.CurrentAsync(data.Reader, data.Input.SupplierId))["eligible"].AsBoolean);
            Assert.Equal("approved", (await data.Service.ReadAsync(data.DetailsReader, good["proposalId"].AsInt64))["state"]);
            Assert.Equal(approved, await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "bank-policy-current-approved"));
            await data.Rows(D.Policies).ReplaceOneAsync(new BsonDocument("_id", D.PolicyId), policy);
            Assert.True((await data.Service.CurrentAsync(data.Reader, data.Input.SupplierId))["eligible"].AsBoolean);
        }
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(16); data.Checker = data.Checker with { AuthenticatedAt = data.Clock.UtcNow };
        Assert.False((await data.Service.CurrentAsync(data.Reader, data.Input.SupplierId))["eligible"].AsBoolean);
        Assert.Equal(approved, await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "bank-policy-current-approved"));
        await data.NoFinancialEffectsAsync();
    }

    [Fact]
    public async Task Competing_bank_revisions_cannot_both_approve_and_stale_base_requires_new_review()
    {
        await using var data = await D.CreateAsync(fixture); var proposals = new[] { await data.ProposeAsync("bank-race-one"), await data.ProposeAsync("bank-race-two", data.Input with { Details = D.Details with { AccountNumber = "000087654321" } }) };
        var observations = new[] { await data.ObserveAsync(proposals[0], "bank-race-one-observed"), await data.ObserveAsync(proposals[1], "bank-race-two-observed", D.Details with { AccountNumber = "000087654321" }) };
        var previews = new[] { await data.Service.PreviewDecisionAsync(data.Checker, proposals[0]["proposalId"].AsInt64, SupplierBankDecision.Approve, observations[0]["recordId"].AsInt64),
            await data.Service.PreviewDecisionAsync(data.Checker, proposals[1]["proposalId"].AsInt64, SupplierBankDecision.Approve, observations[1]["recordId"].AsInt64) };
        var results = await Task.WhenAll(proposals.Select(async (proposal, index) => await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, proposal["proposalId"].AsInt64, 1,
            SupplierBankDecision.Approve, observations[index]["recordId"].AsInt64, previews[index]["binding"].AsString, D.Meta("bank-race-approve-" + index)))));
        Assert.Single(results, result => result is null); Assert.IsType<PersistenceConflictException>(Assert.Single(results, result => result is not null));
        Assert.Equal(1, await data.Rows(D.Versions).CountDocumentsAsync(new BsonDocument()));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewAsync(data.Maker, data.Input));
        await data.NoFinancialEffectsAsync();
    }

    [Fact]
    public async Task Every_version_and_effect_write_is_atomic_and_missing_immutable_proof_never_becomes_successful_recovery()
    {
        await using var data = await D.CreateAsync(fixture); var proposal = await data.ProposeAsync("bank-atomic"); var id = proposal["proposalId"].AsInt64;
        var observation = await data.ObserveAsync(proposal, "bank-atomic-observed"); var observationId = observation["recordId"].AsInt64;
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, SupplierBankDecision.Approve, observationId); var before = await data.StateAsync();
        foreach (var (table, field, value) in new[] { (D.Decisions, "proposalId", (BsonValue)id), (D.Versions, "proposalId", (BsonValue)id),
            (D.Heads, "version", (BsonValue)1L), ("auditEvents", "kind", (BsonValue)(D.Prefix + "approve")), ("outboxEvents", "kind", (BsonValue)(D.Prefix + "approve")), ("idempotencyRecords", "operation", (BsonValue)(D.Prefix + "approve")) }) {
            var collection = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync(); var validator = collection["options"]["validator"].AsBsonDocument.DeepClone().AsBsonDocument;
            try {
                await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", new BsonDocument("$and", new BsonArray { validator, new BsonDocument(field, new BsonDocument("$ne", value)) }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
                Assert.NotNull(await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1, SupplierBankDecision.Approve, observationId, preview["binding"].AsString, D.Meta("bank-atomic-approved"))));
                Assert.Equal(before, await data.StateAsync());
            } finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } }); }
        }
        await data.DecideAsync(proposal, "bank-atomic-approved", observationId);
        foreach (var (table, filter) in new[] { (D.Versions, new BsonDocument()), (D.Observations, new BsonDocument()), (D.Decisions, new BsonDocument()),
            ("auditEvents", new BsonDocument("kind", D.Prefix + "approve")), ("outboxEvents", new BsonDocument("kind", D.Prefix + "approve")), ("idempotencyRecords", new BsonDocument("operation", D.Prefix + "approve")) }) {
            var original = await data.Rows(table).Find(filter).SingleAsync(); Assert.Equal(1, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", original["_id"]))).DeletedCount);
            try {
                await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.ReadAsync(data.DetailsReader, id));
                await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "bank-atomic-approved"));
            } finally { await data.Rows(table).InsertOneAsync(original); }
        }
        Assert.Equal("approved", (await data.Service.ReadAsync(data.DetailsReader, id))["state"]); await data.NoFinancialEffectsAsync();
    }

    [Fact]
    public async Task Bank_inputs_remain_exact_bounded_and_require_current_approved_supplier_and_policy_identity()
    {
        await using var data = await D.CreateAsync(fixture); var input = data.Input; var before = await data.StateAsync();
        foreach (var details in new[] { D.Details with { AccountNumber = " 000012345678" }, D.Details with { AccountNumber = "1e12" }, D.Details with { AccountNumber = "000" },
            D.Details with { BankCode = "bni" }, D.Details with { HolderName = " PT Synthetic Supplier" }, D.Details with { Currency = "USD" } })
            await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.PreviewAsync(data.Maker, input with { Details = details }));
        foreach (var invalid in new[] { input with { SupplierVersion = 2 }, input with { SupplierVersionId = input.SupplierVersionId + 999 }, input with { PolicyRevision = 2 }, input with { PolicyId = 999999 } })
            await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewAsync(data.Maker, invalid));
        Assert.Equal(before, await data.StateAsync());
    }

    [Theory]
    [InlineData("policy")] [InlineData("grant")] [InlineData("session")] [InlineData("mfa")] [InlineData("person")]
    public async Task Concurrent_policy_person_session_or_grant_changes_cannot_escape_bank_approval_preview(string change)
    {
        await using var data = await D.CreateAsync(fixture); var proposal = await data.ProposeAsync("bank-active-race"); var observedBank = await data.ObserveAsync(proposal, "bank-active-race-observed");
        await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture-connection", "fixture-policy"), SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var proof = new ProviderIdentityProof(data.Checker.ProviderIssuer, data.Checker.ProviderSubject, "bank-race@example.invalid", true, "fixture-connection", data.Clock.UtcNow, data.Clock.UtcNow, "otp", "fixture-policy");
        var issued = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(data.Checker.UserId, 1, data.Checker.GrantVersion, data.Checker.ProviderIssuer, data.Checker.ProviderSubject), proof, "synthetic-bank-race", ct));
        data.Checker = data.Checker with { Session = issued.Session.Reference };
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var requestId = 0; var observed = 0; var reads = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => { if (command.Command.GetValue("find", BsonNull.Value) == D.Proposals && Interlocked.Increment(ref reads) == 1) requestId = command.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(command => { if (requestId != 0 && command.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Supplier bank approval preview race did not complete");
            } });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName); var unit = new MongoUnitOfWork(client);
        var service = new SupplierBankService(database, unit, new(database), data.Clock, new CatalogAuthoringService(database, unit, new(database), data.Clock));
        var pending = Task.Run(() => service.PreviewDecisionAsync(data.Checker, proposal["proposalId"].AsInt64, SupplierBankDecision.Approve, observedBank["recordId"].AsInt64));
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (change == "policy") await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", D.PolicyId), Builders<BsonDocument>.Update.Set("active", false));
            if (change == "grant") await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (change == "session") await sessions.RevokeAsync(issued.OpaqueToken, "Synthetic bank session revocation");
            if (change == "mfa") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
            if (change == "person") await OrganizationAddressTests.MergePeopleAsync(data.Procurement.Organizations, data.Maker.UserId, data.Checker.UserId);
        } finally { release.Set(); }
        var error = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(45)));
        Assert.Equal(1, observed); Assert.True(error is AuthorizationDeniedException or PersistenceConflictException, error?.ToString());
        Assert.False(await data.Rows(D.Decisions).Find(new BsonDocument()).AnyAsync()); Assert.False(await data.Rows(D.Versions).Find(new BsonDocument()).AnyAsync()); await data.NoFinancialEffectsAsync();
    }
}
