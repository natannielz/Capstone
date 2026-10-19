using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Ordering;

internal static class OrderDraftBson
{
    internal const string Heads = "orderDrafts", Versions = "orderDraftVersions", Operation = "ordering.draft.save",
        DiscardOperation = "ordering.draft.discard", RestoreOperation = "ordering.draft.restore";
    internal static BsonDocument Context(OrderBuyingContext value) => new() {
        { "organizationId", value.OrganizationId.Value }, { "branchId", value.BranchId.Value },
        { "divisionId", value.DivisionId is { } division ? new BsonInt64(division.Value) : BsonNull.Value } };
    internal static OrderBuyingContext Context(BsonDocument value) => new(new(value["organizationId"].AsInt64), new(value["branchId"].AsInt64),
        value["divisionId"].IsBsonNull ? null : new(value["divisionId"].AsInt64));
    internal static BsonDocument Input(OrderDraftInput value) => new() {
        { "context", Context(value.Context) }, { "selections", new BsonArray(value.Selections.Select(line => new BsonDocument {
            { "skuId", line.SkuId.Value }, { "definitionId", line.DefinitionId.Value }, { "definitionVersion", line.DefinitionVersion },
            { "priceId", line.PriceId.Value }, { "priceVersion", line.PriceVersion }, { "packages", line.Packages } })) },
        { "delivery", value.Delivery is { } delivery ? new BsonDocument {
            { "recipientName", delivery.RecipientName }, { "phone", delivery.Phone }, { "address", delivery.Address }, { "city", delivery.City },
            { "postalCode", delivery.PostalCode is null ? BsonNull.Value : new BsonString(delivery.PostalCode) },
            { "instructions", delivery.Instructions is null ? BsonNull.Value : new BsonString(delivery.Instructions) } } : BsonNull.Value },
        { "purchaseReference", value.PurchaseReference is null ? BsonNull.Value : new BsonString(value.PurchaseReference) } };
    internal static OrderDraftInput Input(BsonDocument value)
    {
        try {
            var delivery = value["delivery"].IsBsonNull ? null : value["delivery"].AsBsonDocument;
            var input = OrderDraftPolicy.Copy(new(Context(value["context"].AsBsonDocument),
                value["selections"].AsBsonArray.Select(item => new OrderPackageSelection(new(item["skuId"].AsInt64),
                    new(item["definitionId"].AsInt64), item["definitionVersion"].AsInt64, new(item["priceId"].AsInt64), item["priceVersion"].AsInt64, item["packages"].AsInt32)).ToImmutableArray(),
                delivery is null ? null : new(delivery["recipientName"].AsString, delivery["phone"].AsString, delivery["address"].AsString, delivery["city"].AsString,
                    NullText(delivery["postalCode"]), NullText(delivery["instructions"])), NullText(value["purchaseReference"])));
            if (!CatalogSchemas.SemanticEquals(Input(input), value)) throw Invalid(); return input;
        } catch (Exception error) when (error is ArgumentException or InvalidCastException or KeyNotFoundException or OverflowException) { throw Invalid(); }
    }
    private static string? NullText(BsonValue value) => value.IsBsonNull ? null : value.AsString;
    internal static DateTimeOffset Time(BsonValue value) {
        if (!value.IsBsonDateTime || value.AsBsonDateTime.MillisecondsSinceEpoch is <= -62135596800000L or > 253402300799999L) throw Invalid();
        return DateTimeOffset.FromUnixTimeMilliseconds(value.AsBsonDateTime.MillisecondsSinceEpoch);
    }
    internal static BsonDocument Result(BsonDocument version) => new() { { "draftId", version["draftId"] },
        { "versionId", version["_id"] }, { "revision", version["revision"] }, { "recordedAt", version["recordedAt"] } };
    internal static BsonDocument Audit(BsonDocument version) => new() { { "_id", version["auditId"] }, { "schemaVersion", 1 },
        { "kind", "ordering.draft.saved" }, { "actorId", version["createdBy"] }, { "actorGrantVersion", version["actorGrantVersion"] },
        { "operation", Operation }, { "requestKey", version["requestKey"] }, { "bodyHash", version["bodyHash"] },
        { "reason", version["reason"] }, { "correlationId", version["correlationId"] }, { "createdAt", version["recordedAt"] },
        { "context", version["input"]["context"].DeepClone() }, { "result", Result(version) } };
    internal static AuthorizationDeniedException Invalid(string code = "order_draft_invalid") => new(code);
}
