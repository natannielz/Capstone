using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Fulfillment;

public sealed partial class ShipmentQueryService
{
    internal static ResourceScope CurrentResource(ShipmentCourierRecords.Capture capture) =>
        ShipmentPreparationBson.Scope(capture.Head["scope"].AsBsonDocument) with { AssignedUserIds = [capture.CourierId] };
    internal Task CompleteAssignmentAsync(IClientSessionHandle tx, ShipmentDocumentSource source, CancellationToken ct) =>
        (source.Assignment ?? throw ShipmentCourierRecords.Invalid()).CompleteAsync(tx, ct);
    internal async Task<List<BsonDocument>> CurrentRowsAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        string permission, long? beforeId, long? orderId, long? shipmentId, CancellationToken ct) {
        var query = Query(actor, authority, permission);
        // A missing assignment is broken history, not a reason to fall back to the old
        // courier or silently hide a task. Check coverage in the same physical scope.
        var coverage = query with { Clauses = [.. query.Clauses.Select(clause => clause.Scope.Kind == GrantScopeKind.CourierAssignment
            ? clause with { Scope = clause.Scope with { Kind = clause.Scope.WarehouseId.HasValue ? GrantScopeKind.FulfillmentWarehouse : GrantScopeKind.FulfillmentShop } }
            : clause)] };
        var prefilter = ScopeQuery.Create(coverage);
        if (orderId.HasValue) prefilter &= F.Eq("orderId", orderId.Value);
        if (shipmentId.HasValue) prefilter &= F.Eq("_id", shipmentId.Value);
        var joined = Rows("shipments").Aggregate(tx).Match(prefilter)
            .AppendStage<BsonDocument>(new BsonDocument("$lookup", new BsonDocument { { "from", ShipmentCourierRecords.Heads }, { "localField", "_id" }, { "foreignField", "_id" }, { "as", "assignment" } }));
        if (await joined.Match(new BsonDocument("$expr", new BsonDocument("$ne", new BsonArray { new BsonDocument("$size", "$assignment"), 1 }))).Limit(1).FirstOrDefaultAsync(ct) is not null)
            throw ShipmentCourierRecords.Invalid();
        var filter = ScopeQuery.Create(query); if (beforeId.HasValue) filter &= F.Lt("_id", beforeId.Value);
        return await joined.Unwind<BsonDocument>("assignment")
            .AppendStage<BsonDocument>(new BsonDocument("$set", new BsonDocument("assignedUserIds", new BsonArray { "$assignment.courierId" })))
            .Match(filter).Sort(new BsonDocument("_id", -1)).Limit(shipmentId.HasValue ? 1 : 26).ToListAsync(ct);
    }
    internal async Task<ShipmentDocumentSource> AssignmentSourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        long shipmentId, CancellationToken ct) => await DocumentSourceAsync(actor, authority, tx, shipmentId, Permissions.ShipmentAssign, ct);
}
