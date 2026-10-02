using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using B = UnitToko.Platform.Organizations.OrganizationAddressBson;

namespace UnitToko.Tests.Organizations;

[Collection("Mongo")]
public sealed class OrganizationAddressTests(MongoFixture fixture)
{
    [Fact]
    public async Task Actual_independent_approval_retains_postal_versions_and_exact_actor_key_replay_and_recovery() {
        await using var data = await OrganizationAddressTestData.CreateAsync(fixture);
        var profile = await data.Rows("organizationProfiles").Find(new BsonDocument("_id", 11L)).SingleAsync();
        var input = data.Input(); var preview = await data.Service.PreviewAsync(data.Maker, input);
        var proposal = await data.Service.ProposeAsync(data.Maker, input, preview.Binding, OrganizationAddressTestData.Meta("address-first"));
        var pending = await data.StateAsync();
        Assert.Equal(proposal, await data.Service.ProposeAsync(data.Maker, input, preview.Binding, OrganizationAddressTestData.Meta("address-first")));
        Assert.Equal(proposal, await data.Service.RecoverAsync(data.Maker, B.Prefix + "propose", "address-first"));
        Assert.Equal(pending, await data.StateAsync());
        Assert.Null(await data.Service.RecoverAsync(data.Maker, B.Prefix + "propose", "address-never-submitted"));
        var decisionPreview = await data.Service.PreviewDecisionAsync(data.Checker, proposal["proposalId"].AsInt64, 1, OrganizationAddressDecision.Approve);
        var approved = await data.Service.DecideAsync(data.Checker, proposal["proposalId"].AsInt64, 1, OrganizationAddressDecision.Approve,
            decisionPreview.Binding, OrganizationAddressTestData.Meta("address-first-approve"));
        var first = await data.Rows(B.Versions).Find(new BsonDocument("_id", approved["versionId"])).SingleAsync();
        Assert.Equal(1L, first["version"].AsInt64); Assert.True(first["previousVersionId"].IsBsonNull); Assert.True(first["previousHash"].IsBsonNull);
        var saved = await data.StateAsync();
        Assert.Equal(approved, await data.Service.DecideAsync(data.Checker, proposal["proposalId"].AsInt64, 1, OrganizationAddressDecision.Approve,
            decisionPreview.Binding, OrganizationAddressTestData.Meta("address-first-approve")));
        Assert.Equal(approved, await data.Service.RecoverAsync(data.Checker, B.Prefix + "approve", "address-first-approve"));
        Assert.Equal(saved, await data.StateAsync());
        var mismatch = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.ProposeAsync(data.Maker,
            input with { Address = input.Address with { PostalCode = "12811" } }, preview.Binding, OrganizationAddressTestData.Meta("address-first")));
        Assert.Equal("idempotency_key_reused", mismatch.Code); Assert.Equal(saved, await data.StateAsync());
        var next = await data.ProposeAsync("address-second", data.Input(1) with { Address = input.Address with { Line1 = "Jalan Tebet Raya 14" } });
        var second = await data.DecideAsync(next, "address-second-approve");
        var version = await data.Rows(B.Versions).Find(new BsonDocument("_id", second["versionId"])).SingleAsync();
        Assert.Equal(2L, version["version"].AsInt64); Assert.Equal(first["_id"], version["previousVersionId"]); Assert.Equal(first["snapshotHash"], version["previousHash"]);
        Assert.Equal(first, await data.Rows(B.Versions).Find(new BsonDocument("_id", first["_id"])).SingleAsync());
        Assert.Equal(profile, await data.Rows("organizationProfiles").Find(new BsonDocument("_id", 11L)).SingleAsync());
        Assert.Equal(2, await data.Rows(B.Proposals).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(2, await data.Rows(B.Versions).CountDocumentsAsync(new BsonDocument()));
        foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("idempotencyRecords", "operation"), ("outboxEvents", "kind") })
            Assert.Equal(4, await data.Rows(table).CountDocumentsAsync(new BsonDocument(field, new BsonRegularExpression(@"^organization\.address\."))));
        var people = (await data.Rows(B.Decisions).Find(new BsonDocument("_id", approved["recordId"])).SingleAsync())["people"].AsBsonArray;
        Assert.Equal(new long[] { 301, 302 }, people.Select(row => row["userId"].AsInt64));
        Assert.NotEqual(people[0]["personId"], people[1]["personId"]);
        var readAudits = await data.Rows("authorizationReadEvents").Find(new BsonDocument("purpose", "operation-recovery")).ToListAsync();
        foreach (var (actor, operation, key) in new[] { (data.Maker, B.Prefix + "propose", "address-first"),
            (data.Checker, B.Prefix + "approve", "address-first-approve") }) {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
            var audit = Assert.Single(readAudits, row => row["actorId"] == actor.UserId && row["selection"]["operation"] == operation && row["selection"]["keyHash"] == hash);
            Assert.Equal("organization.workbench.read", audit["operation"].AsString); Assert.Equal(actor.GrantVersion, audit["actorGrantVersion"].AsInt64);
            Assert.Equal(1, audit["resultCount"].AsInt32);
            Assert.Equal(new BsonArray { AuthorizationBson.Scope(new GrantScope(GrantScopeKind.System)) }, audit["scopes"]);
            Assert.All(audit["selection"].AsBsonDocument.Names, field => Assert.Contains(field, new[] { "subjectId", "offset", "limit", "operation", "keyHash" }));
            Assert.False(audit["selection"].AsBsonDocument.Contains("key")); Assert.DoesNotContain(key, audit.ToJson(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Approval_requires_current_fresh_permission_and_independent_real_people_without_partial_versions() {
        await using var data = await OrganizationAddressTestData.CreateAsync(fixture); var proposal = await data.ProposeAsync("address-independence");
        var before = await data.StateAsync(); var proposalId = proposal["proposalId"].AsInt64;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(data.Maker, proposalId, 1, OrganizationAddressDecision.Approve));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(data.Checker with { Assurance = FactorAssurance.SingleFactor }, proposalId, 1, OrganizationAddressDecision.Approve));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(data.Checker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) }, proposalId, 1, OrganizationAddressDecision.Approve));
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, proposalId, 1, OrganizationAddressDecision.Approve);
        var grant = await data.Rows("authorizationGrants").Find(new BsonDocument("_id", data.Checker.UserId)).SingleAsync();
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", grant["_id"]), Builders<BsonDocument>.Update.Set("status", "revoked")
            .Set("revokedAt", data.Clock.UtcNow.UtcDateTime).Inc("version", 1L));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.DecideAsync(data.Checker, proposalId, 1, OrganizationAddressDecision.Approve, preview.Binding, OrganizationAddressTestData.Meta("address-revoked-checker")));
        await data.Rows("authorizationGrants").ReplaceOneAsync(new BsonDocument("_id", grant["_id"]), grant);
        await MergePeopleAsync(data, data.Maker.UserId, data.Checker.UserId);
        var people = await data.Unit.ExecuteAsync((tx, ct) => new PersonRegistryService(data.Database, data.Clock)
            .ResolveAsync(tx, [data.Maker.UserId, data.Checker.UserId], ct));
        var maker = people.Single(person => person.UserId == data.Maker.UserId);
        var checker = people.Single(person => person.UserId == data.Checker.UserId);
        Assert.Equal(PersonVerification.Verified, checker.Verification); Assert.Equal(maker.PersonId, checker.PersonId);
        Assert.Equal("independent_person_required", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.DecideAsync(data.Checker, proposalId, 1, OrganizationAddressDecision.Approve, preview.Binding, OrganizationAddressTestData.Meta("address-equivalent-checker")))).Code);
        Assert.Equal(before, await data.StateAsync()); Assert.False(await data.Rows(B.Versions).Find(new BsonDocument()).AnyAsync());
    }

    [Fact]
    public async Task Governed_profile_change_invalidates_pending_approval_but_explicit_rejection_and_new_source_review_remain_possible() {
        await using var data = await OrganizationAddressTestData.CreateAsync(fixture); var proposal = await data.ProposeAsync("address-old-profile");
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, proposal["proposalId"].AsInt64, 1, OrganizationAddressDecision.Approve);
        var change = await data.Organizations.ProposeAsync(data.ProfileMaker,
            new(OrganizationChangeKind.UpdateProfile, ScopeNodeKind.Branch, 10, "TEBET", "Synthetic reviewed branch revision", 11, 2, 1), OrganizationAddressTestData.Meta("address-profile-update"));
        await data.Organizations.ApproveAsync(data.ProfileChecker, change.Id, 1, OrganizationAddressTestData.Meta("address-profile-update-approved"));
        var state = await data.StateAsync();
        Assert.Equal("organization_address_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.DecideAsync(data.Checker,
            proposal["proposalId"].AsInt64, 1, OrganizationAddressDecision.Approve, preview.Binding, OrganizationAddressTestData.Meta("address-stale-source")))).Code);
        Assert.Equal(state, await data.StateAsync()); Assert.False(await data.Rows(B.Versions).Find(new BsonDocument()).AnyAsync());
        var rejection = await data.DecideAsync(proposal, "address-stale-rejected", OrganizationAddressDecision.Reject);
        Assert.True(rejection["versionId"].IsBsonNull); Assert.True(rejection["addressVersion"].IsBsonNull);
        var input = data.Input() with { ExpectedScopeVersion = 3, ExpectedProfileVersion = 2 };
        var fresh = await data.ProposeAsync("address-fresh-source", input); var approved = await data.DecideAsync(fresh, "address-fresh-approved");
        Assert.Equal(1L, approved["addressVersion"].AsInt64);
        var version = await data.Rows(B.Versions).Find(new BsonDocument("_id", approved["versionId"])).SingleAsync();
        Assert.Equal("Synthetic reviewed branch revision", version["source"]["profile"]["name"].AsString);
        Assert.Equal(2L, version["source"]["profile"]["version"].AsInt64);
    }

    [Fact]
    public async Task Two_actual_checkers_cannot_create_duplicate_address_versions_from_one_reviewed_proposal() {
        await using var data = await OrganizationAddressTestData.CreateAsync(fixture); var proposal = await data.ProposeAsync("address-race");
        var second = await data.Base.AddActorAsync(306, new(GrantScopeKind.System), Permissions.OrganizationApprove);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, second.UserId);
        var firstPreview = await data.Service.PreviewDecisionAsync(data.Checker, proposal["proposalId"].AsInt64, 1, OrganizationAddressDecision.Approve);
        var secondPreview = await data.Service.PreviewDecisionAsync(second, proposal["proposalId"].AsInt64, 1, OrganizationAddressDecision.Approve);
        var results = await Task.WhenAll(new[] { (data.Checker, firstPreview, "address-race-first"), (second, secondPreview, "address-race-second") }
            .Select(async value => await Record.ExceptionAsync(() => data.Service.DecideAsync(value.Item1, proposal["proposalId"].AsInt64, 1,
                OrganizationAddressDecision.Approve, value.Item2.Binding, OrganizationAddressTestData.Meta(value.Item3)))));
        Assert.Single(results, result => result is null); Assert.IsType<PersistenceConflictException>(Assert.Single(results, result => result is not null));
        Assert.Equal(1, await data.Rows(B.Decisions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1, await data.Rows(B.Versions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(1L, (await data.Rows(B.Heads).Find(new BsonDocument()).SingleAsync())["version"].AsInt64);
    }

    [Fact]
    public async Task Required_version_and_each_command_effect_write_are_atomic_and_exact_failed_key_can_retry() {
        await using var data = await OrganizationAddressTestData.CreateAsync(fixture); var proposal = await data.ProposeAsync("address-atomic-proposal");
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, proposal["proposalId"].AsInt64, 1, OrganizationAddressDecision.Approve);
        var state = await data.StateAsync(); const string key = "address-atomic-approve";
        foreach (var (table, field, value) in new[] { (B.Versions, "purpose", "delivery"), ("auditEvents", "kind", B.Prefix + "approve"),
            ("idempotencyRecords", "operation", B.Prefix + "approve"), ("outboxEvents", "kind", B.Prefix + "approve") }) {
            var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
            var validator = definition["options"]["validator"].DeepClone().AsBsonDocument;
            try {
                await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", new BsonDocument("$and", new BsonArray {
                    validator, new BsonDocument(field, new BsonDocument("$ne", value)) }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
                Assert.NotNull(await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, proposal["proposalId"].AsInt64, 1,
                    OrganizationAddressDecision.Approve, preview.Binding, OrganizationAddressTestData.Meta(key))));
                Assert.Equal(state, await data.StateAsync());
            } finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } }); }
        }
        var saved = await data.Service.DecideAsync(data.Checker, proposal["proposalId"].AsInt64, 1, OrganizationAddressDecision.Approve,
            preview.Binding, OrganizationAddressTestData.Meta(key));
        Assert.Equal(saved, await data.Service.RecoverAsync(data.Checker, B.Prefix + "approve", key));
        Assert.Equal(1, await data.Rows(B.Versions).CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task Scoped_postal_read_and_relationship_marker_confer_no_order_recipient_or_financial_authority() {
        await using var data = await OrganizationAddressTestData.CreateAsync(fixture);
        var pending = await data.ProposeAsync("address-read-branch");
        var pendingView = await data.Service.ReadAsync(data.Reader, OrganizationAddressTestData.Branch, OrganizationAddressPurpose.Delivery);
        Assert.Equal(0L, pendingView["version"].AsInt64); Assert.True(pendingView["addressId"].IsBsonNull); Assert.True(pendingView["address"].IsBsonNull);
        var approved = await data.DecideAsync(pending, "address-read-branch-approved");
        var billing = await data.ProposeAsync("address-read-legal", data.Input(target: OrganizationAddressTestData.Buyer, purpose: OrganizationAddressPurpose.LegalBilling)
            with { Address = OrganizationAddressTestData.Postal with { Line1 = "Jalan Legal Billing 5" } });
        await data.DecideAsync(billing, "address-read-legal-approved");
        var dispatch = await data.ProposeAsync("address-read-dispatch", data.Input(target: OrganizationAddressTestData.Warehouse, purpose: OrganizationAddressPurpose.Dispatch)
            with { Address = OrganizationAddressTestData.Postal with { Line1 = "Jalan Gudang 9" } });
        await data.DecideAsync(dispatch, "address-read-dispatch-approved");
        var view = await data.Service.ReadAsync(data.Reader, OrganizationAddressTestData.Branch, OrganizationAddressPurpose.Delivery);
        Assert.Equal(approved["versionId"], view["versionId"]); Assert.True(view["current"].AsBoolean); Assert.Equal(B.Details(OrganizationAddressTestData.Postal), view["address"]);
        Assert.Equal("Jalan Legal Billing 5", (await data.Service.ReadAsync(data.Reader, OrganizationAddressTestData.Buyer, OrganizationAddressPurpose.LegalBilling))["address"]["line1"].AsString);
        Assert.Equal("Jalan Gudang 9", (await data.Service.ReadAsync(data.WarehouseReader, OrganizationAddressTestData.Warehouse, OrganizationAddressPurpose.Dispatch))["address"]["line1"].AsString);
        var authority = await data.Base.Store.ReadCurrentAsync(data.Reader);
        foreach (var permission in new[] { Permissions.OrderSubmit, Permissions.InvoiceIssue, Permissions.ReceiptAccept, Permissions.CreditRequest,
            Permissions.CreditApprove, Permissions.RefundApprove, Permissions.DisbursementRecord, Permissions.DisbursementVerify })
            Assert.False(ScopePolicy.Authorize(data.Reader, authority, permission, FullResource, data.Clock.UtcNow).Allowed);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Reader, data.Input(1)));
        Assert.Equal("scope_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.ReadAsync(data.Reader, OrganizationAddressTestData.Warehouse, OrganizationAddressPurpose.Dispatch))).Code);
        var outside = await data.Base.AddActorAsync(307, new(GrantScopeKind.BuyingBranch, 10, BranchId: 12), Permissions.OrganizationAddressRead, Permissions.BranchPic);
        Assert.Equal("scope_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            data.Service.ReadAsync(outside, OrganizationAddressTestData.Branch, OrganizationAddressPurpose.Delivery))).Code);
        Assert.True((await data.Service.ReadAsync(data.Maker, OrganizationAddressTestData.Branch, OrganizationAddressPurpose.Delivery))["current"].AsBoolean);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Base.Store.ReadAsync(data.Maker, async (snapshot, tx, ct) => {
            await data.Service.ReadCurrentSourceAsync(data.Maker, snapshot, tx, OrganizationAddressTestData.Branch,
                OrganizationAddressPurpose.Delivery, FullResource, Permissions.OrderSubmit, ct); return true;
        }, CancellationToken.None));
        var consumed = await data.Base.Store.ReadAsync(data.Orderer, async (snapshot, tx, ct) => {
            Assert.False(ScopePolicy.Compile(data.Orderer, snapshot, Permissions.OrganizationAddressRead, data.Clock.UtcNow).IsAllowed);
            var source = await data.Service.ReadCurrentSourceAsync(data.Orderer, snapshot, tx, OrganizationAddressTestData.Branch,
                OrganizationAddressPurpose.Delivery, FullResource, Permissions.OrderSubmit, ct);
            source.RequireContext(data.Orderer, snapshot, tx); await source.FenceAsync(ct); return source.Address;
        }, CancellationToken.None);
        Assert.Equal(OrganizationAddressTestData.Postal, consumed);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Base.Store.ReadAsync(data.Orderer, async (snapshot, tx, ct) => {
            await data.Service.ReadCurrentSourceAsync(data.Orderer, snapshot, tx, OrganizationAddressTestData.Branch,
                OrganizationAddressPurpose.Delivery, FullResource with { BuyingOrganizationId = 20 }, Permissions.OrderSubmit, ct); return true;
        }, CancellationToken.None));
    }

    [Fact]
    public async Task Original_proposer_canonical_person_remap_does_not_rewrite_retained_independence_or_allow_key_replay() {
        await using var data = await OrganizationAddressTestData.CreateAsync(fixture);
        var input = data.Input(); var preview = await data.Service.PreviewAsync(data.Maker, input);
        var proposal = await data.Service.ProposeAsync(data.Maker, input, preview.Binding, OrganizationAddressTestData.Meta("address-original-person"));
        var decisionPreview = await data.Service.PreviewDecisionAsync(data.Checker, proposal["proposalId"].AsInt64, 1, OrganizationAddressDecision.Approve);
        const long replacement = 309; await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, replacement);
        await MergePeopleAsync(data, replacement, data.Maker.UserId);
        var people = await data.Unit.ExecuteAsync((tx, ct) => new PersonRegistryService(data.Database, data.Clock).ResolveAsync(tx, [data.Maker.UserId], ct));
        var current = Assert.Single(people);
        Assert.Equal(PersonVerification.Verified, current.Verification); Assert.Equal(PersonTestData.PersonId(replacement), current.PersonId);
        var before = await data.StateAsync();
        foreach (var action in new Func<Task>[] {
            async () => { await data.Service.PreviewDecisionAsync(data.Checker, proposal["proposalId"].AsInt64, 1, OrganizationAddressDecision.Approve); },
            async () => { await data.Service.DecideAsync(data.Checker, proposal["proposalId"].AsInt64, 1, OrganizationAddressDecision.Approve, decisionPreview.Binding, OrganizationAddressTestData.Meta("address-remapped-approve")); },
            async () => { await data.Service.ProposeAsync(data.Maker, input, preview.Binding, OrganizationAddressTestData.Meta("address-original-person")); },
            async () => { await data.Service.RecoverAsync(data.Maker, B.Prefix + "propose", "address-original-person"); } })
            Assert.Equal("organization_address_person_changed", (await Assert.ThrowsAsync<AuthorizationDeniedException>(action)).Code);
        Assert.Equal(before, await data.StateAsync()); Assert.False(await data.Rows(B.Versions).Find(new BsonDocument()).AnyAsync());
    }

    [Theory]
    [InlineData("grant")] [InlineData("profile")] [InlineData("address")]
    public async Task Final_same_transaction_consumer_fence_refuses_changed_actual_authority_profile_or_address_and_rolls_back(string fault) {
        await using var data = await OrganizationAddressTestData.CreateAsync(fixture);
        await data.DecideAsync(await data.ProposeAsync("address-final-fence"), "address-final-fence-approved");
        var state = await data.StateAsync(); var profile = await data.Rows("organizationProfiles").Find(new BsonDocument("_id", 11L)).SingleAsync();
        var grant = await data.Rows("authorizationGrants").Find(new BsonDocument("_id", data.Orderer.UserId)).SingleAsync();
        var failure = await Record.ExceptionAsync(() => data.Base.Store.ReadAsync(data.Orderer, async (snapshot, tx, ct) => {
            var source = await data.Service.ReadCurrentSourceAsync(data.Orderer, snapshot, tx, OrganizationAddressTestData.Branch,
                OrganizationAddressPurpose.Delivery, FullResource, Permissions.OrderSubmit, ct);
            source.RequireContext(data.Orderer, snapshot, tx);
            if (fault == "grant") await data.Rows("authorizationGrants").UpdateOneAsync(tx, new BsonDocument("_id", data.Orderer.UserId),
                Builders<BsonDocument>.Update.Set("status", "revoked").Set("revokedAt", data.Clock.UtcNow.UtcDateTime).Inc("version", 1L), cancellationToken: ct);
            if (fault == "profile") await data.Rows("organizationProfiles").UpdateOneAsync(tx, new BsonDocument("_id", 11L),
                Builders<BsonDocument>.Update.Set("name", "Unapproved same-transaction name"), cancellationToken: ct);
            if (fault == "address") await data.Rows(B.Heads).UpdateOneAsync(tx, new BsonDocument("_id", source.AddressId),
                Builders<BsonDocument>.Update.Set("version", 2L), cancellationToken: ct);
            await source.FenceAsync(ct); return true;
        }, CancellationToken.None));
        if (fault == "grant") Assert.Equal("historical_authority_changed", Assert.IsType<AuthorizationDeniedException>(failure).Code);
        else Assert.Equal("organization_address_integrity_unavailable", Assert.IsType<PersistenceConflictException>(failure).Code);
        Assert.Equal(state, await data.StateAsync());
        Assert.Equal(profile, await data.Rows("organizationProfiles").Find(new BsonDocument("_id", 11L)).SingleAsync());
        var actualGrant = await data.Rows("authorizationGrants").Find(new BsonDocument("_id", data.Orderer.UserId)).SingleAsync();
        actualGrant.Remove("fence"); grant.Remove("fence"); Assert.Equal(grant, actualGrant);
    }

    [Fact]
    public async Task Consumer_source_rejects_exposed_mutation_and_cross_actor_context_and_stale_profile_keeps_only_historical_postal_view() {
        await using var data = await OrganizationAddressTestData.CreateAsync(fixture);
        await data.DecideAsync(await data.ProposeAsync("address-capture"), "address-capture-approved"); var state = await data.StateAsync();
        var mutation = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Base.Store.ReadAsync(data.Orderer, async (snapshot, tx, ct) => {
            var source = await data.Service.ReadCurrentSourceAsync(data.Orderer, snapshot, tx, OrganizationAddressTestData.Branch,
                OrganizationAddressPurpose.Delivery, FullResource, Permissions.OrderSubmit, ct);
            source.Snapshot["details"]["postalCode"] = "99999"; await source.FenceAsync(ct); return true;
        }, CancellationToken.None));
        Assert.Equal("organization_address_integrity_unavailable", mutation.Code); Assert.Equal(state, await data.StateAsync());
        Assert.Equal("organization_address_integrity_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Base.Store.ReadAsync(data.Orderer, async (snapshot, tx, ct) => {
            var source = await data.Service.ReadCurrentSourceAsync(data.Orderer, snapshot, tx, OrganizationAddressTestData.Branch,
                OrganizationAddressPurpose.Delivery, FullResource, Permissions.OrderSubmit, ct);
            source.RequireContext(data.Reader, snapshot, tx); return true;
        }, CancellationToken.None))).Code);
        var change = await data.Organizations.ProposeAsync(data.ProfileMaker,
            new(OrganizationChangeKind.UpdateProfile, ScopeNodeKind.Branch, 10, "TEBET", "Synthetic changed invoice party profile", 11, 2, 1), OrganizationAddressTestData.Meta("address-capture-profile"));
        await data.Organizations.ApproveAsync(data.ProfileChecker, change.Id, 1, OrganizationAddressTestData.Meta("address-capture-profile-approved"));
        var view = await data.Service.ReadAsync(data.Reader, OrganizationAddressTestData.Branch, OrganizationAddressPurpose.Delivery);
        Assert.False(view["current"].AsBoolean); Assert.Equal("Synthetic Tebet branch", view["name"].AsString); Assert.Equal(B.Details(OrganizationAddressTestData.Postal), view["address"]);
        Assert.Equal("organization_address_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Base.Store.ReadAsync(data.Orderer, async (snapshot, tx, ct) => {
            await data.Service.ReadCurrentSourceAsync(data.Orderer, snapshot, tx, OrganizationAddressTestData.Branch,
                OrganizationAddressPurpose.Delivery, FullResource, Permissions.OrderSubmit, ct); return true;
        }, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Missing_immutable_version_or_real_effect_never_reconstructs_approved_postal_facts_or_overwrites_damaged_history() {
        await using var data = await OrganizationAddressTestData.CreateAsync(fixture);
        var approved = await data.DecideAsync(await data.ProposeAsync("address-history"), "address-history-approved");
        var decision = await data.Rows(B.Decisions).Find(new BsonDocument("_id", approved["recordId"])).SingleAsync();
        var version = await data.Rows(B.Versions).Find(new BsonDocument("_id", approved["versionId"])).SingleAsync();
        var audit = await data.Rows("auditEvents").Find(new BsonDocument("_id", decision["auditId"])).SingleAsync();
        var receipt = await data.Rows("idempotencyRecords").Find(new BsonDocument { { "actorId", data.Checker.UserId }, { "operation", B.Prefix + "approve" }, { "key", "address-history-approved" } }).SingleAsync();
        var outbox = await data.Rows("outboxEvents").Find(new BsonDocument("sourceKey", B.SourceKey(decision))).SingleAsync();
        foreach (var (table, row) in new[] { (B.Versions, version), ("auditEvents", audit), ("idempotencyRecords", receipt), ("outboxEvents", outbox) }) {
            Assert.Equal(1, (await data.Rows(table).DeleteOneAsync(new BsonDocument("_id", row["_id"]))).DeletedCount); var damaged = await data.StateAsync();
            try {
                foreach (var action in new Func<Task>[] {
                    async () => { await data.Service.ReadAsync(data.Reader, OrganizationAddressTestData.Branch, OrganizationAddressPurpose.Delivery); },
                    async () => { await data.Service.RecoverAsync(data.Checker, B.Prefix + "approve", "address-history-approved"); },
                    async () => { await data.Service.PreviewAsync(data.Maker, data.Input(1)); } })
                    Assert.Equal("organization_address_integrity_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(action)).Code);
                Assert.Equal(damaged, await data.StateAsync());
            } finally { await data.Rows(table).InsertOneAsync(row); }
        }
        Assert.True((await data.Service.ReadAsync(data.Reader, OrganizationAddressTestData.Branch, OrganizationAddressPurpose.Delivery))["current"].AsBoolean);
    }

    [Fact]
    public async Task Foreign_profile_corruption_is_not_joined_before_scoped_postal_target_selection() {
        await using var data = await OrganizationAddressTestData.CreateAsync(fixture);
        await data.DecideAsync(await data.ProposeAsync("address-foreign-filter-own"), "address-foreign-filter-own-approved");
        foreach (var (id, kind, parent, code, name) in new (long, ScopeNodeKind, long?, string, string)[] {
            (20, ScopeNodeKind.BuyingOrganization, null, "FOREIGN", "Synthetic foreign buying organization"),
            (21, ScopeNodeKind.Branch, 20, "FOREIGN-BRANCH", "Synthetic foreign branch") }) {
            var profile = await data.Organizations.ProposeAsync(data.ProfileMaker,
                new(OrganizationChangeKind.UpdateProfile, kind, parent, code, name, id, 1, 0), OrganizationAddressTestData.Meta("address-foreign-profile-" + id));
            await data.Organizations.ApproveAsync(data.ProfileChecker, profile.Id, 1, OrganizationAddressTestData.Meta("address-foreign-profile-" + id + "-approved"));
        }
        var foreign = new OrganizationAddressTarget(ScopeNodeKind.Branch, 21);
        await data.Rows("organizationProfiles").UpdateOneAsync(new BsonDocument("_id", 21L), Builders<BsonDocument>.Update.Set("name", "Unapproved foreign detail"));
        Assert.True((await data.Service.ReadAsync(data.Reader, OrganizationAddressTestData.Branch, OrganizationAddressPurpose.Delivery))["current"].AsBoolean);
        var targets = await data.Service.TargetsAsync(data.Reader, OrganizationAddressPurpose.Delivery);
        Assert.Equal(11L, Assert.Single(targets["items"].AsBsonArray)["target"]["id"].AsInt64);
        Assert.Equal("scope_denied", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Reader, foreign, OrganizationAddressPurpose.Delivery))).Code);
        Assert.Equal("organization_address_integrity_unavailable", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Service.ReadAsync(data.Maker, foreign, OrganizationAddressPurpose.Delivery))).Code);
    }

    private static readonly ResourceScope FullResource = new(BuyingOrganizationId: 10, BranchId: 11,
        FulfillmentOrganizationId: 100, ShopId: 101, WarehouseId: 102, ChannelId: 103, CreatedBy: 305);

    internal static Task<bool> MergePeopleAsync(OrganizationAddressTestData data, long canonicalUser, long aliasUser) => data.Unit.ExecuteAsync(async (tx, ct) => {
        await data.Rows("personRecords").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.PersonId(aliasUser)),
            Builders<BsonDocument>.Update.Set("currentEquivalenceId", PersonTestData.GroupId(canonicalUser)).Inc("version", 1L), cancellationToken: ct);
        await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(canonicalUser)),
            Builders<BsonDocument>.Update.Set("memberPersonIds", new BsonArray(new[] { PersonTestData.PersonId(canonicalUser), PersonTestData.PersonId(aliasUser) }.Order()))
                .Set("memberUserIds", new BsonArray(new[] { canonicalUser, aliasUser }.Order())).Inc("version", 1L), cancellationToken: ct);
        await data.Rows("personEquivalenceGroups").UpdateOneAsync(tx, new BsonDocument("_id", PersonTestData.GroupId(aliasUser)),
            Builders<BsonDocument>.Update.Set("status", "superseded").Inc("version", 1L), cancellationToken: ct); return true;
    });
}
