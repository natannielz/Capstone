using System.Runtime.Versioning;
using UnitToko.Tests.Documents;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Tests.Authorization;
using S = UnitToko.Platform.Ordering.OrderSubstitutionBson;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class OrderSubstitutionLineageTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Genuine_A_to_B_to_A_and_same_sku_new_price_keep_distinct_buyer_lines_and_cancel_shared_lot_once() {
        var prepared = await OrderSubstitutionTestData.CreateAsync(fixture, systemClock: false); await using var data = prepared.Data;
        var originalOrder = await data.Rows("orders").Find(new BsonDocument("_id", data.OrderId)).SingleAsync(); originalOrder.Remove("fence");
        var originalBalance = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        await RequireReleaseCaptureBoundaryAsync(data, originalBalance);
        var immutableMoney = new BsonDocument();
        foreach (var name in new[] { "invoices", "journalEntries", LotValueStore.Transitions })
            immutableMoney.Add(name, new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        async Task Renew(string key) {
            var spend = await data.Orders.ReviewOrderAsync(data.Spender, data.OrderId, null, OrderReviewStage.Spending);
            await data.Orders.DecideOrderAsync(data.Spender, data.OrderId, null, OrderReviewStage.Spending, spend.Revision, spend.Review, GenuineFiniteDispatchTestData.Meta(key + "-spending"));
            var store = await data.Orders.ReviewOrderAsync(data.Dispatcher, data.OrderId, data.SuborderId, OrderReviewStage.Store);
            await data.Orders.DecideOrderAsync(data.Dispatcher, data.OrderId, data.SuborderId, OrderReviewStage.Store, store.Revision, store.Review, GenuineFiniteDispatchTestData.Meta(key + "-store"));
        }
        async Task<BsonDocument> Replace(long sku, long lineRevision, int oldCount, long replacementSku, int newCount, string key) {
            var target = Assert.Single((await data.Orders.ListSubstitutionTargetsAsync(data.Dispatcher)).Items);
            var original = Assert.Single(target.Lines, line => line.Catalog.Definition.SkuId.Value == sku && line.Catalog.LineRevision == lineRevision);
            Assert.True(original.RemainingPackages >= oldCount);
            var replacement = Assert.Single((await data.Orders.SubstitutionPackagesAsync(data.Dispatcher, data.OrderId, data.SuborderId)).Items,
                line => line.Definition.SkuId.Value == replacementSku);
            var input = new OrderSubstitutionInput(new(data.OrderId), new(data.SuborderId), target.Revision, target.DispatchSequence,
                new(new(sku), oldCount, new(replacement.Definition.SkuId, replacement.Definition.Id, replacement.Definition.Version,
                    replacement.Price.Id, replacement.Price.Version, newCount), lineRevision));
            var review = await data.Orders.PreviewSubstitutionAsync(data.Dispatcher, input);
            var proposal = await data.Orders.ProposeSubstitutionAsync(data.Dispatcher, input, review.Review, GenuineFiniteDispatchTestData.Meta(key + "-proposal"));
            var consent = await data.Orders.PreviewSubstitutionDecisionAsync(data.Buyer, proposal["proposalId"].AsInt64, OrderSubstitutionDecision.Accept);
            var result = await data.Orders.DecideSubstitutionAsync(data.Buyer, consent.ProposalId.Value, consent.Revision,
                OrderSubstitutionDecision.Accept, consent.Review, GenuineFiniteDispatchTestData.Meta(key + "-consent"));
            Assert.Equal(result, await data.Orders.RecoverSubstitutionDecisionAsync(data.Buyer, key + "-consent", OrderSubstitutionDecision.Accept));
            return result;
        }
        var toB = await Replace(data.SkuId, 0, 4, prepared.ReplacementSku, 1, "lineage-a-b"); await Renew("lineage-a-b");
        var toA = await Replace(prepared.ReplacementSku, toB["revision"].AsInt64, 1, data.SkuId, 4, "lineage-b-a"); await Renew("lineage-b-a");
        var returned = (await data.Orders.ListSubstitutionTargetsAsync(data.Dispatcher)).Items.Single().Lines.Where(line => line.Catalog.Definition.SkuId.Value == data.SkuId).ToArray();
        Assert.Equal(2, returned.Length); Assert.Equal(8, returned.Single(line => line.Catalog.LineRevision == 0).RemainingPackages);
        Assert.Equal(4, returned.Single(line => line.Catalog.LineRevision == toA["revision"].AsInt64).RemainingPackages);
        var maker = prepared.PriceMaker; var checker = prepared.PriceChecker;
        var prices = new CatalogPriceService(data.Database, data.Unit, new(data.Database), data.Clock);
        var prior = await prices.PreviewAsync(maker, data.SkuId, CatalogPriceAccess.Prepare);
        var price = await prices.ProposeAsync(maker, data.SkuId, prior.Expected, new("IDR", new Money(21000m)), GenuineFiniteDispatchTestData.Meta("lineage-new-price"));
        await prices.DecideAsync(checker, price["proposalId"].AsInt64, 1, CatalogPriceDecision.Approve,
            "Explicitly renewed buyer price", GenuineFiniteDispatchTestData.Meta("lineage-new-price-approved"));
        var revised = await Replace(data.SkuId, toA["revision"].AsInt64, 4, data.SkuId, 4, "lineage-a-reprice"); await Renew("lineage-a-reprice");
        var targetNow = Assert.Single((await data.Orders.ListSubstitutionTargetsAsync(data.Dispatcher)).Items);
        var sameSku = targetNow.Lines.Where(line => line.Catalog.Definition.SkuId.Value == data.SkuId).ToArray();
        Assert.Equal(2, sameSku.Length);
        Assert.Equal(new Money(20000m), sameSku.Single(line => line.Catalog.LineRevision == 0).Catalog.Price.UnitPrice);
        Assert.Equal(new Money(21000m), sameSku.Single(line => line.Catalog.LineRevision == revised["revision"].AsInt64).Catalog.Price.UnitPrice);
        var actual = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        Assert.Equal(originalBalance.Balance.Reserved, actual.Balance.Reserved); Assert.Equal(originalBalance.Balance.OnHand, actual.Balance.OnHand);
        var cancel = await data.Orders.ReviewOrderAsync(data.Buyer, data.OrderId, null, OrderReviewStage.Cancellation);
        var cancelled = await data.Orders.DecideOrderAsync(data.Buyer, data.OrderId, null, OrderReviewStage.Cancellation, cancel.Revision,
            cancel.Review, GenuineFiniteDispatchTestData.Meta("lineage-cancel"), action: OrderReviewAction.CancelRemainder);
        var decision = await data.Rows("orderDecisions").Find(new BsonDocument("_id", cancelled["decisionId"])).SingleAsync();
        Assert.Equal(2, decision["releases"].AsBsonArray.Count);
        var movement = await data.Rows("movements").Find(new BsonDocument { { "source.namespace", "ordering.decision" }, { "source.documentId", decision["_id"] } }).SingleAsync();
        var leg = Assert.Single(movement["legs"].AsBsonArray); Assert.Equal(12L, leg["packages"].AsInt64);
        Assert.Equal(decision["revision"], movement["sourceRevision"]);
        Assert.Equal(0L, (await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync())["reserved"].AsInt64);
        _ = await data.Unit.ExecuteAsync((tx, ct) => data.Orders.ReadStockSourceGraphAsync(tx, data.OrderId, ct));
        var after = await data.Rows("orders").Find(new BsonDocument("_id", data.OrderId)).SingleAsync(); after.Remove("fence"); Assert.Equal(originalOrder, after);
        foreach (var field in immutableMoney)
            Assert.Equal(field.Value, new BsonArray(await data.Rows(field.Name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.Equal(3L, await data.Rows(S.Decisions).CountDocumentsAsync(new BsonDocument("decision", "Accept")));
    }

    // Exercise the command-local carrier on actual authenticated reservation facts. Full
    // proposal/consent provenance is separately exercised through service commands above.
    private static async Task RequireReleaseCaptureBoundaryAsync(GenuineFiniteDispatchTestData data, InventoryBalanceDocument physical) {
        using var tx = await data.Database.Client.StartSessionAsync(); tx.StartTransaction();
        _ = await data.Orders.ReadStockSourceGraphAsync(tx, data.OrderId, default);
        var reservation = await data.Rows("reservations").Find(tx, new BsonDocument("orderId", data.OrderId)).SingleAsync();
        var allocation = OrderSubmissionBson.Allocation(reservation["allocation"].AsBsonDocument);
        var release = allocation with { Change = allocation.Change with { ExpectedRevision = physical.Balance.Revision, Packages = 4 } };
        var facts = new BsonArray { new BsonDocument { { "reservationId", reservation["_id"] },
            { "allocation", OrderSubmissionBson.Allocation(release) }, { "before", OrderStockSourceRecords.State(reservation) } } };
        var capture = new OrderSubstitutionReleaseCapture(data.Database, data.Buyer, tx, data.Clock, facts, async (written, ct) => {
            _ = await data.Orders.ReadStockSourceGraphAsync(tx, data.OrderId, ct);
        });
        facts[0]["allocation"]["packages"] = 99L;
        var exposed = capture.Rows; exposed[0]["allocation"]["packages"] = 98L;
        Assert.Equal(4L, capture.Rows[0]["allocation"]["packages"].AsInt64);
        Assert.Equal(physical with { Balance = UnitToko.Modules.Inventory.StockBalancePolicy.Release(physical.Balance, physical.Balance.Revision, 4) }, capture.BeforeReserve(physical));
        capture.Require(data.Database, data.Clock, data.Buyer, tx); await capture.FenceAsync(false, default);
        Assert.Throws<AuthorizationDeniedException>(() => capture.Require(data.Database, data.Clock, data.Dispatcher, tx));
        Assert.Throws<AuthorizationDeniedException>(() => capture.Require(data.Database.Client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName + "_foreign"), data.Clock, data.Buyer, tx));
        Assert.Throws<AuthorizationDeniedException>(() => capture.Require(data.Database, new MutableAuthorizationClock(data.Clock.UtcNow), data.Buyer, tx));
        using (var other = await data.Database.Client.StartSessionAsync()) {
            other.StartTransaction(); Assert.Throws<AuthorizationDeniedException>(() => capture.Require(data.Database, data.Clock, data.Buyer, other)); await other.AbortTransactionAsync();
        }
        var clockValue = data.Clock.UtcNow; var mode = data.Clock.UseSystemClock;
        try {
            data.Clock.UseSystemClock = false; data.Clock.UtcNow = clockValue.AddMinutes(-1);
            Assert.True(data.Clock.UtcNow < clockValue);
            Assert.Throws<AuthorizationDeniedException>(() => capture.Require(data.Database, data.Clock, data.Buyer, tx));
        } finally { data.Clock.UtcNow = clockValue; data.Clock.UseSystemClock = mode; }
        Assert.Equal(1L, (await data.Rows("reservations").DeleteOneAsync(tx, new BsonDocument("_id", reservation["_id"]))).DeletedCount);
        var denied = await Assert.ThrowsAnyAsync<Exception>(() => capture.FenceAsync(false, default));
        Assert.True(denied is AuthorizationDeniedException or UnitToko.Modules.Contracts.DomainValidationException, denied.ToString());
        await tx.AbortTransactionAsync(); tx.StartTransaction();
        Assert.Throws<AuthorizationDeniedException>(() => capture.Require(data.Database, data.Clock, data.Buyer, tx));
        await tx.AbortTransactionAsync();
        Assert.Equal(reservation["remaining"], (await data.Rows("reservations").Find(new BsonDocument("_id", reservation["_id"])).SingleAsync())["remaining"]);
    }
}
