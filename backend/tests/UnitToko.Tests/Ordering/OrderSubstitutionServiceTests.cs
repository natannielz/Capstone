using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Authorization;
using UnitToko.Tests.Catalog;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Inventory;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;
using S = UnitToko.Platform.Ordering.OrderSubstitutionBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OrderSubstitutionServiceTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Sealed_receipt_origins_preserve_the_genuine_substitution_and_replacement_dispatch_flow() {
        UnitToko.Platform.Integrity.ProvenanceSealRing.Configure(UnitToko.Platform.Integrity.SealKeyRing.ForKeys("k1",
            new Dictionary<string, byte[]> { ["k1"] = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32) }));
        try { await Genuine_partial_dispatch_proposal_decline_and_buyer_consent_preserve_history_and_atomically_replace_only_unshipped_reservations(); }
        finally { UnitToko.Platform.Integrity.ProvenanceSealRing.Configure(null); }
    }

    [WindowsStorageFact]
    public async Task Genuine_partial_dispatch_proposal_decline_and_buyer_consent_preserve_history_and_atomically_replace_only_unshipped_reservations() {
        var prepared = await ApprovedShipmentDispatchAdmissionReaderTests.PreparedAsync(fixture, converted: true);
        await using var data = prepared.Data;
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
            [new(new(501), assortment["version"].AsInt64)]), GenuineFiniteDispatchTestData.Meta("substitution-carton-publication"));
        var prices = new CatalogPriceService(data.Database, data.Unit, new(data.Database), data.Clock);
        var priceView = await prices.PreviewAsync(maker, sourceSku, CatalogPriceAccess.Prepare);
        var priceProposal = await prices.ProposeAsync(maker, sourceSku, priceView.Expected, new("IDR", new(30000m)), GenuineFiniteDispatchTestData.Meta("substitution-carton-price"));
        await prices.DecideAsync(checker, priceProposal["proposalId"].AsInt64, 1, CatalogPriceDecision.Approve,
            "Independent package price for an explicit buyer replacement", GenuineFiniteDispatchTestData.Meta("substitution-carton-price-approved"));
        // The actual conversion consumed one of two genuinely valued/quality-accepted cartons.
        // Release the second through the same owner; no balance or value row is fabricated.
        var release = await data.Stock.ProposeAsync("substitution-second-carton-release");
        await data.Stock.DecideAsync(release["proposalId"].AsInt64, "substitution-second-carton-approved");
        var original = (await data.Rows(B.Orders).Find(new BsonDocument("_id", data.OrderId)).SingleAsync()).DeepClone().AsBsonDocument;
        original.Remove("fence");
        var shipmentReview = await data.PrepareDispatchAsync(8, "substitution-original-eight");
        var shipped = await data.DispatchAsync(shipmentReview, "substitution-original-eight-dispatched");
        var shipment = await data.Rows("shipments").Find(new BsonDocument("_id", shipped["shipmentId"])).SingleAsync();
        var historicalCommercial = await data.Unit.ExecuteAsync((tx, ct) => data.Orders.InvoiceCommercialAsync(tx, data.Buyer, shipment, ct));
        Assert.Equal(12, Assert.Single(historicalCommercial["quote"]["lines"].AsBsonArray)["packages"].AsInt32);
        var packages = await data.Orders.SubstitutionPackagesAsync(data.Dispatcher, data.OrderId, data.SuborderId);
        var replacement = Assert.Single(packages.Items, line => line.Definition.SkuId.Value == sourceSku);
        async Task<OrderSubstitutionInput> Input() {
            var targets = await data.Orders.ListSubstitutionTargetsAsync(data.Dispatcher);
            var target = Assert.Single(targets.Items, item => item.SuborderId.Value == data.SuborderId);
            Assert.Equal(4, Assert.Single(target.Lines, line => line.Catalog.Definition.SkuId.Value == data.SkuId).RemainingPackages);
            return new(new(data.OrderId), new(data.SuborderId), target.Revision, target.DispatchSequence,
                new(new(data.SkuId), 4, new(replacement.Definition.SkuId, replacement.Definition.Id, replacement.Definition.Version, replacement.Price.Id, replacement.Price.Version, 1)));
        }
        async Task<BsonDocument> Effects() {
            var state = new BsonDocument();
            foreach (var name in new[] { "stockBalances", LotValueStore.AvailableHeads, LotValueStore.HeldHeads, LotValueStore.Transitions,
                "journalEntries", "invoices", "paymentAllocations" }) state.Add(name, new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
            return state;
        }
        var input = await Input(); var beforeProposal = await Effects();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Orders.PreviewSubstitutionAsync(data.Buyer, input));
        var proposalReview = await data.Orders.PreviewSubstitutionAsync(data.Dispatcher, input);
        var proposed = await data.Orders.ProposeSubstitutionAsync(data.Dispatcher, input, proposalReview.Review, GenuineFiniteDispatchTestData.Meta("substitution-proposed-decline"));
        Assert.Equal(beforeProposal, await Effects());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => data.Orders.PreviewSubstitutionDecisionAsync(data.Dispatcher, proposed["proposalId"].AsInt64, OrderSubstitutionDecision.Accept));
        var decline = await data.Orders.PreviewSubstitutionDecisionAsync(data.Buyer, proposed["proposalId"].AsInt64, OrderSubstitutionDecision.Decline);
        var declined = await data.Orders.DecideSubstitutionAsync(data.Buyer, decline.ProposalId.Value, decline.Revision, OrderSubstitutionDecision.Decline, decline.Review,
            GenuineFiniteDispatchTestData.Meta("substitution-declined"));
        Assert.Equal(beforeProposal, await Effects());
        Assert.Equal(declined, await data.Orders.RecoverSubstitutionDecisionAsync(data.Buyer, "substitution-declined", OrderSubstitutionDecision.Decline));
        input = await Input(); proposalReview = await data.Orders.PreviewSubstitutionAsync(data.Dispatcher, input);
        proposed = await data.Orders.ProposeSubstitutionAsync(data.Dispatcher, input, proposalReview.Review, GenuineFiniteDispatchTestData.Meta("substitution-proposed-accept"));
        var changedPrice = await prices.PreviewAsync(maker, sourceSku, CatalogPriceAccess.Prepare);
        var changedProposal = await prices.ProposeAsync(maker, sourceSku, changedPrice.Expected, new("IDR", new(29000m)), GenuineFiniteDispatchTestData.Meta("substitution-price-new-version"));
        await prices.DecideAsync(checker, changedProposal["proposalId"].AsInt64, 1, CatalogPriceDecision.Approve,
            "A new approved price version requires consent to the exact current price", GenuineFiniteDispatchTestData.Meta("substitution-price-new-version-approved"));
        var unchanged = await Effects();
        var changedFailure = await Assert.ThrowsAnyAsync<Exception>(() => data.Orders.PreviewSubstitutionDecisionAsync(data.Buyer, proposed["proposalId"].AsInt64, OrderSubstitutionDecision.Accept));
        Assert.True(changedFailure is DomainValidationException or UnitToko.Platform.Mongo.PersistenceConflictException or AuthorizationDeniedException, changedFailure.ToString());
        Assert.Equal(unchanged, await Effects());
        packages = await data.Orders.SubstitutionPackagesAsync(data.Dispatcher, data.OrderId, data.SuborderId);
        replacement = Assert.Single(packages.Items, line => line.Definition.SkuId.Value == sourceSku);
        input = await Input(); proposalReview = await data.Orders.PreviewSubstitutionAsync(data.Dispatcher, input);
        proposed = await data.Orders.ProposeSubstitutionAsync(data.Dispatcher, input, proposalReview.Review, GenuineFiniteDispatchTestData.Meta("substitution-proposed-current-version"));
        var consent = await data.Orders.PreviewSubstitutionDecisionAsync(data.Buyer, proposed["proposalId"].AsInt64, OrderSubstitutionDecision.Accept);
        Assert.Equal(8, Assert.Single(consent.CatalogAfter.Lines, line => line.Definition.SkuId.Value == data.SkuId).Packages);
        Assert.Equal(1, Assert.Single(consent.CatalogAfter.Lines, line => line.Definition.SkuId.Value == sourceSku).Packages);
        // 8 retained packages at 20000 plus one carton at the current approved 29000 price version.
        Assert.Equal(new Money(189000m), consent.CatalogAfter.CatalogSubtotal);
        Assert.Equal(OrderSpendingState.Pending.ToString(), consent.SpendingState);
        var originalPhysical = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        var replacementPhysical = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.Stock.Valuation.Receipt["balanceId"])).SingleAsync());
        var beforeAccept = await Effects();
        var accepted = await data.Orders.DecideSubstitutionAsync(data.Buyer, consent.ProposalId.Value, consent.Revision, OrderSubstitutionDecision.Accept, consent.Review,
            GenuineFiniteDispatchTestData.Meta("substitution-accepted"));
        Assert.Equal(originalPhysical with { Balance = StockBalancePolicy.Release(originalPhysical.Balance, originalPhysical.Balance.Revision, 4) },
            InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync()));
        Assert.Equal(replacementPhysical with { Balance = StockBalancePolicy.Reserve(replacementPhysical.Balance, replacementPhysical.Balance.Revision, 1) },
            InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", replacementPhysical.Id)).SingleAsync()));
        var afterAccept = await Effects();
        foreach (var field in beforeAccept.Where(field => field.Name != "stockBalances")) Assert.Equal(field.Value, afterAccept[field.Name]);
        Assert.Equal(accepted, await data.Orders.RecoverSubstitutionDecisionAsync(data.Buyer, "substitution-accepted", OrderSubstitutionDecision.Accept));
        Assert.Equal(afterAccept, await Effects());
        Assert.Equal(2L, await data.Rows("movements").CountDocumentsAsync(new BsonDocument("source.namespace", "ordering.substitution")));
        var immutable = (await data.Rows(B.Orders).Find(new BsonDocument("_id", data.OrderId)).SingleAsync()).DeepClone().AsBsonDocument; immutable.Remove("fence");
        Assert.Equal(original, immutable);
        Assert.Equal(historicalCommercial, await data.Unit.ExecuteAsync((tx, ct) => data.Orders.InvoiceCommercialAsync(tx, data.Buyer, shipment, ct)));
        var renewed = await data.Orders.ReadFulfillmentOrderAsync(data.Dispatcher, data.SuborderId); Assert.False(renewed.CanPrepare);
        var spend = await data.Orders.ReviewOrderAsync(data.Spender, data.OrderId, null, OrderReviewStage.Spending);
        Assert.Equal(consent.Total, spend.Total);
        await data.Orders.DecideOrderAsync(data.Spender, data.OrderId, null, OrderReviewStage.Spending, spend.Revision, spend.Review, GenuineFiniteDispatchTestData.Meta("substitution-spending-renewed"));
        var store = await data.Orders.ReviewOrderAsync(data.Dispatcher, data.OrderId, data.SuborderId, OrderReviewStage.Store);
        await data.Orders.DecideOrderAsync(data.Dispatcher, data.OrderId, data.SuborderId, OrderReviewStage.Store, store.Revision, store.Review, GenuineFiniteDispatchTestData.Meta("substitution-store-renewed"));
        renewed = await data.Orders.ReadFulfillmentOrderAsync(data.Dispatcher, data.SuborderId); Assert.True(renewed.CanPrepare);
        Assert.Equal(0, Assert.Single(renewed.Lines, line => line.SkuId.Value == data.SkuId).RemainingPackages);
        Assert.Equal(1, Assert.Single(renewed.Lines, line => line.SkuId.Value == sourceSku).RemainingPackages);
        var previousPreparation = await data.Preparations.ReadAsync(data.Dispatcher, data.SuborderId);
        Assert.NotNull(previousPreparation);
        await data.Preparations.SaveAsync(data.Dispatcher, data.SuborderId, previousPreparation.Revision, renewed.Review,
            [new(new(sourceSku), 1, accepted["revision"].AsInt64)], GenuineFiniteDispatchTestData.Meta("substitution-replacement-prepared"));
        var replacementPreparation = await data.Dispatch.ReadPreparationAsync(data.Dispatcher, data.SuborderId);
        var replacementLot = Assert.Single(replacementPreparation.Lots, lot => lot.Reservation.SkuId.Value == sourceSku);
        var replacementReview = await data.Dispatch.PreviewAsync(data.Dispatcher, data.SuborderId, replacementPreparation.PreparationId.Value,
            replacementPreparation.PreparationRevision, data.Courier.UserId, [new(replacementLot.Reservation.ReservationId, 1)]);
        var replacementDispatch = await data.DispatchAsync(replacementReview, "substitution-replacement-dispatched");
        var replacementShipment = await data.Rows("shipments").Find(new BsonDocument("_id", replacementDispatch["shipmentId"])).SingleAsync();
        var replacementCommercial = await data.Unit.ExecuteAsync((tx, ct) => data.Orders.InvoiceCommercialAsync(tx, data.Buyer, replacementShipment, ct));
        var replacementQuote = B.Quote(replacementCommercial["quote"].AsBsonDocument);
        Assert.Equal(consent.CatalogAfter.CatalogSubtotal, replacementQuote.CatalogSubtotal);
        Assert.Equal(8, Assert.Single(replacementQuote.Lines, line => line.Definition.SkuId.Value == data.SkuId).Packages);
        Assert.Equal(1, Assert.Single(replacementQuote.Lines, line => line.Definition.SkuId.Value == sourceSku).Packages);
        Assert.Equal(historicalCommercial, await data.Unit.ExecuteAsync((tx, ct) => data.Orders.InvoiceCommercialAsync(tx, data.Buyer, shipment, ct)));
        var persisted = await data.Rows(S.Decisions).Find(new BsonDocument("_id", accepted["decisionId"])).SingleAsync();
        foreach (var (collection, filter) in new[] {
            ("idempotencyRecords", new BsonDocument { { "actorId", data.Buyer.UserId }, { "operation", S.AcceptOperation }, { "key", "substitution-accepted" } }),
            ("auditEvents", new BsonDocument("_id", persisted["auditId"])),
            (S.Proposals, new BsonDocument("_id", persisted["proposalId"])),
            (B.Reservations, new BsonDocument("_id", Assert.Single(persisted["reservationIds"].AsBsonArray)))
        }) {
            using var tx = await data.Database.Client.StartSessionAsync(); tx.StartTransaction();
            _ = await data.Orders.ReadStockSourceGraphAsync(tx, data.OrderId, default);
            Assert.Equal(1L, (await data.Rows(collection).DeleteOneAsync(tx, filter)).DeletedCount);
            var denied = await Assert.ThrowsAnyAsync<Exception>(() => data.Orders.ReadStockSourceGraphAsync(tx, data.OrderId, default));
            Assert.True(denied is AuthorizationDeniedException or DomainValidationException, denied.ToString());
            await tx.AbortTransactionAsync();
        }
        Assert.Equal(persisted, await data.Rows(S.Decisions).Find(new BsonDocument("_id", accepted["decisionId"])).SingleAsync());
        Assert.Equal(accepted, await data.Orders.RecoverSubstitutionDecisionAsync(data.Buyer, "substitution-accepted", OrderSubstitutionDecision.Accept));
    }
}
