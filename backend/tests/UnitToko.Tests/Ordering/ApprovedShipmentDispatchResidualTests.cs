using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ApprovedShipmentDispatchResidualTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Actual_approved_converted_dispatch_conserves_one_then_eleven_residual_value_and_original_receipts()
    {
        // Reuse the genuine producers and actual first-order admission. Only the
        // established local identity/catalog bootstrap remains synthetic.
        var prepared = await ApprovedShipmentDispatchAdmissionReaderTests.PreparedAsync(fixture, true);
        await using var data = prepared.Data;
        var organizations = data.Stock.Valuation.Inspection.Receiving.Procurement.Organizations.Organizations;
        data.Dispatch = ShipmentDispatchService.CreateWithApprovedAdmission(data.Database, data.Unit, new(data.Database),
            data.Clock, data.Orders, data.Preparations, organizations);
        var unrelatedJournals = new BsonArray(await data.Rows("journalEntries").Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        async Task<BsonDocument> CustomerMoneyAsync() {
            var snapshot = new BsonDocument();
            foreach (var table in new[] { "invoices", "invoiceSourceAllocations", "paymentDeclarations", "paymentDeclarationVersions",
                "paymentAllocations", "paymentReceiptBalances", "paymentInvoiceBalances" })
                snapshot.Add(table, new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync()));
            return snapshot;
        }
        var customerMoney = await CustomerMoneyAsync();
        var before = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        Assert.Equal(12L, before.Balance.Reserved);
        var initialValue = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        Assert.Equal(12L, initialValue["remainingPackages"].AsInt64); Assert.Equal("11250.00", initialValue["remainingValue"].AsString);
        var reservation = await data.Rows("reservations").Find(new BsonDocument("orderId", data.OrderId)).SingleAsync();
        var reservationId = reservation["_id"].AsInt64; var originalAuthority = reservation["allocation"]["authority"].DeepClone();

        async Task<BsonDocument> RequireDispatchAsync(BsonDocument result, ShipmentDispatchReview review, long packages, string amount) {
            var shipment = await data.Rows("shipments").Find(new BsonDocument("_id", result["shipmentId"])).SingleAsync();
            Assert.Equal(2, shipment["schemaVersion"].AsInt32); Assert.Equal("committed", shipment["phase"].AsString);
            Assert.Equal(review.Preparation.PreparationId.Value, shipment["preparationId"].AsInt64);
            Assert.Equal(review.Preparation.PreparationRevision, shipment["preparationRevision"].AsInt64);
            Assert.Equal(originalAuthority, Assert.Single(shipment["admission"]["lots"].AsBsonArray)["sourceAuthority"]);
            var effect = Assert.Single(shipment["valueEffects"].AsBsonArray);
            Assert.Equal(reservationId, effect["reservationId"].AsInt64); Assert.Equal(data.BalanceId, effect["balanceId"].AsInt64);
            Assert.Equal(packages, effect["packages"].AsInt64); Assert.Equal(amount, effect["transferredValue"].AsString);
            var transition = await data.Rows(LotValueStore.Transitions).Find(new BsonDocument("_id", effect["transitionId"])).SingleAsync();
            Assert.Equal("consume", transition["operation"].AsString); Assert.Equal("consumeLot", transition["source"]["eventKind"].AsString);
            Assert.Equal(shipment["_id"], transition["source"]["documentId"]); Assert.Equal(data.BalanceId, transition["source"]["occurrence"].AsInt64);
            Assert.Equal(effect["transitionHash"], transition["transitionHash"]); Assert.Equal(amount, transition["transferredValue"].AsString);
            Assert.Equal(effect["valueBefore"], transition["availableBefore"]); Assert.Equal(effect["valueAfter"], transition["availableAfter"]);
            var journal = await data.Rows("journalEntries").Find(new BsonDocument("_id", shipment["journalId"])).SingleAsync();
            Assert.Equal(shipment["_id"], journal["source"]["documentId"]); Assert.Equal("dispatchCogs", journal["source"]["eventKind"].AsString);
            Assert.Equal(2, journal["lines"].AsBsonArray.Count);
            var cogs = journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == "cogs");
            var inventory = journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == "inventory");
            var expected = UnitToko.Modules.Accounting.Money.Parse(amount).Value;
            Assert.Equal(expected, cogs["debit"].AsDecimal); Assert.Equal(0m, cogs["credit"].AsDecimal);
            Assert.Equal(0m, inventory["debit"].AsDecimal); Assert.Equal(expected, inventory["credit"].AsDecimal);
            return shipment;
        }

        var firstReview = await data.PrepareDispatchAsync(1, "approved-residual-first-preparation");
        Assert.Equal(reservationId, Assert.Single(firstReview.Selected).ReservationId.Value);
        Assert.Equal(1, Assert.Single(firstReview.Selected).Packages);
        var first = await data.DispatchAsync(firstReview, "approved-residual-first");
        var firstRow = await RequireDispatchAsync(first, firstReview, 1, "937.50");
        var residual = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        Assert.Equal(before with { Balance = StockBalancePolicy.Dispatch(before.Balance, before.Balance.Revision, 1) }, residual);
        Assert.Equal(11L, residual.Balance.Reserved);
        var residualValue = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        Assert.Equal(11L, residualValue["remainingPackages"].AsInt64); Assert.Equal("10312.50", residualValue["remainingValue"].AsString);
        var residualReservation = await data.Rows("reservations").Find(new BsonDocument("_id", reservationId)).SingleAsync();
        Assert.Equal(11L, residualReservation["remaining"].AsInt64); Assert.Equal(1L, residualReservation["dispatched"].AsInt64);
        Assert.Equal(0L, residualReservation["released"].AsInt64); Assert.Equal(reservation["allocation"], residualReservation["allocation"]);
        var firstStable = await data.BusinessAsync();
        Assert.Equal(first, await data.DispatchAsync(firstReview, "approved-residual-first"));
        Assert.Equal(first, await data.Dispatch.RecoverAsync(data.Dispatcher, "approved-residual-first"));
        Assert.Null(await data.Dispatch.RecoverAsync(data.Dispatcher, "approved-residual-unknown"));
        Assert.Equal(firstStable, await data.BusinessAsync());

        var secondReview = await data.PrepareDispatchAsync(11, "approved-residual-second-preparation", firstReview.Preparation.PreparationRevision);
        Assert.Equal(reservationId, Assert.Single(secondReview.Selected).ReservationId.Value);
        Assert.Equal(11, Assert.Single(secondReview.Selected).Packages);
        var second = await data.DispatchAsync(secondReview, "approved-residual-second");
        var secondRow = await RequireDispatchAsync(second, secondReview, 11, "10312.50");
        Assert.NotEqual(firstRow["_id"], secondRow["_id"]); Assert.NotEqual(firstRow["preparationVersionId"], secondRow["preparationVersionId"]);
        var final = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        Assert.Equal(residual with { Balance = StockBalancePolicy.Dispatch(residual.Balance, residual.Balance.Revision, 11) }, final);
        Assert.Equal(0L, final.Balance.OnHand);
        Assert.Equal(0L, final.Balance.Reserved); Assert.Equal(before.Balance.Held, final.Balance.Held); Assert.Equal(before.Balance.Expired, final.Balance.Expired);
        var finalValue = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        Assert.Equal(0L, finalValue["remainingPackages"].AsInt64); Assert.Equal("0.00", finalValue["remainingValue"].AsString);
        var finalReservation = await data.Rows("reservations").Find(new BsonDocument("_id", reservationId)).SingleAsync();
        Assert.Equal(0L, finalReservation["remaining"].AsInt64); Assert.Equal(12L, finalReservation["dispatched"].AsInt64);
        Assert.Equal(0L, finalReservation["released"].AsInt64); Assert.Equal(reservation["allocation"], finalReservation["allocation"]);
        Assert.Equal(2L, await data.Rows("shipments").CountDocumentsAsync(new BsonDocument("orderId", data.OrderId)));
        Assert.Equal(2L, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument("source.namespace", "fulfillment.shipment")));
        var cogsJournals = await data.Rows("journalEntries").Find(new BsonDocument("source.namespace", "fulfillment.shipment")).ToListAsync();
        Assert.Equal(11250m, cogsJournals.Sum(row => row["lines"].AsBsonArray.Single(line => line["mappingKey"] == "cogs")["debit"].AsDecimal));
        Assert.Equal(11250m, cogsJournals.Sum(row => row["lines"].AsBsonArray.Single(line => line["mappingKey"] == "inventory")["credit"].AsDecimal));
        Assert.Equal(unrelatedJournals, new BsonArray(await data.Rows("journalEntries").Find(new BsonDocument("source.namespace", new BsonDocument("$ne", "fulfillment.shipment")))
            .Sort(new BsonDocument("_id", 1)).ToListAsync()));
        Assert.Equal(customerMoney, await CustomerMoneyAsync());
        var finalStable = await data.BusinessAsync();
        Assert.Equal(first, await data.DispatchAsync(firstReview, "approved-residual-first"));
        Assert.Equal(first, await data.Dispatch.RecoverAsync(data.Dispatcher, "approved-residual-first"));
        Assert.Equal(second, await data.DispatchAsync(secondReview, "approved-residual-second"));
        Assert.Equal(second, await data.Dispatch.RecoverAsync(data.Dispatcher, "approved-residual-second"));
        Assert.Null(await data.Dispatch.RecoverAsync(data.Dispatcher, "approved-residual-unknown"));
        Assert.Equal(finalStable, await data.BusinessAsync()); Assert.Equal(customerMoney, await CustomerMoneyAsync());
    }
}
