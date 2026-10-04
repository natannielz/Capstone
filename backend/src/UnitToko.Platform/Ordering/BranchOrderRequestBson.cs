using System.Collections.Immutable;
using MongoDB.Bson;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;

namespace UnitToko.Platform.Ordering;

internal static class BranchOrderRequestBson
{
    internal const string Requests = "branchOrderRequests", Decisions = "branchOrderRequestDecisions";
    internal const string SubmitOperation = "ordering.branchrequest.submit", DecideOperation = "ordering.branchrequest.decide";
    internal static BsonDocument Input(BranchOrderRequestInput value) => new() {
        { "context", OrderDraftBson.Context(value.Context) },
        { "lines", new BsonArray(value.Lines.Select(line => new BsonDocument { { "skuId", line.SkuId.Value }, { "definitionId", line.DefinitionId.Value },
            { "definitionVersion", line.DefinitionVersion }, { "packages", line.Packages } })) },
        { "note", value.Note is null ? BsonNull.Value : new BsonString(value.Note) } };
    internal static BranchOrderRequestInput Input(BsonValue value) => BranchOrderRequestRules.Copy(new(
        OrderDraftBson.Context(value["context"].AsBsonDocument),
        value["lines"].AsBsonArray.Select(line => new BranchOrderRequestLine(new(line["skuId"].AsInt64), new(line["definitionId"].AsInt64),
            line["definitionVersion"].AsInt64, line["packages"].AsInt32)).ToImmutableArray(),
        value["note"].IsBsonNull ? null : value["note"].AsString));
    internal static BsonDocument Owner(GrantScope value) => CatalogRepository.Owner(value);
    internal static GrantScope Owner(BsonValue value) => CatalogRepository.Owner(value.AsBsonDocument);
    internal static BsonDocument Line(OrderCatalogFact fact, BranchOrderRequestLine selected) {
        var definition = fact.Definition;
        return new() { { "skuId", selected.SkuId.Value }, { "definitionId", definition.Id.Value }, { "definitionVersion", definition.Version },
            { "packages", selected.Packages }, { "skuCode", fact.Sku.Code }, { "familyName", fact.Family.Name },
            { "packageLabel", definition.PackageLabel }, { "packageKind", definition.PackageKind }, { "baseUnitCode", definition.BaseUnitCode },
            { "baseUnitsPerPackage", definition.BaseUnitsPerPackage }, { "owner", Owner(fact.Family.AuthoringOwner) } };
    }
    internal static BsonDocument Scope(BsonDocument row) => new() { { "buyingOrganizationId", row["buyingOrganizationId"] },
        { "branchId", row["branchId"] }, { "divisionId", row["divisionId"] }, { "fulfillmentOrganizationId", row["fulfillmentOrganizationId"] },
        { "shopId", row["shopId"] }, { "warehouseId", row["warehouseId"] }, { "channelId", row["channelId"] }, { "createdBy", row["createdBy"] } };
    internal static string Hash(BsonDocument row) {
        var value = row.DeepClone().AsBsonDocument; value.Remove("fence"); value.Remove("state"); value.Remove("decisionId"); value.Remove("snapshotHash"); return CatalogRepository.Digest(value);
    }
    internal static BsonDocument SubmitResult(BsonDocument row) => new() { { "requestId", row["_id"] }, { "number", row["number"] },
        { "version", 1L }, { "state", "pending" }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument DecideResult(BsonDocument row, BsonDocument request) => new() { { "requestId", request["_id"] },
        { "decisionId", row["_id"] }, { "number", request["number"] }, { "version", 2L },
        { "state", row["action"] == "accept" ? "accepted" : "rejected" }, { "recordedAt", row["recordedAt"] } };
    internal static BsonDocument SubmitAudit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 },
        { "kind", "ordering.branchrequest.submitted" }, { "actorId", row["createdBy"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "operation", SubmitOperation }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "snapshotHash", row["snapshotHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] },
        { "createdAt", row["recordedAt"] }, { "result", SubmitResult(row) } };
    internal static BsonDocument DecideAudit(BsonDocument row, BsonDocument request) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 },
        { "kind", "ordering.branchrequest.decided" }, { "actorId", row["decidedBy"] }, { "actorGrantVersion", row["actorGrantVersion"] },
        { "operation", DecideOperation }, { "requestKey", row["requestKey"] }, { "bodyHash", row["bodyHash"] },
        { "snapshotHash", row["requestHash"] }, { "reason", row["reason"] }, { "correlationId", row["correlationId"] },
        { "createdAt", row["recordedAt"] }, { "result", DecideResult(row, request) } };
    internal static BsonDocument Outbox(string key, string kind, BsonDocument result, DateTimeOffset at) => new() {
        { "_id", ObjectId.GenerateNewId() }, { "schemaVersion", 1 }, { "sourceKey", key }, { "kind", kind },
        { "payload", result }, { "createdAt", at.UtcDateTime }, { "dueAt", at.UtcDateTime }, { "status", "pending" }, { "attempts", 0 } };
}
