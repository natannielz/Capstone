using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Procurement;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;

namespace UnitToko.Api.Procurement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementLocationDto([property: JsonRequired] LongId OrganizationId,
    [property: JsonRequired] LongId ShopId, [property: JsonRequired] LongId WarehouseId, [property: JsonRequired] LongId ChannelId) {
    internal ProcurementLocation Domain() => ProcurementRequestRules.Location(new(OrganizationId.Value, ShopId.Value, WarehouseId.Value, ChannelId.Value));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementRequestLineInputDto([property: JsonRequired] LongId SkuId, [property: JsonRequired] string ExpectedSkuRevision,
    [property: JsonRequired] LongId DefinitionId, [property: JsonRequired] string DefinitionVersion,
    [property: JsonRequired] int Packages, [property: JsonRequired] string? Specification);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementRequestInputDto([property: JsonRequired] ProcurementLocationDto Location,
    [property: JsonRequired] string Route, [property: JsonRequired] string Kind, [property: JsonRequired] string BusinessDate,
    [property: JsonRequired] string? ExternalReference, [property: JsonRequired] string Reason,
    [property: JsonRequired] ProcurementRequestLineInputDto[] Lines) {
    internal ProcurementRequestInput Domain() {
        if (Location is null || Lines is not { Length: > 0 and <= 100 } || Lines.Any(line => line is null) ||
            !DateOnly.TryParseExact(BusinessDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw ProcurementRequestRules.Invalid();
        var route = Route switch { "omi_regular" => ProcurementRoute.OmiRegular, "ddo" => ProcurementRoute.Ddo,
            "smart" => ProcurementRoute.Smart, "pasar_kering" => ProcurementRoute.PasarKering, _ => throw ProcurementRequestRules.Invalid() };
        var kind = Kind switch { "stock_need" => ProcurementRequestKind.StockNeed, "incoming_notice" => ProcurementRequestKind.IncomingNotice,
            "purchase_request" => ProcurementRequestKind.PurchaseRequest, _ => throw ProcurementRequestRules.Invalid() };
        return ProcurementRequestRules.Copy(new(Location.Domain(), route, kind, date, ExternalReference, Reason,
            [.. Lines.Select(line => new ProcurementRequestLineInput(line.SkuId.Value, Version(line.ExpectedSkuRevision),
                line.DefinitionId.Value, Version(line.DefinitionVersion), line.Packages, line.Specification))]));
    }
    private static long Version(string value) {
        if (value is not { Length: > 0 and <= 19 } || value[0] == '0' || value.Any(c => c is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version <= 0) throw ProcurementRequestRules.Invalid();
        return version;
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementPackageQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementLocationDto Location,
    [property: JsonRequired] int Offset, [property: JsonRequired] int Limit, string? Search = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementRequestPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementRequestInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementRequestSubmitDto([property: JsonRequired] ProcurementRequestInputDto Input, [property: JsonRequired] string Binding);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementRequestListInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementDecisionPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Decision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementDecisionInputDto([property: JsonRequired] string Decision, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementRecoveryInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
public sealed record ProcurementLabelDto(LongId Id, string Code, string Name, string ScopeVersion, string ProfileVersion);
public sealed record ProcurementSourceDto(ProcurementLabelDto Organization, ProcurementLabelDto Shop, ProcurementLabelDto Warehouse, ProcurementLabelDto Channel);
public sealed record ProcurementTargetDto(ProcurementLocationDto Location, ProcurementLabelDto Organization, ProcurementLabelDto Shop, ProcurementLabelDto Warehouse, ProcurementLabelDto Channel);
public sealed record ProcurementTargetsDto(ProcurementTargetDto[] Items);
public sealed record ProcurementPackageDto(LongId SkuId, string SkuRevision, LongId DefinitionId, string DefinitionVersion,
    string SkuCode, string FamilyName, string PackageLabel, string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage);
public sealed record ProcurementPackagesDto(ProcurementPackageDto[] Items, int Offset, bool HasMore);
public sealed record ProcurementLineDto(LongId SkuId, string SkuRevision, LongId DefinitionId, string DefinitionVersion,
    string SkuCode, string FamilyName, string PackageLabel, string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage, int Packages, string? Specification);
public sealed record ProcurementPreviewDto(ProcurementRequestInputDto Input, DateTimeOffset ObservedAt, string Binding, ProcurementSourceDto Source, ProcurementLineDto[] Lines);
public sealed record ProcurementDecisionDto(LongId Id, string Decision, string Reason, DateTimeOffset DecidedAt);
public sealed record ProcurementDownstreamDto(string Supplier, string Commercial, string Payment, string Receipt);
public sealed record ProcurementRequestDto(LongId Id, string Version, ProcurementRequestInputDto Input, ProcurementSourceDto Source,
    ProcurementLineDto[] Lines, DateTimeOffset SubmittedAt, string State, ProcurementDecisionDto? Decision, ProcurementDownstreamDto Downstream);
public sealed record ProcurementRequestListDto(ProcurementLocationDto Location, ProcurementRequestDto[] Items, LongId? NextRequestId);
public sealed record ProcurementDecisionPreviewDto(ProcurementRequestDto Request, string Decision, string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementResultDto(LongId RequestId, string RequestVersion, LongId? DecisionId, string Status);
public sealed record ProcurementRecoveryDto(string Status, ProcurementResultDto? Result);

public static class ProcurementRequestEndpoints
{
    public static void MapProcurementRequestEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/procurement/requests").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (ProcurementReadDto request, HttpContext http, [FromServices] ProcurementRequestService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.TargetsAsync(Actor(http), ct);
            return TypedResults.Ok(new ProcurementTargetsDto([.. row["items"].AsBsonArray.Select(Target)]));
        }).WithName("ProcurementRequestTargets");
        group.MapPost("/packages/query", async (ProcurementPackageQueryDto request, HttpContext http, [FromServices] ProcurementRequestService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PackagesAsync(Actor(http), Location(request.Location), request.Offset, request.Limit, request.Search, ct);
            return TypedResults.Ok(new ProcurementPackagesDto([.. row["items"].AsBsonArray.Select(Package)], row["offset"].AsInt32, row["hasMore"].AsBoolean));
        }).WithName("ProcurementRequestPackages");
        group.MapPost("/preview/query", async (ProcurementRequestPreviewInputDto request, HttpContext http, [FromServices] ProcurementRequestService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw ProcurementRequestRules.Invalid();
            var row = await service.PreviewAsync(Actor(http), request.Input.Domain(), ct);
            return TypedResults.Ok(new ProcurementPreviewDto(Input(row["input"]), Date(row["observedAt"]), row["binding"].AsString, Source(row["source"]), Lines(row["lines"])));
        }).WithName("ProcurementRequestPreview");
        group.MapPost("/submissions", async (ProcurementRequestSubmitDto request, HttpContext http, [FromServices] ProcurementRequestService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) => {
            if (request.Input is null) throw ProcurementRequestRules.Invalid();
            return TypedResults.Ok(Result(await service.SubmitAsync(Actor(http), request.Input.Domain(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Input.Reason, http.TraceIdentifier), ct)));
        }).WithName("SubmitProcurementRequest");
        group.MapPost("/query", async (ProcurementRequestListInputDto request, HttpContext http, [FromServices] ProcurementRequestService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ListAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementRequestListDto(Location(row["location"]), [.. row["items"].AsBsonArray.Select(Request)], Id(row["nextRequestId"])));
        }).WithName("ProcurementRequestHistory");
        group.MapPost("/{id}/query", async (string id, ProcurementReadDto request, HttpContext http, [FromServices] ProcurementRequestService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Request(await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("ProcurementRequestRead");
        group.MapPost("/{id}/decision-preview/query", async (string id, ProcurementDecisionPreviewInputDto request, HttpContext http, [FromServices] ProcurementRequestService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PreviewDecisionAsync(Actor(http), LongId.Parse(id).Value, Decision(request.Decision), ct);
            return TypedResults.Ok(new ProcurementDecisionPreviewDto(Request(row["request"]), row["decision"].AsString, row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("ProcurementRequestDecisionPreview");
        group.MapPost("/{id}/decisions", async (string id, ProcurementDecisionInputDto request, HttpContext http, [FromServices] ProcurementRequestService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            return TypedResults.Ok(Result(await service.DecideAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                Decision(request.Decision), request.Binding, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("DecideProcurementRequest");
        group.MapPost("/operations/query", async (ProcurementRecoveryInputDto request, HttpContext http, [FromServices] ProcurementRequestService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw ProcurementRequestRules.Invalid();
            var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new ProcurementRecoveryDto(row is null ? "not_confirmed" : "confirmed", row is null ? null : Result(row)));
        }).WithName("RecoverProcurementRequest");
    }
    private static ActorContext Actor(HttpContext http) { A.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string value) { if (value != "procurement-request-review") throw ProcurementRequestRules.Invalid(); }
    private static ProcurementRequestDecision Decision(string value) => value switch { "approve" => ProcurementRequestDecision.Approve, "reject" => ProcurementRequestDecision.Reject, _ => throw ProcurementRequestRules.Invalid() };
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static string? Text(BsonValue value) => value.IsBsonNull ? null : value.AsString;
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static ProcurementLocation Location(ProcurementLocationDto value) => value is null ? throw ProcurementRequestRules.Invalid() : value.Domain();
    internal static ProcurementLocationDto Location(BsonValue value) => new(new(value["organizationId"].AsInt64), new(value["shopId"].AsInt64), new(value["warehouseId"].AsInt64), new(value["channelId"].AsInt64));
    internal static ProcurementLabelDto Label(BsonValue value) => new(new(value["id"].AsInt64), value["code"].AsString, value["name"].AsString, Number(value["scopeVersion"]), Number(value["profileVersion"]));
    internal static ProcurementSourceDto Source(BsonValue value) => new(Label(value["organization"]), Label(value["shop"]), Label(value["warehouse"]), Label(value["channel"]));
    internal static ProcurementTargetDto Target(BsonValue value) => new(Location(value["location"]), Label(value["organization"]), Label(value["shop"]), Label(value["warehouse"]), Label(value["channel"]));
    internal static ProcurementPackageDto Package(BsonValue value) => new(new(value["skuId"].AsInt64), Number(value["skuRevision"]), new(value["definitionId"].AsInt64), Number(value["definitionVersion"]),
        value["skuCode"].AsString, value["familyName"].AsString, value["packageLabel"].AsString, value["packageKind"].AsString, value["baseUnitCode"].AsString, value["baseUnitsPerPackage"].AsInt32);
    private static ProcurementLineDto[] Lines(BsonValue value) => [.. value.AsBsonArray.Select(line => new ProcurementLineDto(new(line["skuId"].AsInt64), Number(line["skuRevision"]),
        new(line["definitionId"].AsInt64), Number(line["definitionVersion"]), line["skuCode"].AsString, line["familyName"].AsString, line["packageLabel"].AsString,
        line["packageKind"].AsString, line["baseUnitCode"].AsString, line["baseUnitsPerPackage"].AsInt32, line["packages"].AsInt32, Text(line["specification"])))];
    private static ProcurementRequestInputDto Input(BsonValue value) => new(Location(value["location"]), value["route"].AsString, value["kind"].AsString, value["businessDate"].AsString,
        Text(value["externalReference"]), value["reason"].AsString, [.. value["lines"].AsBsonArray.Select(line => new ProcurementRequestLineInputDto(new(line["skuId"].AsInt64),
            Number(line["expectedSkuRevision"]), new(line["definitionId"].AsInt64), Number(line["definitionVersion"]), line["packages"].AsInt32, Text(line["specification"])))]);
    private static ProcurementDecisionDto DecisionView(BsonValue value) => new(new(value["id"].AsInt64), value["decision"].AsString, value["reason"].AsString, Date(value["decidedAt"]));
    private static ProcurementRequestDto Request(BsonValue value) => new(new(value["id"].AsInt64), Number(value["version"]), Input(value["input"]), Source(value["source"]), Lines(value["lines"]),
        Date(value["submittedAt"]), value["state"].AsString, value["decision"].IsBsonNull ? null : DecisionView(value["decision"]),
        new(value["downstream"]["supplier"].AsString, value["downstream"]["commercial"].AsString, value["downstream"]["payment"].AsString, value["downstream"]["receipt"].AsString));
    private static ProcurementResultDto Result(BsonValue value) => new(new(value["requestId"].AsInt64), Number(value["requestVersion"]), Id(value["decisionId"]), value["status"].AsString);
}
