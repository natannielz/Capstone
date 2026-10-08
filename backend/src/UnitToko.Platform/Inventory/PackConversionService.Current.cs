using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Procurement;
using UnitToko.Platform.Fulfillment;
using B = UnitToko.Platform.Inventory.PackConversionBson;

namespace UnitToko.Platform.Inventory;

public sealed partial class PackConversionService
{
    /// <summary>Current physical ownership only. The caller must separately prove
    /// actor, commercial, expiry and source authority. Historical dispatch requires
    /// its genuine finite-value owner; structural validity supplies no admission.</summary>
    internal Task<InventoryBalanceDocument> ReadCurrentDestinationAsync(IClientSessionHandle tx, long balanceId, CancellationToken ct,
        ShipmentDispatchValueRecords.HistoricalRead? valueHistory = null, FiniteHistoryRead? retained = null) => retained is null
            ? ReadCurrentDestinationCoreAsync(tx, balanceId, ct, valueHistory)
            : retained.ReadCurrentDestinationAsync(this, tx, balanceId, ct, valueHistory);
    private async Task<InventoryBalanceDocument> ReadCurrentDestinationCoreAsync(IClientSessionHandle tx, long balanceId, CancellationToken ct,
        ShipmentDispatchValueRecords.HistoricalRead? valueHistory = null, ConversionHistoryRead? retained = null) =>
        (await ReadCurrentDestinationValueCoreAsync(tx, balanceId, ct, valueHistory, retained)).Physical;
    private async Task<(InventoryBalanceDocument Physical, LotValueBalance Value)> ReadCurrentDestinationValueCoreAsync(IClientSessionHandle tx,
        long balanceId, CancellationToken ct, ShipmentDispatchValueRecords.HistoricalRead? valueHistory = null, ConversionHistoryRead? retained = null) {
        new InventoryRepository(database).RequireSession(tx);
        valueHistory ??= new(database, clock, tx); valueHistory.Require(database, tx);
        if (balanceId <= 0) throw CurrentDestinationUnavailable();
        var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; var capturedAt = Now;
        await StorageAsync(ct);
        var capture = new PurchaseOrderSourceReadCapture();
        var actual = await capture.OneAsync(database, tx, "stockBalances", new BsonDocument("_id", balanceId), ct) ?? throw CurrentDestinationUnavailable();
        var current = InventoryBson.ReadBalance(actual);
        var lot = await capture.OneAsync(database, tx, "lots", new BsonDocument("_id", current.Balance.Key.LotId.Value), ct) ?? throw CurrentDestinationUnavailable();
        var facts = InventoryGraph.ReadLot(lot);
        if (lot["origin"] != "packageConversion" || facts.ParentId is null || facts.OrganizationId != current.Balance.Key.Location.OrganizationId.Value ||
            facts.SkuId != current.Balance.Key.SkuId.Value || facts.DefinitionId != current.Balance.Key.DefinitionId.Value) throw CurrentDestinationUnavailable();
        var reads = retained ?? new ConversionHistoryRead(this, tx, capturedAt, valueHistory); reads.Require(this, tx);
        try {
            var birth = await ConversionAsync(tx, lot["sourceId"].AsInt64, ct, read: reads);
            if (birth["destinationLot"] != lot) throw CurrentDestinationUnavailable();
            var original = InventoryBson.ReadBalance(birth["destinationAfter"].AsBsonDocument);
            if (original.Id != current.Id || original.PhysicalPoolId != current.PhysicalPoolId || original.Balance.Key != current.Balance.Key)
                throw CurrentDestinationUnavailable();
            var key = InventoryBson.Balance(original);
            var linked = new BsonDocument("$or", new BsonArray {
                new BsonDocument("legs.balanceId", original.Id), new BsonDocument("legs.before._id", original.Id), new BsonDocument("legs.after._id", original.Id),
                new BsonDocument("_id", birth["movementId"]), new BsonDocument("sourceEvent", B.Source(birth["_id"].AsInt64, original.PhysicalPoolId).EventKey),
                new BsonDocument("legs", new BsonDocument("$elemMatch", new BsonDocument { { "before.physicalPoolId", original.PhysicalPoolId }, { "before.location", key["location"] },
                    { "before.skuId", key["skuId"] }, { "before.definitionId", key["definitionId"] }, { "before.lotId", key["lotId"] } })),
                new BsonDocument("legs", new BsonDocument("$elemMatch", new BsonDocument { { "after.physicalPoolId", original.PhysicalPoolId }, { "after.location", key["location"] },
                    { "after.skuId", key["skuId"] }, { "after.definitionId", key["definitionId"] }, { "after.lotId", key["lotId"] } })) });
            var movements = await CapturedManyAsync(tx, "movements", linked, HeldReceiptMovementHistory.MaximumMovements + 1, capture, ct);
            if (movements.Count is < 1 or > HeldReceiptMovementHistory.MaximumMovements) throw CurrentDestinationUnavailable();
            foreach (var movement in movements) {
                if (!await Rows("movements").Find(tx, new BsonDocument { { "_id", movement["_id"] }, { "$and", new BsonArray { InventoryMovementSchemas.Validator() } } }).AnyAsync(ct) ||
                    movement["physicalPoolId"] != original.PhysicalPoolId || movement["recordedAt"].ToUniversalTime() > capturedAt.UtcDateTime ||
                    movement["legs"].AsBsonArray.Count(leg => leg["balanceId"] == original.Id) != 1) throw CurrentDestinationUnavailable();
            }
            var ordered = movements.OrderBy(movement => DestinationLeg(movement, original.Id)["before"]["version"].AsInt64).ToArray();
            var first = ordered[0]; var genesis = DestinationLeg(first, original.Id);
            if (first["_id"] != birth["movementId"] || genesis["action"] != "ConvertIn" || genesis["before"] != birth["destinationBefore"] ||
                genesis["after"] != birth["destinationAfter"] || genesis["packages"] != birth["quantities"]["destinationPackages"]) throw CurrentDestinationUnavailable();
            var state = original; var time = first["recordedAt"]; var poolRevision = first["poolRevision"].AsInt64;
            var events = new HashSet<string>(StringComparer.Ordinal) { first["sourceEvent"].AsString };
            var minimumFence = checked(birth["expected"]["poolFence"].AsInt64 + 3L);
            foreach (var movement in ordered.Skip(1)) {
                if (!events.Add(movement["sourceEvent"].AsString) || movement["recordedAt"].CompareTo(time) < 0 || movement["poolRevision"].AsInt64 < poolRevision)
                    throw CurrentDestinationUnavailable();
                await RequireDestinationOrderMovementAsync(tx, movement, ct, valueHistory);
                var leg = DestinationLeg(movement, original.Id);
                if (leg["before"] != InventoryBson.Balance(state)) throw CurrentDestinationUnavailable();
                state = DestinationTransition(state, leg);
                if (leg["after"] != InventoryBson.Balance(state)) throw CurrentDestinationUnavailable();
                time = movement["recordedAt"]; poolRevision = movement["poolRevision"].AsInt64;
                minimumFence = checked(minimumFence + movement["legs"].AsBsonArray.Count);
            }
            if (state != current) throw CurrentDestinationUnavailable();
            var pool = await PoolAsync(tx, current.PhysicalPoolId, ct);
            if (pool["revision"].AsInt64 < poolRevision || pool["fence"].AsInt64 < minimumFence || InventoryGraph.Location(pool["location"].AsBsonDocument) != current.Balance.Key.Location)
                throw CurrentDestinationUnavailable();
            capture.RecordPool(pool);
            await new InventoryGraph(database).RequireAsync(tx, current, false, ct);
            var orderLinked = ordered.Length > 1;
            foreach (var (collection, prefix) in new[] { ("orders", "allocations"), ("reservations", "allocation"),
                ("orderDecisions", "releases.allocation"), ("shipments", "stockEffects.allocation") }) {
                var identity = new BsonDocument("$or", new BsonArray {
                    new BsonDocument(prefix + ".balanceId", current.Id),
                    new BsonDocument { { prefix + ".poolId", current.PhysicalPoolId }, { prefix + ".location", key["location"] },
                        { prefix + ".skuId", key["skuId"] }, { prefix + ".definitionId", key["definitionId"] }, { prefix + ".lotId", key["lotId"] } } });
                var linkedRows = await CapturedManyAsync(tx, collection, identity, OrderStockSourceRecords.MaximumRows + 1, capture, ct);
                if (linkedRows.Count > OrderStockSourceRecords.MaximumRows) throw CurrentDestinationUnavailable();
                orderLinked |= linkedRows.Count > 0;
            }
            if (orderLinked) await RequireDestinationOrderMembershipAsync(tx, pool, ct, valueHistory);
            // Reservations and releases retain the available lot's capitalized
            // packages/value. Only actual release/conversion owners authenticate
            // this bounded head; dispatch requires its exact accepted owner effect.
            await CaptureDestinationValueAsync(tx, current, capture, ct);
            var value = await values.ReadAvailableAsync(tx, current,
                (owner, session, source, hash, token) => retained is null ? AuthenticateSourceAsync(owner, session, source, hash, token, valueHistory)
                    : AuthenticateSourceAsync(owner, session, source, hash, ImmutableHashSet<long>.Empty, token, reads), ct);
            capture.Seal(); await capture.RequireAsync(database, tx, ct);
            if (orderLinked) await RequireDestinationOrderMembershipAsync(tx, await PoolAsync(tx, current.PhysicalPoolId, ct), ct, valueHistory);
            var freshBirth = await ConversionAsync(tx, birth["_id"].AsInt64, ct, read: reads);
            if (freshBirth != birth || Now < capturedAt || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw CurrentDestinationUnavailable();
            ct.ThrowIfCancellationRequested(); return (current, value);
        } finally { reads.End(this); }
    }
    private async Task CaptureDestinationValueAsync(IClientSessionHandle tx, InventoryBalanceDocument current, PurchaseOrderSourceReadCapture capture, CancellationToken ct) {
        var key = LotValueStore.Key(current.Balance.Key);
        await capture.OneAsync(database, tx, LotValueStore.AvailableHeads, new BsonDocument("_id", current.Id), ct);
        var linked = new BsonDocument("$or", new BsonArray {
            new BsonDocument("availableId", current.Id), new BsonDocument("destinationId", current.Id),
            new BsonDocument { { "poolId", current.PhysicalPoolId }, { "key", key } },
            new BsonDocument { { "poolId", current.PhysicalPoolId }, { "destinationKey", key } } });
        var history = await CapturedManyAsync(tx, LotValueStore.Transitions, linked, LotValueStore.MaximumTransitions + 1, capture, ct);
        if (history.Count is < 1 or > LotValueStore.MaximumTransitions) throw CurrentDestinationUnavailable();
    }
    private async Task RequireDestinationOrderMembershipAsync(IClientSessionHandle tx, BsonDocument pool, CancellationToken ct,
        ShipmentDispatchValueRecords.HistoricalRead valueHistory) {
        await OrderStockSourceStorage.RequireAsync(database, ct);
        var owner = new OrderSubmissionService(database, unit, ids, clock, null, valueHistory);
        _ = await new OrderStockSourceRecords(database).ReadAsync(tx, owner,
            new(pool["_id"].AsInt64, InventoryGraph.Location(pool["location"].AsBsonDocument), pool["revision"].AsInt64, pool["fence"].AsInt64), ct);
    }
    private async Task RequireDestinationOrderMovementAsync(IClientSessionHandle tx, BsonDocument movement, CancellationToken ct,
        ShipmentDispatchValueRecords.HistoricalRead valueHistory) {
        var source = movement["source"];
        var kind = (source["namespace"].AsString, source["eventKind"].AsString) switch {
            ("ordering.order", "reserve") => "submission", ("ordering.decision", "release") => "decision",
            ("fulfillment.shipment", "dispatch") => "shipment", _ => throw CurrentDestinationUnavailable() };
        var actual = await OrderSubmissionService.ReadStockSourceEventAsync(database, tx, kind, source["documentId"].AsInt64, true, ct, valueHistory, clock);
        if (movement["actorId"] != actual.Audit["actorId"] || movement["actorGrantVersion"] != actual.Audit["actorGrantVersion"] ||
            movement["recordedAt"].CompareTo(actual.Time) < 0 || movement["reason"] != actual.Record["reason"] || movement["correlationId"] != actual.Record["correlationId"] ||
            movement["sourceRevision"] != 1L || source["occurrence"] != movement["physicalPoolId"]) throw CurrentDestinationUnavailable();
        var allocations = kind == "submission" ? actual.Record["allocations"].AsBsonArray.Select(value => value.AsBsonDocument) :
            actual.Record[kind == "decision" ? "releases" : "stockEffects"].AsBsonArray.Select(value => value["allocation"].AsBsonDocument);
        var facts = allocations.Where(value => value["poolId"] == movement["physicalPoolId"]).Select(OrderSubmissionBson.Allocation).ToArray();
        if (facts.Length is < 1 or > 100 || facts.Length != movement["legs"].AsBsonArray.Count || facts.Select(fact => fact.Change.Id).Distinct().Count() != facts.Length)
            throw CurrentDestinationUnavailable();
        foreach (var fact in facts) {
            var leg = DestinationLeg(movement, fact.Change.Id); var before = InventoryBson.ReadBalance(leg["before"].AsBsonDocument);
            if (movement["poolRevision"] != fact.Pool.Revision || before.Id != fact.Change.Id || before.PhysicalPoolId != fact.Pool.Id || before.Balance.Key != fact.Change.Key ||
                before.Balance.Revision != fact.Change.ExpectedRevision || leg["packages"] != fact.Change.Packages ||
                leg["action"] != (kind == "submission" ? "Reserve" : kind == "decision" ? "Release" : "Dispatch") ||
                leg["after"] != InventoryBson.Balance(DestinationTransition(before, leg))) throw CurrentDestinationUnavailable();
        }
    }
    private static BsonDocument DestinationLeg(BsonDocument movement, long id) => movement["legs"].AsBsonArray
        .SingleOrDefault(leg => leg["balanceId"] == id)?.AsBsonDocument ?? throw CurrentDestinationUnavailable();
    private static InventoryBalanceDocument DestinationTransition(InventoryBalanceDocument before, BsonDocument leg) => before with {
        Balance = leg["action"].AsString switch {
            "Reserve" => StockBalancePolicy.Reserve(before.Balance, before.Balance.Revision, leg["packages"].AsInt64),
            "Release" => StockBalancePolicy.Release(before.Balance, before.Balance.Revision, leg["packages"].AsInt64),
            "Dispatch" => StockBalancePolicy.Dispatch(before.Balance, before.Balance.Revision, leg["packages"].AsInt64),
            _ => throw CurrentDestinationUnavailable() } };
    internal static DomainValidationException CurrentDestinationUnavailable() => new("pack_conversion_current_destination_unavailable",
        "The exact converted lot, complete current physical history and conserved value require genuine owning records");
}
