using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using B = UnitToko.Platform.Inventory.PackConversionBson;
using Prepared = UnitToko.Tests.Inventory.PackConversionServiceTests.PreparedConversion;

namespace UnitToko.Tests.Inventory;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class PackConversionCurrentDestinationTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Genuine_converted_destination_birth_and_current_finite_value_are_read_without_business_writes() {
        await using var prepared = await Prepared.CreateAsync(fixture);
        var data = prepared.Data; var result = prepared.Result!; var before = await prepared.StateAsync();
        var accounting = await data.Valuation.StateAsync(); var documents = await data.Valuation.Documents.StateAsync();
        var current = await data.Unit.ExecuteAsync((tx, ct) => prepared.Service().ReadCurrentDestinationAsync(tx, result["destinationBalanceId"].AsInt64, ct), default);
        Assert.Equal(result["destinationBalanceId"].AsInt64, current.Id); Assert.Equal(result["destinationLotId"].AsInt64, current.Balance.Key.LotId.Value);
        Assert.Equal((12L, 0L, 0L, 0L, 2L), (current.Balance.OnHand, current.Balance.Reserved, current.Balance.Held, current.Balance.Expired, current.Balance.Revision));
        var head = await data.Rows(LotValueStore.AvailableHeads).Find(new BsonDocument("_id", current.Id)).SingleAsync();
        Assert.Equal((12L, "11250.00"), (head["remainingPackages"].AsInt64, head["remainingValue"].AsString));
        Assert.Equal(before, await prepared.StateAsync()); Assert.Equal(accounting, await data.Valuation.StateAsync()); Assert.Equal(documents, await data.Valuation.Documents.StateAsync());
    }

    [WindowsStorageFact]
    public async Task Missing_birth_relinked_legs_changed_end_and_unowned_reserve_or_dispatch_cannot_supply_current_authority() {
        await using var prepared = await Prepared.CreateAsync(fixture);
        var data = prepared.Data; var result = prepared.Result!; var id = result["destinationBalanceId"].AsInt64;
        var state = await prepared.StateAsync(); var row = await data.Rows(B.Conversions).Find(new BsonDocument("_id", result["conversionId"])).SingleAsync();
        var birthMovement = await data.Rows("movements").Find(new BsonDocument("_id", row["movementId"])).SingleAsync();
        var destination = InventoryBson.ReadBalance(await data.Rows("stockBalances").Find(new BsonDocument("_id", id)).SingleAsync());
        foreach (var fault in new[] { "missing-birth", "relinked-birth", "changed-end", "unowned-reserve", "unowned-dispatch" }) {
            var observed = 0;
            async Task Attempt() => await data.Unit.ExecuteAsync(async (tx, ct) => {
                // Warm the actual owning reader in this transaction, then corrupt
                // only the negative fixture. No positive movement/source is seeded.
                await prepared.Service().ReadCurrentDestinationAsync(tx, id, ct);
                if (fault == "missing-birth") await data.Rows(B.Conversions).DeleteOneAsync(tx, new BsonDocument("_id", row["_id"]), cancellationToken: ct);
                else if (fault == "relinked-birth") {
                    var corrupted = birthMovement.DeepClone().AsBsonDocument; corrupted["legs"][1]["balanceId"] = id + 100000;
                    await data.Rows("movements").ReplaceOneAsync(tx, new BsonDocument("_id", corrupted["_id"]), corrupted,
                        new ReplaceOptions { BypassDocumentValidation = true }, ct);
                } else if (fault == "changed-end") {
                    var changed = destination with { Balance = destination.Balance with { OnHand = 13, Revision = 3 } };
                    await data.Rows("stockBalances").ReplaceOneAsync(tx, new BsonDocument("_id", id), InventoryBson.Balance(changed), cancellationToken: ct);
                } else {
                    var movementId = (await new IdAllocator(data.Database).NextAsync("movements", ct)).Value;
                    var before = fault == "unowned-dispatch" ? destination with { Balance = destination.Balance with { Reserved = 1 } } : destination;
                    var after = before with { Balance = fault == "unowned-dispatch" ? StockBalancePolicy.Dispatch(before.Balance, before.Balance.Revision, 1)
                        : StockBalancePolicy.Reserve(before.Balance, before.Balance.Revision, 1) };
                    var source = new InventorySource(fault == "unowned-dispatch" ? "fulfillment.shipment" : "ordering.order", 999999, fault == "unowned-dispatch" ? "dispatch" : "reserve", destination.PhysicalPoolId);
                    var negative = birthMovement.DeepClone().AsBsonDocument; negative["_id"] = movementId; negative["sourceEvent"] = source.EventKey;
                    negative["source"] = new BsonDocument { { "namespace", source.Namespace }, { "documentId", source.DocumentId }, { "eventKind", source.EventKind }, { "occurrence", source.Occurrence } };
                    negative["legs"] = new BsonArray { new BsonDocument { { "balanceId", id }, { "action", fault == "unowned-dispatch" ? "Dispatch" : "Reserve" }, { "packages", 1L },
                        { "before", InventoryBson.Balance(before) }, { "after", InventoryBson.Balance(after) } } };
                    await data.Rows("movements").InsertOneAsync(tx, negative, new InsertOneOptions { BypassDocumentValidation = true }, ct);
                    await data.Rows("stockBalances").ReplaceOneAsync(tx, new BsonDocument("_id", id), InventoryBson.Balance(after), cancellationToken: ct);
                }
                observed++;
                await prepared.Service().ReadCurrentDestinationAsync(tx, id, ct);
                return true;
            }, default);
            if (fault == "missing-birth")
                Assert.Equal(B.Broken().Code, (await Assert.ThrowsAsync<PersistenceConflictException>(Attempt)).Code);
            else if (fault == "relinked-birth")
                Assert.Equal("held_receipt_movement_history_unavailable", (await Assert.ThrowsAsync<DomainValidationException>(Attempt)).Code);
            else {
                var error = await Assert.ThrowsAsync<DomainValidationException>(Attempt);
                Assert.Equal(fault is "unowned-reserve" or "unowned-dispatch" ? "order_source_obligation_unavailable" : PackConversionService.CurrentDestinationUnavailable().Code, error.Code);
            }
            Assert.Equal(1, observed); Assert.Equal(state, await prepared.StateAsync());
        }
        // Genuine subsequent reservation/release acceptance is deliberately absent:
        // it requires the real ordering commercial/source admission integration.
    }
}
