using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using B = UnitToko.Platform.Inventory.StockSourceActivationBson;

namespace UnitToko.Tests.Inventory;

/// <summary>Actual registered storage and all seven real obligation owners. Provider,
/// personnel and physical attestations are synthetic, never production acceptance.</summary>
[Collection("Mongo")]
public sealed class StockSourceActivationTests(MongoFixture fixture)
{
    [Fact]
    public async Task Reviewed_empty_web_pool_activates_with_seven_real_owners_durable_history_and_original_key_recovery_without_importing_stock_or_value() {
        await using var data = await Data.CreateAsync(fixture); var physical = await data.PhysicalAsync();
        var preview = await data.Sources.PreviewActivationAsync(data.Maker, data.PoolId);
        Assert.False(preview["active"].AsBoolean); Assert.Equal(7, preview["obligations"].AsBsonArray.Count);
        foreach (var domain in Enum.GetValues<StockSourceObligationDomain>()) Assert.Contains(preview["obligations"].AsBsonArray,
            value => value["domain"] == domain.ToString() && value["complete"] == true && value["openCount"] == 0L && value["producerRevision"].AsInt64 > 0);
        var expected = B.ReadExpected(preview["expected"].AsBsonDocument); var handoff = data.Handoff();
        var proposed = await data.Sources.ProposeActivationAsync(data.Maker, data.PoolId, expected, handoff, Data.Meta("empty-handoff-original"));
        Assert.Equal(proposed, await data.Sources.ProposeActivationAsync(data.Maker, data.PoolId, expected, handoff, Data.Meta("empty-handoff-original")));
        Assert.False((await data.PoolAsync())["active"].AsBoolean);
        var proposalId = proposed["proposalId"].AsInt64;
        var review = await data.Sources.PreviewActivationDecisionAsync(data.Checker, proposalId, StockSourceSetupDecision.Approve, handoff);
        var current = B.ReadExpected(review["expected"].AsBsonDocument);
        var committed = await data.Sources.DecideActivationAsync(data.Checker, proposalId, 1, StockSourceSetupDecision.Approve, handoff,
            current, review["binding"].AsString, Data.Meta("empty-handoff-approval"));
        Assert.Equal("activated", committed["status"].AsString); Assert.True((await data.PoolAsync())["active"].AsBoolean);
        var restarted = new StockSourceSetupService(data.Catalog.Database, data.Unit, new(data.Catalog.Database), data.Catalog.Clock);
        Assert.Equal(committed, await restarted.DecideActivationAsync(data.Checker, proposalId, 1, StockSourceSetupDecision.Approve, handoff,
            current, review["binding"].AsString, Data.Meta("empty-handoff-approval")));
        Assert.Equal(proposed, await restarted.RecoverActivationAsync(data.Maker, data.PoolId, B.Propose, "empty-handoff-original", "recover-proposal"));
        Assert.Equal(committed, await restarted.RecoverActivationAsync(data.Checker, data.PoolId, B.Decide, "empty-handoff-approval", "recover-decision"));
        Assert.Null(await restarted.RecoverActivationAsync(data.Checker, data.PoolId, B.Decide, "unknown-original-key", "recover-absent"));
        Assert.Equal("activated", (await restarted.ReadActivationAsync(data.Checker, data.PoolId, proposalId))!["status"].AsString);
        Assert.Single((await restarted.ListActivationsAsync(data.Checker, data.PoolId, 0, 25)).Items);
        Assert.Equal(1L, await data.CountAsync(B.Proposals)); Assert.Equal(1L, await data.CountAsync(B.Decisions)); Assert.Equal(1L, await data.CountAsync(B.Activations));
        Assert.Equal(physical, await data.PhysicalAsync());
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction();
        await restarted.RequireActivatedPoolAsync(tx, await data.PoolAsync(), default); await tx.AbortTransactionAsync();
    }

