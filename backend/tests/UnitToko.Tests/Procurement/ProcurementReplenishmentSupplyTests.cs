using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;
using D = UnitToko.Tests.Procurement.ProcurementStockReceiptTestData;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ProcurementReplenishmentSupplyTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Approved_PO_remaining_supply_uses_real_receipts_reviewed_arrival_and_rejects_warmed_missing_receipt()
    {
        await using var data = await D.CreateAsync(fixture);
        var received = await data.RecordAsync("replenishment-received-one", data.Input(packages: 1));
        var receiving = data.Inspection.Receiving; var orders = receiving.Orders;
        var skuId = receiving.Procurement.Sku.Id.Value;
        var definitionId = receiving.Procurement.Sku.CurrentDefinitionId!.Value.Value;
        var location = new StockLocation(new(100), new(101), new(102), new(103));
        var authorization = new AuthorizationStore(data.Database, data.Unit, data.Clock);
        var reader = new ProcurementReplenishmentSupplyReader(data.Database, data.Unit, new(data.Database), data.Clock);
        async Task<(bool Complete, ReplenishmentSupply[] Supplies, BsonDocument Provenance)> Read(long? definition = null) =>
            await authorization.ReadAsync(data.Reader, async (authority, tx, ct) => {
                var capture = await reader.ReadAsync(data.Reader, authority, tx, location, data.PoolId, skuId, definition ?? definitionId, ct);
                await capture.CompleteAsync(ct);
                return (capture.CoverageComplete, capture.Supplies.ToArray(), capture.Provenance);
            }, CancellationToken.None);

        var physicalBefore = await data.StateAsync();
        var poolBefore = await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync();
        var missingArrival = await Read();
        Assert.True(missingArrival.Complete);
        var unknown = Assert.Single(missingArrival.Supplies);
        Assert.Equal(2L, unknown.Packages); // The real approved PO is three packages; one has arrived.
        Assert.Null(unknown.ExpectedAt); Assert.False(unknown.Verified);
        var uncertain = ReplenishmentPolicy.Evaluate(new(1, 5, 0, 1, 1, 1), new(true, 0, 0, 0),
            missingArrival.Complete, missingArrival.Supplies, data.Clock.UtcNow);
        Assert.Null(uncertain.EligibleSupplyPackages); Assert.Null(uncertain.SuggestedPurchasePackages);

        var eta = new PurchaseArrivalInput(receiving.OrderId, 1, receiving.ApprovalId, skuId, definitionId,
            1, data.Clock.UtcNow.AddDays(3), "SYNTHETIC explicit supplier expected arrival for remaining packages");
        var proposed = await orders.Service.PreviewArrivalAsync(orders.Maker, eta);
        await orders.Service.ProposeArrivalAsync(orders.Maker, eta, proposed["binding"].AsString, PurchaseOrderTestData.Meta("remaining-eta-proposal"));
        var pending = await orders.Service.ReadArrivalAsync(data.Reader, receiving.OrderId, skuId);
        var proposalId = pending["proposalId"].AsInt64;
        var reviewed = await orders.Service.PreviewArrivalDecisionAsync(orders.Checker, proposalId, "approve");
        await orders.Service.DecideArrivalAsync(orders.Checker, proposalId, 2, "approve", reviewed["binding"].AsString,
            PurchaseOrderTestData.Meta("remaining-eta-approval"));
        var known = await Read();
        Assert.True(known.Complete); var supply = Assert.Single(known.Supplies);
        Assert.Equal(2L, supply.Packages); Assert.True(supply.Verified); Assert.Equal(eta.ExpectedAt, supply.ExpectedAt);
        var advice = ReplenishmentPolicy.Evaluate(new(1, 5, 0, 1, 1, 1), new(true, 0, 0, 0), known.Complete, known.Supplies, data.Clock.UtcNow);
        Assert.Equal(2L, advice.EligibleSupplyPackages); Assert.Equal(3L, advice.SuggestedPurchasePackages);
        foreach (var privateField in new[] { "netPricePerPackage", "taxAmount", "supplierChargeAmount", "paymentTerms", "confirmationReference" })
            Assert.DoesNotContain(privateField, known.Provenance.ToJson(), StringComparison.Ordinal);
        var wrongDefinition = await Read(definitionId + 100000);
        Assert.False(wrongDefinition.Complete); Assert.Empty(wrongDefinition.Supplies);
        Assert.Equal(physicalBefore, await data.StateAsync());
        Assert.Equal(poolBefore, await data.Rows("inventoryPools").Find(new BsonDocument("_id", data.PoolId)).SingleAsync());

        using (var tx = await fixture.Client.StartSessionAsync()) {
            tx.StartTransaction();
            try {
                var authority = await authorization.LoadAsync(data.Reader, tx, CancellationToken.None);
                var capture = await reader.ReadAsync(data.Reader, authority, tx, location, data.PoolId, skuId, definitionId, CancellationToken.None);
                Assert.Equal(1L, (await data.Rows(ProcurementStockReceiptBson.Receipts)
                    .DeleteOneAsync(tx, new BsonDocument("_id", received["receiptId"]))).DeletedCount);
                var failure = await Record.ExceptionAsync(() => capture.CompleteAsync(CancellationToken.None));
                var refused = Assert.IsType<AuthorizationDeniedException>(failure);
                Assert.Equal("procurement_quality_acceptance_integrity_unavailable", refused.Code);
                Assert.Equal(ProcurementStockReceiptBson.Receipts, refused.Data["purchaseOrderCapture.collection"]);
                Assert.Equal(1, refused.Data["purchaseOrderCapture.expectedCount"]);
                Assert.Equal(0, refused.Data["purchaseOrderCapture.actualCount"]);
            } finally { await InventoryRepository.AbortAsync(tx); }
        }
        Assert.Equal(physicalBefore, await data.StateAsync());

        await data.RecordAsync("replenishment-received-rest", data.Input("SYNTHETIC-STOCK-REMAINING", packages: 2));
        var closed = await Read(); Assert.True(closed.Complete); Assert.Empty(closed.Supplies);
        var closedAdvice = ReplenishmentPolicy.Evaluate(new(1, 5, 0, 1, 1, 1), new(true, 0, 0, 0), closed.Complete, closed.Supplies, data.Clock.UtcNow);
        Assert.Equal(0L, closedAdvice.EligibleSupplyPackages);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => authorization.ReadAsync(data.Outsider, async (authority, tx, ct) => {
            var capture = await reader.ReadAsync(data.Outsider, authority, tx, location, data.PoolId, skuId, definitionId, ct);
            await capture.CompleteAsync(ct); return true;
        }, CancellationToken.None));
    }
}
