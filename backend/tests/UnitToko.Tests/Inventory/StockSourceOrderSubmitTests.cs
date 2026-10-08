using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Catalog;

namespace UnitToko.Tests.Inventory;

/// <summary>Actual catalog authoring/publication/price approval and empty-source
/// activation. Existing fixture identities, catalog assignments and physical empty
/// handoff inputs are explicitly synthetic; no available lot/value/order is seeded.</summary>
[Collection("Mongo")]
public sealed class StockSourceOrderSubmitTests(MongoFixture fixture)
{
    [Fact]
    public async Task Current_buyer_source_capture_uses_real_catalog_and_control_without_staff_authority_or_stock_effects() {
        await using var data = await Data.CreateAsync(fixture); var physical = await data.Source.PhysicalAsync();
        await data.Authorization.ReadAsync(data.Buyer, async (authority, tx, ct) => {
            foreach (var permission in new[] { Permissions.StockRead, Permissions.StockReceive, Permissions.StockConvert, Permissions.StockSourceRead })
                Assert.False(ScopePolicy.Compile(data.Buyer, authority, permission, data.Source.Catalog.Clock.UtcNow).IsAllowed);
            var capture = await data.CaptureAsync(authority, tx, data.Line, data.Location, ct);
            capture.RequireContext(data.Sources, data.Buyer, authority, tx, data.Buying, data.Line);
            Assert.Equal(data.Source.PoolId, capture.Pool.Id); Assert.Equal(data.Location, capture.Pool.Location);
            Assert.True(capture.Authority.Id > 0); Assert.True(capture.Authority.Revision > 0);
            Assert.Equal(capture.Authority.Id, capture.Snapshot["policyView"]["policy"]["_id"].AsInt64);
            var clone = capture.Snapshot; clone["policyView"]["proposal"]["input"]["writer"] = "external";
            Assert.Equal("unitToko", capture.Snapshot["policyView"]["proposal"]["input"]["writer"].AsString);
            Assert.Equal("order_stock_source_changed", Assert.Throws<PersistenceConflictException>(() =>
                capture.RequireContext(new(data.Source.Catalog.Database, data.Source.Unit, new(data.Source.Catalog.Database), data.Source.Catalog.Clock),
                    data.Buyer, authority, tx, data.Buying, data.Line)).Code);
            Assert.Equal("order_stock_source_changed", Assert.Throws<PersistenceConflictException>(() =>
                capture.RequireContext(data.Sources, data.Buyer, authority with { }, tx, data.Buying, data.Line)).Code);
            await capture.FenceAsync(ct); capture.RequireCurrent(); return true;
        }, default);
        Assert.Equal(physical, await data.Source.PhysicalAsync());
    }

