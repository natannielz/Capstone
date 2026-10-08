using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Procurement.ProcurementStockReceiptBson;

namespace UnitToko.Platform.Inventory;

internal sealed record HeldReceiptMovementState(InventoryBalanceDocument Original, InventoryBalanceDocument Current,
    ImmutableArray<long> MovementIds);

/// <summary>Movement-only companion to the genuine receipt owner's semantic reconstruction.
/// The owning caller must still authenticate PO, inspection, private evidence, source configuration,
/// quality and valuation lineage and all write/disclosure authority. This creates no approval or
/// reusable write proof and never changes stock. Every call reconstructs within its actual transaction.
/// ReleaseHold requires its genuine release/value owner; ExpireHeld remains unsupported.</summary>
internal sealed class HeldReceiptMovementHistory(IMongoDatabase database, MongoUnitOfWork unit, IClock clock,
    ShipmentDispatchValueRecords.HistoricalRead? dispatchHistory = null)
{
    internal const int MaximumMovements = 1024;
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static BsonDocument OriginFilter(FilterDefinition<BsonDocument> filter) =>
        filter.Render(new(BsonDocumentSerializer.Instance, BsonSerializer.SerializerRegistry));
    private async Task<BsonDocument?> OriginOneAsync(IClientSessionHandle tx, string name, FilterDefinition<BsonDocument> filter,
        PurchaseOrderSourceReadCapture? capture, CancellationToken ct) => capture is null
        ? await Rows(name).Find(tx, filter).SingleOrDefaultAsync(ct) : await capture.OneAsync(database, tx, name, OriginFilter(filter), ct);
    internal Task RequireOriginStorageAsync(CancellationToken ct) => RequireStorageAsync(ct);
    internal static DomainValidationException Unavailable() => new("held_receipt_movement_history_unavailable",
        "A complete bounded authenticated held-receipt movement history is required");
    internal static DomainValidationException Unsupported() => new("held_receipt_movement_source_unavailable",
        "This held-stock transition has no implemented owning proof and cannot be approved by quantity arithmetic");

    /// <summary>Immutable receipt genesis only. It grants no current quantity, availability or write authority.
    /// Release-origin authentication uses this edge so its full current movement chain does not recurse.</summary>
    internal async Task<InventoryBalanceDocument> RequireOriginAsync(IClientSessionHandle tx, BsonDocument ownerReceipt, CancellationToken ct,
        PurchaseOrderSourceReadCapture? capture = null)
    {
        new InventoryRepository(database).RequireSession(tx);
        dispatchHistory?.Require(database, tx);
        await RequireStorageAsync(ct);
        var actual = await OriginOneAsync(tx, B.Receipts, F.Eq("_id", ownerReceipt["_id"]) & ProcurementStockReceiptSchemas.Validator(B.Receipts), capture, ct) ?? throw Unavailable();
        Equal(actual, ownerReceipt); Equal(actual["snapshotHash"], B.Hash(actual));
        if (actual["recordedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw Unavailable();
        await RequireReceiptEffectsAsync(tx, actual, ct, capture);
        Equal(await OriginOneAsync(tx, "lots", F.Eq("_id", actual["lotId"]), capture, ct) ?? throw Unavailable(), InventoryHeldReceiptWriter.Lot(actual));
        var root = await OriginOneAsync(tx, "movements", F.Eq("_id", actual["movementId"]) & InventoryMovementSchemas.Validator(), capture, ct) ?? throw Unavailable();
        Equal(root, RootMovement(actual));
        dispatchHistory?.Require(database, tx);
        return InventoryHeldReceiptWriter.After(actual);
    }

    internal async Task<HeldReceiptMovementState> RequireAsync(IClientSessionHandle tx, BsonDocument ownerReceipt,
        CancellationToken ct = default, ProcurementQualityProofGraph? currentGraph = null, HeldReceiptOriginProof? retainedOrigin = null)
    {
        try {
            var repository = new InventoryRepository(database); repository.RequireSession(tx);
            dispatchHistory?.Require(database, tx);
            currentGraph?.Require(tx);
            var transactionNumber = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
            var capturedAt = clock.UtcNow;
            if (capturedAt == default || capturedAt.Offset != TimeSpan.Zero || ownerReceipt is null) throw Unavailable();
            var receipt = ownerReceipt.DeepClone().AsBsonDocument;
            await RequireStorageAsync(ct);
            var actual = await Rows(B.Receipts).Find(tx, F.Eq("_id", receipt["_id"]) &
                ProcurementStockReceiptSchemas.Validator(B.Receipts)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
            Equal(actual, receipt);
            Equal(receipt["snapshotHash"], B.Hash(receipt));
            var input = B.Input(receipt["input"]);
            Equal(receipt["input"], B.Input(input));
            if (receipt["_id"] != receipt["receiptId"] || receipt["revision"] != 1L ||
                receipt["recordedAt"].ToUniversalTime() > capturedAt.UtcDateTime) throw Unavailable();
            await RequireReceiptEffectsAsync(tx, receipt, ct);
            var lot = await Rows("lots").Find(tx, F.Eq("_id", receipt["lotId"])).SingleOrDefaultAsync(ct) ?? throw Unavailable();
            Equal(lot, InventoryHeldReceiptWriter.Lot(receipt));
            var original = InventoryHeldReceiptWriter.After(receipt);
            var currentRow = await Rows("stockBalances").Find(tx, F.Eq("_id", receipt["balanceId"])).SingleOrDefaultAsync(ct) ?? throw Unavailable();
            var current = InventoryBson.ReadBalance(currentRow);
            if (current.Id != original.Id || current.PhysicalPoolId != original.PhysicalPoolId || current.Balance.Key != original.Balance.Key)
                throw Unavailable();
            await new InventoryGraph(database).RequireAsync(tx, current, false, ct);
            // Reverse IDs and immutable SKU/definition/lot key scopes catch a changed/missing leg
            // linkage instead of treating a partial query as complete history.
            var key = InventoryBson.Balance(original);
            var linked = F.Eq("legs.balanceId", original.Id) | F.Eq("legs.before._id", original.Id) | F.Eq("legs.after._id", original.Id) |
                F.Eq("_id", receipt["movementId"]) | F.Eq("sourceEvent", InventoryHeldReceiptWriter.Source(receipt["_id"].AsInt64).EventKey) |
                F.ElemMatch("legs", F.Eq("before.physicalPoolId", original.PhysicalPoolId) & F.Eq("before.location", key["location"]) &
                    F.Eq("before.skuId", key["skuId"]) & F.Eq("before.definitionId", key["definitionId"]) & F.Eq("before.lotId", key["lotId"])) |
                F.ElemMatch("legs", F.Eq("after.physicalPoolId", original.PhysicalPoolId) & F.Eq("after.location", key["location"]) &
                    F.Eq("after.skuId", key["skuId"]) & F.Eq("after.definitionId", key["definitionId"]) & F.Eq("after.lotId", key["lotId"]));
            var movements = await Rows("movements").Find(tx, linked).Limit(MaximumMovements + 1).ToListAsync(ct);
            if (movements.Count is < 1 or > MaximumMovements) throw Unavailable();
            foreach (var movement in movements) {
                if (!await Rows("movements").Find(tx, F.Eq("_id", movement["_id"]) & InventoryMovementSchemas.Validator()).AnyAsync(ct) ||
                    movement["physicalPoolId"] != original.PhysicalPoolId || movement["recordedAt"].ToUniversalTime() > capturedAt.UtcDateTime ||
                    movement["legs"].AsBsonArray.Count(leg => leg["balanceId"] == original.Id) != 1) throw Unavailable();
            }
            var ordered = movements.OrderBy(movement => TargetLeg(movement, original.Id)["before"]["version"].AsInt64).ToArray();
            Equal(ordered[0], RootMovement(receipt));
            var state = original;
            var time = receipt["recordedAt"];
            var poolRevision = receipt["input"]["poolRevision"].AsInt64;
            var minimumPoolFence = receipt["poolSource"]["poolFence"].AsInt64;
            if (minimumPoolFence >= long.MaxValue) throw Unavailable();
            minimumPoolFence++;
            var sources = new HashSet<string>(StringComparer.Ordinal) { ordered[0]["sourceEvent"].AsString };
            var supported = ordered.Skip(1).Select(movement => (Movement: movement, Kind: SourceKind(movement))).ToArray();
            // Release provenance uses immutable receipt genesis, never this current chain.
            HeldReceiptOriginProof? origin = null;
            if (supported.Any(item => item.Kind is "stock-release" or "pack-conversion")) {
                origin = retainedOrigin ?? currentGraph?.CurrentMovementOrigin(database, tx, receipt["_id"].AsInt64);
                if (origin is null) {
                    var ids = new IdAllocator(database); var catalog = new UnitToko.Platform.Catalog.CatalogAuthoringService(database, unit, ids, clock);
                    var requests = new ProcurementRequestService(database, unit, ids, clock, catalog);
                    var orders = new PurchaseOrderService(database, unit, ids, clock, catalog, requests, new SupplierService(database, unit, ids, clock, catalog));
                    origin = await HeldReceiptOriginProof.CaptureAsync(database, unit, clock, catalog, orders,
                        new(database, unit, ids, clock), tx, receipt["_id"].AsInt64, ct);
                } else await origin.RequireAsync(database, tx, ct);
                if (origin.Receipt != receipt) throw Unavailable();
            }
            if (supported.Any(item => item.Kind is "submission" or "decision" or "shipment")) {
                await OrderStockSourceStorage.RequireAsync(database, ct);
                var pool = await Rows("inventoryPools").Find(tx, F.Eq("_id", original.PhysicalPoolId)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
                var owner = new OrderSubmissionService(database, unit, new(database), clock, reader: null, history: dispatchHistory);
                _ = await new OrderStockSourceRecords(database).ReadAsync(tx, owner,
                    new(original.PhysicalPoolId, original.Balance.Key.Location, pool["revision"].AsInt64, pool["fence"].AsInt64), ct);
            }
            foreach (var (movement, kind) in supported) {
                if (!sources.Add(movement["sourceEvent"].AsString) || movement["recordedAt"].CompareTo(time) < 0 ||
                    movement["poolRevision"].AsInt64 < poolRevision) throw Unavailable();
                await RequireSourceAsync(tx, movement, kind, origin, ct);
                var leg = TargetLeg(movement, state.Id);
                Equal(leg["before"], InventoryBson.Balance(state));
                state = Transition(state, leg);
                Equal(leg["after"], InventoryBson.Balance(state));
                time = movement["recordedAt"]; poolRevision = movement["poolRevision"].AsInt64;
                var writtenLegs = movement["legs"].AsBsonArray.Count;
                if (minimumPoolFence > long.MaxValue - writtenLegs) throw Unavailable();
                // Each actual InventoryLedger leg writes this same canonical pool. Other
                // source/governance fences may increase it further; they never justify regression.
                minimumPoolFence += writtenLegs;
            }
            Equal(currentRow, InventoryBson.Balance(state));
            var actualPool = await Rows("inventoryPools").Find(tx, F.Eq("_id", state.PhysicalPoolId)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
            InventoryGraph.RequirePool(actualPool);
            if (actualPool["revision"].AsInt64 < poolRevision || actualPool["fence"].AsInt64 < minimumPoolFence) throw Unavailable();
            repository.RequireSession(tx);
            if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber || clock.UtcNow < capturedAt) throw Unavailable();
            ct.ThrowIfCancellationRequested();
            if (origin is not null) currentGraph?.RememberCurrentMovementOrigin(database, tx, receipt, origin);
            dispatchHistory?.Require(database, tx);
            return new(original, current, ordered.Select(movement => movement["_id"].AsInt64).ToImmutableArray());
        } catch { await InventoryRepository.AbortAsync(tx); throw; }
    }

    private async Task RequireStorageAsync(CancellationToken ct)
    {
        await InventoryRepository.RequireStorageAsync(database, B.Receipts, ProcurementStockReceiptSchemas.Validator(B.Receipts),
            ProcurementStockReceiptSchemas.Keys(B.Receipts), ct);
        await InventoryRepository.RequireStorageAsync(database, "lots", InventoryLotSchemas.Validator(), new BsonDocument("_id", 1), ct);
        await InventoryRepository.RequireStorageAsync(database, "stockBalances", InventorySchemas.BalanceValidator(), InventorySchemas.BalanceKey(), ct);
        await InventoryRepository.RequireStorageAsync(database, "movements", InventoryMovementSchemas.Validator(), InventoryMovementSchemas.Key(), ct);
    }
    private async Task RequireReceiptEffectsAsync(IClientSessionHandle tx, BsonDocument receipt, CancellationToken ct,
        PurchaseOrderSourceReadCapture? capture = null)
    {
        Equal(receipt["bodyHash"], AuthorizationStore.CommandHash(B.Body(B.Input(receipt["input"]), receipt["binding"].AsString), receipt["reason"].AsString));
        Equal(await OriginOneAsync(tx, "auditEvents", F.Eq("_id", receipt["auditId"]), capture, ct) ?? throw Unavailable(), B.Audit(receipt));
        var filter = F.Eq("actorId", receipt["actorId"]) & F.Eq("operation", B.Operation) & F.Eq("key", receipt["requestKey"]);
        var receipts = await Rows("idempotencyRecords").Find(tx, filter).Limit(2).ToListAsync(ct);
        capture?.Record("idempotencyRecords", OriginFilter(filter), receipts);
        if (receipts.Count != 1 || receipts[0]["createdAt"].CompareTo(receipt["recordedAt"]) > 0 ||
            receipts[0]["bodyHash"] != receipt["bodyHash"] || receipts[0]["result"] != B.Result(receipt)) throw Unavailable();
        var outbox = await OriginOneAsync(tx, "outboxEvents", F.Eq("sourceKey", B.EffectKey(receipt)), capture, ct) ?? throw Unavailable();
        if (outbox["kind"] != B.Operation || outbox["actorId"] != receipt["actorId"] || outbox["requestKey"] != receipt["requestKey"] ||
            outbox["location"] != receipt["location"] || outbox["orderId"] != receipt["orderId"] || outbox["payload"] != B.Result(receipt) ||
            outbox["createdAt"] != receipt["recordedAt"]) throw Unavailable();
    }
    /// <summary>Authenticates the exact historical image selected by a conversion.
    /// Its strictly earlier movement prefix is read from actual membership, never
    /// the current stock chain of the command being authenticated.</summary>
    internal async Task RequireBeforeAsync(IClientSessionHandle tx, HeldReceiptOriginProof origin,
        InventoryBalanceDocument expected, DateTimeOffset selectedAt, ImmutableHashSet<long> ancestors, CancellationToken ct,
        Func<BsonDocument, HeldReceiptOriginProof, ImmutableHashSet<long>?, CancellationToken, Task>? conversionOwner = null) {
        dispatchHistory?.Require(database, tx); origin.Require(database, tx); await RequireStorageAsync(ct);
        var original = InventoryHeldReceiptWriter.After(origin.Receipt);
        if (expected.Id != original.Id || expected.PhysicalPoolId != original.PhysicalPoolId || expected.Balance.Key != original.Balance.Key ||
            expected.Balance.Revision < original.Balance.Revision) throw Unavailable();
        var current = InventoryBson.ReadBalance(await Rows("stockBalances").Find(tx, F.Eq("_id", original.Id)).SingleOrDefaultAsync(ct));
        if (current.Id != original.Id || current.PhysicalPoolId != original.PhysicalPoolId || current.Balance.Key != original.Balance.Key ||
            current.Balance.Revision < expected.Balance.Revision) throw Unavailable();
        var key = InventoryBson.Balance(original);
        var linked = F.Eq("legs.balanceId", original.Id) | F.Eq("legs.before._id", original.Id) | F.Eq("legs.after._id", original.Id) |
            F.Eq("_id", origin.Receipt["movementId"]) | F.Eq("sourceEvent", InventoryHeldReceiptWriter.Source(origin.ReceiptId).EventKey) |
            F.ElemMatch("legs", F.Eq("before.physicalPoolId", original.PhysicalPoolId) & F.Eq("before.location", key["location"]) &
                F.Eq("before.skuId", key["skuId"]) & F.Eq("before.definitionId", key["definitionId"]) & F.Eq("before.lotId", key["lotId"])) |
            F.ElemMatch("legs", F.Eq("after.physicalPoolId", original.PhysicalPoolId) & F.Eq("after.location", key["location"]) &
                F.Eq("after.skuId", key["skuId"]) & F.Eq("after.definitionId", key["definitionId"]) & F.Eq("after.lotId", key["lotId"]));
        var movements = await Rows("movements").Find(tx, linked).Limit(MaximumMovements + 1).ToListAsync(ct);
        if (movements.Count is < 1 or > MaximumMovements) throw Unavailable();
        foreach (var movement in movements) {
            if (!await Rows("movements").Find(tx, F.Eq("_id", movement["_id"]) & InventoryMovementSchemas.Validator()).AnyAsync(ct) ||
                movement["physicalPoolId"] != original.PhysicalPoolId || movement["recordedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime ||
                movement["legs"].AsBsonArray.Count(leg => leg["balanceId"] == original.Id) != 1) throw Unavailable();
        }
        var ordered = movements.OrderBy(movement => TargetLeg(movement, original.Id)["before"]["version"].AsInt64).ToArray();
        Equal(ordered[0], RootMovement(origin.Receipt));
        var state = original; var time = origin.Receipt["recordedAt"];
        var events = new HashSet<string>(StringComparer.Ordinal) { ordered[0]["sourceEvent"].AsString };
        foreach (var movement in ordered.Skip(1)) {
            var leg = TargetLeg(movement, original.Id);
            var before = InventoryBson.ReadBalance(leg["before"].AsBsonDocument);
            if (before.Balance.Revision >= expected.Balance.Revision) continue;
            if (!events.Add(movement["sourceEvent"].AsString) || before != state || movement["recordedAt"].CompareTo(time) < 0) throw Unavailable();
            await RequireSourceAsync(tx, movement, SourceKind(movement), origin, ct, ancestors, conversionOwner);
            state = Transition(state, leg); Equal(leg["after"], InventoryBson.Balance(state)); time = movement["recordedAt"];
        }
        if (state != expected || time.ToUniversalTime() > selectedAt.UtcDateTime) throw Unavailable();
        await origin.RequireAsync(database, tx, ct);
        dispatchHistory?.Require(database, tx);
    }
    private async Task RequireSourceAsync(IClientSessionHandle tx, BsonDocument movement, string kind, HeldReceiptOriginProof? origin,
        CancellationToken ct, ImmutableHashSet<long>? ancestors = null,
        Func<BsonDocument, HeldReceiptOriginProof, ImmutableHashSet<long>?, CancellationToken, Task>? conversionOwner = null)
    {
        dispatchHistory?.Require(database, tx);
        if (kind == "pack-conversion") {
            if (conversionOwner is null) await PackConversionService.RequireMovementSourceAsync(database, unit, clock, tx, movement, origin ?? throw Unavailable(), ct, ancestors, dispatchHistory);
            else {
                if (conversionOwner.Target is not PackConversionService) throw Unavailable();
                await conversionOwner(movement, origin ?? throw Unavailable(), ancestors, ct);
            }
            return;
        }
        if (kind == "stock-release") {
            if (conversionOwner is null) await StockReleaseService.RequireMovementSourceAsync(database, unit, clock, tx, movement, origin ?? throw Unavailable(), ct, dispatchHistory);
            else {
                if (conversionOwner.Target is not PackConversionService) throw Unavailable();
                await conversionOwner(movement, origin ?? throw Unavailable(), ancestors, ct);
            }
            return;
        }
        var source = movement["source"].AsBsonDocument;
        var actual = await OrderSubmissionService.ReadStockSourceEventAsync(database, tx, kind, source["documentId"].AsInt64, true, ct, dispatchHistory, clock);
        if (movement["actorId"] != actual.Audit["actorId"] || movement["actorGrantVersion"] != actual.Audit["actorGrantVersion"] ||
            movement["recordedAt"].CompareTo(actual.Time) < 0 || movement["reason"] != actual.Record["reason"] ||
            movement["correlationId"] != actual.Record["correlationId"] || movement["sourceRevision"] != 1L ||
            source["occurrence"] != movement["physicalPoolId"]) throw Unavailable();
        var allocations = kind == "submission" ? actual.Record["allocations"].AsBsonArray.Select(value => value.AsBsonDocument) :
            actual.Record[kind == "decision" ? "releases" : "stockEffects"].AsBsonArray.Select(value => value["allocation"].AsBsonDocument);
        var facts = allocations.Where(value => value["poolId"] == movement["physicalPoolId"]).Select(OrderSubmissionBson.Allocation).ToArray();
        if (facts.Length is < 1 or > 100 || facts.Length != movement["legs"].AsBsonArray.Count ||
            facts.Select(fact => fact.Change.Id).Distinct().Count() != facts.Length) throw Unavailable();
        foreach (var fact in facts) {
            var leg = TargetLeg(movement, fact.Change.Id);
            var before = InventoryBson.ReadBalance(leg["before"].AsBsonDocument);
            var after = Transition(before, leg);
            if (movement["poolRevision"] != fact.Pool.Revision || before.Id != fact.Change.Id ||
                before.PhysicalPoolId != fact.Pool.Id || before.Balance.Key != fact.Change.Key ||
                before.Balance.Revision != fact.Change.ExpectedRevision || leg["packages"] != fact.Change.Packages ||
                leg["action"] != (kind == "submission" ? "Reserve" : kind == "decision" ? "Release" : "Dispatch")) throw Unavailable();
            Equal(leg["after"], InventoryBson.Balance(after));
        }
    }
    private static string SourceKind(BsonDocument movement) => (movement["source"]["namespace"].AsString, movement["source"]["eventKind"].AsString) switch {
        ("ordering.order", "reserve") => "submission",
        ("ordering.decision", "release") => "decision",
        ("fulfillment.shipment", "dispatch") => "shipment",
        ("inventory.stock-release", "releaseHeld") => "stock-release",
        ("inventory.pack-conversion", "convertPackages") => "pack-conversion",
        _ => throw Unsupported()
    };
    private static BsonDocument TargetLeg(BsonDocument movement, long balanceId) =>
        movement["legs"].AsBsonArray.SingleOrDefault(leg => leg["balanceId"] == balanceId)?.AsBsonDocument ?? throw Unavailable();
    private static InventoryBalanceDocument Transition(InventoryBalanceDocument before, BsonDocument leg) => before with {
        Balance = leg["action"].AsString switch {
            "Reserve" => StockBalancePolicy.Reserve(before.Balance, before.Balance.Revision, leg["packages"].AsInt64),
            "Release" => StockBalancePolicy.Release(before.Balance, before.Balance.Revision, leg["packages"].AsInt64),
            "Dispatch" => StockBalancePolicy.Dispatch(before.Balance, before.Balance.Revision, leg["packages"].AsInt64),
            "ReleaseHold" => StockBalancePolicy.ReleaseHold(before.Balance, before.Balance.Revision, leg["packages"].AsInt64),
            "ConvertOut" => StockBalancePolicy.ConvertOut(before.Balance, before.Balance.Revision, leg["packages"].AsInt64),
            "ConvertIn" => StockBalancePolicy.ConvertIn(before.Balance, before.Balance.Revision, leg["packages"].AsInt64),
            _ => throw Unsupported()
        }
    };
    private static void Equal(BsonValue actual, BsonValue expected) { if (actual != expected) throw Unavailable(); }
    private static BsonDocument RootMovement(BsonDocument receipt)
    {
        var before = InventoryHeldReceiptWriter.Before(receipt); var source = InventoryHeldReceiptWriter.Source(receipt["_id"].AsInt64);
        return new() { { "_id", receipt["movementId"] }, { "schemaVersion", 1 }, { "kind", "inventory.movement" }, { "sourceEvent", source.EventKey },
            { "source", new BsonDocument { { "namespace", source.Namespace }, { "documentId", source.DocumentId }, { "eventKind", source.EventKind }, { "occurrence", source.Occurrence } } },
            { "sourceRevision", 1L }, { "physicalPoolId", receipt["poolId"] }, { "poolRevision", receipt["input"]["poolRevision"] },
            { "actorId", receipt["actorId"] }, { "actorGrantVersion", receipt["actorGrantVersion"] }, { "recordedAt", receipt["recordedAt"] },
            { "reason", receipt["reason"] }, { "correlationId", receipt["correlationId"] },
            { "legs", new BsonArray { new BsonDocument { { "balanceId", before.Id }, { "action", "ReceiveHeld" }, { "packages", receipt["input"]["packages"].ToInt64() },
                { "before", InventoryBson.Balance(before) }, { "after", InventoryBson.Balance(InventoryHeldReceiptWriter.After(receipt)) } } } } };
    }
}
