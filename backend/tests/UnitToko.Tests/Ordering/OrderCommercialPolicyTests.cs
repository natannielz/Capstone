using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Organizations;
using B = UnitToko.Platform.Ordering.OrderCommercialPolicyBson;
using D = UnitToko.Tests.Ordering.OrderSpendingPolicyTestData;

namespace UnitToko.Tests.Ordering;

// Genuine existing organization approvals and actual commercial producers. These
// native cases require the separate permission registration before they can run;
// this fixture never mutates the permission catalog or seeds approved policy rows.
[Collection("Mongo")]
public sealed class OrderCommercialPolicyTests(MongoFixture fixture)
{
    internal sealed class Data(D existing) : IAsyncDisposable
    {
        internal D Existing { get; } = existing;
        internal IMongoDatabase Database => Existing.Database;
        internal OrderCommercialPolicyService Service => new(Database, Existing.Unit, new(Database), Existing.Clock, Existing.Organizations.Organizations);
        internal ActorContext Maker = null!, Checker = null!, OtherMaker = null!;
        internal OrderCommercialSellerVersions SellerVersions = null!;
        internal static readonly OperationalBookKey Seller = new(100, 101, 103, "IDR");
        internal static readonly OrderBuyingContext Buying = new(new(10), new(11));
        internal OrderCommercialPolicyInput Input(long version = 0) => new(new(ScopeNodeKind.Branch, 11, Seller), 2, 1, SellerVersions, version,
            OrderCommercialPolicyRuleTests.Rule, Existing.Clock.UtcNow.AddMinutes(-1), Existing.Clock.UtcNow.AddDays(1));
        internal static AuthorizationCommandMetadata Meta(string key) => new(key, "Synthetic independently reviewed commercial bundle", "commercial-policy-test");
        internal IMongoCollection<BsonDocument> Rows(string name) => Database.GetCollection<BsonDocument>(name);
        internal async Task<BsonDocument> ProposeAsync(string key, OrderCommercialPolicyInput? input = null) {
            input ??= Input(); var preview = await Service.PreviewAsync(Maker, input);
            return await Service.ProposeAsync(Maker, input, preview.Binding, Meta(key));
        }
        internal async Task<BsonDocument> ApproveAsync(BsonDocument proposal, string key) {
            var preview = await Service.PreviewDecisionAsync(Checker, proposal["proposalId"].AsInt64, 1, OrderCommercialPolicyDecision.Approve);
            return await Service.DecideAsync(Checker, proposal["proposalId"].AsInt64, 1, OrderCommercialPolicyDecision.Approve, preview.Binding, Meta(key));
        }
        internal Task<BsonDocument> ConsumeAsync(OrderBuyingContext? buying = null, OperationalBookKey? seller = null) =>
            Existing.Base.Store.ReadAsync(Existing.Orderer, async (authority, tx, ct) => {
                var source = await Service.ReadCurrentSourceAsync(Existing.Orderer, authority, tx, buying ?? Buying, seller ?? Seller, ct);
                source.RequireContext(Existing.Orderer, authority, tx); var result = source.Snapshot;
                result["details"]["rule"]["taxRate"] = new BsonDecimal128(99m);
                Assert.Equal(10m, source.Rule.TaxRate); await source.FenceAsync(ct); return source.Snapshot;
            }, CancellationToken.None);
        internal async Task<BsonDocument> StateAsync() {
            var value = new BsonDocument();
            foreach (var name in OrderCommercialPolicySchemas.Collections) {
                var rows = await Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
                foreach (var row in rows) row.Remove("fence"); value[name] = new BsonArray(rows);
            }
            foreach (var (name, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
                value[name] = new BsonArray(await Rows(name).Find(new BsonDocument(field, new BsonRegularExpression("^ordering\\.commercial\\.policy\\."))).Sort(new BsonDocument("_id", 1)).ToListAsync());
            return value;
        }
        internal async Task NoBusinessEffectsAsync() {
            foreach (var name in new[] { "orders", "invoices", "stockBalances", "reservations", "movements", "journalEntries", "creditVersions" })
                Assert.False(await Rows(name).Find(new BsonDocument()).AnyAsync());
        }
        public ValueTask DisposeAsync() => Existing.DisposeAsync();
    }
    internal async Task<Data> CreateAsync() {
        // Missing registration is a real integration failure, never a test-local grant bypass.
        Assert.True(Permissions.Catalog.ContainsKey(B.ProposePermission), "Register the narrow commercial permissions before running native producer cases");
        var existing = await D.CreateAsync(fixture);
        try {
            var data = new Data(existing);
            foreach (var name in OrderCommercialPolicySchemas.Collections) {
                // Reuse the actual registered106 installation; no runtime storage
                // setup, deletion or reseeding may stand in for its migration.
                var definition = await AccountContactStorage.DefinitionAsync(data.Database, name, default); Assert.NotNull(definition);
                Assert.True(AccountContactStorage.SafeDefinition(definition, OrderCommercialPolicySchemas.Validator(name)));
                var keys = OrderCommercialPolicySchemas.Keys(name);
                var indexes = await (await data.Rows(name).Indexes.ListAsync()).ToListAsync();
                Assert.Contains(indexes, index => index["name"] == "_id_" && index["key"] == new BsonDocument("_id", 1));
                for (var i = 0; i < keys.Length; i++) {
                    var matching = indexes.Where(index => index["key"] == keys[i] || index["name"] == "ux_" + name + "_" + i).ToArray();
                    Assert.NotEmpty(matching);
                    Assert.All(matching, index => { Assert.Equal(keys[i], index["key"]); Assert.True(index.GetValue("unique", false).AsBoolean); });
                }
                Assert.All(indexes, index => {
                    Assert.False(InvoiceStorage.Unsafe(index)); Assert.False(index.Contains("partialFilterExpression"));
                    if (index["name"] != "_id_" && index.GetValue("unique", false).AsBoolean) Assert.Contains(keys, key => index["key"] == key);
                });
                Assert.False(await data.Rows(name).Find(new BsonDocument()).AnyAsync());
                var counter = await data.Rows("idCounters").Find(new BsonDocument("_id", name)).SingleOrDefaultAsync();
                Assert.True(counter is null || counter.GetValue("value", BsonNull.Value).IsInt64 && counter["value"] == 0L);
            }
            var channel = await existing.Organizations.Organizations.ProposeAsync(existing.Organizations.ProfileMaker,
                new(OrganizationChangeKind.UpdateProfile, ScopeNodeKind.Channel, 100, "RETAIL", "Synthetic retail channel", 103, 1, 0), Data.Meta("commercial-channel"));
            await existing.Organizations.Organizations.ApproveAsync(existing.Organizations.ProfileChecker, channel.Id, 1, Data.Meta("commercial-channel-approved"));
            var scope = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
            data.Maker = await existing.Base.AddActorAsync(901, scope, B.ProposePermission, B.ReadPermission);
            data.Checker = await existing.Base.AddActorAsync(902, scope, B.ApprovePermission, B.ReadPermission);
            data.OtherMaker = await existing.Base.AddActorAsync(903, scope, B.ReadPermission, B.ProposePermission);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, existing.Clock, 901, 902, 903);
            data.SellerVersions = await existing.Unit.ExecuteAsync(async (tx, ct) => {
                var organization = await existing.Organizations.Organizations.ReadAddressProfileAsync(tx, new(ScopeNodeKind.FulfillmentOrganization, 100), ct);
                var shop = await existing.Organizations.Organizations.ReadAddressProfileAsync(tx, new(ScopeNodeKind.Shop, 101), ct);
                var merchantChannel = await existing.Organizations.Organizations.ReadAddressProfileAsync(tx, new(ScopeNodeKind.Channel, 103), ct);
                return new OrderCommercialSellerVersions(organization["target"]["version"].AsInt64, organization["profile"]["version"].AsInt64,
                    shop["target"]["version"].AsInt64, shop["profile"]["version"].AsInt64, merchantChannel["target"]["version"].AsInt64, merchantChannel["profile"]["version"].AsInt64);
            });
            return data;
        } catch { await existing.DisposeAsync(); throw; }
    }

