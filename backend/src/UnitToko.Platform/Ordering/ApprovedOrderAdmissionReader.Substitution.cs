using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Ordering.OrderSubmissionBson;
using S = UnitToko.Platform.Ordering.OrderSubstitutionBson;

namespace UnitToko.Platform.Ordering;

internal sealed partial class ApprovedOrderAdmissionReader
{
    internal Task<OrderAdmission> ReadForSubstitutionAsync(IClientSessionHandle tx, ActorContext actor, OrderDraftInput replacementInput,
        OrderCatalogQuote revisedCatalog, OrderCatalogQuote replacement, OrderSubstitutionReleaseCapture release, CancellationToken ct) =>
        ReadCoreAsync(tx, actor, replacementInput, revisedCatalog, ct, complete: false, replacement: replacement, release: release);

    internal void BindSubstitution(IClientSessionHandle tx, ActorContext actor, OrderAdmission admission, long decisionId) {
        if (!frames.TryGetValue(tx, out var frame) || !frame.ForSubstitution || decisionId <= 0 || frame.SubstitutionId is not null) throw Unavailable();
        Require(frame, actor, tx, admission); frame.SubstitutionId = decisionId;
    }
    internal void RequireSubstitutionCurrent(IClientSessionHandle tx, ActorContext actor, OrderAdmission admission) {
        if (!frames.TryGetValue(tx, out var frame) || !frame.ForSubstitution) throw Unavailable();
        Require(frame, actor, tx, admission);
    }
    private async Task RequireSubstitutionReleaseAsync(Frame frame, CancellationToken ct) {
        if (frame.SubstitutionId is not { } id || frame.Release is not { } release) throw Unavailable();
        release.Require(database, clock, frame.Actor, frame.Tx);
        var row = await Rows(S.Decisions).Find(frame.Tx, F.Eq("_id", id) & OrderSubstitutionSchemas.Validator(S.Decisions)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
        if (row["recordHash"] != S.Hash(row) || row["decision"] != "Accept" || row["actorId"] != frame.Actor.UserId ||
            row["actorGrantVersion"] != frame.Actor.GrantVersion || row["releases"] != release.Rows || row["catalog"] != B.Quote(frame.Quote) ||
            row["terms"] != B.Terms(frame.Admission.Terms)) throw Unavailable();
        foreach (var fact in row["releases"].AsBsonArray) {
            var reservation = await Rows(B.Reservations).Find(frame.Tx, F.Eq("_id", fact["reservationId"]) &
                OrderLineageSchemas.OrderValidator(B.Reservations)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
            if (reservation["orderId"] != row["orderId"] || reservation["suborderId"] != row["suborderId"]) throw Unavailable();
            OrderSubstitutionReservationFacts.RequireRelease(reservation, fact.AsBsonDocument, true);
        }
        foreach (var group in row["releases"].AsBsonArray.GroupBy(value => value["allocation"]["poolId"].AsInt64)) {
            var source = new InventorySource("ordering.substitution", id, "release", group.Key);
            var movement = await Rows("movements").Find(frame.Tx, F.Eq("sourceEvent", source.EventKey) & InventoryMovementSchemas.Validator()).SingleOrDefaultAsync(ct) ?? throw Unavailable();
            await OrderSubmissionService.RequireSubstitutionMovementAsync(row, movement, "release", group.Select(value => value["allocation"].AsBsonDocument).ToArray(), ct);
        }
    }
    private async Task RequireSubstitutionReserveAsync(Frame frame, Lot lot, InventoryBalanceDocument after, CancellationToken ct) {
        if (frame.SubstitutionId is not { } id) throw Unavailable("order_substitution_owner_unavailable");
        var row = await Rows(S.Decisions).Find(frame.Tx, F.Eq("_id", id) & OrderSubstitutionSchemas.Validator(S.Decisions)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
        if (row["recordHash"] != S.Hash(row) || row["actorId"] != frame.Actor.UserId || row["actorGrantVersion"] != frame.Actor.GrantVersion ||
            row["decision"] != "Accept" || row["catalog"] != B.Quote(frame.Quote) || row["terms"] != B.Terms(frame.Admission.Terms) ||
            row["allocations"] != new BsonArray(frame.Admission.Stock.Select(B.Allocation)) || row["recordedAt"].ToUniversalTime() < frame.At.UtcDateTime ||
            S.Input(row["input"].AsBsonDocument).Selection.Replacement != frame.Input.Selections.Single()) throw Unavailable();
        var order = await Rows(B.Orders).Find(frame.Tx, F.Eq("_id", row["orderId"]) & OrderLineageSchemas.OrderValidator(B.Orders)).SingleOrDefaultAsync(ct) ?? throw Unavailable();
        if (row["orderSnapshot"] != order["snapshotHash"] || B.SnapshotHash(order) != order["snapshotHash"].AsString ||
            OrderDraftBson.Context(order["input"]["context"].AsBsonDocument) != frame.Input.Context ||
            OrderDraftBson.Input(order["input"].AsBsonDocument).Delivery != frame.Input.Delivery) throw Unavailable();
        var body = S.DecisionBody(row["proposalId"].AsInt64, row["expectedRevision"].AsInt64, OrderSubstitutionDecision.Accept, row["review"].AsString);
        if (row["bodyHash"] != AuthorizationStore.CommandHash(body, row["reason"].AsString)) throw Unavailable();
        var receipt = await Rows("idempotencyRecords").Find(frame.Tx, F.Eq("actorId", frame.Actor.UserId) & F.Eq("operation", S.AcceptOperation) &
            F.Eq("key", row["requestKey"])).SingleOrDefaultAsync(ct) ?? throw Unavailable();
        if (receipt["bodyHash"] != row["bodyHash"] || receipt["createdAt"].CompareTo(row["recordedAt"]) > 0 ||
            receipt["result"] != new BsonDocument() && receipt["result"] != S.DecisionResult(row)) throw Unavailable();
        if (receipt["result"] != new BsonDocument()) _ = await new OrderSubstitutionRecords(database, clock).ReadAsync(frame.Tx, S.Decisions, id, order, ct);
        var reservations = await Rows(B.Reservations).Find(frame.Tx, F.In("_id", row["reservationIds"].AsBsonArray)).Limit(101).ToListAsync(ct);
        if (reservations.Count != frame.Lots.Length || reservations.Count != row["reservationIds"].AsBsonArray.Count ||
            reservations.Any(value => value["orderId"] != order["_id"] || value["suborderId"] != row["suborderId"] || value["revision"] != 1L ||
                value["remaining"] != value["allocation"]["packages"] || value["released"] != 0L || value["dispatched"] != 0L ||
                row["allocations"].AsBsonArray.Count(allocation => allocation == value["allocation"]) != 1)) throw Unavailable();
        var source = new InventorySource("ordering.substitution", id, "reserve", lot.Allocation.Pool.Id);
        var movements = await Rows("movements").Find(frame.Tx, F.Eq("sourceEvent", source.EventKey) |
            (F.Eq("source.namespace", source.Namespace) & F.Eq("source.documentId", id) & F.Eq("source.eventKind", "reserve") & F.Eq("source.occurrence", source.Occurrence)) |
            F.ElemMatch("legs", F.Eq("balanceId", lot.Allocation.Change.Id) & F.Eq("before.version", lot.Physical.Before.Balance.Revision) & F.Eq("after.version", after.Balance.Revision)))
            .Limit(2).ToListAsync(ct);
        if (movements.Count != 1) throw Unavailable(); var movement = movements[0];
        if (!await Rows("movements").Find(frame.Tx, F.Eq("_id", movement["_id"]) & InventoryMovementSchemas.Validator()).AnyAsync(ct) ||
            movement["sourceEvent"] != source.EventKey || movement["sourceRevision"] != row["revision"] || movement["physicalPoolId"] != source.Occurrence ||
            movement["poolRevision"] != lot.Allocation.Pool.Revision || movement["actorId"] != frame.Actor.UserId || movement["actorGrantVersion"] != frame.Actor.GrantVersion ||
            movement["reason"] != row["reason"] || movement["correlationId"] != row["correlationId"] || movement["recordedAt"].CompareTo(row["recordedAt"]) < 0 ||
            movement["source"] != new BsonDocument { { "namespace", source.Namespace }, { "documentId", id }, { "eventKind", "reserve" }, { "occurrence", source.Occurrence } }) throw Unavailable();
        var expected = frame.Lots.Where(value => value.Allocation.Pool.Id == source.Occurrence).Select(value => {
            var before = frame.Release?.BeforeReserve(value.Physical.Before) ?? value.Physical.Before;
            return new BsonDocument { { "balanceId", before.Id }, { "action", "Reserve" }, { "packages", value.Allocation.Change.Packages },
                { "before", InventoryBson.Balance(before) }, { "after", InventoryBson.Balance(before with {
                    Balance = StockBalancePolicy.Reserve(before.Balance, value.Allocation.Change.ExpectedRevision, value.Allocation.Change.Packages) }) } };
        }).ToArray();
        if (movement["legs"].AsBsonArray.Count != expected.Length || expected.Any(leg => movement["legs"].AsBsonArray.Count(value => value == leg) != 1)) throw Unavailable();
    }
}
