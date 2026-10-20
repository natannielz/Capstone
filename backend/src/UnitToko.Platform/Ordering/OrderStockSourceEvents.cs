using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Fulfillment;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;
using SB = UnitToko.Platform.Fulfillment.ShipmentDispatchBson;

namespace UnitToko.Platform.Ordering;

internal sealed record OrderStockSourceEvent(string Kind, long Id, BsonDocument Record, long[] PoolIds,
    BsonDocument Audit, BsonValue Time, BsonArray Effects)
{
    internal string Hash => CatalogRepository.Digest(new BsonDocument { { "kind", Kind }, { "record", Record } });
}

public sealed partial class OrderSubmissionService
{
    internal OrderSubmissionService WithDispatchHistory(IClientSessionHandle tx, ShipmentDispatchValueRecords.HistoricalRead history) {
        history.Require(database, tx);
        stockSourceLiveRead?.Require(database, clock, tx);
        if (stockSourceHistory is not null && !ReferenceEquals(stockSourceHistory, history)) throw OrderStockSourceRecords.Unavailable();
        return new(database, unit, ids, clock, admissionReader, history, stockSourceLiveRead);
    }
    internal OrderSubmissionService WithLiveDispatch(IClientSessionHandle tx, ShipmentDispatchService.LiveValueRead liveRead) {
        liveRead.Require(database, clock, tx);
        return new(database, unit, ids, clock, admissionReader, stockSourceHistory, liveRead);
    }
    // Same owning graph; no fabricated requester. The caller separately owns disclosure authority.
    internal async Task<BsonDocument> ReadStockSourceGraphAsync(IClientSessionHandle tx, long orderId, CancellationToken ct) {
        stockSourceHistory?.Require(database, tx);
        stockSourceLiveRead?.Require(database, clock, tx);
        var row = await GraphAsync(tx, null, orderId, ct, requireOwner: false);
        stockSourceHistory?.Require(database, tx); stockSourceLiveRead?.Require(database, clock, tx); return row;
    }
    internal Task RecordStockSourceTransitionAsync(IClientSessionHandle tx, string sourceKind, long sourceId, CancellationToken ct) =>
        new OrderStockSourceProducer(database, ids, clock, stockSourceHistory, stockSourceLiveRead).RecordAsync(tx, sourceKind, sourceId, ct);

