using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Organizations;
using D = UnitToko.Tests.Ordering.OrderSpendingPolicyTestData;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
public sealed class OrderSpendingPolicyTests(MongoFixture fixture)
{
    private static readonly OrderBuyingContext Context = new(new(10), new(11));
    private static Task<OrderSpendingRule> ConsumeAsync(D data, OrderBuyingContext? context = null, ActorContext? actor = null) {
        var caller = actor ?? data.Orderer;
        return data.Base.Store.ReadAsync(caller, async (authority, tx, ct) => {
            var source = await data.Service.ReadCurrentSourceAsync(caller, authority, tx, context ?? Context, ct);
            source.RequireContext(caller, authority, tx); await source.FenceAsync(ct); return source.Rule;
        }, CancellationToken.None);
    }
    [Fact]
    public async Task Actual_independent_policy_approval_retains_history_and_lookup_only_original_actor_key_recovery() {
        await using var data = await D.CreateAsync(fixture);
        var missing = await data.Service.ReadAsync(data.Reader, D.Branch);
        Assert.Equal(0L, missing["version"].AsInt64); Assert.True(missing["rule"].IsBsonNull);
        Assert.Equal("order_spending_policy_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() => ConsumeAsync(data))).Code);
        var input = data.Input(); var preview = await data.Service.PreviewAsync(data.Maker, input);
        var proposed = await data.Service.ProposeAsync(data.Maker, input, preview.Binding, D.Meta("policy-first"));
        Assert.Equal(proposed, await data.Service.ProposeAsync(data.Maker, input, preview.Binding, D.Meta("policy-first")));
        var state = await data.StateAsync();
        Assert.Equal(proposed, await data.Service.LookupAsync(data.Maker, D.Prefix + "propose", "policy-first"));
        var otherMaker = await data.Base.AddActorAsync(417, D.Scope, D.Propose, Permissions.BranchPic);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, otherMaker.UserId);
        Assert.Null(await data.Service.LookupAsync(otherMaker, D.Prefix + "propose", "policy-first"));
        Assert.Null(await data.Service.LookupAsync(data.Maker, D.Prefix + "propose", "policy-wrong-key")); Assert.Equal(state, await data.StateAsync());
        var approved = await data.DecideAsync(proposed, "policy-first-approved");
        Assert.Equal(approved, await data.Service.LookupAsync(data.Checker, D.Prefix + "approve", "policy-first-approved"));
        var first = await data.Rows(D.Versions).Find(new BsonDocument("_id", approved["versionId"])).SingleAsync();
        Assert.Equal(input.Rule, await ConsumeAsync(data));
        Assert.Equal(OrderSpendingState.NotRequired, OrderSpendingPolicy.Initial(await ConsumeAsync(data), Money.Parse("500000.25")));
        Assert.Equal(OrderSpendingState.Pending, OrderSpendingPolicy.Initial(await ConsumeAsync(data), Money.Parse("500000.26")));
        var second = await data.DecideAsync(await data.ProposeAsync("policy-second", data.Input(1) with { Rule = new(OrderSpendingMode.NotRequired, null) }), "policy-second-approved");
        Assert.Equal(new OrderSpendingRule(OrderSpendingMode.NotRequired, null), await ConsumeAsync(data));
        Assert.Equal(first, await data.Rows(D.Versions).Find(new BsonDocument("_id", first["_id"])).SingleAsync());
        var successor = await data.Rows(D.Versions).Find(new BsonDocument("_id", second["versionId"])).SingleAsync();
        Assert.Equal(first["_id"], successor["previousVersionId"]); Assert.Equal(first["snapshotHash"], successor["previousHash"]);
        var current = await data.Service.ReadAsync(data.Reader, D.Branch);
        Assert.True(current["current"].AsBoolean); Assert.Equal(2L, current["version"].AsInt64); Assert.Equal(second["versionId"], current["versionId"]);
        foreach (var table in new[] { "orders", "orderDrafts", "stockBalances", "reservations", "movements", "journalEntries", "invoices", "creditVersions" })
            Assert.False(await data.Rows(table).Find(new BsonDocument()).AnyAsync());
    }
    [Fact]
    public async Task Approval_needs_fresh_current_permission_and_independent_canonical_people() {
        await using var data = await D.CreateAsync(fixture); var proposed = await data.ProposeAsync("policy-person");
        var id = proposed["proposalId"].AsInt64; var before = await data.StateAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(data.Maker, id, 1, OrderSpendingPolicyDecision.Approve));
        foreach (var actor in new[] { data.Checker with { Assurance = FactorAssurance.SingleFactor }, data.Checker with { AuthenticatedAt = data.Clock.UtcNow.AddMinutes(-6) } })
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(actor, id, 1, OrderSpendingPolicyDecision.Approve));
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, 1, OrderSpendingPolicyDecision.Approve);
        var grant = await data.Rows("authorizationGrants").Find(new BsonDocument("_id", data.Checker.UserId)).SingleAsync();
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.DecideAsync(data.Checker, id, 1, OrderSpendingPolicyDecision.Approve, preview.Binding, D.Meta("policy-revoked")));
        await data.Rows("authorizationGrants").ReplaceOneAsync(new BsonDocument("_id", data.Checker.UserId), grant);
        await OrganizationAddressTests.MergePeopleAsync(data.Organizations, data.Maker.UserId, data.Checker.UserId);
        var people = await data.Unit.ExecuteAsync((tx, ct) => new PersonRegistryService(data.Database, data.Clock).ResolveAsync(tx, [data.Maker.UserId, data.Checker.UserId], ct));
        Assert.Equal(people.Single(p => p.UserId == data.Maker.UserId).PersonId, people.Single(p => p.UserId == data.Checker.UserId).PersonId);
        var failure = await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1, OrderSpendingPolicyDecision.Approve, preview.Binding, D.Meta("policy-same-person")));
        Assert.True(failure is AuthorizationDeniedException or DomainValidationException, failure?.ToString());
        Assert.Equal(before, await data.StateAsync()); Assert.False(await data.Rows(D.Versions).Find(new BsonDocument()).AnyAsync());
    }
    [Fact]
    public async Task Scope_is_checked_before_policy_facts_and_selected_division_has_no_branch_fallback() {
        await using var data = await D.CreateAsync(fixture); await data.DecideAsync(await data.ProposeAsync("policy-branch"), "policy-branch-approved");
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.ReadAsync(data.Outsider, D.Branch));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewAsync(data.Outsider, data.Input(1)));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => ConsumeAsync(data, new(new(10), new(12))));
        var proposal = await data.Organizations.Organizations.ProposeAsync(data.Organizations.ProfileMaker,
            new(OrganizationChangeKind.Create, ScopeNodeKind.Division, 11, "SPEND", "Synthetic spending division"), D.Meta("policy-division"));
        var approved = await data.Organizations.Organizations.ApproveAsync(data.Organizations.ProfileChecker, proposal.Id, 1, D.Meta("policy-division-approved"));
        var division = approved.ScopeId!.Value; var context = Context with { DivisionId = new(division) };
        await data.Base.AddGrantAsync(data.Orderer, 415, new(GrantScopeKind.BuyerOwned, 10, 11, division), [Permissions.OrderSubmit]);
        Assert.Equal("order_spending_policy_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() => ConsumeAsync(data, context))).Code);
        var input = data.Input() with { Target = new(ScopeNodeKind.Division, division), ExpectedScopeVersion = 1,
            Rule = new(OrderSpendingMode.EveryOrder, null) };
        await data.DecideAsync(await data.ProposeAsync("policy-division-rule", input), "policy-division-rule-approved");
        Assert.Equal(input.Rule, await ConsumeAsync(data, context)); Assert.Equal(data.Input().Rule, await ConsumeAsync(data));
    }
    [Theory]
    [InlineData("source")] [InlineData("base")]
    public async Task Stale_review_cannot_approve_changed_profile_or_overwrite_new_policy_version(string fault) {
        await using var data = await D.CreateAsync(fixture); var proposed = await data.ProposeAsync("policy-stale");
        var id = proposed["proposalId"].AsInt64; var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, 1, OrderSpendingPolicyDecision.Approve);
        if (fault == "source") {
            var changed = await data.Organizations.Organizations.ProposeAsync(data.Organizations.ProfileMaker,
                new(OrganizationChangeKind.UpdateProfile, ScopeNodeKind.Branch, 10, "TEBET", "Synthetic changed branch", 11, 2, 1), D.Meta("policy-profile-change"));
            await data.Organizations.Organizations.ApproveAsync(data.Organizations.ProfileChecker, changed.Id, 1, D.Meta("policy-profile-change-approved"));
        } else await data.DecideAsync(await data.ProposeAsync("policy-other-proposal"), "policy-other-approved");
        var before = await data.StateAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.DecideAsync(data.Checker, id, 1, OrderSpendingPolicyDecision.Approve, preview.Binding, D.Meta("policy-stale-approved")));
        Assert.Equal(before, await data.StateAsync());
    }
    [Fact]
    public async Task Invalid_threshold_mode_window_or_target_is_rejected_before_a_proposal_exists() {
        await using var data = await D.CreateAsync(fixture); var input = data.Input(); var before = await data.StateAsync();
        foreach (var invalid in new[] {
            input with { Rule = new(OrderSpendingMode.AboveThreshold, null) }, input with { Rule = new(OrderSpendingMode.NotRequired, Money.Parse("1")) },
            input with { Rule = new((OrderSpendingMode)99, null) }, input with { ValidUntil = input.ValidFrom },
            input with { ValidUntil = input.ValidFrom.AddMilliseconds(-1) },
            input with { ValidFrom = DateTimeOffset.MinValue }, input with { Target = new(ScopeNodeKind.Warehouse, 102) },
            input with { ExpectedScopeVersion = 0 }, input with { ExpectedPolicyVersion = -1 } })
            await Assert.ThrowsAsync<DomainValidationException>(() => data.Service.PreviewAsync(data.Maker, invalid));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.PreviewAsync(data.Maker, input with { ValidUntil = data.Clock.UtcNow.AddMilliseconds(-1) }));
        Assert.Equal(before, await data.StateAsync());
        var zero = input with { Rule = new(OrderSpendingMode.AboveThreshold, Money.Parse("0.00")) };
        await data.DecideAsync(await data.ProposeAsync("policy-zero", zero), "policy-zero-approved");
        Assert.Equal(OrderSpendingState.Pending, OrderSpendingPolicy.Initial(await ConsumeAsync(data), Money.Parse("0.01")));
    }
    [Fact]
    public async Task Duplicate_approval_race_has_one_version_and_failed_required_effect_rolls_back_for_exact_key_retry() {
        await using var data = await D.CreateAsync(fixture); var proposal = await data.ProposeAsync("policy-atomic");
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, proposal["proposalId"].AsInt64, 1, OrderSpendingPolicyDecision.Approve);
        var before = await data.StateAsync(); const string key = "policy-atomic-approved";
        foreach (var (table, field) in new[] { (D.Versions, "version"), ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") }) {
            var definition = await (await data.Database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", table) })).SingleAsync();
            var validator = definition["options"]["validator"].AsBsonDocument.DeepClone().AsBsonDocument;
            try {
                BsonValue prohibited = table == D.Versions ? new BsonInt64(1) : new BsonString(D.Prefix + "approve");
                await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", new BsonDocument("$and", new BsonArray {
                    validator, new BsonDocument(field, new BsonDocument("$ne", prohibited)) }) }, { "validationLevel", "strict" }, { "validationAction", "error" } });
                Assert.NotNull(await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, proposal["proposalId"].AsInt64, 1, OrderSpendingPolicyDecision.Approve, preview.Binding, D.Meta(key))));
                Assert.Equal(before, await data.StateAsync());
            } finally { await data.Database.RunCommandAsync<BsonDocument>(new BsonDocument { { "collMod", table }, { "validator", validator }, { "validationLevel", "strict" }, { "validationAction", "error" } }); }
        }
        var second = await data.Base.AddActorAsync(416, D.Scope, D.Approve, Permissions.BranchPic);
        await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, second.UserId);
        var secondPreview = await data.Service.PreviewDecisionAsync(second, proposal["proposalId"].AsInt64, 1, OrderSpendingPolicyDecision.Approve);
        var results = await Task.WhenAll(new[] { (data.Checker, preview, key), (second, secondPreview, "policy-other-checker") }
            .Select(async value => await Record.ExceptionAsync(() => data.Service.DecideAsync(value.Item1, proposal["proposalId"].AsInt64, 1, OrderSpendingPolicyDecision.Approve, value.Item2.Binding, D.Meta(value.Item3)))));
        Assert.Single(results, failure => failure is null); Assert.IsType<PersistenceConflictException>(Assert.Single(results, failure => failure is not null));
        Assert.Equal(1, await data.Rows(D.Versions).CountDocumentsAsync(new BsonDocument())); Assert.Equal(1, await data.Rows(D.Decisions).CountDocumentsAsync(new BsonDocument()));
    }
    [Fact]
    public async Task Consumer_cannot_reuse_another_actor_context_or_expired_policy_after_awaits() {
        await using var data = await D.CreateAsync(fixture);
        await data.DecideAsync(await data.ProposeAsync("policy-consumer", data.Input() with { ValidUntil = data.Clock.UtcNow.AddMinutes(1) }), "policy-consumer-approved");
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Base.Store.ReadAsync(data.Orderer, async (authority, tx, ct) => {
            var source = await data.Service.ReadCurrentSourceAsync(data.Orderer, authority, tx, Context, ct);
            source.RequireContext(data.Reader, authority, tx); return true;
        }, CancellationToken.None));
        var before = data.Clock.UtcNow;
        try {
            Assert.Equal("order_spending_policy_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Base.Store.ReadAsync(data.Orderer, async (authority, tx, ct) => {
                var source = await data.Service.ReadCurrentSourceAsync(data.Orderer, authority, tx, Context, ct);
                data.Clock.UtcNow = before.AddMinutes(2); await source.FenceAsync(ct); return true;
            }, CancellationToken.None))).Code);
        } finally { data.Clock.UtcNow = before; }
    }
    [Theory]
    [InlineData("grant")] [InlineData("policy")]
    public async Task Concurrent_authority_or_policy_successor_cannot_escape_consumer_final_fences(string fault) {
        await using var data = await D.CreateAsync(fixture); await data.DecideAsync(await data.ProposeAsync("policy-race-first"), "policy-race-first-approved");
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = 0; var observed = 0; var reads = 0;
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("UNITTOKO_TEST_MONGO_URI") ?? "mongodb://127.0.0.1:27028/?replicaSet=rs0");
        settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => { if (command.Command.GetValue("find", BsonNull.Value) == D.Heads && Interlocked.Increment(ref reads) == 1) requestId = command.RequestId; });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (requestId != 0 && command.RequestId == requestId && Interlocked.CompareExchange(ref observed, 1, 0) == 0) {
                    entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Spending consumer race did not complete");
                }
            });
        };
        var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName); var unit = new MongoUnitOfWork(client);
        var authorization = new AuthorizationStore(database, unit, data.Clock);
        var service = new OrderSpendingPolicyService(database, unit, new(database), data.Clock, new(database, authorization, new(database), data.Clock));
        var pending = Task.Run(() => authorization.ReadAsync(data.Orderer, async (authority, tx, ct) => {
            var source = await service.ReadCurrentSourceAsync(data.Orderer, authority, tx, Context, ct);
            source.RequireContext(data.Orderer, authority, tx); await source.FenceAsync(ct); return source.Rule;
        }, CancellationToken.None));
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (fault == "grant") await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Orderer.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
            else await data.DecideAsync(await data.ProposeAsync("policy-race-next", data.Input(1) with { Rule = new(OrderSpendingMode.EveryOrder, null) }), "policy-race-next-approved");
        } finally { release.Set(); }
        OrderSpendingRule? result = null; var error = await Record.ExceptionAsync(async () => { result = await pending.WaitAsync(TimeSpan.FromSeconds(30)); });
        Assert.Equal(1, observed);
        if (error is not null) Assert.True(error is AuthorizationDeniedException or PersistenceConflictException, error.ToString());
        else { Assert.Equal("policy", fault); Assert.Equal(new OrderSpendingRule(OrderSpendingMode.EveryOrder, null), result); }
        if (fault == "grant") Assert.IsType<AuthorizationDeniedException>(error);
    }
}
