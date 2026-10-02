using Microsoft.AspNetCore.DataProtection;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
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
using D = UnitToko.Tests.Procurement.SupplierTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
public sealed class SupplierTests(MongoFixture fixture)
{
    [Fact]
    public async Task Real_catalog_mapping_requires_independent_trusted_observation_then_publishes_immutable_version_without_business_effects()
    {
        await using var data = await D.CreateAsync(fixture); var input = data.Input;
        var preview = await data.Service.PreviewAsync(data.Maker, input);
        var proposed = await data.Service.ProposeAsync(data.Maker, input, preview["binding"].AsString, D.Meta("supplier-first"));
        Assert.Equal(proposed, await data.Service.ProposeAsync(data.Maker, input, preview["binding"].AsString, D.Meta("supplier-first")));
        Assert.Equal(proposed, await data.Service.LookupAsync(data.Maker, D.Prefix + "propose", "supplier-first"));
        var original = await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync();
        Assert.False(await data.Rows(D.Versions).Find(new BsonDocument()).AnyAsync()); await data.NoBusinessEffectsAsync();
        var observation = await data.ObserveAsync(proposed, "supplier-observed");
        Assert.Equal(observation, await data.ObserveAsync(proposed, "supplier-observed"));
        var approved = await data.DecideAsync(proposed, "supplier-approved", observation["recordId"].AsInt64);
        Assert.Equal(approved, await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "supplier-approved"));
        Assert.Equal(original, await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync());
        var current = await data.Service.CurrentAsync(data.Checker, SupplierAccess.Approve, proposed["supplierId"].AsInt64);
        Assert.Equal(1L, current["version"].AsInt64); Assert.Equal(approved["versionId"], current["versionId"]);
        var read = await data.Service.ReadAsync(data.Checker, SupplierAccess.Approve, proposed["proposalId"].AsInt64);
        Assert.Equal("approved", read["state"]); Assert.Equal("000001", read["input"]["mappings"][0]["sourceAccountReference"].AsString);
        Assert.Equal("000012", read["input"]["mappings"][0]["externalItemCode"].AsString);
        Assert.Equal("Dus", read["mappingSources"][0]["package"]["packageLabel"].AsString);
        Assert.Equal(data.Procurement.Sku.CurrentDefinitionId!.Value.Value, read["mappingSources"][0]["package"]["definitionId"].AsInt64);
        var firstVersion = await data.Rows(D.Versions).Find(new BsonDocument()).SingleAsync();
        var revisedInput = input with { SupplierId = proposed["supplierId"].AsInt64, BaseVersion = 1, Details = input.Details with { DisplayName = "Synthetic supplier revised display" } };
        var revised = await data.ProposeAsync("supplier-second", revisedInput);
        var secondObservation = await data.ObserveAsync(revised, "supplier-second-observed", revisedInput.Details);
        var second = await data.DecideAsync(revised, "supplier-second-approved", secondObservation["recordId"].AsInt64);
        Assert.Equal(2L, second["version"].AsInt64);
        Assert.Equal(firstVersion, await data.Rows(D.Versions).Find(new BsonDocument("_id", firstVersion["_id"])).SingleAsync());
        Assert.Equal("approved", (await data.Service.ReadAsync(data.Maker, SupplierAccess.Propose, proposed["proposalId"].AsInt64))["state"]);
        var changedIdentity = revisedInput.Details with { SubjectReference = "DIFFERENT-SYNTHETIC-SUBJECT" };
        await data.Rows(D.Policies).InsertOneAsync(data.Policy(7002, changedIdentity)); var before = await data.StateAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewAsync(data.Maker, revisedInput with { BaseVersion = 2, PolicyId = 7002, Details = changedIdentity }));
        Assert.Equal(before, await data.StateAsync());
        await data.NoBusinessEffectsAsync();
    }

    [Fact]
    public async Task Proposed_contact_and_reference_alone_cannot_establish_trust_or_observe_for_the_maker()
    {
        await using var data = await D.CreateAsync(fixture); var proposed = await data.ProposeAsync("supplier-trust"); var id = proposed["proposalId"].AsInt64;
        var before = await data.StateAsync();
        foreach (var observe in new Func<Task>[] {
            async () => await data.Service.ObserveAsync(data.Maker, id, 1, D.Details, D.PolicyId, 1, D.TrustedChannel, D.SourceReference, true, D.Meta("maker-observation")),
            async () => await data.Service.ObserveAsync(data.Checker, id, 1, D.Details, D.PolicyId, 1, D.Details.ContactValue, D.SourceReference, true, D.Meta("proposed-contact-trust")),
            async () => await data.Service.ObserveAsync(data.Checker, id, 1, D.Details, D.PolicyId, 1, D.TrustedChannel, D.SourceReference, false, D.Meta("unattested-observation")),
            async () => await data.Service.ObserveAsync(data.Checker, id, 1, D.Details, D.PolicyId + 99, 1, D.TrustedChannel, D.SourceReference, true, D.Meta("unknown-policy")),
            async () => await data.Service.PreviewDecisionAsync(data.Checker, id, SupplierDecision.Approve, null) }) {
            var error = await Record.ExceptionAsync(observe);
            Assert.True(error is AuthorizationDeniedException or DomainValidationException or PersistenceConflictException, error?.ToString());
        }
        Assert.Equal(before, await data.StateAsync());
        await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", D.PolicyId), Builders<BsonDocument>.Update.Set("allowReferenceOnly", false));
        var disallowed = await Record.ExceptionAsync(() => data.ObserveAsync(proposed, "reference-only-disallowed"));
        Assert.True(disallowed is AuthorizationDeniedException or DomainValidationException or PersistenceConflictException, disallowed?.ToString());
        Assert.False(await data.Rows(D.Observations).Find(new BsonDocument()).AnyAsync()); await data.NoBusinessEffectsAsync();
    }

    [Fact]
    public async Task Shop_authority_is_exact_warehouse_grants_are_never_promoted_and_procurement_choices_redact_verification_details()
    {
        await using var data = await D.CreateAsync(fixture); var proposed = await data.ProposeAsync("supplier-scope");
        foreach (var actor in new[] { data.Outsider, data.Warehouse, data.Reader }) {
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(actor, SupplierAccess.Propose, proposed["proposalId"].AsInt64));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(actor, data.Input));
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Maker, data.Input with {
            Owner = D.Owner with { ShopId = data.Procurement.ForeignShopId }, Mappings = [data.Mapping with { Location = ProcurementRequestTestData.Location with {
                ShopId = data.Procurement.ForeignShopId, WarehouseId = data.Procurement.ForeignWarehouseId } }] }));
        var observation = await data.ObserveAsync(proposed, "supplier-scope-observed"); await data.DecideAsync(proposed, "supplier-scope-approved", observation["recordId"].AsInt64);
        var choices = await data.Service.ChoicesAsync(data.Reader, ProcurementRequestTestData.Location, null);
        Assert.Single(choices["items"].AsBsonArray);
        var serialized = choices.ToJson();
        foreach (var secret in new[] { D.Details.ContactValue, D.Details.ContactName, D.Details.Address, D.TrustedChannel, D.SourceReference, "synthetic-business-policy-approval", "synthetic-operator-approval-owner" })
            Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        foreach (var field in new[] { "contactValue", "contactName", "trustedChannelReference", "approvalReference", "operatingOwnerReference", "policy" })
            Assert.DoesNotContain('"' + field + '"', serialized, StringComparison.Ordinal);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ChoicesAsync(data.Procurement.Outsider, ProcurementRequestTestData.Location, null));
        await data.NoBusinessEffectsAsync();
    }

    [Fact]
    public async Task Ongoing_choices_require_current_policy_but_immutable_history_survives_withdrawal_and_observation_age()
    {
        await using var data = await D.CreateAsync(fixture); var proposed = await data.ProposeAsync("supplier-current-trust");
        var observed = await data.ObserveAsync(proposed, "supplier-current-trust-observed");
        var approved = await data.DecideAsync(proposed, "supplier-current-trust-approved", observed["recordId"].AsInt64);
        Assert.Single((await data.Service.ChoicesAsync(data.Reader, ProcurementRequestTestData.Location, null))["items"].AsBsonArray);
        data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(16); data.Checker = data.Checker with { AuthenticatedAt = data.Clock.UtcNow };
        // A completed approval does not require a new observation every fifteen minutes.
        Assert.Single((await data.Service.ChoicesAsync(data.Reader, ProcurementRequestTestData.Location, null))["items"].AsBsonArray);
        var policy = await data.Rows(D.Policies).Find(new BsonDocument("_id", D.PolicyId)).SingleAsync();
        foreach (var change in new[] { "withdrawn", "revised", "expired" }) {
            var unavailable = policy.DeepClone().AsBsonDocument;
            if (change == "withdrawn") unavailable["active"] = false;
            if (change == "revised") unavailable["revision"] = 2L;
            if (change == "expired") unavailable["expiresAt"] = data.Clock.UtcNow.AddMinutes(-1).UtcDateTime;
            await data.Rows(D.Policies).ReplaceOneAsync(new BsonDocument("_id", D.PolicyId), unavailable);
            Assert.Empty((await data.Service.ChoicesAsync(data.Reader, ProcurementRequestTestData.Location, null))["items"].AsBsonArray);
            Assert.Equal("approved", (await data.Service.ReadAsync(data.Checker, SupplierAccess.Approve, proposed["proposalId"].AsInt64))["state"]);
            Assert.Equal(approved, await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "supplier-current-trust-approved"));
            await data.Rows(D.Policies).ReplaceOneAsync(new BsonDocument("_id", D.PolicyId), policy);
        }
        await data.NoBusinessEffectsAsync();
    }

    [Theory]
    [InlineData("mismatch")] [InlineData("same_person")]
    public async Task Recorded_mismatched_observation_cannot_approve_and_shared_canonical_person_cannot_replace_independent_verification(string fault)
    {
        await using var data = await D.CreateAsync(fixture); var proposed = await data.ProposeAsync("supplier-person"); var id = proposed["proposalId"].AsInt64;
        var matching = await data.ObserveAsync(proposed, "supplier-observation", fault == "mismatch" ? D.Details with { ContactValue = "different@example.invalid" } : D.Details);
        if (fault == "mismatch") {
            await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewDecisionAsync(data.Checker, id, SupplierDecision.Approve, matching["recordId"].AsInt64));
            await data.DecideAsync(proposed, "supplier-mismatch-reject", null, SupplierDecision.Reject);
            Assert.Equal("rejected", (await data.Service.ReadAsync(data.Checker, SupplierAccess.Approve, id))["state"]); await data.NoBusinessEffectsAsync(); return;
        }
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, SupplierDecision.Approve, matching["recordId"].AsInt64);
        await OrganizationAddressTests.MergePeopleAsync(data.Procurement.Organizations, data.Maker.UserId, data.Checker.UserId);
        var before = await data.StateAsync();
        var denied = await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1, SupplierDecision.Approve, matching["recordId"].AsInt64, preview["binding"].AsString, D.Meta("same-person-approve")));
        Assert.True(denied is AuthorizationDeniedException or DomainValidationException, denied?.ToString()); Assert.Equal(before, await data.StateAsync());
        await data.NoBusinessEffectsAsync();
    }

    [Theory]
    [InlineData("catalog")] [InlineData("policy_revision")] [InlineData("policy_inactive")] [InlineData("observation_age")]
    public async Task Stale_source_policy_or_observation_never_activates_supplier_but_allows_independent_rejection(string change)
    {
        await using var data = await D.CreateAsync(fixture); var proposed = await data.ProposeAsync("supplier-stale"); var observed = await data.ObserveAsync(proposed, "supplier-stale-observed");
        var id = proposed["proposalId"].AsInt64; var original = await data.Rows(D.Proposals).Find(new BsonDocument("_id", id)).SingleAsync();
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, SupplierDecision.Approve, observed["recordId"].AsInt64);
        if (change == "catalog") await data.Procurement.Catalog.ReviseDefinitionAsync(data.Procurement.Author,
            new(data.Procurement.Sku.Id, data.Procurement.Sku.Version, CatalogAuthoringTestData.Definition(12, "Dus revisi")), D.Meta("supplier-catalog-revision"));
        if (change == "policy_revision") await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", D.PolicyId), Builders<BsonDocument>.Update.Set("revision", 2L));
        if (change == "policy_inactive") await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", D.PolicyId), Builders<BsonDocument>.Update.Set("active", false));
        if (change == "observation_age") { data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(16); data.Checker = data.Checker with { AuthenticatedAt = data.Clock.UtcNow }; }
        var before = await data.StateAsync();
        var failed = await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1, SupplierDecision.Approve, observed["recordId"].AsInt64, preview["binding"].AsString, D.Meta("supplier-stale-approve")));
        Assert.True(failed is AuthorizationDeniedException or DomainValidationException or PersistenceConflictException, failed?.ToString()); Assert.Equal(before, await data.StateAsync());
        await data.DecideAsync(proposed, "supplier-stale-reject", null, SupplierDecision.Reject);
        Assert.Equal("rejected", (await data.Service.ReadAsync(data.Checker, SupplierAccess.Approve, id))["state"]);
        Assert.Equal(original, await data.Rows(D.Proposals).Find(new BsonDocument("_id", id)).SingleAsync());
        Assert.False(await data.Rows(D.Versions).Find(new BsonDocument()).AnyAsync()); await data.NoBusinessEffectsAsync();
    }

    [Fact]
    public async Task Exact_actor_operation_and_key_recovery_never_resends_or_exposes_another_reviewers_observation()
    {
        await using var data = await D.CreateAsync(fixture); var proposed = await data.ProposeAsync("supplier-recovery");
        var observed = await data.ObserveAsync(proposed, "supplier-recovery-observe");
        var other = await data.Base.AddActorAsync(624, D.Scope, Permissions.SupplierChangeApprove); await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, other.UserId);
        var before = await data.StateAsync();
        Assert.Equal(proposed, await data.Service.LookupAsync(data.Maker, D.Prefix + "propose", "supplier-recovery"));
        Assert.Equal(observed, await data.Service.LookupAsync(data.Checker, D.Prefix + "observe", "supplier-recovery-observe"));
        Assert.Null(await data.Service.LookupAsync(other, D.Prefix + "observe", "supplier-recovery-observe"));
        Assert.Null(await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "supplier-recovery-observe"));
        Assert.Null(await data.Service.LookupAsync(data.Maker, D.Prefix + "propose", "wrong-key"));
        Assert.Equal(before, await data.StateAsync()); await data.NoBusinessEffectsAsync();
    }

    [Fact]
    public async Task Namespace_overlap_is_serialized_across_suppliers_and_half_open_intervals_preserve_exact_external_codes()
    {
        await using var data = await D.CreateAsync(fixture); var first = await data.ProposeAsync("supplier-overlap-first"); var firstObservation = await data.ObserveAsync(first, "supplier-overlap-first-observe");
        var secondDetails = D.Details with { SubjectReference = "SYNTHETIC-SUBJECT-002", LegalName = "PT Synthetic Second Supplier" };
        await data.Rows(D.Policies).InsertOneAsync(data.Policy(7002, secondDetails));
        var secondInput = data.Input with { PolicyId = 7002, Details = secondDetails };
        var second = await data.ProposeAsync("supplier-overlap-second", secondInput);
        var secondObservation = await data.Service.ObserveAsync(data.Checker, second["proposalId"].AsInt64, 1, secondDetails, 7002, 1, D.TrustedChannel, D.SourceReference, true, D.Meta("supplier-overlap-second-observe"));
        var attempts = new[] { (first, firstObservation, "supplier-overlap-first-approve"), (second, secondObservation, "supplier-overlap-second-approve") };
        var previews = await Task.WhenAll(attempts.Select(item => data.Service.PreviewDecisionAsync(data.Checker, item.Item1["proposalId"].AsInt64, SupplierDecision.Approve, item.Item2["recordId"].AsInt64)));
        var results = await Task.WhenAll(attempts.Select(async (item, index) => await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, item.Item1["proposalId"].AsInt64, 1,
            SupplierDecision.Approve, item.Item2["recordId"].AsInt64, previews[index]["binding"].AsString, D.Meta(item.Item3)))));
        Assert.Single(results, value => value is null); Assert.IsType<PersistenceConflictException>(Assert.Single(results, value => value is not null));
        Assert.Equal(1, await data.Rows(D.Versions).CountDocumentsAsync(new BsonDocument()));
        var loser = Array.FindIndex(results, error => error is not null); var originalInput = loser == 0 ? data.Input : secondInput;
        var previousUntil = originalInput.Mappings[0].ValidUntil;
        var adjacentInput = originalInput with { SupplierId = attempts[loser].Item1["supplierId"].AsInt64, BaseVersion = 0,
            Mappings = [originalInput.Mappings[0] with { ValidFrom = previousUntil, ValidUntil = previousUntil.AddDays(1) }] };
        var adjacent = await data.ProposeAsync("supplier-adjacent", adjacentInput);
        var adjacentObservation = await data.Service.ObserveAsync(data.Checker, adjacent["proposalId"].AsInt64, 1, adjacentInput.Details,
            adjacentInput.PolicyId, adjacentInput.PolicyRevision, D.TrustedChannel, D.SourceReference, true, D.Meta("supplier-adjacent-observe"));
        await data.DecideAsync(adjacent, "supplier-adjacent-approve", adjacentObservation["recordId"].AsInt64);
        Assert.Equal(2, await data.Rows(D.Versions).CountDocumentsAsync(new BsonDocument()));
        var approvedAdjacent = await data.Service.ReadAsync(data.Checker, SupplierAccess.Approve, adjacent["proposalId"].AsInt64);
        Assert.Equal("approved", approvedAdjacent["state"]); Assert.Equal(previousUntil.UtcDateTime, approvedAdjacent["input"]["mappings"][0]["validFrom"].ToUniversalTime());
        Assert.Equal("000012", approvedAdjacent["input"]["mappings"][0]["externalItemCode"].AsString);
        // The adjacent future mapping is approved but is not prematurely offered for ordering today.
        Assert.Single((await data.Service.ChoicesAsync(data.Reader, ProcurementRequestTestData.Location, null))["items"].AsBsonArray);
        await data.NoBusinessEffectsAsync();
    }

    [Fact]
    public async Task Parallel_new_supplier_proposals_cannot_duplicate_the_exact_owner_and_subject_identity()
    {
        await using var data = await D.CreateAsync(fixture); var input = data.Input; var preview = await data.Service.PreviewAsync(data.Maker, input);
        var outcomes = await Task.WhenAll(new[] { "supplier-identity-first", "supplier-identity-second" }.Select(async key =>
            await Record.ExceptionAsync(() => data.Service.ProposeAsync(data.Maker, input, preview["binding"].AsString, D.Meta(key)))));
        Assert.Single(outcomes, error => error is null); Assert.IsType<PersistenceConflictException>(Assert.Single(outcomes, error => error is not null));
        Assert.Equal(1, await data.Rows(D.Heads).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows(D.Proposals).CountDocumentsAsync(new BsonDocument()));
        await data.NoBusinessEffectsAsync();
    }

    [Fact]
    public async Task Required_approval_effect_failure_rolls_back_head_version_namespace_and_all_command_effects()
    {
        await using var data = await D.CreateAsync(fixture); var proposed = await data.ProposeAsync("supplier-atomic"); var observed = await data.ObserveAsync(proposed, "supplier-atomic-observe");
        var id = proposed["proposalId"].AsInt64; var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, SupplierDecision.Approve, observed["recordId"].AsInt64); var before = await data.StateAsync();
        foreach (var (table, field, denied) in new[] { (D.Decisions, "decision", "approve"), ("auditEvents", "kind", D.Prefix + "approve"), ("outboxEvents", "kind", D.Prefix + "approve"), ("idempotencyRecords", "operation", D.Prefix + "approve") }) {
            var collection = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
            var validator = collection["options"]["validator"].AsBsonDocument.DeepClone().AsBsonDocument;
            try {
                await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", new BsonDocument("$and", new BsonArray { validator, new BsonDocument(field, new BsonDocument("$ne", denied)) }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
                Assert.NotNull(await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1, SupplierDecision.Approve, observed["recordId"].AsInt64, preview["binding"].AsString, D.Meta("supplier-atomic-approve"))));
                Assert.Equal(before, await data.StateAsync());
            } finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } }); }
        }
        await data.DecideAsync(proposed, "supplier-atomic-approve", observed["recordId"].AsInt64); Assert.Equal(1, await data.Rows(D.Versions).CountDocumentsAsync(new BsonDocument()));
        await data.NoBusinessEffectsAsync();
    }

    [Fact]
    public async Task Noncanonical_supplier_identity_and_ambiguous_mapping_intervals_are_rejected_before_writes()
    {
        await using var data = await D.CreateAsync(fixture); var input = data.Input; var mapping = data.Mapping; var before = await data.StateAsync();
        foreach (var invalid in new[] { input with { Details = D.Details with { LegalName = " padded " } }, input with { Details = D.Details with { RegistrationValue = null } },
            input with { Details = D.Details with { ContactMethod = "unknown" } }, input with { Mappings = [] }, input with { Mappings = [mapping, mapping] },
            input with { Mappings = [mapping with { SourceAccountReference = " padded " }] }, input with { Mappings = [mapping with { ExternalItemCode = "" }] },
            input with { Mappings = [mapping with { ValidUntil = mapping.ValidFrom }] }, input with { Mappings = [mapping with { ValidFrom = mapping.ValidFrom.AddTicks(1) }] },
            input with { Mappings = [mapping with { ValidFrom = mapping.ValidFrom.ToOffset(TimeSpan.FromHours(7)) }] },
            input with { Mappings = [mapping with { Location = mapping.Location with { ShopId = data.Procurement.ForeignShopId } }] } })
            await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.PreviewAsync(data.Maker, invalid));
        Assert.Equal(before, await data.StateAsync());
    }

    [Theory]
    [InlineData("policy")] [InlineData("grant")] [InlineData("session")] [InlineData("mfa")]
    public async Task Concurrent_policy_or_actor_authority_changes_cannot_escape_real_supplier_preview_fences(string change)
    {
        await using var data = await D.CreateAsync(fixture);
        await data.Rows("identities").UpdateOneAsync(new BsonDocument("_id", data.Maker.UserId), Builders<BsonDocument>.Update.Set("securityVersion", 1L));
        var sessions = new MongoIdentitySessionStore(data.Database, data.Unit, data.Clock, new("https://approved.example/", "fixture-connection", "fixture-policy"),
            SessionPayloadProtector.FromTestProvider(new EphemeralDataProtectionProvider()));
        var proof = new ProviderIdentityProof(data.Maker.ProviderIssuer, data.Maker.ProviderSubject, "supplier-race@example.invalid", true,
            "fixture-connection", data.Clock.UtcNow, data.Clock.UtcNow, "otp", "fixture-policy");
        var issued = await data.Unit.ExecuteAsync((tx, ct) => sessions.CreateAsync(tx, new(data.Maker.UserId, 1, data.Maker.GrantVersion, data.Maker.ProviderIssuer, data.Maker.ProviderSubject), proof, "synthetic-supplier-race", ct));
        data.Maker = data.Maker with { Session = issued.Session.Reference };
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = 0; var observed = 0; var reads = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => { if (command.Command.GetValue("find", BsonNull.Value) == "skus" && Interlocked.Increment(ref reads) == 1) requestId = command.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(command => { if (requestId != 0 && command.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Supplier preview race did not complete");
            } });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName); var unit = new MongoUnitOfWork(client);
        var service = new SupplierService(database, unit, new(database), data.Clock, new CatalogAuthoringService(database, unit, new(database), data.Clock));
        var input = data.Input; var pending = Task.Run(() => service.PreviewAsync(data.Maker, input));
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (change == "policy") await data.Rows(D.Policies).UpdateOneAsync(new BsonDocument("_id", D.PolicyId), Builders<BsonDocument>.Update.Set("revision", 2L));
            if (change == "grant") await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Maker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
            if (change == "session") await sessions.RevokeAsync(issued.OpaqueToken, "Synthetic supplier session revocation");
            if (change == "mfa") data.Clock.UtcNow = data.Clock.UtcNow.AddMinutes(6);
        } finally { release.Set(); }
        var error = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, observed); Assert.True(error is AuthorizationDeniedException or DomainValidationException or PersistenceConflictException, error?.ToString());
        Assert.False(await data.Rows(D.Proposals).Find(new BsonDocument()).AnyAsync()); await data.NoBusinessEffectsAsync();
    }
}