    internal static async Task<OrderStockSourceEvent> ReadStockSourceEventAsync(IMongoDatabase database, IClientSessionHandle tx,
        string kind, long sourceId, bool requireReceipt, CancellationToken ct,
        ShipmentDispatchValueRecords.HistoricalRead? history = null, IClock? clock = null, ShipmentDispatchService.LiveValueRead? liveRead = null,
        Action<ShipmentDispatchFiniteSource.AuthenticatedRelease>? releaseAuthenticated = null,
        PackConversionService.FiniteHistoryRead.HeadRead? headRead = null)
    {
        history?.Require(database, tx);
        liveRead?.Require(database, clock ?? throw OrderStockSourceRecords.Unavailable(), tx);
        if (kind == "substitution") {
            var substitutionResult = await ReadSubstitutionStockSourceAsync(database, tx, sourceId, requireReceipt, clock ?? throw OrderStockSourceRecords.Unavailable(), ct);
            history?.Require(database, tx); liveRead?.Require(database, clock!, tx); return substitutionResult;
        }
        var rows = database.GetCollection<BsonDocument>(kind switch { "submission" => B.Orders, "decision" => Decisions,
            "shipment" => SB.Collection, _ => throw OrderStockSourceRecords.Unavailable() });
        // One decision reads the same lineage rows for every allocation it proves; each read is remembered for the transaction
        // attempt until it writes (TransactionReadMemo), and the schema checks are built only when they actually run.
        var source = await TransactionReadMemo.OneAsync(rows, tx, new BsonDocument("_id", sourceId), ct) ?? throw OrderStockSourceRecords.Unavailable();
        if (!await TransactionReadMemo.AnyAsync(rows, tx, "lineage-" + kind + ":" + sourceId, () => F.Eq("_id", sourceId) &
            (kind == "shipment" ? OrderLineageSchemas.DispatchValidator() : OrderLineageSchemas.OrderValidator(kind == "submission" ? B.Orders : Decisions)), ct))
            throw OrderStockSourceRecords.Unavailable();
        var orders = database.GetCollection<BsonDocument>(B.Orders);
        var order = kind == "submission" ? source : await TransactionReadMemo.OneAsync(orders, tx, new BsonDocument("_id", source["orderId"]), ct)
            ?? throw OrderStockSourceRecords.Unavailable();
        if (!await TransactionReadMemo.AnyAsync(orders, tx, "lineage-order:" + order["_id"].ToJson(), () => F.Eq("_id", order["_id"]) & OrderLineageSchemas.OrderValidator(B.Orders), ct) ||
            B.SnapshotHash(order) != order["snapshotHash"].AsString ||
            order["bodyHash"] != AuthorizationStore.CommandHash(Body(OrderDraftBson.Input(order["input"].AsBsonDocument), order["review"].AsString), order["reason"].AsString))
            throw OrderStockSourceRecords.Unavailable();
        var cutoff = kind == "submission" ? 1L : source[kind == "decision" ? "expectedRevision" : "decisionRevision"].AsInt64;
        var amendments = await SubstitutionOriginsAsync(database, tx, order, clock, ct, cutoff);
        var sourceAllocations = order["allocations"].AsBsonArray.Concat(amendments.Where(row => row["revision"].AsInt64 <= cutoff).SelectMany(row => row["allocations"].AsBsonArray)).ToArray();
        var allAllocations = order["allocations"].AsBsonArray.Concat(amendments.SelectMany(row => row["allocations"].AsBsonArray)).ToArray();
        var pools = sourceAllocations.Select(value => value["poolId"].AsInt64).Distinct().Order().ToArray();
        BsonDocument expectedAudit, result; string operation, actorField, timeField;
        if (kind == "submission") {
            expectedAudit = B.Audit(source); result = B.Result(source); operation = B.Operation; actorField = "createdBy"; timeField = "orderedAt";
        } else if (kind == "decision") {
            if (source["orderSnapshot"] != order["snapshotHash"]) throw OrderStockSourceRecords.Unavailable();
            var stage = Enum.Parse<OrderReviewStage>(source["stage"].AsString); var action = Enum.Parse<OrderReviewAction>(source["action"].AsString);
            operation = DecisionOperation(stage, action); actorField = "decidedBy"; timeField = "decidedAt";
            if (source["bodyHash"] != AuthorizationStore.CommandHash(DecisionBody(source["orderId"].AsInt64,
                source["suborderId"].IsBsonNull ? null : source["suborderId"].AsInt64, stage, source["expectedRevision"].AsInt64,
                source["review"].AsString, action), source["reason"].AsString)) throw OrderStockSourceRecords.Unavailable();
            expectedAudit = DecisionAudit(source); result = DecisionResult(source);
        } else {
            expectedAudit = SB.Audit(source); result = SB.Result(source); operation = SB.Operation; actorField = "dispatchedBy"; timeField = "recordedAt";
            if (source["bodyHash"] != AuthorizationStore.CommandHash(SB.Body(source["preparationId"].AsInt64,
                source["preparationRevision"].AsInt64, source["courierId"].AsInt64, source["review"].AsString,
                SB.Lots(source["pickedLots"].AsBsonArray)), source["reason"].AsString)) throw OrderStockSourceRecords.Unavailable();
            if (requireReceipt) _ = await new ShipmentDispatchRecords(database, clock, history, liveRead, releaseAuthenticated, headRead).ReadAsync(tx, sourceId, ct);
        }
        var audit = await database.GetCollection<BsonDocument>("auditEvents").Find(tx, F.Eq("_id", source["auditId"])).SingleOrDefaultAsync(ct);
        if (audit is null || !CatalogSchemas.SemanticEquals(audit, expectedAudit)) throw OrderStockSourceRecords.Unavailable();
        if (requireReceipt) {
            var receipt = await database.GetCollection<BsonDocument>("idempotencyRecords").Find(tx,
                F.Eq("actorId", source[actorField]) & F.Eq("operation", operation) & F.Eq("key", source["requestKey"])).SingleOrDefaultAsync(ct);
            if (receipt is null || receipt.GetValue("bodyHash", BsonNull.Value) != source["bodyHash"] ||
                !CatalogSchemas.SemanticEquals(receipt.GetValue("result", BsonNull.Value), result)) throw OrderStockSourceRecords.Unavailable();
            if (kind == "decision" && source["stage"] == "Spending") {
                if (PersonApprovalReceipt.Read(receipt, UnitToko.Modules.Identity.Permissions.OrderSpendApprove) !=
                        new UnitToko.Modules.Identity.ApprovalRequest(1, 1, RequestedBy: amendments.LastOrDefault(row => row["revision"].AsInt64 <= cutoff)?["actorId"].AsInt64 ?? order["createdBy"].AsInt64))
                    throw OrderStockSourceRecords.Unavailable();
            } else if (receipt.Contains(PersonApprovalReceipt.Field)) throw OrderStockSourceRecords.Unavailable();
        }
        var sourceKey = kind switch { "submission" => "ordering.order.submitted:", "decision" => "ordering.order.decision:", _ => "fulfillment.shipment.dispatched:" };
        var outboxKind = kind switch { "submission" => "ordering.order.submitted", "decision" => "ordering.order.decided", _ => "fulfillment.shipment.dispatched" };
        var outbox = await database.GetCollection<BsonDocument>("outboxEvents").Find(tx, F.Eq("sourceKey", sourceKey + sourceId)).SingleOrDefaultAsync(ct);
        if (outbox is null || outbox["kind"] != outboxKind || outbox["createdAt"] != source[timeField] ||
            !CatalogSchemas.SemanticEquals(outbox["payload"], result)) throw OrderStockSourceRecords.Unavailable();
        var reservations = await database.GetCollection<BsonDocument>(B.Reservations).Find(tx, F.Eq("orderId", order["_id"]))
            .Sort(new BsonDocument("_id", 1)).Limit(OrderReservationHistoryLimits.MaximumRetained + 1).ToListAsync(ct);
        OrderReservationHistoryLimits.Require(reservations);
        reservations = reservations.Where(row => allAllocations.Contains(row["allocation"])).ToList();
        if (reservations.Count != allAllocations.Length || reservations.Count < 1 ||
            reservations.Select(row => row["_id"].AsInt64).Distinct().Count() != reservations.Count) throw OrderStockSourceRecords.Unavailable();
        var effects = new BsonArray();
        foreach (var reservation in reservations) {
            var allocation = reservation["allocation"].AsBsonDocument;
            if (allAllocations.Count(value => CatalogSchemas.SemanticEquals(value, allocation)) != 1) throw OrderStockSourceRecords.Unavailable();
            if (kind == "submission" && !order["allocations"].AsBsonArray.Contains(allocation)) continue;
            BsonDocument? before; BsonDocument after;
            if (kind == "submission") {
                before = null; after = new() { { "revision", 1L }, { "remaining", allocation["packages"] }, { "released", 0L }, { "dispatched", 0L } };
            } else {
                var changes = source[kind == "decision" ? "releases" : "stockEffects"].AsBsonArray;
                var selected = changes.SingleOrDefault(value => value["reservationId"] == reservation["_id"]);
                if (selected is null) continue;
                before = selected["before"].DeepClone().AsBsonDocument; after = before.DeepClone().AsBsonDocument;
                var packages = selected["allocation"]["packages"].AsInt64;
                var supplied = B.Allocation(selected["allocation"].AsBsonDocument); var original = B.Allocation(allocation);
                if (supplied.Pool.Id != original.Pool.Id || supplied.Pool.Location != original.Pool.Location || supplied.SourceAuthority != original.SourceAuthority ||
                    supplied.SkuId != original.SkuId || supplied.LineRevision != original.LineRevision ||
                    supplied.Change.Id != original.Change.Id || supplied.Change.Key != original.Change.Key || packages <= 0 || packages > before["remaining"].AsInt64 ||
                    checked(before["remaining"].AsInt64 + before["released"].AsInt64 + before["dispatched"].AsInt64) != original.Change.Packages)
                    throw OrderStockSourceRecords.Unavailable();
                after["revision"] = checked(before["revision"].AsInt64 + 1); after["remaining"] = checked(before["remaining"].AsInt64 - packages);
                var field = kind == "decision" ? "released" : "dispatched"; after[field] = checked(before[field].AsInt64 + packages);
            }
            if (!requireReceipt && !CatalogSchemas.SemanticEquals(after, OrderStockSourceRecords.State(reservation))) throw OrderStockSourceRecords.Unavailable();
            effects.Add(new BsonDocument { { "reservationId", reservation["_id"] }, { "orderId", order["_id"] }, { "suborderId", reservation["suborderId"] },
                { "allocation", allocation.DeepClone() }, { "before", (BsonValue?)before ?? BsonNull.Value }, { "after", after } });
        }
        var canonical = source.DeepClone().AsBsonDocument; if (kind == "submission") canonical.Remove("fence");
        history?.Require(database, tx);
        liveRead?.Require(database, clock ?? throw OrderStockSourceRecords.Unavailable(), tx);
        return new(kind, sourceId, canonical, pools, expectedAudit, source[timeField], effects);
    }
}
