using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform;
using UnitToko.Platform.Identity;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Identity;
using UnitToko.Tests.Inventory;
using UnitToko.Tests.Ordering;
using UnitToko.Tests.Organizations;
using Xunit.Abstractions;
using G = UnitToko.Tests.Journeys.GoldenJourneyBrowserTests;

namespace UnitToko.Tests.Journeys;

/// <summary>Browser rehearsals for functions the journey matrix found untested (docs/quality/website-ready/journeys.md,
/// findings F-013, F-017, F-018). Same synthetic-world pattern and script as the golden journey.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class FunctionGapBrowserTests(MongoFixture fixture, ITestOutputHelper output)
{
    /// <summary>F-018 and F-017: the keranjang quantity changes and is saved; a store price change is shown and accepted only
    /// explicitly; removing the last line empties the keranjang; a draft is restored and discarded again; pemohon cancels an
    /// outstanding order. Stock is only reserved and released, never moved.</summary>
    [LocalStockBrowserFact]
    public async Task Cart_changes_draft_discard_and_buyer_cancellation_work_in_the_browser() {
        ActorContext? maker = null, checker = null;
        await G.OrderWorldAsync(fixture, output, "cart-and-cancellation", "FUNCTION_GAP_CART_AND_CANCELLATION_PASS", async (data, action) => {
            if (action != "change-price") return null;
            var shop = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
            maker ??= await data.Base.AddActorAsync(719120, shop, Permissions.CatalogManage, Permissions.PricingManage);
            checker ??= await data.Base.AddActorAsync(719121, shop, Permissions.PricingApprove);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, checker.UserId);
            var prices = new UnitToko.Platform.Catalog.CatalogPriceService(data.Database, data.Unit, new(data.Database), data.Clock);
            var preview = await prices.PreviewAsync(maker, data.SkuId, UnitToko.Modules.Catalog.CatalogPriceAccess.Prepare);
            var proposed = await prices.ProposeAsync(maker, data.SkuId, preview.Expected, new("IDR", UnitToko.Modules.Accounting.Money.Parse("25000.00")), GenuineFiniteDispatchTestData.Meta("cart-price-change"));
            await prices.DecideAsync(checker, proposed["proposalId"].AsInt64, 1, UnitToko.Modules.Catalog.CatalogPriceDecision.Approve,
                "Synthetic independent price change", GenuineFiniteDispatchTestData.Meta("cart-price-change-approved"));
            return new { display = "Rp25.000" };
        }, async (data, actors, lines) => {
            var orders = await data.Rows("orders").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            Assert.Single(orders); Assert.All(orders, order => Assert.Equal(actors["pemohon"].UserId, order["createdBy"].AsInt64));
            Assert.Equal(0L, await data.Rows("shipments").CountDocumentsAsync(new BsonDocument()));
            var balance = await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
            Assert.Equal(0L, UnitToko.Platform.Inventory.InventoryBson.ReadBalance(balance).Balance.Reserved);
        });
    }

    /// <summary>F-030 and F-017 (store part): after a first order from the keranjang (cancelled, so its stock returns), the next
    /// keranjang checks out as a new order, and peninjau-toko rejects its part of it. RED today: the second checkout reopens the
    /// first order as already recorded (checkout request key = actor + draft + group).</summary>
    [LocalStockBrowserFact]
    public async Task Second_order_from_the_keranjang_is_new_and_the_store_can_reject_its_part() =>
        await G.OrderWorldAsync(fixture, output, "second-order-store-rejection", "FUNCTION_GAP_SECOND_ORDER_STORE_REJECTION_PASS", null, async (data, actors, lines) => {
            var orders = await data.Rows("orders").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
            Assert.Equal(2, orders.Count);
            var balance = await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
            Assert.Equal(0L, UnitToko.Platform.Inventory.InventoryBson.ReadBalance(balance).Balance.Reserved);
        });

    /// <summary>F-013: pemohon composes and sends a branch request (PT-); peninjau-toko accepts it; a second one is rejected and
    /// the buyer reads the reason. Full release, not the home.arpa pilot.</summary>
    [LocalStockBrowserFact]
    public async Task Branch_request_is_sent_by_the_buyer_and_accepted_or_rejected_by_the_store_in_the_browser()
    {
        var root = G.RepositoryRoot(); var node = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_NODE");
        var playwright = Environment.GetEnvironmentVariable("UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE");
        Assert.True(Path.IsPathFullyQualified(node ?? "") && File.Exists(node)); Assert.True(Path.IsPathFullyQualified(playwright ?? "") && File.Exists(playwright));
        var buildPath = Path.Combine(root, "frontend", ".next-full", "BUILD_ID"); Assert.True(File.Exists(buildPath));
        await using var source = await StoreTestData.CreateAsync(fixture); source.Clock.UseSystemClock = true;
        var profiles = await OrganizationAddressTestData.PrepareAsync(source, fixture.Client);
        foreach (var (profileId, kind, parent, code, name) in new (long, ScopeNodeKind, long, string, string)[] {
            (12, ScopeNodeKind.Branch, 10, "OTHER", "Synthetic other branch"), (103, ScopeNodeKind.Channel, 100, "WEB", "Synthetic web channel") }) {
            var proposal = await profiles.Organizations.ProposeAsync(profiles.ProfileMaker,
                new(OrganizationChangeKind.UpdateProfile, kind, parent, code, name, profileId, 1, 0), CatalogAuthoringTestData.Metadata("profile-" + profileId));
            await profiles.Organizations.ApproveAsync(profiles.ProfileChecker, proposal.Id, 1, CatalogAuthoringTestData.Metadata("profile-approve-" + profileId));
        }
        var catalog = await CatalogAuthoringTestData.AttachAsync(source, fixture.Client);
        var sku = await catalog.SkuAsync(); await catalog.AssortmentsAsync();
        await catalog.Service.SetPublicationsAsync(catalog.Maker, new(sku.Id, 1, [new LongId(501)], [new(new LongId(501), 1)]), CatalogAuthoringTestData.Metadata("request-publish"));
        await source.AddGrantAsync(catalog.Buyer, 719301, CatalogAuthoringTestData.Audience, [Permissions.BranchOrderRequestRead, Permissions.BranchOrderRequestSubmit]);
        var store = await catalog.ActorAsync(719302, CatalogAuthoringTestData.Shop, Permissions.BranchOrderRequestRead, Permissions.BranchOrderRequestDecide);
        await PersonTestData.SeedDistinctPeopleAsync(source.Database, source.Clock, catalog.Buyer.UserId, store.UserId);
        var actors = new Dictionary<string, ActorContext> { ["pemohon"] = catalog.Buyer, ["peninjau-toko"] = store };
        foreach (var (name, actor) in actors)
            await source.Database.GetCollection<BsonDocument>("accountProfiles").InsertOneAsync(new BsonDocument { { "_id", actor.UserId }, { "schemaVersion", 1 }, { "version", 1L },
                { "displayName", "Peserta permintaan " + name }, { "verifiedEmail", "request-" + actor.UserId + "@example.invalid" },
                { "verifiedAt", source.Clock.UtcNow.UtcDateTime }, { "updatedAt", source.Clock.UtcNow.UtcDateTime } });
        var skuRow = await source.Database.GetCollection<BsonDocument>("skus").Find(new BsonDocument("_id", sku.Id.Value)).SingleAsync();

        using var keys = new IdentityRuntimeTests.RuntimeKeys(); var settings = IdentityRuntimeTests.Settings(keys);
        settings["Release:Profile"] = "full"; settings["Mongo:Database"] = source.Database.DatabaseNamespace.DatabaseName;
        settings["Identity:TrustedProxies:0"] = "127.0.0.1"; settings["Logging:LogLevel:Default"] = "Warning";
        var controlKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        using var factory = IdentityRuntimeTests.Factory(settings).WithWebHostBuilder(web => web.UseEnvironment("Production").ConfigureTestServices(services => {
            services.RemoveAll<IClock>(); services.AddSingleton<IClock>(source.Clock);
            services.RemoveAll<MongoIdentitySessionStore>(); services.AddSingleton(catalog.Sessions);
            services.AddSingleton<IStartupFilter>(new G.RenewControl(controlKey, _ => Task.FromResult<string?>(null)));
        }));
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0)); factory.StartServer();
        var apiAddress = Assert.Single(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        var prefix = "golden-r-" + Guid.NewGuid().ToString("N");
        var config = G.Hosting(apiAddress, playwright!, buildPath, prefix, controlKey, new {
            stage = "branch-requests", tokens = actors.ToDictionary(pair => pair.Key, pair => catalog.Issued[pair.Value.UserId].OpaqueToken),
            actorIds = actors.ToDictionary(pair => pair.Key, pair => pair.Value.UserId.ToString(CultureInfo.InvariantCulture)),
            skuCode = skuRow["sku"].AsString, branch = "10:11:" });
        await G.RunScriptAsync(output, "verify-golden-journey-browser.mjs", root, node!, config, prefix, "FUNCTION_GAP_BRANCH_REQUESTS_PASS", TimeSpan.FromMinutes(10));

        var requests = await source.Database.GetCollection<BsonDocument>("branchOrderRequests").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync();
        Assert.Equal(["accepted", "rejected"], requests.Select(row => row["state"].AsString).ToArray());
        Assert.All(requests, row => Assert.Equal(catalog.Buyer.UserId, row["createdBy"].AsInt64));
        // A branch request never reserves stock or creates an order.
        Assert.Equal(0L, await source.Database.GetCollection<BsonDocument>("orders").CountDocumentsAsync(new BsonDocument()));
    }
}
