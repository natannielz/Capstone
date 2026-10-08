using System.Collections.Concurrent;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Organizations;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Ordering;

/// <summary>Actual approved first-order and dispatch owners with genuine finite
/// stock/value/journal effects. Roster/provider inputs and forward-only fixture
/// time are explicit synthetic test seams; no admission reader is substituted.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ApprovedShipmentDispatchRaceAndExpiryTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Two_actual_approved_dispatches_have_one_finite_stock_value_COGS_winner_and_stable_original_keys() {
        var prepared = await ApprovedShipmentDispatchAdmissionReaderTests.PreparedAsync(fixture, false); await using var data = prepared.Data;
        var review = await data.PrepareDispatchAsync(1, "approved-finite-race-preparation");
        var physical = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync());
        var value = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        Assert.Equal("11250.00", value["remainingValue"].AsString);
        var journals = await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument());
        var movements = await data.Rows("movements").CountDocumentsAsync(new BsonDocument());
        var transitions = await data.Rows(LotValueStore.Transitions).CountDocumentsAsync(new BsonDocument());
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keys = new[] { "approved-finite-race-a", "approved-finite-race-b" };
        var results = new BsonDocument?[2]; var started = 0;
        var attempts = Enumerable.Range(0, 2).Select(index => Task.Run(async () => {
            await gate.Task; Interlocked.Increment(ref started);
            return await Record.ExceptionAsync(async () => { results[index] = await data.DispatchAsync(review, keys[index]); });
        })).ToArray();
        gate.SetResult(); var failures = await Task.WhenAll(attempts);
        Assert.Equal(2, started); var winner = Assert.Single(Enumerable.Range(0, 2), index => results[index] is not null); var loser = 1 - winner;
        Assert.Null(failures[winner]); Assert.True(failures[loser] is PersistenceConflictException or AuthorizationDeniedException or DomainValidationException, failures[loser]?.ToString());
        var shipment = await data.Rows("shipments").Find(new BsonDocument()).SingleAsync();
        Assert.Equal(2, shipment["schemaVersion"].AsInt32); Assert.Equal("committed", shipment["phase"].AsString); Assert.Equal(keys[winner], shipment["requestKey"].AsString);
        Assert.Equal("11250.00", Assert.Single(shipment["valueEffects"].AsBsonArray)["transferredValue"].AsString);
        Assert.Equal(physical with { Balance = StockBalancePolicy.Dispatch(physical.Balance, physical.Balance.Revision, 1) },
            InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", data.BalanceId)).SingleAsync()));
        var remaining = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", data.BalanceId)).SingleAsync();
        Assert.Equal(0L, remaining["remainingPackages"].AsInt64); Assert.Equal("0.00", remaining["remainingValue"].AsString);
        Assert.Equal(movements + 1, await data.Rows("movements").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(transitions + 1, await data.Rows(LotValueStore.Transitions).CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(journals + 1, await data.Rows("journalEntries").CountDocumentsAsync(new BsonDocument()));
        var journal = await data.Rows("journalEntries").Find(new BsonDocument("_id", shipment["journalId"])).SingleAsync();
        Assert.Equal(11250m, journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == "cogs")["debit"].AsDecimal);
        Assert.Equal(11250m, journal["lines"].AsBsonArray.Single(line => line["mappingKey"] == "inventory")["credit"].AsDecimal);
        Assert.Equal(1L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument("operation", ShipmentDispatchBson.Operation)));
        var stable = await data.BusinessAsync(); var valuation = await data.Stock.Valuation.StateAsync();
        Assert.Equal(results[winner], await data.Dispatch.RecoverAsync(data.Dispatcher, keys[winner]));
        Assert.Null(await data.Dispatch.RecoverAsync(data.Dispatcher, keys[loser]));
        Assert.Equal(results[winner], await data.DispatchAsync(review, keys[winner]));
        Assert.Equal(stable, await data.BusinessAsync()); Assert.Equal(valuation, await data.Stock.Valuation.StateAsync());
    }

    [WindowsStorageFact]
    public async Task Approved_dispatch_MFA_expiry_after_acknowledged_own_movement_consume_and_COGS_rolls_back_all_transactional_rows_and_receipt() {
        var prepared = await ApprovedShipmentDispatchAdmissionReaderTests.PreparedAsync(fixture, false); await using var data = prepared.Data;
        var signed = await data.Catalog.Sessions.ReadAsync(data.Catalog.Issued[data.Dispatcher.UserId].OpaqueToken);
        Assert.NotNull(signed); Assert.Equal(data.Dispatcher.Session, signed.Reference); Assert.Equal(data.Dispatcher.FactorCompletedAt, signed.Proof.FactorCompletedAt);
        var maximumAge = Permissions.Catalog[Permissions.ShipmentDispatch].Assurance.MaximumAge ?? SessionPolicy.FreshFactorLifetime;
        Assert.Equal(TimeSpan.FromMinutes(5), maximumAge);
        var expiredAt = signed.Proof.FactorCompletedAt.Add(maximumAge).AddMilliseconds(1);
        Assert.True(expiredAt > data.Clock.UtcNow); Assert.False(data.Clock.UseSystemClock);
        var requests = new ConcurrentDictionary<int, string>();
        var movementAck = 0; var consumeAck = 0; var journalAck = 0; var advanced = 0;
        var settings = fixture.Client.Settings.Clone(); settings.ClusterConfigurator = builder => {
            builder.Subscribe<CommandStartedEvent>(command => {
                if (!command.Command.TryGetValue("insert", out var table) || !command.Command.TryGetValue("documents", out var documents) ||
                    !documents.IsBsonArray) return;
                if (table != "movements" && table != LotValueStore.Transitions && table != "journalEntries") return;
                foreach (var value in documents.AsBsonArray) {
                    var row = value.AsBsonDocument;
                    if (!row.TryGetValue("source", out var source) || !source.IsBsonDocument || source["namespace"] != "fulfillment.shipment") continue;
                    if (table == "movements") {
                        var leg = Assert.Single(row["legs"].AsBsonArray); Assert.Equal("Dispatch", leg["action"].AsString); Assert.Equal(1L, leg["packages"].ToInt64());
                        requests[command.RequestId] = "movement";
                    } else if (table == LotValueStore.Transitions) {
                        Assert.Equal("consume", row["operation"].AsString); Assert.Equal("11250.00", row["transferredValue"].AsString); Assert.Equal(1L, row["packages"].AsInt64);
                        requests[command.RequestId] = "consume";
                    } else if (table == "journalEntries") {
                        Assert.Equal("dispatchCogs", source["eventKind"].AsString);
                        Assert.Equal(11250m, row["lines"].AsBsonArray.Single(line => line["mappingKey"] == "cogs")["debit"].AsDecimal);
                        Assert.Equal(11250m, row["lines"].AsBsonArray.Single(line => line["mappingKey"] == "inventory")["credit"].AsDecimal);
                        requests[command.RequestId] = "journal";
                    }
                }
            });
            builder.Subscribe<CommandSucceededEvent>(command => {
                if (!requests.TryRemove(command.RequestId, out var kind)) return;
                Assert.Equal(1L, command.Reply.GetValue("n", new BsonInt32(0)).ToInt64());
                Assert.Empty(command.Reply.GetValue("writeErrors", new BsonArray()).AsBsonArray); Assert.False(command.Reply.Contains("writeConcernError"));
                if (kind == "movement") Interlocked.Increment(ref movementAck);
                else if (kind == "consume") Interlocked.Increment(ref consumeAck);
                else if (kind == "journal") {
                    Interlocked.Increment(ref journalAck); Assert.Equal(1, Volatile.Read(ref movementAck)); Assert.Equal(1, Volatile.Read(ref consumeAck));
                    if (Interlocked.CompareExchange(ref advanced, 1, 0) == 0) data.Clock.UtcNow = expiredAt;
                }
            });
        };
        using var client = new MongoClient(settings); var database = client.GetDatabase(data.Database.DatabaseNamespace.DatabaseName);
        var unit = new MongoUnitOfWork(client); var ids = new IdAllocator(database);
        var organizations = new OrganizationMasterDataService(database, new AuthorizationStore(database, unit, data.Clock), ids, data.Clock);
        var orders = OrderSubmissionService.CreateWithApprovedAdmission(database, unit, ids, data.Clock, organizations);
        var preparations = new ShipmentPreparationService(database, unit, ids, data.Clock, orders);
        var dispatch = ShipmentDispatchService.CreateWithApprovedAdmission(database, unit, ids, data.Clock, orders, preparations, organizations);
        var source = await orders.ReadFulfillmentOrderAsync(data.Dispatcher, data.SuborderId);
        await preparations.SaveAsync(data.Dispatcher, data.SuborderId, 0, source.Review, [new(new(data.SkuId), 1)], GenuineFiniteDispatchTestData.Meta("approved-expiry-preparation"));
        var ready = await dispatch.ReadPreparationAsync(data.Dispatcher, data.SuborderId);
        var selected = new UnitToko.Modules.Fulfillment.ShipmentLotSelection(Assert.Single(ready.Lots).Reservation.ReservationId, 1);
        var review = await dispatch.PreviewAsync(data.Dispatcher, data.SuborderId, ready.PreparationId.Value, ready.PreparationRevision, data.Courier.UserId, [selected]);
        var before = await TransactionalState(data); const string key = "approved-after-own-journal-expiry";
        var failure = await Record.ExceptionAsync(() => dispatch.DispatchAsync(data.Dispatcher, data.SuborderId, ready.PreparationId.Value,
            ready.PreparationRevision, data.Courier.UserId, [selected], review.Review, GenuineFiniteDispatchTestData.Meta(key)));
        Assert.Equal(1, movementAck); Assert.Equal(1, consumeAck); Assert.Equal(1, journalAck); Assert.Equal(1, advanced); Assert.Equal(expiredAt, data.Clock.UtcNow);
        Assert.True(failure is AuthorizationDeniedException { Code: "step_up_required" } or DomainValidationException { Code: "shipment_dispatch_admission_unavailable" }, failure?.ToString());
        Assert.Equal(before, await TransactionalState(data));
        Assert.Equal(0L, await data.Rows("shipments").CountDocumentsAsync(new BsonDocument()));
        Assert.Equal(0L, await data.Rows("idempotencyRecords").CountDocumentsAsync(new BsonDocument { { "operation", ShipmentDispatchBson.Operation }, { "key", key } }));
        // A real subsequent provider session may inspect the original key. The
        // expired proof is never edited, and fixture time is never rewound.
        data.Dispatcher = await data.IssueExistingSessionAsync(data.Dispatcher);
        Assert.Null(await dispatch.RecoverAsync(data.Dispatcher, key));
    }
    private static async Task<BsonDocument> TransactionalState(GenuineFiniteDispatchTestData data) {
        var state = new BsonDocument(); using var names = await data.Database.ListCollectionNamesAsync();
        foreach (var name in (await names.ToListAsync()).Order(StringComparer.Ordinal)) {
            // IDs reserved before the owner transaction may legitimately leave
            // gaps. The actual shipment number is allocated inside that transaction.
            var filter = name == "idCounters" ? new BsonDocument("_id", new BsonRegularExpression("^shipment-number\\.")) : new BsonDocument();
            state[name] = new BsonArray(await data.Rows(name).Find(filter).Sort(new BsonDocument("_id", 1)).ToListAsync());
        }
        return state;
    }
}
