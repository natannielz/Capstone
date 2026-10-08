using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Ordering;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;

/// <summary>Actual order admission, accepted reservation and dispatcher admission.
/// People/catalog entitlement bootstrap remains explicitly local synthetic data.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ApprovedShipmentDispatchAdmissionReaderTests(MongoFixture fixture)
{
    internal static async Task<(GenuineFiniteDispatchTestData Data, ApprovedShipmentDispatchAdmissionReader Reader)> PreparedAsync(MongoFixture fixture, bool converted) {
        var data = await GenuineFiniteDispatchTestData.CreateAsync(fixture, converted, createOrder: false, reviewedSafetyBufferBaseUnits: 0);
        try {
            foreach (var actor in new[] { data.Buyer, data.Spender, data.Dispatcher, data.Courier }) {
                Assert.NotNull(actor.Session);
                var verified = await data.Catalog.Sessions.ReadAsync(data.Catalog.Issued[actor.UserId].OpaqueToken);
                Assert.NotNull(verified); Assert.Equal(actor.UserId, verified.UserId); Assert.Equal(actor.Session, verified.Reference);
            }
            var organizations = data.Stock.Valuation.Inspection.Receiving.Procurement.Organizations.Organizations;
            var orderAdmission = new ApprovedOrderAdmissionReader(data.Database, data.Unit, new(data.Database), data.Clock, organizations);
            data.Orders = new(data.Database, data.Unit, new(data.Database), data.Clock, orderAdmission);
            var preview = await data.Orders.PreviewAsync(data.Buyer, data.Input);
            var ordered = await data.Orders.SubmitAsync(data.Buyer, data.Input, preview.Review, GenuineFiniteDispatchTestData.Meta("approved-dispatch-order"));
            data.OrderId = ordered["orderId"].AsInt64; data.SuborderId = Assert.Single(ordered["suborderIds"].AsBsonArray).AsInt64;
            var spending = await data.Orders.ReviewOrderAsync(data.Spender, data.OrderId, null, OrderReviewStage.Spending);
            await data.Orders.DecideOrderAsync(data.Spender, data.OrderId, null, OrderReviewStage.Spending, spending.Revision, spending.Review,
                GenuineFiniteDispatchTestData.Meta("approved-dispatch-spending"));
            var store = await data.Orders.ReviewOrderAsync(data.Dispatcher, data.OrderId, data.SuborderId, OrderReviewStage.Store);
            await data.Orders.DecideOrderAsync(data.Dispatcher, data.OrderId, data.SuborderId, OrderReviewStage.Store, store.Revision, store.Review,
                GenuineFiniteDispatchTestData.Meta("approved-dispatch-acceptance"));
            data.Preparations = new(data.Database, data.Unit, new(data.Database), data.Clock, data.Orders);
            var reader = new ApprovedShipmentDispatchAdmissionReader(data.Database, data.Unit, new(data.Database), data.Clock, data.Orders, organizations);
            data.Dispatch = new(data.Database, data.Unit, new(data.Database), data.Clock, data.Orders, data.Preparations, reader);
            reader.AttachDispatchOwner(data.Dispatch); return (data, reader);
        } catch { await data.DisposeAsync(); throw; }
    }
    [WindowsStorageTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Actual_order_and_dispatch_admissions_consume_released_or_converted_finite_value_post_COGS_and_recover_once(bool converted) {
        var prepared = await PreparedAsync(fixture, converted); await using var data = prepared.Data;
        Assert.Throws<DomainValidationException>(() => prepared.Reader.AttachDispatchOwner(data.Dispatch));
        var quantity = converted ? 12 : 1;
        var before = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        var reservation = await data.Rows("reservations").Find(new BsonDocument("orderId", data.OrderId)).SingleAsync();
        var originalSource = reservation["allocation"]["authority"].DeepClone();
        var review = await data.PrepareDispatchAsync(quantity, "approved-dispatch-preparation");
        var result = await data.DispatchAsync(review, "approved-dispatch-original");
        var shipment = await data.Rows("shipments").Find(new BsonDocument("_id", result["shipmentId"])).SingleAsync();
        Assert.Equal(2, shipment["schemaVersion"].AsInt32); Assert.Equal("committed", shipment["phase"].AsString);
        Assert.Equal(originalSource, Assert.Single(shipment["admission"]["lots"].AsBsonArray)["sourceAuthority"]);
        Assert.DoesNotContain('\n', shipment["admission"]["seller"]["address"].AsString);
        Assert.Contains(UnitToko.Tests.Organizations.OrganizationAddressTestData.Postal.City, shipment["admission"]["seller"]["address"].AsString);
        var effect = Assert.Single(shipment["valueEffects"].AsBsonArray); Assert.Equal("11250.00", effect["transferredValue"].AsString);
        var journal = await data.Rows("journalEntries").Find(new BsonDocument("_id", shipment["journalId"])).SingleAsync();
        Assert.Equal(11250m, journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == "cogs")["debit"].AsDecimal);
        Assert.Equal(11250m, journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == "inventory")["credit"].AsDecimal);
        var physical = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        Assert.Equal(before with { Balance = StockBalancePolicy.Dispatch(before.Balance, before.Balance.Revision, quantity) }, physical);
        var head = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        Assert.Equal(0L, head["remainingPackages"].AsInt64); Assert.Equal("0.00", head["remainingValue"].AsString);
        var stable = await data.BusinessAsync();
        Assert.Equal(result, await data.DispatchAsync(review, "approved-dispatch-original"));
        Assert.Equal(result, await data.Dispatch.RecoverAsync(data.Dispatcher, "approved-dispatch-original"));
        Assert.Equal(stable, await data.BusinessAsync());
    }
    [WindowsStorageFact]
    public async Task Genuine_dispatch_capture_refuses_foreign_actor_attempt_and_unowned_physical_dispatch_with_whole_rollback() {
        var prepared = await PreparedAsync(fixture, false); await using var data = prepared.Data; var reader = prepared.Reader;
        var before = await data.BusinessAsync(); var reached = 0;
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => data.Unit.ExecuteAsync(async (tx, ct) => {
            var authority = await data.Base.Store.LoadAsync(data.Dispatcher, tx, ct);
            var source = await data.Orders.ReadDispatchReservationsAsync(data.Dispatcher, authority, tx, data.SuborderId, ct);
            var reservation = Assert.Single(source.Reservations); var selected = new ShipmentLotSelection(reservation.Fact.ReservationId, 1);
            var admission = await reader.ReadAndFenceAsync(tx, data.Dispatcher, source, [selected], ct);
            Assert.Equal("shipment_dispatch_admission_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() =>
                reader.CompleteAsync(tx, data.Buyer, admission, ct))).Code);
            _ = await new InventoryRepository(data.Database).ApplyAsync(tx, [reservation.Allocation.Change with { Action = InventoryBalanceAction.Dispatch, Packages = 1 }], ct);
            reached++; await reader.CompleteAsync(tx, data.Dispatcher, admission, ct); return true;
        }));
        Assert.Equal("shipment_dispatch_admission_unavailable", error.Code); Assert.Equal(1, reached); Assert.Equal(before, await data.BusinessAsync());
        using var next = await data.Database.Client.StartSessionAsync(); next.StartTransaction();
        var authorityNext = await data.Base.Store.LoadAsync(data.Dispatcher, next, default);
        var sourceNext = await data.Orders.ReadDispatchReservationsAsync(data.Dispatcher, authorityNext, next, data.SuborderId, default);
        var retained = await reader.ReadAndFenceAsync(next, data.Dispatcher, sourceNext, [new(Assert.Single(sourceNext.Reservations).Fact.ReservationId, 1)], default);
        await next.CommitTransactionAsync(); next.StartTransaction();
        Assert.Equal("shipment_dispatch_admission_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(() =>
            reader.CompleteAsync(next, data.Dispatcher, retained, default))).Code);
        await next.AbortTransactionAsync(); Assert.Equal(before, await data.BusinessAsync());
    }
    [WindowsStorageFact]
    public async Task Actual_dispatch_source_rejects_original_policy_rebase_buyer_permission_and_warmed_policy_deletion() {
        var prepared = await PreparedAsync(fixture, false); await using var data = prepared.Data; var before = await data.BusinessAsync();
        var checkedGuards = 0; var activationQueries = 0;
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => data.Unit.ExecuteAsync(async (tx, ct) => {
            var authority = await data.Base.Store.LoadAsync(data.Dispatcher, tx, ct);
            var source = await data.Orders.ReadDispatchReservationsAsync(data.Dispatcher, authority, tx, data.SuborderId, ct);
            var allocation = Assert.Single(source.Reservations).Allocation; var owner = data.Stock.Valuation.Sources;
            var rebased = allocation.SourceAuthority with { Revision = allocation.SourceAuthority.Revision + 1 };
            Assert.Equal("shipment_dispatch_source_changed", (await Assert.ThrowsAsync<DomainValidationException>(() => owner.CaptureShipmentDispatchSourceAsync(
                data.Dispatcher, authority, tx, source.Order.Resource, allocation.Change.Key.Location, allocation.Pool.Id, allocation.Pool.Revision, rebased, ct))).Code); checkedGuards++;
            var buyerAuthority = await data.Base.Store.LoadAsync(data.Buyer, tx, ct);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => owner.CaptureShipmentDispatchSourceAsync(data.Buyer, buyerAuthority, tx,
                source.Order.Resource, allocation.Change.Key.Location, allocation.Pool.Id, allocation.Pool.Revision, allocation.SourceAuthority, ct)); checkedGuards++;
            var captured = await owner.CaptureShipmentDispatchSourceAsync(data.Dispatcher, authority, tx, source.Order.Resource,
                allocation.Change.Key.Location, allocation.Pool.Id, allocation.Pool.Revision, allocation.SourceAuthority, ct);
            captured.RequireContext(owner, data.Dispatcher, authority, tx, source.Order.Resource, allocation.Change.Key.Location, allocation.SourceAuthority);
            fixture.CommandCompleted = (command, _) => {
                if (command == "find/stockSourceActivationProposals") Interlocked.Increment(ref activationQueries);
            };
            try { await captured.FenceAsync(ct); }
            finally { fixture.CommandCompleted = null; }
            var policy = captured.Snapshot["policyView"]["policy"].AsBsonDocument;
            Assert.Equal(1L, (await data.Rows(StockSourcePolicyBson.Policies).DeleteOneAsync(tx, new BsonDocument("_id", policy["_id"]), cancellationToken: ct)).DeletedCount);
            checkedGuards++; await captured.FenceAsync(ct); return true;
        }));
        Assert.True(failure is AuthorizationDeniedException or DomainValidationException, failure.ToString());
        Assert.Equal(3, checkedGuards); Assert.Equal(before, await data.BusinessAsync());
        Assert.Equal(2, activationQueries); // One exact-membership read before and after the owning fences.
    }
}
