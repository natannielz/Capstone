using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;

[SupportedOSPlatform("windows")]
internal static class OrderSubstitutionTestData
{
    internal static async Task<(GenuineFiniteDispatchTestData Data, long ReplacementSku, ActorContext PriceMaker, ActorContext PriceChecker)> CreateAsync(MongoFixture fixture, bool systemClock, bool includeCustomerMappings = false) {
        var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, converted: true, createOrder: false, reviewedSafetyBufferBaseUnits: 0, useSystemClock: systemClock, includeCustomerMappings: includeCustomerMappings);
        try {
            var organizations = data.Stock.Valuation.Inspection.Receiving.Procurement.Organizations.Organizations;
            var admission = new ApprovedOrderAdmissionReader(data.Database, data.Unit, new(data.Database), data.Clock, organizations);
            data.Orders = new(data.Database, data.Unit, new(data.Database), data.Clock, admission);
            var preview = await data.Orders.PreviewAsync(data.Buyer, data.Input);
            var ordered = await data.Orders.SubmitAsync(data.Buyer, data.Input, preview.Review, GenuineFiniteDispatchTestData.Meta("substitution-fixture-order"));
            data.OrderId = ordered["orderId"].AsInt64; data.SuborderId = Assert.Single(ordered["suborderIds"].AsBsonArray).AsInt64;
            var spending = await data.Orders.ReviewOrderAsync(data.Spender, data.OrderId, null, OrderReviewStage.Spending);
            await data.Orders.DecideOrderAsync(data.Spender, data.OrderId, null, OrderReviewStage.Spending, spending.Revision, spending.Review, GenuineFiniteDispatchTestData.Meta("substitution-fixture-spending"));
            var store = await data.Orders.ReviewOrderAsync(data.Dispatcher, data.OrderId, data.SuborderId, OrderReviewStage.Store);
            await data.Orders.DecideOrderAsync(data.Dispatcher, data.OrderId, data.SuborderId, OrderReviewStage.Store, store.Revision, store.Review, GenuineFiniteDispatchTestData.Meta("substitution-fixture-acceptance"));
            data.Preparations = new(data.Database, data.Unit, new(data.Database), data.Clock, data.Orders);
            var reader = new ApprovedShipmentDispatchAdmissionReader(data.Database, data.Unit, new(data.Database), data.Clock, data.Orders, organizations);
            data.Dispatch = new(data.Database, data.Unit, new(data.Database), data.Clock, data.Orders, data.Preparations, reader); reader.AttachDispatchOwner(data.Dispatch);
            var scope = new GrantScope(GrantScopeKind.FulfillmentShop, 100, ShopId: 101, ChannelId: 103);
            await data.Base.AddGrantAsync(data.Dispatcher, 790101, scope, [Permissions.SubstitutionPropose]);
            await data.Base.AddGrantAsync(data.Buyer, 790102, CatalogAuthoringTestData.Audience, [Permissions.SubstitutionAccept]);
            var maker = await data.Base.AddActorAsync(790001, scope, Permissions.CatalogManage, Permissions.PricingManage);
            var checker = await data.Base.AddActorAsync(790002, scope, Permissions.PricingApprove);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, maker.UserId, checker.UserId);
            var sourceSku = data.Stock.Valuation.Inspection.Receiving.Procurement.Sku.Id.Value;
            var sku = await data.Rows("skus").Find(new BsonDocument("_id", sourceSku)).SingleAsync();
            var assortment = await data.Rows("catalogAssortments").Find(new BsonDocument("_id", 501L)).SingleAsync();
            await data.Catalog.Service.SetPublicationsAsync(maker, new(new(sourceSku), sku["revision"].AsInt64, [new(501)],
                [new(new(501), assortment["version"].AsInt64)]), GenuineFiniteDispatchTestData.Meta("substitution-fixture-publication"));
            var prices = new CatalogPriceService(data.Database, data.Unit, new(data.Database), data.Clock);
            var price = await prices.PreviewAsync(maker, sourceSku, CatalogPriceAccess.Prepare);
            var proposed = await prices.ProposeAsync(maker, sourceSku, price.Expected, new("IDR", new(30000m)), GenuineFiniteDispatchTestData.Meta("substitution-fixture-price"));
            await prices.DecideAsync(checker, proposed["proposalId"].AsInt64, 1, CatalogPriceDecision.Approve,
                "Independent actual replacement price", GenuineFiniteDispatchTestData.Meta("substitution-fixture-price-approved"));
            var release = await data.Stock.ProposeAsync("substitution-fixture-second-carton");
            // A real checker completes MFA again right before approving; system-clock fixtures exceed its freshness window.
            data.Stock.Checker = await data.IssueExistingSessionAsync(data.Stock.Checker);
            await data.Stock.DecideAsync(release["proposalId"].AsInt64, "substitution-fixture-second-carton-approved");
            return (data, sourceSku, maker, checker);
        } catch { await data.DisposeAsync(); throw; }
    }
}
