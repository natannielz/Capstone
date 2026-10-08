using MongoDB.Bson;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Ordering;

internal static class OrderViewBson
{
    internal const string Heads = "orderSavedViews", Versions = "orderSavedViewVersions", Operation = "ordering.order-view.change";
    internal static BsonDocument Filter(OrderHistoryFilter value) => new() {
        { "context", value.Context is null ? BsonNull.Value : OrderDraftBson.Context(value.Context) }, { "sort", value.Sort } };
    internal static OrderHistoryFilter Filter(BsonValue value) {
        var result = new OrderHistoryFilter(value["context"].IsBsonNull ? null : OrderDraftBson.Context(value["context"].AsBsonDocument), value["sort"].AsString);
        result.Validate(); return result;
    }
    internal static object Body(string action, long? viewId, long revision, string? name, OrderHistoryFilter? filter) => new { action, viewId, revision, name, filter };
    internal static object Body(BsonDocument version) => Body(version["action"].AsString, version["revision"] == 1L ? null : version["viewId"].AsInt64,
        version["revision"].AsInt64 - 1, version["action"] == "remove" ? null : version["name"].AsString, version["action"] == "create" ? Filter(version["filter"]) : null);
    internal static BsonDocument Result(BsonDocument version) => new() { { "viewId", version["viewId"] }, { "versionId", version["_id"] },
        { "revision", version["revision"] }, { "action", version["action"] }, { "recordedAt", version["recordedAt"] } };
    internal static BsonDocument Audit(BsonDocument version) => new() { { "_id", version["auditId"] }, { "schemaVersion", 1 },
        { "kind", "ordering.order-view.changed" }, { "actorId", version["createdBy"] }, { "actorGrantVersion", version["actorGrantVersion"] },
        { "operation", Operation }, { "requestKey", version["requestKey"] }, { "bodyHash", version["bodyHash"] },
        { "reason", version["reason"] }, { "correlationId", version["correlationId"] }, { "createdAt", version["recordedAt"] },
        { "filter", version["filter"].DeepClone() }, { "result", Result(version) } };
    internal static AuthorizationDeniedException Invalid() => new("order_view_unavailable");
}