    [Fact]
    public async Task Genuine_independent_bundle_producer_replay_current_read_and_version_chain_have_no_capacity_or_order_effects() {
        await using var data = await CreateAsync();
        Assert.Equal("order_commercial_policy_changed", (await Assert.ThrowsAsync<PersistenceConflictException>(() => data.ConsumeAsync())).Code);
        var input = data.Input(); var preview = await data.Service.PreviewAsync(data.Maker, input);
        var proposal = await data.Service.ProposeAsync(data.Maker, input, preview.Binding, Data.Meta("commercial-original"));
        Assert.Equal(proposal, await data.Service.ProposeAsync(data.Maker, input, preview.Binding, Data.Meta("commercial-original")));
        Assert.Equal(proposal, await data.Service.LookupAsync(data.Maker, B.Prefix + "propose", "commercial-original"));
        Assert.Null(await data.Service.LookupAsync(data.OtherMaker, B.Prefix + "propose", "commercial-original"));
        var approved = await data.ApproveAsync(proposal, "commercial-original-approved"); var first = await data.ConsumeAsync();
        Assert.Equal(approved["versionId"], first["_id"]); Assert.Equal(1L, first["version"].AsInt64); Assert.Equal(10m, B.Rule(first["details"]["rule"]).TaxRate);
        var successor = await data.ApproveAsync(await data.ProposeAsync("commercial-successor", data.Input(1)), "commercial-successor-approved");
        var next = await data.ConsumeAsync(); Assert.Equal(successor["versionId"], next["_id"]); Assert.Equal(first["_id"], next["previousVersionId"]);
        Assert.Equal(first, await data.Rows(B.Versions).Find(new BsonDocument("_id", first["_id"])).SingleAsync()); await data.NoBusinessEffectsAsync();
    }
    [Fact]
    public async Task Original_maker_same_canonical_person_and_revoked_checker_cannot_approve_the_bundle() {
        await using var data = await CreateAsync(); var proposal = await data.ProposeAsync("commercial-independence"); var id = proposal["proposalId"].AsInt64;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(data.Maker, id, 1, OrderCommercialPolicyDecision.Approve));
        var staleFactor = data.Checker with { AuthenticatedAt = data.Existing.Clock.UtcNow.AddMinutes(-6) };
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.PreviewDecisionAsync(staleFactor, id, 1, OrderCommercialPolicyDecision.Approve));
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, id, 1, OrderCommercialPolicyDecision.Approve); var before = await data.StateAsync();
        var grant = await data.Rows("authorizationGrants").Find(new BsonDocument("_id", data.Checker.UserId)).SingleAsync();
        await data.Rows("authorizationGrants").UpdateOneAsync(new BsonDocument("_id", data.Checker.UserId), Builders<BsonDocument>.Update.Set("status", "revoked"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Service.DecideAsync(data.Checker, id, 1, OrderCommercialPolicyDecision.Approve, preview.Binding, Data.Meta("commercial-revoked")));
        await data.Rows("authorizationGrants").ReplaceOneAsync(new BsonDocument("_id", data.Checker.UserId), grant);
        await OrganizationAddressTests.MergePeopleAsync(data.Existing.Organizations, data.Maker.UserId, data.Checker.UserId);
        var error = await Record.ExceptionAsync(() => data.Service.DecideAsync(data.Checker, id, 1, OrderCommercialPolicyDecision.Approve, preview.Binding, Data.Meta("commercial-same-person")));
        Assert.True(error is AuthorizationDeniedException or DomainValidationException, error?.ToString()); Assert.Equal(before, await data.StateAsync()); await data.NoBusinessEffectsAsync();
    }
    [Fact]
    public async Task Stale_review_cannot_replace_a_new_current_bundle_and_exact_buyer_seller_scope_has_no_fallback() {
        await using var data = await CreateAsync(); var first = await data.ProposeAsync("commercial-stale");
        var preview = await data.Service.PreviewDecisionAsync(data.Checker, first["proposalId"].AsInt64, 1, OrderCommercialPolicyDecision.Approve);
        await data.ApproveAsync(await data.ProposeAsync("commercial-other"), "commercial-other-approved"); var before = await data.StateAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Service.DecideAsync(data.Checker, first["proposalId"].AsInt64, 1, OrderCommercialPolicyDecision.Approve, preview.Binding, Data.Meta("commercial-stale-approved")));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.ConsumeAsync(Data.Buying with { BranchId = new(12) }));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.ConsumeAsync(seller: Data.Seller with { ShopId = long.MaxValue }));
        Assert.Equal(before, await data.StateAsync()); await data.NoBusinessEffectsAsync();
    }
    [Fact]
    public async Task Future_expired_and_clock_rollback_bundle_cannot_supply_current_source_after_await() {
        await using var data = await CreateAsync(); var input = data.Input() with { ValidFrom = data.Existing.Clock.UtcNow.AddMinutes(1) };
        await data.ApproveAsync(await data.ProposeAsync("commercial-future", input), "commercial-future-approved");
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.ConsumeAsync());
        data.Existing.Clock.UtcNow = data.Existing.Clock.UtcNow.AddMinutes(2); await data.ConsumeAsync();
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Existing.Base.Store.ReadAsync(data.Existing.Orderer, async (authority, tx, ct) => {
            var source = await data.Service.ReadCurrentSourceAsync(data.Existing.Orderer, authority, tx, Data.Buying, Data.Seller, ct);
            await Task.Yield(); data.Existing.Clock.UtcNow = data.Existing.Clock.UtcNow.AddMilliseconds(-1); await source.FenceAsync(ct); return true;
        }, CancellationToken.None));
        data.Existing.Clock.UtcNow = input.ValidUntil; await Assert.ThrowsAsync<PersistenceConflictException>(() => data.ConsumeAsync()); await data.NoBusinessEffectsAsync();
    }
    [Fact]
    public async Task Genuine_changed_seller_profile_invalidates_current_bundle_without_rewriting_approved_history() {
        await using var data = await CreateAsync();
        await data.ApproveAsync(await data.ProposeAsync("commercial-profile"), "commercial-profile-approved"); var original = await data.ConsumeAsync();
        var changed = await data.Existing.Organizations.Organizations.ProposeAsync(data.Existing.Organizations.ProfileMaker,
            new(OrganizationChangeKind.UpdateProfile, ScopeNodeKind.Channel, 100, "RETAIL", "Synthetic changed retail channel", 103, 2, 1), Data.Meta("commercial-channel-change"));
        await data.Existing.Organizations.Organizations.ApproveAsync(data.Existing.Organizations.ProfileChecker, changed.Id, 1, Data.Meta("commercial-channel-change-approved"));
        await Assert.ThrowsAsync<PersistenceConflictException>(() => data.ConsumeAsync());
        Assert.Equal(original, await data.Rows(B.Versions).Find(new BsonDocument("_id", original["_id"])).SingleAsync()); await data.NoBusinessEffectsAsync();
    }
}