    [Fact]
    public async Task Constructed_quote_owner_package_or_price_cannot_replace_current_published_buyer_facts() {
        await using var data = await Data.CreateAsync(fixture); var before = await data.Source.PhysicalAsync(); var pool = await data.Source.PoolAsync();
        foreach (var forged in new[] {
            data.Line with { FulfillmentOwner = data.Line.FulfillmentOwner with { ChannelId = 203 } },
            data.Line with { Definition = data.Line.Definition with { BaseUnitsPerPackage = data.Line.Definition.BaseUnitsPerPackage + 1 } },
            data.Line with { Price = data.Line.Price with { UnitPrice = new Money(data.Line.Price.UnitPrice.Value + 1m) } } }) {
            var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => data.Authorization.ReadAsync(data.Buyer,
                async (authority, tx, ct) => { await data.PriorWorkAsync(tx, ct); return await data.CaptureAsync(authority, tx, forged, data.Location, ct); }, default));
            Assert.Equal("order_stock_source_changed", error.Code);
            await data.AssertRollbackAsync(before, pool);
        }
        var wrongBranch = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Authorization.ReadAsync(data.Buyer,
            (authority, tx, ct) => data.Sources.CaptureOrderSubmitSourceAsync(data.Buyer, authority, tx,
                new(new(10), new(12)), data.Line, data.Location, data.Source.PoolId, 1, ct), default));
        Assert.Equal("scope_denied", wrongBranch.Code); await data.AssertRollbackAsync(before, pool);
    }

    [Fact]
    public async Task Warm_capture_rechecks_deleted_activation_revoked_entitlement_and_actual_session_deadline_with_rollback() {
        await using var data = await Data.CreateAsync(fixture); var before = await data.Source.PhysicalAsync(); var pool = await data.Source.PoolAsync();
        var activation = await data.Rows(StockSourceActivationBson.Activations).Find(new BsonDocument()).SingleAsync();
        var clockBefore = data.Source.Catalog.Clock.UtcNow;
        try {
            foreach (var fault in new[] { "activation", "entitlement", "deadline" }) {
                if (fault == "entitlement") {
                    var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => WarmThenFailAsync(fault));
                    Assert.Equal("catalog_visibility_denied", error.Code);
                } else {
                    var error = await Assert.ThrowsAsync<PersistenceConflictException>(() => WarmThenFailAsync(fault));
                    Assert.Equal(fault == "activation" ? "stock_source_activation_unavailable" : "order_stock_source_changed", error.Code);
                }
                data.Source.Catalog.Clock.UtcNow = clockBefore;
                await data.AssertRollbackAsync(before, pool);
                Assert.Equal(activation, await data.Rows(StockSourceActivationBson.Activations).Find(new BsonDocument()).SingleAsync());
            }
        } finally { data.Source.Catalog.Clock.UtcNow = clockBefore; }
        Task<bool> WarmThenFailAsync(string fault) => data.Authorization.ReadAsync(data.Buyer, async (authority, tx, ct) => {
            var capture = await data.CaptureAsync(authority, tx, data.Line, data.Location, ct);
            await capture.FenceAsync(ct); capture.RequireCurrent(); await data.PriorWorkAsync(tx, ct);
            // Negative corruption of actual accepted rows, never a positive approval seed.
            if (fault == "activation") await data.Rows(StockSourceActivationBson.Activations).DeleteOneAsync(tx, new BsonDocument("_id", activation["_id"]), cancellationToken: ct);
            if (fault == "entitlement") await data.Rows("catalogEntitlements").UpdateOneAsync(tx, new BsonDocument("_id", 10001L),
                Builders<BsonDocument>.Update.Set("status", "revoked"), cancellationToken: ct);
            if (fault == "deadline") {
                Assert.True(capture.ValidUntil < DateTimeOffset.MaxValue);
                data.Source.Catalog.Clock.UtcNow = capture.ValidUntil;
            }
            await capture.FenceAsync(ct); return true;
        }, default);
    }

    [Fact]
    public async Task A_genuinely_approved_channel_alias_does_not_replace_the_quotes_canonical_fulfillment_owner() {
        await using var data = await Data.CreateAsync(fixture, alias: true); var before = await data.Source.PhysicalAsync(); var pool = await data.Source.PoolAsync();
        var alias = await data.Rows("stockSourceAliases").Find(new BsonDocument()).SingleAsync();
        Assert.True(alias["enabled"].AsBoolean); Assert.Equal(203L, alias["requestedLocation"]["channelId"].AsInt64);
        var error = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Authorization.ReadAsync(data.Buyer,
            async (authority, tx, ct) => { await data.PriorWorkAsync(tx, ct); return await data.CaptureAsync(authority, tx, data.Line,
                data.Location with { ChannelId = new(203) }, ct); }, default));
        Assert.Equal("scope_denied", error.Code); await data.AssertRollbackAsync(before, pool);
        Assert.Equal(alias, await data.Rows("stockSourceAliases").Find(new BsonDocument()).SingleAsync());
    }

    private sealed class Data : IAsyncDisposable
    {
        internal StockSourceActivationTests.Data Source { get; }
        internal StockSourceSetupService Sources { get; }
        internal AuthorizationStore Authorization { get; }
        internal ActorContext Buyer => Source.Catalog.Buyer;
        internal OrderBuyingContext Buying { get; } = new(new(10), new(11));
        internal StockLocation Location { get; } = new(new(100), new(101), new(102), new(103));
        internal OrderQuotedLine Line { get; }
        private Data(StockSourceActivationTests.Data source, OrderQuotedLine line) {
            Source = source; Line = line; Sources = source.Sources;
            Authorization = new(source.Catalog.Database, source.Unit, source.Catalog.Clock);
        }
        internal IMongoCollection<BsonDocument> Rows(string name) => Source.Rows(name);
        internal Task<StockSourceSetupService.OrderSourceCapture> CaptureAsync(AuthorizationSnapshot authority, IClientSessionHandle tx,
            OrderQuotedLine line, StockLocation location, CancellationToken ct) =>
            Sources.CaptureOrderSubmitSourceAsync(Buyer, authority, tx, Buying, line, location, Source.PoolId, 1, ct);
        internal Task PriorWorkAsync(IClientSessionHandle tx, CancellationToken ct) => Rows("fixtureBuyerSourcePriorWork").InsertOneAsync(tx,
            new BsonDocument("_id", 1L), cancellationToken: ct);
        internal async Task AssertRollbackAsync(BsonDocument physical, BsonDocument pool) {
            Assert.Equal(physical, await Source.PhysicalAsync()); Assert.Equal(pool, await Source.PoolAsync());
            Assert.Equal(0L, await Rows("fixtureBuyerSourcePriorWork").CountDocumentsAsync(new BsonDocument()));
        }
        internal static async Task<Data> CreateAsync(MongoFixture fixture, bool alias = false) {
            var source = await StockSourceActivationTests.Data.CreateAsync(fixture);
            try {
                var catalog = source.Catalog;
                var prices = await CatalogPriceTestData.CreateAsync(fixture, attachedCatalog: catalog);
                await catalog.Base.AddGrantAsync(catalog.Buyer, 6200, CatalogAuthoringTestData.Audience, [Permissions.OrderSubmit]);
                // Existing synthetic reviewed catalog assignments remain fixture inputs;
                // publication and independent approved price are genuine owner commands.
                await catalog.AssortmentsAsync();
                await catalog.Service.SetPublicationsAsync(prices.Maker, new(new(prices.SkuId), 1, [new(501)], [new(new(501), 1)]),
                    CatalogPriceTestData.Meta("buyer-source-publish"));
                await prices.ApproveAsync(await prices.ProposeAsync("buyer-source-price"), "buyer-source-price-approved");
                if (alias) {
                    var location = new StockLocation(new(100), new(101), new(102), new(203));
                    await catalog.Base.AddGrantAsync(source.Maker, 102110, location.ToScope(),
                        [Permissions.StockSourcePropose, Permissions.StockSourcePolicyPropose, Permissions.StockSourceRead]);
                    await catalog.Base.AddGrantAsync(source.Checker, 102210, location.ToScope(),
                        [Permissions.StockSourceApprove, Permissions.StockSourcePolicyApprove, Permissions.StockSourceRead]);
                    var proposal = await source.Sources.ProposeAliasAsync(source.Maker, source.PoolId, 1, location, 0, true,
                        StockSourceActivationTests.Data.Meta("buyer-source-alias"));
                    await source.Sources.DecideAliasAsync(source.Checker, proposal["proposalId"].AsInt64, 1, StockSourceSetupDecision.Approve,
                        "Synthetic independently reviewed mapped channel", StockSourceActivationTests.Data.Meta("buyer-source-alias-approved"));
                    await source.ApprovePolicyAsync("buyer-source-alias-policy");
                }
                var (id, handoff) = await source.ProposeAsync("buyer-source-activation");
                var review = await source.Sources.PreviewActivationDecisionAsync(source.Checker, id, StockSourceSetupDecision.Approve, handoff);
                await source.Sources.DecideActivationAsync(source.Checker, id, 1, StockSourceSetupDecision.Approve, handoff,
                    StockSourceActivationBson.ReadExpected(review["expected"].AsBsonDocument), review["binding"].AsString,
                    StockSourceActivationTests.Data.Meta("buyer-source-activation-approved"));
                var item = Assert.Single((await catalog.BuyerService.QueryAsync(catalog.Buyer, 0, 25, null)).Items);
                var quote = OrderQuotePolicy.Create([new(item.Sku.Id, item.Definition.Id, item.Definition.Version, item.CurrentPrice!.Id,
                    item.CurrentPrice.Version, 1)], [new(item.Family, item.Sku, item.Definition, item.CurrentPrice)]);
                return new(source, Assert.Single(quote.Lines));
            } catch { await source.DisposeAsync(); throw; }
        }
        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }
}