    [Theory]
    [InlineData("same-person")] [InlineData("expired-session")] [InlineData("revoked-maker")]
    [InlineData("revoked-checker")] [InlineData("stale-mfa")] [InlineData("stale-handoff")]
    [InlineData("changed-pool")] [InlineData("foreign-branch")]
    public async Task Invalid_independence_authority_time_or_binding_cannot_activate_or_append_effects(string fault) {
        await using var data = await Data.CreateAsync(fixture);
        var (id, handoff) = await data.ProposeAsync("negative-original");
        var review = await data.Sources.PreviewActivationDecisionAsync(data.Checker, id, StockSourceSetupDecision.Approve, handoff);
        var expected = B.ReadExpected(review["expected"].AsBsonDocument); var checker = data.Checker;
        switch (fault) {
            case "same-person": await PersonTestData.SetEquivalenceAsync(data.Unit, data.Catalog.Database, data.Maker.UserId, data.Checker.UserId, true); break;
            case "expired-session": await data.Rows("identitySessions").UpdateOneAsync(new BsonDocument("_id", checker.Session!.Id),
                Builders<BsonDocument>.Update.Set("idleExpiresAt", data.Catalog.Clock.UtcNow.UtcDateTime)); break;
            case "revoked-maker": await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("userId", data.Maker.UserId),
                Builders<BsonDocument>.Update.Set("status", "revoked")); break;
            case "revoked-checker": await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("userId", checker.UserId),
                Builders<BsonDocument>.Update.Set("status", "revoked")); break;
            case "stale-mfa": data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddMinutes(5).AddMilliseconds(1); break;
            case "stale-handoff": data.Catalog.Clock.UtcNow = data.Catalog.Clock.UtcNow.AddMinutes(1); break;
            case "changed-pool": await data.Rows("inventoryPools").UpdateOneAsync(new BsonDocument("_id", data.PoolId), Builders<BsonDocument>.Update.Inc("fence", 1L)); break;
            case "foreign-branch": checker = await data.Catalog.ActorAsync(102400, new(UnitToko.Modules.Organizations.GrantScopeKind.FulfillmentWarehouse,
                100, ShopId: 201, WarehouseId: 202, ChannelId: 203), Permissions.StockSourceApprove, Permissions.StockSourceRead); break;
        }
        var before = await data.ActivationStateAsync(); var physical = await data.PhysicalAsync();
        var error = await Record.ExceptionAsync(() => data.Sources.DecideActivationAsync(checker, id, 1, StockSourceSetupDecision.Approve,
            handoff, expected, review["binding"].AsString, Data.Meta("negative-approval")));
        Assert.True(error is AuthorizationDeniedException or PersistenceConflictException or DomainValidationException, error?.ToString());
        Assert.Equal(before, await data.ActivationStateAsync()); Assert.Equal(physical, await data.PhysicalAsync()); Assert.False((await data.PoolAsync())["active"].AsBoolean);
        Assert.Equal(0L, await data.CountAsync(B.Decisions)); Assert.Equal(0L, await data.CountAsync(B.Activations));
    }

    [Fact]
    public async Task Concurrent_original_key_approval_commits_one_activation_and_changed_body_cannot_reuse_the_key() {
        await using var data = await Data.CreateAsync(fixture); var physical = await data.PhysicalAsync();
        var (id, handoff) = await data.ProposeAsync("racing-original");
        var review = await data.Sources.PreviewActivationDecisionAsync(data.Checker, id, StockSourceSetupDecision.Approve, handoff);
        var expected = B.ReadExpected(review["expected"].AsBsonDocument);
        async Task<BsonDocument?> Decide() {
            try { return await data.Sources.DecideActivationAsync(data.Checker, id, 1, StockSourceSetupDecision.Approve, handoff,
                expected, review["binding"].AsString, Data.Meta("racing-approval")); }
            catch (PersistenceConflictException error) when (error.Code == "version_conflict") { return null; }
        }
        var results = await Task.WhenAll(Decide(), Decide()); Assert.Contains(results, result => result is not null);
        var committed = results.First(result => result is not null)!;
        foreach (var result in results.Where(result => result is not null)) Assert.Equal(committed, result);
        Assert.Equal(1L, await data.CountAsync(B.Decisions)); Assert.Equal(1L, await data.CountAsync(B.Activations)); Assert.True((await data.PoolAsync())["active"].AsBoolean);
        Assert.Equal("idempotency_key_reused", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Sources.DecideActivationAsync(data.Checker,
            id, 1, StockSourceSetupDecision.Approve, handoff, expected, review["binding"].AsString,
            new("racing-approval", "Changed approval reason", "changed-body")))).Code);
        Assert.Equal(physical, await data.PhysicalAsync());
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(3, 0)] [InlineData(3, 3)]
    public async Task Even_zero_raw_balance_rows_available_or_held_stock_are_not_silently_imported(long packages, long held) {
        await using var data = await Data.CreateAsync(fixture); var pool = await data.PoolAsync();
        var balance = InventoryBson.Balance(InventoryGraphTests.Balance() with { PhysicalPoolId = data.PoolId });
        balance["onHand"] = packages; balance["held"] = held;
        await data.Rows("stockBalances").InsertOneAsync(balance); var before = await data.ActivationStateAsync(); var physical = await data.PhysicalAsync();
        Assert.Equal("stock_source_nonempty_handoff_required", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Sources.PreviewActivationAsync(data.Maker, data.PoolId))).Code);
        Assert.Equal(before, await data.ActivationStateAsync()); Assert.Equal(physical, await data.PhysicalAsync()); Assert.Equal(pool, await data.PoolAsync());
    }

    [Fact]
    public async Task Genuine_open_count_producer_prevents_activation_and_independent_rejection_preserves_its_original_record() {
        await using var data = await Data.CreateAsync(fixture); var (id, handoff) = await data.ProposeAsync("open-work-proposal");
        var sku = await data.Catalog.SkuAsync(12, "ACTIVATION-COUNT-12", "ACT");
        var counter = await data.Catalog.ActorAsync(102300, CatalogAuthoringTestData.Warehouse, Permissions.StockCount, Permissions.StockRead);
        await PersonTestData.SeedDistinctPeopleAsync(data.Catalog.Database, data.Catalog.Clock, counter.UserId);
        var counts = new StockCountService(data.Catalog.Database, data.Unit, new(data.Catalog.Database), data.Catalog.Clock);
        var source = Assert.Single((await counts.SourcesAsync(counter, CatalogAuthoringTestData.Warehouse, 0, 25)).Items);
        var count = await counts.RecordCountAsync(counter, new(CatalogAuthoringTestData.Warehouse, sku.Id.Value, sku.Version,
            sku.CurrentDefinitionId!.Value.Value, 2, "OBSERVED-LOCAL-LOT", PoolSelection: source.Pool.Selection), Data.Meta("open-count-original"));
        Assert.Equal("stock_source_obligations_outstanding", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            data.Sources.PreviewActivationDecisionAsync(data.Checker, id, StockSourceSetupDecision.Approve, handoff))).Code);
        var original = await data.Rows(StockCountBson.Collection).Find(new BsonDocument("_id", count.Id)).SingleAsync(); var physical = await data.PhysicalAsync();
        var reject = await data.Sources.PreviewActivationDecisionAsync(data.Checker, id, StockSourceSetupDecision.Reject, null);
        var result = await data.Sources.DecideActivationAsync(data.Checker, id, 1, StockSourceSetupDecision.Reject, null,
            B.ReadExpected(reject["expected"].AsBsonDocument), reject["binding"].AsString, Data.Meta("rejected-open-work"));
        Assert.Equal("rejected", result["status"].AsString); Assert.False((await data.PoolAsync())["active"].AsBoolean);
        Assert.Equal(0L, await data.CountAsync(B.Activations)); Assert.Equal(original, await data.Rows(StockCountBson.Collection).Find(new BsonDocument("_id", count.Id)).SingleAsync());
        Assert.Equal(count, await counts.RecoverAsync(counter, "open-count-original")); Assert.Equal(physical, await data.PhysicalAsync());
    }

    [Fact]
    public async Task Active_bit_without_immutable_activation_provenance_and_tampered_accepted_history_are_refused() {
        await using var data = await Data.CreateAsync(fixture);
        await data.Rows("inventoryPools").UpdateOneAsync(new BsonDocument("_id", data.PoolId), Builders<BsonDocument>.Update.Set("active", true));
        using (var tx = await fixture.Client.StartSessionAsync()) {
            tx.StartTransaction(); await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Sources.RequireActivatedPoolAsync(tx, data.Rows("inventoryPools")
                .Find(new BsonDocument("_id", data.PoolId)).Single(), default)); await tx.AbortTransactionAsync();
        }
        await data.Rows("inventoryPools").UpdateOneAsync(new BsonDocument("_id", data.PoolId), Builders<BsonDocument>.Update.Set("active", false));
        var (id, handoff) = await data.ProposeAsync("tamper-proposal"); var preview = await data.Sources.PreviewActivationDecisionAsync(data.Checker, id, StockSourceSetupDecision.Approve, handoff);
        await data.Sources.DecideActivationAsync(data.Checker, id, 1, StockSourceSetupDecision.Approve, handoff,
            B.ReadExpected(preview["expected"].AsBsonDocument), preview["binding"].AsString, Data.Meta("tamper-approval"));
        var before = await data.PhysicalAsync();
        await data.Rows(B.Activations).UpdateOneAsync(new BsonDocument("_id", id), Builders<BsonDocument>.Update.Set("decisionHash", new string('a', 64)));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Sources.ReadActivationAsync(data.Checker, data.PoolId, id));
        Assert.Equal(before, await data.PhysicalAsync());
    }

    [Fact]
    public async Task Suspension_invalidates_active_authority_and_empty_reactivation_requires_a_new_independently_reviewed_policy_and_handoff() {
        await using var data = await Data.CreateAsync(fixture); var physical = await data.PhysicalAsync();
        var (firstId, handoff) = await data.ProposeAsync("first-activation");
        var review = await data.Sources.PreviewActivationDecisionAsync(data.Checker, firstId, StockSourceSetupDecision.Approve, handoff);
        await data.Sources.DecideActivationAsync(data.Checker, firstId, 1, StockSourceSetupDecision.Approve, handoff,
            B.ReadExpected(review["expected"].AsBsonDocument), review["binding"].AsString, Data.Meta("first-activation-approved"));
        var accepted = await data.Rows(B.Activations).Find(new BsonDocument("_id", firstId)).SingleAsync();
        var stop = await data.Sources.PreviewSuspensionAsync(data.Checker, data.PoolId);
        await data.Sources.SuspendAsync(data.Checker, data.PoolId, new(stop["poolRevision"].AsInt64, stop["suspensionRevision"].AsInt64,
            stop["configurationHash"].AsString), Data.Meta("active-source-stopped"));
        Assert.False((await data.PoolAsync())["active"].AsBoolean);
        Assert.Equal("stock_source_activation_policy_required", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            data.Sources.PreviewActivationAsync(data.Maker, data.PoolId))).Code);
        await data.ApprovePolicyAsync("review-after-stop");
        var (secondId, secondHandoff) = await data.ProposeAsync("second-activation");
        var secondReview = await data.Sources.PreviewActivationDecisionAsync(data.Checker, secondId, StockSourceSetupDecision.Approve, secondHandoff);
        var expected = B.ReadExpected(secondReview["expected"].AsBsonDocument); Assert.Equal(1L, expected.ActivationRevision); Assert.Equal(1L, expected.SuspensionRevision);
        await data.Sources.DecideActivationAsync(data.Checker, secondId, 1, StockSourceSetupDecision.Approve, secondHandoff,
            expected, secondReview["binding"].AsString, Data.Meta("second-activation-approved"));
        Assert.True((await data.PoolAsync())["active"].AsBoolean); Assert.Equal(2L, await data.CountAsync(B.Activations));
        Assert.Equal(accepted, await data.Rows(B.Activations).Find(new BsonDocument("_id", firstId)).SingleAsync());
        Assert.Equal(physical, await data.PhysicalAsync());
    }

    internal sealed class Data(CatalogAuthoringTestData catalog) : IAsyncDisposable
    {
        internal CatalogAuthoringTestData Catalog => catalog;
        internal ActorContext Maker = null!, Checker = null!;
        internal long PoolId;
        internal MongoUnitOfWork Unit => new(catalog.Client);
        internal StockSourceSetupService Sources => new(catalog.Database, Unit, new(catalog.Database), catalog.Clock);
        internal IMongoCollection<BsonDocument> Rows(string name) => catalog.Rows(name);
        internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic independently reviewed empty handoff", "activation-native");
        internal StockSourceEmptyHandoff Handoff() => new("Synthetic executed empty physical handoff", catalog.Clock.UtcNow, 0, 0, true, true, true);
        internal Task<BsonDocument> PoolAsync() => Rows("inventoryPools").Find(new BsonDocument("_id", PoolId)).SingleAsync();
        internal Task<long> CountAsync(string name) => Rows(name).CountDocumentsAsync(new BsonDocument());
        internal static async Task<Data> CreateAsync(MongoFixture fixture) {
            var catalog = await CatalogAuthoringTestData.CreateAsync(fixture); var data = new Data(catalog);
            try {
                Assert.True(CollectionMigrations.CurrentVersion >= 102); Assert.True(await new CollectionMigrations(catalog.Database, catalog.Clock).IsCurrentAsync());
                data.Maker = await catalog.ActorAsync(102100, CatalogAuthoringTestData.Warehouse,
                    Permissions.StockSourcePropose, Permissions.StockSourcePolicyPropose, Permissions.StockSourceRead);
                data.Checker = await catalog.ActorAsync(102200, CatalogAuthoringTestData.Warehouse,
                    Permissions.StockSourceApprove, Permissions.StockSourcePolicyApprove, Permissions.StockSourceRead, Permissions.StockSourceSuspend);
                await PersonTestData.SeedDistinctPeopleAsync(catalog.Database, catalog.Clock, data.Maker.UserId, data.Checker.UserId);
                var input = new StockSourceSetupInput(StockSourceSetupService.StockCountLocation(CatalogAuthoringTestData.Warehouse),
                    "synthetic-empty-activation", "Synthetic empty WebLedger", StockAuthorityMode.WebLedger, StockPoolControl.SharedWebLedger,
                    60000, 0, null, null, "Synthetic discrepancy owner", "Synthetic executed empty physical handoff");
                var proposal = await data.Sources.ProposeAsync(data.Maker, input, Meta("activation-setup"));
                var approved = await data.Sources.DecideAsync(data.Checker, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
                    "Synthetic independent source setup", Meta("activation-setup-approved")); data.PoolId = approved["poolId"].AsInt64;
                await data.ApprovePolicyAsync("activation-policy"); return data;
            } catch { await data.DisposeAsync(); throw; }
        }
        internal async Task ApprovePolicyAsync(string key) {
            var preview = await Sources.PreviewOperatingPolicyAsync(Maker, PoolId);
            var expected = new StockSourcePolicyExpectation(preview["poolRevision"].AsInt64, preview["suspensionRevision"].AsInt64,
                preview["policyRevision"].AsInt64, preview["configurationHash"].AsString);
            var policy = await Sources.ProposeOperatingPolicyAsync(Maker, PoolId, expected,
                new(StockMovementWriter.UnitToko, null, 0, "Synthetic zero-stock reconciliation", "Synthetic suspend before outage"), Meta(key));
            await Sources.DecideOperatingPolicyAsync(Checker, policy["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
                "Synthetic independent policy review", Meta(key + "-approved"));
        }
        internal async Task<(long Id, StockSourceEmptyHandoff Handoff)> ProposeAsync(string key) {
            var preview = await Sources.PreviewActivationAsync(Maker, PoolId); var handoff = Handoff();
            var result = await Sources.ProposeActivationAsync(Maker, PoolId, B.ReadExpected(preview["expected"].AsBsonDocument), handoff, Meta(key));
            return (result["proposalId"].AsInt64, handoff);
        }
        internal async Task<BsonDocument> ActivationStateAsync() {
            var state = new BsonDocument();
            foreach (var name in new[] { "inventoryPools", B.Proposals, B.Decisions, B.Activations })
                state[name] = new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
            foreach (var (name, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "operation"), ("outboxEvents", "kind") })
                state[name] = new BsonArray(await Rows(name).Find(new BsonDocument(field, new BsonDocument("$in", new BsonArray { B.Propose, B.Decide })))
                    .Sort(new BsonDocument("_id", 1)).ToListAsync()); return state;
        }
        internal async Task<BsonDocument> PhysicalAsync() {
            var state = new BsonDocument();
            foreach (var name in new[] { "stockBalances", "lots", "movements", "reservations", "journalEntries", "invoices", "paymentSubmissions", "paymentAllocations", "cashMovements" })
                state[name] = new BsonArray(await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()); return state;
        }
        public ValueTask DisposeAsync() => catalog.DisposeAsync();
    }
}
