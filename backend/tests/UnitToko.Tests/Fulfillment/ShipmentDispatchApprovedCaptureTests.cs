using System.Collections.Immutable;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Ordering;

namespace UnitToko.Tests.Fulfillment;

/// <summary>Genuine approved order/source/finite capture. The synthetic roster
/// fixture is explicit; no positive admission, stock or value rows are seeded.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ShipmentDispatchApprovedCaptureTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Private_original_capture_refuses_foreign_owner_actor_database_session_attempt_and_claimed_source_quantity_with_full_rollback()
    {
        await using var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, false, createOrder: false, reviewedSafetyBufferBaseUnits: 0);
        var organizations = data.Stock.Valuation.Inspection.Receiving.Procurement.Organizations.Organizations;
        data.Orders = OrderSubmissionService.CreateWithApprovedAdmission(data.Database, data.Unit, new(data.Database), data.Clock, organizations);
        var preview = await data.Orders.PreviewAsync(data.Buyer, data.Input);
        var result = await data.Orders.SubmitAsync(data.Buyer, data.Input, preview.Review, GenuineFiniteDispatchTestData.Meta("retained-capture-order"));
        data.OrderId = result["orderId"].AsInt64; data.SuborderId = Assert.Single(result["suborderIds"].AsBsonArray).AsInt64;
        var spending = await data.Orders.ReviewOrderAsync(data.Spender, data.OrderId, null, OrderReviewStage.Spending);
        await data.Orders.DecideOrderAsync(data.Spender, data.OrderId, null, OrderReviewStage.Spending, spending.Revision, spending.Review,
            GenuineFiniteDispatchTestData.Meta("retained-capture-spending"));
        var store = await data.Orders.ReviewOrderAsync(data.Dispatcher, data.OrderId, data.SuborderId, OrderReviewStage.Store);
        await data.Orders.DecideOrderAsync(data.Dispatcher, data.OrderId, data.SuborderId, OrderReviewStage.Store, store.Revision, store.Review,
            GenuineFiniteDispatchTestData.Meta("retained-capture-store"));
        data.Preparations = new(data.Database, data.Unit, new(data.Database), data.Clock, data.Orders);
        var reader = new ApprovedShipmentDispatchAdmissionReader(data.Database, data.Unit, new(data.Database), data.Clock, data.Orders, organizations);
        data.Dispatch = new(data.Database, data.Unit, new(data.Database), data.Clock, data.Orders, data.Preparations, reader);
        reader.AttachDispatchOwner(data.Dispatch);
        async Task<BsonDocument> StoredRows() {
            var state = new BsonDocument(); using var names = await data.Database.ListCollectionNamesAsync();
            foreach (var name in (await names.ToListAsync()).Order(StringComparer.Ordinal))
                state[name] = new BsonArray(await data.Rows(name).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
            return state;
        }
        var before = await StoredRows(); var refusals = 0; var changedPhysical = 0;
        void Refuse(Action action) {
            Assert.Equal("shipment_dispatch_admission_unavailable", Assert.Throws<DomainValidationException>(action).Code); refusals++;
        }
        using var tx = await data.Database.Client.StartSessionAsync(); tx.StartTransaction();
        OrderDispatchSource source; ShipmentDispatchAdmission admission; InventoryBalanceDocument physical;
        ShipmentLotSelection chosen; ImmutableArray<ShipmentLotSelection> selected;
        try {
            var authority = await data.Base.Store.LoadAsync(data.Dispatcher, tx, default);
            source = await data.Orders.ReadDispatchReservationsAsync(data.Dispatcher, authority, tx, data.SuborderId, default);
            var reserved = Assert.Single(source.Reservations); chosen = new(reserved.Fact.ReservationId, Assert.Single(data.Input.Selections).Packages); selected = [chosen];
            admission = await reader.ReadAndFenceAsync(tx, data.Dispatcher, source, selected, default);
            physical = await new InventoryRepository(data.Database).ReadAsync(tx, reserved.Allocation.Change.Id, default);
            var capture = reader.RetainedFiniteCapture(data.Dispatch, data.Database, tx, data.Dispatcher, source, selected, admission, chosen, physical);
            Assert.Equal(physical, capture.Before); Assert.Equal(Money.Parse("11250.00"), capture.Value.RemainingValue);
            Assert.Same(capture, reader.RetainedFiniteCapture(data.Dispatch, data.Database, tx, data.Dispatcher, source, selected, admission, chosen, physical));
            ShipmentDispatchFiniteSource.Capture Claim(ShipmentDispatchService owner, IMongoDatabase database, IClientSessionHandle session,
                ActorContext actor, OrderDispatchSource suppliedSource, ImmutableArray<ShipmentLotSelection> suppliedSelected,
                ShipmentDispatchAdmission suppliedAdmission, ShipmentLotSelection suppliedChosen, InventoryBalanceDocument suppliedPhysical) =>
                reader.RetainedFiniteCapture(owner, database, session, actor, suppliedSource, suppliedSelected, suppliedAdmission, suppliedChosen, suppliedPhysical);
            Refuse(() => Claim(data.Dispatch, data.Database, tx, data.Spender, source, selected, admission, chosen, physical));
            Refuse(() => Claim(data.Dispatch, data.Database, tx, data.Dispatcher with { Session = data.Courier.Session }, source, selected, admission, chosen, physical));
            var changed = reserved.Reservation.DeepClone().AsBsonDocument; changed["remaining"] = changed["remaining"].AsInt64 + 1;
            Refuse(() => Claim(data.Dispatch, data.Database, tx, data.Dispatcher, source with { Reservations = [reserved with { Reservation = changed }] }, selected, admission, chosen, physical));
            var otherQuantity = chosen with { Packages = chosen.Packages + 1 };
            Refuse(() => Claim(data.Dispatch, data.Database, tx, data.Dispatcher, source, [otherQuantity], admission, otherQuantity, physical));
            var otherReservation = chosen with { ReservationId = new(chosen.ReservationId.Value + 1) };
            Refuse(() => Claim(data.Dispatch, data.Database, tx, data.Dispatcher, source, [otherReservation], admission, otherReservation, physical));
            Refuse(() => Claim(data.Dispatch, data.Database, tx, data.Dispatcher, source, selected, admission, chosen,
                physical with { Balance = physical.Balance with { Revision = physical.Balance.Revision + 1 } }));
            Refuse(() => Claim(data.Dispatch, data.Database, tx, data.Dispatcher, source, selected, admission with { }, chosen, physical));
            Refuse(() => Claim(data.Dispatch, data.Database, tx, data.Dispatcher, source, selected,
                admission with { FiniteValue = new BsonDocument("total", "11250.00") }, chosen, physical));
            var foreignOwner = ShipmentDispatchService.CreateWithApprovedAdmission(data.Database, data.Unit, new(data.Database), data.Clock, data.Orders, data.Preparations, organizations);
            Refuse(() => Claim(foreignOwner, data.Database, tx, data.Dispatcher, source, selected, admission, chosen, physical));
            var foreignClockOwner = new ShipmentDispatchService(data.Database, data.Unit, new(data.Database), new UnitToko.Platform.SystemClock(), data.Orders, data.Preparations, reader);
            Refuse(() => Claim(foreignClockOwner, data.Database, tx, data.Dispatcher, source, selected, admission, chosen, physical));
            var unattached = new ApprovedShipmentDispatchAdmissionReader(data.Database, data.Unit, new(data.Database), data.Clock, data.Orders, organizations);
            var unattachedOwner = new ShipmentDispatchService(data.Database, data.Unit, new(data.Database), data.Clock, data.Orders, data.Preparations, unattached);
            Refuse(() => unattached.RetainedFiniteCapture(unattachedOwner, data.Database, tx, data.Dispatcher, source, selected, admission, chosen, physical));
            Refuse(() => Claim(data.Dispatch, data.Database.Client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName + "-foreign"), tx,
                data.Dispatcher, source, selected, admission, chosen, physical));
            using var foreignClient = new MongoClient(fixture.Client.Settings);
            Refuse(() => Claim(data.Dispatch, foreignClient.GetDatabase(data.Database.DatabaseNamespace.DatabaseName), tx,
                data.Dispatcher, source, selected, admission, chosen, physical));
            using var otherSession = await data.Database.Client.StartSessionAsync(); otherSession.StartTransaction();
            try { Refuse(() => Claim(data.Dispatch, data.Database, otherSession, data.Dispatcher, source, selected, admission, chosen, physical)); }
            finally { await otherSession.AbortTransactionAsync(); }
            // An unowned actual physical write is negative evidence only and is
            // rolled back; a claim of the captured before-state cannot authorize it.
            _ = await new InventoryRepository(data.Database).ApplyAsync(tx,
                [reserved.Allocation.Change with { Action = InventoryBalanceAction.Dispatch, Packages = chosen.Packages }], default);
            var currentPhysical = await new InventoryRepository(data.Database).ReadAsync(tx, physical.Id, default); changedPhysical++;
            Refuse(() => Claim(data.Dispatch, data.Database, tx, data.Dispatcher, source, selected, admission, chosen, currentPhysical));
        } finally { if (tx.IsInTransaction) await tx.AbortTransactionAsync(); }
        Assert.Equal(1, changedPhysical); Assert.Equal(15, refusals); Assert.Equal(before, await StoredRows());
        tx.StartTransaction();
        try { Refuse(() => reader.RetainedFiniteCapture(data.Dispatch, data.Database, tx, data.Dispatcher, source, selected, admission, chosen, physical)); }
        finally { await tx.AbortTransactionAsync(); }
        Assert.Equal(16, refusals); Assert.Equal(before, await StoredRows());
    }
}
