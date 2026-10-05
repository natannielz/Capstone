using MongoDB.Bson;
using MongoDB.Driver;
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
    // Same owning graph; no fabricated requester. The caller separately owns disclosure authority.
    internal Task<BsonDocument> ReadStockSourceGraphAsync(IClientSessionHandle tx, long orderId, CancellationToken ct) =>
        GraphAsync(tx, null, orderId, ct, requireOwner: false);
    internal Task RecordStockSourceTransitionAsync(IClientSessionHandle tx, string sourceKind, long sourceId, CancellationToken ct) =>
        new OrderStockSourceProducer(database, ids, clock).RecordAsync(tx, sourceKind, sourceId, ct);

    internal static async Task<OrderStockSourceEvent> ReadStockSourceEventAsync(IMongoDatabase database, IClientSessionHandle tx,
        string kind, long sourceId, bool requireReceipt, CancellationToken ct)
    {
        var rows = database.GetCollection<BsonDocument>(kind switch { "submission" => B.Orders, "decision" => Decisions,
            "shipment" => SB.Collection, _ => throw OrderStockSourceRecords.Unavailable() });
        var source = await rows.Find(tx, new BsonDocument("_id", sourceId)).SingleOrDefaultAsync(ct) ?? throw OrderStockSourceRecords.Unavailable();
        var validator = kind == "shipment" ? ShipmentDispatchSchemas.Validator() : OrderSubmissionSchemas.Validator(kind == "submission" ? B.Orders : Decisions);
        if (!await rows.Find(tx, F.Eq("_id", sourceId) & validator).AnyAsync(ct)) throw OrderStockSourceRecords.Unavailable();
        var order = kind == "submission" ? source : await database.GetCollection<BsonDocument>(B.Orders)
            .Find(tx, F.Eq("_id", source["orderId"])).SingleOrDefaultAsync(ct) ?? throw OrderStockSourceRecords.Unavailable();
        if (!await database.GetCollection<BsonDocument>(B.Orders).Find(tx, F.Eq("_id", order["_id"]) & OrderSubmissionSchemas.Validator(B.Orders)).AnyAsync(ct) ||
            B.SnapshotHash(order) != order["snapshotHash"].AsString ||
            order["bodyHash"] != AuthorizationStore.CommandHash(Body(OrderDraftBson.Input(order["input"].AsBsonDocument), order["review"].AsString), order["reason"].AsString))
            throw OrderStockSourceRecords.Unavailable();
        var pools = order["allocations"].AsBsonArray.Select(value => value["poolId"].AsInt64).Distinct().Order().ToArray();
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
            if (requireReceipt) _ = await new ShipmentDispatchRecords(database).ReadAsync(tx, sourceId, ct);
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
                    new UnitToko.Modules.Identity.ApprovalRequest(1, 1, RequestedBy: order["createdBy"].AsInt64))
                    throw OrderStockSourceRecords.Unavailable();
            } else if (receipt.Contains(PersonApprovalReceipt.Field)) throw OrderStockSourceRecords.Unavailable();
        }
        var sourceKey = kind switch { "submission" => "ordering.order.submitted:", "decision" => "ordering.order.decision:", _ => "fulfillment.shipment.dispatched:" };
        var outboxKind = kind switch { "submission" => "ordering.order.submitted", "decision" => "ordering.order.decided", _ => "fulfillment.shipment.dispatched" };
        var outbox = await database.GetCollection<BsonDocument>("outboxEvents").Find(tx, F.Eq("sourceKey", sourceKey + sourceId)).SingleOrDefaultAsync(ct);
        if (outbox is null || outbox["kind"] != outboxKind || outbox["createdAt"] != source[timeField] ||
            !CatalogSchemas.SemanticEquals(outbox["payload"], result)) throw OrderStockSourceRecords.Unavailable();
        var reservations = await database.GetCollection<BsonDocument>(B.Reservations).Find(tx, F.Eq("orderId", order["_id"]))
            .Sort(new BsonDocument("_id", 1)).Limit(101).ToListAsync(ct);
        if (reservations.Count != order["allocations"].AsBsonArray.Count || reservations.Count is < 1 or > 100 ||
            reservations.Select(row => row["_id"].AsInt64).Distinct().Count() != reservations.Count) throw OrderStockSourceRecords.Unavailable();
        var effects = new BsonArray();
        foreach (var reservation in reservations) {
            var allocation = reservation["allocation"].AsBsonDocument;
            if (order["allocations"].AsBsonArray.Count(value => CatalogSchemas.SemanticEquals(value, allocation)) != 1) throw OrderStockSourceRecords.Unavailable();
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
        return new(kind, sourceId, canonical, pools, expectedAudit, source[timeField], effects);
    }
}
