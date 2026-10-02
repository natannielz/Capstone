using System.Globalization;
using MongoDB.Bson;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Organizations;

namespace UnitToko.Platform.Procurement;

internal static class ProcurementRequestBson
{
    internal const string Requests = "procurementRequests", Decisions = "procurementRequestDecisions", Prefix = "procurement.request.";
    internal static readonly string[] SourceNames = ["organization", "shop", "warehouse", "channel"];
    internal static BsonDocument Location(ProcurementLocation value) { ProcurementRequestRules.Location(value); return new() {
        { "organizationId", value.OrganizationId }, { "shopId", value.ShopId }, { "warehouseId", value.WarehouseId }, { "channelId", value.ChannelId } }; }
    internal static ProcurementLocation Location(BsonValue value) => ProcurementRequestRules.Location(new(value["organizationId"].AsInt64,
        value["shopId"].AsInt64, value["warehouseId"].AsInt64, value["channelId"].AsInt64));
    internal static string Route(ProcurementRoute value) => value switch { ProcurementRoute.OmiRegular => "omi_regular", ProcurementRoute.Ddo => "ddo", ProcurementRoute.Smart => "smart", ProcurementRoute.PasarKering => "pasar_kering", _ => throw Broken() };
    internal static ProcurementRoute Route(BsonValue value) => value.AsString switch { "omi_regular" => ProcurementRoute.OmiRegular, "ddo" => ProcurementRoute.Ddo, "smart" => ProcurementRoute.Smart, "pasar_kering" => ProcurementRoute.PasarKering, _ => throw Broken() };
    internal static string Kind(ProcurementRequestKind value) => value switch { ProcurementRequestKind.StockNeed => "stock_need", ProcurementRequestKind.IncomingNotice => "incoming_notice", ProcurementRequestKind.PurchaseRequest => "purchase_request", _ => throw Broken() };
    internal static ProcurementRequestKind Kind(BsonValue value) => value.AsString switch { "stock_need" => ProcurementRequestKind.StockNeed, "incoming_notice" => ProcurementRequestKind.IncomingNotice, "purchase_request" => ProcurementRequestKind.PurchaseRequest, _ => throw Broken() };
    internal static string Decision(ProcurementRequestDecision value) => value switch { ProcurementRequestDecision.Approve => "approve", ProcurementRequestDecision.Reject => "reject", _ => throw Broken() };
    internal static ProcurementRequestDecision Decision(BsonValue value) => value.AsString switch { "approve" => ProcurementRequestDecision.Approve, "reject" => ProcurementRequestDecision.Reject, _ => throw Broken() };
    internal static BsonDocument Input(ProcurementRequestInput value) { value = ProcurementRequestRules.Copy(value); return new() {
        { "location", Location(value.Location) }, { "route", Route(value.Route) }, { "kind", Kind(value.Kind) }, { "businessDate", value.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
        { "externalReference", value.ExternalReference is null ? BsonNull.Value : value.ExternalReference }, { "reason", value.Reason },
        { "lines", new BsonArray(value.Lines.Select(line => new BsonDocument { { "skuId", line.SkuId }, { "expectedSkuRevision", line.ExpectedSkuRevision },
            { "definitionId", line.DefinitionId }, { "definitionVersion", line.DefinitionVersion }, { "packages", line.Packages }, { "specification", line.Specification is null ? BsonNull.Value : line.Specification } })) } }; }
    internal static ProcurementRequestInput Input(BsonValue value) => ProcurementRequestRules.Copy(new(Location(value["location"]), Route(value["route"]), Kind(value["kind"]),
        DateOnly.ParseExact(value["businessDate"].AsString, "yyyy-MM-dd", CultureInfo.InvariantCulture), value["externalReference"].IsBsonNull ? null : value["externalReference"].AsString, value["reason"].AsString,
        [.. value["lines"].AsBsonArray.Select(line => new ProcurementRequestLineInput(line["skuId"].AsInt64, line["expectedSkuRevision"].AsInt64,
            line["definitionId"].AsInt64, line["definitionVersion"].AsInt64, line["packages"].AsInt32, line["specification"].IsBsonNull ? null : line["specification"].AsString))]));
    internal static object SubmitBody(ProcurementRequestInput input, string binding) => new { input = ProcurementRequestRules.Copy(input), binding };
    internal static object DecideBody(long requestId, long expectedVersion, ProcurementRequestDecision decision, string binding) => new { requestId, expectedVersion, decision = Decision(decision), binding };
    internal static string Hash(BsonDocument row) => CatalogRepository.Digest(new BsonDocument(row.Where(field => field.Name is not ("snapshotHash" or "fence"))));
    internal static string Binding(long actor, long grantVersion, ProcurementRequestInput input, BsonDocument sources, BsonArray catalog) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grantVersion }, { "input", Input(input) }, { "sources", sources }, { "catalog", catalog } });
    internal static string DecisionBinding(long actor, long grantVersion, BsonDocument request, ProcurementRequestDecision decision, BsonDocument sources, BsonArray catalog) => Hash(new() {
        { "actorId", actor }, { "actorGrantVersion", grantVersion }, { "requestHash", request["snapshotHash"] }, { "decision", Decision(decision) }, { "sources", sources }, { "catalog", catalog } });
    internal static BsonDocument Person(PersonEvidence value) => OrganizationAddressBson.Person(value);
    internal static PersonEvidence Person(BsonValue row) => OrganizationAddressBson.Person(row);
    internal static string Permission(string operation) => operation switch { Prefix + "submit" => Permissions.ProcurementPrepare,
        Prefix + "approve" or Prefix + "reject" => Permissions.ProcurementApprove, _ => throw ProcurementRequestRules.Invalid() };
    internal static void Metadata(BsonDocument row, string operation, long auditId, ActorContext actor, AuthorizationCommandMetadata metadata, object body, DateTimeOffset now) {
        row.Add("operation", operation); row.Add("actorId", actor.UserId); row.Add("actorGrantVersion", actor.GrantVersion); row.Add("recordedAt", now.UtcDateTime);
        row.Add("requestKey", metadata.IdempotencyKey); row.Add("bodyHash", AuthorizationStore.CommandHash(body, metadata.Reason)); row.Add("reason", metadata.Reason);
        row.Add("correlationId", metadata.CorrelationId); row.Add("auditId", auditId); row.Add("snapshotHash", Hash(row));
    }
    internal static BsonDocument Result(BsonDocument row) => new() { { "requestId", row["requestId"] }, { "requestVersion", 1L },
        { "decisionId", row.Contains("decision") ? row["_id"] : BsonNull.Value }, { "status", !row.Contains("decision") ? "submitted" : row["decision"] == "approve" ? "approved" : "rejected" } };
    internal static BsonDocument Audit(BsonDocument row) => new() { { "_id", row["auditId"] }, { "schemaVersion", 1 }, { "kind", row["operation"] },
        { "actorId", row["actorId"] }, { "actorGrantVersion", row["actorGrantVersion"] }, { "bodyHash", row["bodyHash"] }, { "snapshotHash", row["snapshotHash"] },
        { "requestKey", row["requestKey"] }, { "location", row["location"] }, { "correlationId", row["correlationId"] }, { "createdAt", row["recordedAt"] }, { "result", Result(row) } };
    internal static string SourceKey(BsonDocument row) => Prefix + "command:" + row["auditId"].AsInt64;
    internal static BsonDocument Label(BsonValue source) => new() { { "id", source["profile"]["id"] }, { "code", source["profile"]["code"] },
        { "name", source["profile"]["name"] }, { "scopeVersion", source["target"]["version"] }, { "profileVersion", source["profile"]["version"] } };
    internal static BsonDocument SourceView(BsonDocument sources) => new(SourceNames.Select(name => new BsonElement(name, Label(sources[name]))));
    internal static BsonDocument Package(BsonValue source) => new() { { "skuId", source["sku"]["_id"] }, { "skuRevision", source["sku"]["revision"] },
        { "definitionId", source["definition"]["_id"] }, { "definitionVersion", source["definition"]["version"] }, { "skuCode", source["sku"]["sku"] },
        { "familyName", source["family"]["name"] }, { "packageLabel", source["definition"]["packageLabel"] }, { "packageKind", source["definition"]["packageKind"] },
        { "baseUnitCode", source["definition"]["baseUnitCode"] }, { "baseUnitsPerPackage", source["definition"]["baseUnitsPerPackage"] } };
    internal static BsonArray Lines(ProcurementRequestInput input, BsonArray catalog) => new(input.Lines.Select((line, index) => {
        var result = Package(catalog[index]); result.Add("packages", line.Packages); result.Add("specification", line.Specification is null ? BsonNull.Value : line.Specification); return result;
    }));
    internal static void RequireBinding(string binding) { if (binding is not { Length: 64 } || binding.Any(c => !char.IsAsciiHexDigitLower(c))) throw ProcurementRequestRules.Invalid(); }
    internal static PersistenceConflictException Broken() => new("procurement_request_integrity_unavailable", "The complete procurement request history is unavailable");
    internal static PersistenceConflictException Changed() => new("procurement_request_changed", "Reload the current location, catalog and request before continuing");
}
