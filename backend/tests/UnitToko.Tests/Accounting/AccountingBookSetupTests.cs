using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Organizations;
using D = UnitToko.Tests.Accounting.AccountingBookSetupTestData;

namespace UnitToko.Tests.Accounting;

[Collection("Mongo")]
public sealed class AccountingBookSetupTests(MongoFixture fixture)
{
    [Fact]
    public async Task Actual_independent_setup_creates_one_existing_book_chart_and_open_period_with_original_actor_recovery()
    {
        await using var data = await D.CreateAsync(fixture);
        Assert.False((await data.Service.CurrentAsync(data.Reader, D.Key))["configured"].AsBoolean);
        var input = data.Input(); var preview = await data.Service.PreviewAsync(data.Maker, input);
        var proposed = await data.Service.ProposeAsync(data.Maker, input, preview["binding"].AsString, D.Meta("book-original"));
        Assert.Equal(proposed, await data.Service.ProposeAsync(data.Maker, input with { Mappings = [.. input.Mappings.Reverse()] }, preview["binding"].AsString, D.Meta("book-original")));
        Assert.Equal(proposed, await data.Service.LookupAsync(data.Maker, D.Prefix + "propose", "book-original"));
        var other = await data.Base.AddActorAsync(515, D.Scope, Permissions.AccountingBookPropose);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, other.UserId);
        var before = await data.StateAsync();
        Assert.Null(await data.Service.LookupAsync(other, D.Prefix + "propose", "book-original"));
        Assert.Null(await data.Service.LookupAsync(data.Maker, D.Prefix + "propose", "book-wrong-key")); Assert.Equal(before, await data.StateAsync());
        var approved = await data.DecideAsync(proposed, "book-original-approved");
        Assert.Equal(approved, await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "book-original-approved"));
        var book = await data.Rows(D.Books).Find(new BsonDocument()).SingleAsync();
        var chart = await data.Rows(D.Charts).Find(new BsonDocument()).SingleAsync();
        var period = await data.Rows(D.Periods).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(approved["bookId"], book["_id"]); Assert.Equal(D.Key.CanonicalKey, book["canonicalKey"]);
        Assert.Equal(approved["chartId"], chart["_id"]); Assert.Equal(chart["_id"], book["chartVersionId"]); Assert.Equal(book["_id"], chart["bookId"]);
        Assert.Equal(1L, book["version"].AsInt64); Assert.True(book["active"].AsBoolean); Assert.True(book["closedThrough"].IsBsonNull);
        var mappings = chart["mappings"].AsBsonArray;
        Assert.Equal(input.Mappings.Select(item => item.MappingKey).Order(), mappings.Select(row => row["mappingKey"].AsString).Order());
        Assert.All(mappings, row => { Assert.True(row["active"].AsBoolean); Assert.True(row["accountId"].AsInt64 > 0); });
        var payable = mappings.Single(row => row["mappingKey"] == "payable")["accountId"];
        Assert.Equal(payable, mappings.Single(row => row["mappingKey"] == "supplier_debt")["accountId"]);
        Assert.NotEqual(payable, mappings.Single(row => row["mappingKey"] == "inventory")["accountId"]);
        Assert.Equal(approved["periodId"], period["_id"]); Assert.Equal(book["_id"], period["bookId"]); Assert.Equal(input.InitialMonth, period["month"]);
        Assert.Equal("open", period["state"]); Assert.Equal(1L, period["version"].AsInt64); Assert.Equal(0L, period["dataRevision"].AsInt64); Assert.True(period["lastHistoryId"].IsBsonNull);
        var current = await data.Service.CurrentAsync(data.Reader, D.Key); Assert.True(current["configured"].AsBoolean);
        Assert.Equal(proposed["proposalId"], current["setup"]["id"]);
        var immutable = await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync(); var complete = await data.StateAsync();
        Assert.Equal("approved", (await data.Service.ReadAsync(data.Reader, proposed["proposalId"].AsInt64))["state"]);
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewAsync(data.Maker, input));
        Assert.Equal(complete, await data.StateAsync()); Assert.Equal(immutable, await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync());
        await data.NoFinancialEffectsAsync();
    }

    [Fact]
    public async Task Reviewed_setup_remains_valid_after_the_existing_posting_service_advances_its_first_period()
    {
        await using var data = await D.CreateAsync(fixture);
        var proposal = await data.ProposeAsync("book-for-posting"); var approved = await data.DecideAsync(proposal, "book-for-posting-approved");
        await data.NoFinancialEffectsAsync();
        var beforeProposal = await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync();
        var beforeDecision = await data.Rows(D.Decisions).Find(new BsonDocument()).SingleAsync();
        var beforeChart = await data.Rows(D.Charts).Find(new BsonDocument()).SingleAsync();
        var beforeBook = await data.Rows(D.Books).Find(new BsonDocument()).SingleAsync();
        // Existing trusted test adapter reads and writes a synthetic owner record in the same transaction.
        var poster = await data.Base.AddActorAsync(516, new(GrantScopeKind.BuyerOwned, 10, 11), Permissions.OrderSubmit);
        await data.Base.AddResourceAsync(42, 10, 11, poster.UserId);
        await data.Rows("fixtureBusinessRecords").UpdateOneAsync(new BsonDocument("_id", 42L), Builders<BsonDocument>.Update
            .Set("amount", new BsonDecimal128(100m)).Set("date", data.Input().InitialMonth + "-01").Set("mapping", "inventory"));
        var writer = new PostJournal(data.Database, data.Clock, [new FixtureSourceReader(data.Database)]);
        Task<BsonDocument> PostAsync() => data.Base.Store.ExecuteAuthorizedAsync(poster, "fixture.receipt", Permissions.OrderSubmit,
            new("book-consumer-post", "Synthetic owner command after approved setup", "book-consumer-test"), new { AccountingData.Source },
            async (tx, ct) => new(AuthorizationBson.Resource(await data.Rows("fixtureBusinessRecords").Find(tx, new BsonDocument("_id", 42L)).SingleAsync(ct))),
            async (tx, ct) => {
                var result = await writer.PostAsync(tx, poster, AccountingData.Source, 1, AccountingData.Metadata, ct);
                await data.Base.Store.FenceAsync(poster, [], tx, ct); return new BsonDocument("journalId", result.Id);
            });
        var posted = await PostAsync(); Assert.Equal(posted, await PostAsync());
        var journal = await data.Rows("journalEntries").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(approved["bookId"], journal["bookId"]); Assert.Equal(approved["chartId"], journal["chartId"]); Assert.Equal(100m, journal["total"].AsDecimal);
        var period = await data.Rows(D.Periods).Find(new BsonDocument()).SingleAsync();
        Assert.Equal(2L, period["version"].AsInt64); Assert.Equal(1L, period["dataRevision"].AsInt64); Assert.Equal("open", period["state"]); Assert.False(period["lastHistoryId"].IsBsonNull);
        Assert.True((await data.Rows(D.Books).Find(new BsonDocument()).SingleAsync())["fence"].AsInt64 > beforeBook["fence"].AsInt64);
        Assert.True((await data.Service.CurrentAsync(data.Reader, D.Key))["configured"].AsBoolean);
        Assert.Equal("approved", (await data.Service.ReadAsync(data.Reader, proposal["proposalId"].AsInt64))["state"]);
        Assert.Equal(approved, await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "book-for-posting-approved"));
        Assert.Equal(beforeProposal, await data.Rows(D.Proposals).Find(new BsonDocument()).SingleAsync());
        Assert.Equal(beforeDecision, await data.Rows(D.Decisions).Find(new BsonDocument()).SingleAsync());
        Assert.Equal(beforeChart, await data.Rows(D.Charts).Find(new BsonDocument()).SingleAsync());
    }

    [Fact]
    public async Task Rejected_setup_creates_no_core_records_and_does_not_prevent_a_new_independent_proposal()
    {
        await using var data = await D.CreateAsync(fixture);
        var first = await data.ProposeAsync("book-reject"); var rejected = await data.DecideAsync(first, "book-rejected", AccountingBookSetupDecision.Reject);
        Assert.Equal(rejected, await data.Service.LookupAsync(data.Checker, D.Prefix + "reject", "book-rejected"));
        foreach (var field in new[] { "bookId", "chartId", "periodId" }) Assert.True(rejected[field].IsBsonNull);
        foreach (var table in new[] { D.Books, D.Charts, D.Periods }) Assert.False(await data.Rows(table).Find(new BsonDocument()).AnyAsync());
        Assert.False((await data.Service.CurrentAsync(data.Reader, D.Key))["configured"].AsBoolean);
        Assert.Equal("rejected", (await data.Service.ReadAsync(data.Reader, first["proposalId"].AsInt64))["state"]);
        await data.DecideAsync(await data.ProposeAsync("book-after-rejection"), "book-after-rejection-approved");
        Assert.Equal(1, await data.Rows(D.Books).CountDocumentsAsync(new BsonDocument())); Assert.Equal(2, await data.Rows(D.Decisions).CountDocumentsAsync(new BsonDocument()));
        await data.NoFinancialEffectsAsync();
    }

    [Fact]
    public async Task Fresh_explicit_permission_and_independent_canonical_people_are_required_for_approval()
    {
        await using var data = await D.CreateAsync(fixture); var proposal = await data.ProposeAsync("book-person"); var id = proposal["proposalId"].AsInt64;
        foreach (var actor in new[] { data.Maker, data.Reader, data.Checker with { Assurance = FactorAssurance.SingleFactor }, data.Checker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) } })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(actor, id, AccountingBookSetupDecision.Approve));
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, AccountingBookSetupDecision.Approve);
        await OrganizationAddressTests.MergePeopleAsync(data.Organizations, data.Maker.UserId, data.Checker.UserId);
        var people = await data.Unit.ExecuteAsync((tx, ct) => new PersonRegistryService(data.Database, data.Clock).ResolveAsync(tx, [data.Maker.UserId, data.Checker.UserId], ct));
        Assert.Equal(people[0].PersonId, people[1].PersonId); var before = await data.StateAsync();
        var error = await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1, AccountingBookSetupDecision.Approve, preview["binding"].AsString, D.Meta("book-same-person")));
        Assert.True(error is AuthorizationDeniedException or DomainValidationException, error?.ToString()); Assert.Equal(before, await data.StateAsync());
    }

    [Fact]
    public async Task Exact_shop_scope_and_bounded_explicit_input_are_required_before_any_setup_is_written()
    {
        await using var data = await D.CreateAsync(fixture); var input = data.Input();
        foreach (var actor in new[] { data.Outsider, data.Warehouse }) {
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.CurrentAsync(actor, D.Key));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(actor, input));
        }
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Maker, input with { Key = D.Key with { ShopId = data.ForeignShopId } }));
        var before = await data.StateAsync();
        foreach (var invalid in new[] {
            input with { Key = D.Key with { Currency = "USD" } }, input with { InitialMonth = "2026-13" }, input with { InitialMonth = "2026-1" },
            input with { Mappings = [] }, input with { Mappings = [new("inventory", "ASTOCK"), new("inventory", "OTHER")] },
            input with { Mappings = [new(" inventory", "ASTOCK")] }, input with { Mappings = [new("inventory", " ASTOCK")] },
            input with { Mappings = [new("inventory", "1000")] }, input with { Mappings = [new(new string('a', 65), "ASTOCK")] },
            input with { Mappings = [.. Enumerable.Range(0, 1001).Select(index => new AccountingBookMappingInput("mapping" + index, "ASTOCK"))] } })
            await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.PreviewAsync(data.Maker, invalid));
        Assert.Equal(before, await data.StateAsync()); await data.NoFinancialEffectsAsync();
    }

    [Theory]
    [InlineData("profile")] [InlineData("grant")]
    public async Task Stale_profile_or_revoked_grant_invalidates_review_without_partial_core_records(string fault)
    {
        await using var data = await D.CreateAsync(fixture); var proposal = await data.ProposeAsync("book-stale"); var id = proposal["proposalId"].AsInt64;
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, AccountingBookSetupDecision.Approve);
        if (fault == "profile") await ChangeShopAsync(data);
        else await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        var before = await data.StateAsync();
        var error = await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1, AccountingBookSetupDecision.Approve, preview["binding"].AsString, D.Meta("book-stale-approved")));
        Assert.True(error is AuthorizationDeniedException or PersistenceConflictException, error?.ToString()); Assert.Equal(before, await data.StateAsync()); await data.NoFinancialEffectsAsync();
    }

    [Fact]
    public async Task Required_core_and_receipt_failures_roll_back_and_competing_approvals_configure_only_one_book()
    {
        await using var data = await D.CreateAsync(fixture); var first = await data.ProposeAsync("book-race-one"); var second = await data.ProposeAsync("book-race-two");
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, first["proposalId"].AsInt64, AccountingBookSetupDecision.Approve);
        var before = await data.StateAsync();
        foreach (var (table, field) in new[] { (D.Books, "active"), (D.Charts, "version"), (D.Periods, "state"), (D.Decisions, "decision"), ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") }) {
            var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
            var validator = definition["options"]["validator"].AsBsonDocument.DeepClone().AsBsonDocument;
            BsonValue prohibited = table == D.Books ? BsonBoolean.True : table == D.Charts ? new BsonInt64(1) : table == D.Periods ? new BsonString("open") : table == D.Decisions ? new BsonString("approve") : new BsonString(D.Prefix + "approve");
            try {
                await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", new BsonDocument("$and", new BsonArray { validator, new BsonDocument(field, new BsonDocument("$ne", prohibited)) }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
                Assert.NotNull(await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, first["proposalId"].AsInt64, 1, AccountingBookSetupDecision.Approve, preview["binding"].AsString, D.Meta("book-atomic-approved"))));
                Assert.Equal(before, await data.StateAsync());
            } finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } }); }
        }
        var secondPreview = await data.Service.PreviewDecisionAsync(data.Checker, second["proposalId"].AsInt64, AccountingBookSetupDecision.Approve);
        var results = await Task.WhenAll(new[] { (first, preview, "book-atomic-approved"), (second, secondPreview, "book-competitor-approved") }.Select(async item =>
            await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, item.Item1["proposalId"].AsInt64, 1, AccountingBookSetupDecision.Approve, item.Item2["binding"].AsString, D.Meta(item.Item3)))));
        Assert.Single(results, error => error is null); Assert.IsType<PersistenceConflictException>(Assert.Single(results, error => error is not null));
        foreach (var table in new[] { D.Books, D.Charts, D.Periods, D.Decisions }) Assert.Equal(1, await data.Rows(table).CountDocumentsAsync(new BsonDocument()));
        await data.NoFinancialEffectsAsync();
    }

    [Theory]
    [InlineData("profile")] [InlineData("grant")]
    public async Task Concurrent_source_or_authority_change_cannot_escape_the_real_preview_fences(string fault)
    {
        await using var data = await D.CreateAsync(fixture);
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = 0; var observed = 0; var reads = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => { if (command.Command.GetValue("find", BsonNull.Value) == "organizationProfiles" && Interlocked.Increment(ref reads) == 1) requestId = command.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(command => { if (requestId != 0 && command.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Accounting preview race did not complete");
            } });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var service = new AccountingBookSetupService(database, new(client), new(database), data.Clock);
        var pending = Task.Run(() => service.PreviewAsync(data.Maker, data.Input()));
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (fault == "profile") await ChangeShopAsync(data);
            else await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Maker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        } finally { release.Set(); }
        BsonDocument? result = null; var error = await Record.ExceptionAsync(async () => { result = await pending.WaitAsync(TimeSpan.FromSeconds(30)); });
        Assert.Equal(1, observed);
        if (fault == "grant") Assert.IsType<AuthorizationDeniedException>(error);
        else if (error is not null) Assert.IsType<PersistenceConflictException>(error);
        else Assert.Equal("Synthetic renamed shop", result!["shop"]["name"]);
        foreach (var table in new[] { D.Proposals, D.Decisions, D.Books, D.Charts, D.Periods }) Assert.False(await data.Rows(table).Find(new BsonDocument()).AnyAsync());
    }
    private static async Task ChangeShopAsync(D data) {
        var changed = await data.Organizations.Organizations.ProposeAsync(data.Organizations.ProfileMaker,
            new(OrganizationChangeKind.UpdateProfile, ScopeNodeKind.Shop, 100, "SHOP", "Synthetic renamed shop", 101, 2, 1), D.Meta("book-shop-changed"));
        await data.Organizations.Organizations.ApproveAsync(data.Organizations.ProfileChecker, changed.Id, 1, D.Meta("book-shop-changed-approved"));
    }
}
