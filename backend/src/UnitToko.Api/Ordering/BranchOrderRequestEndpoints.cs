using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Ordering;

namespace UnitToko.Api.Ordering;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record BranchRequestLineInputDto([property: JsonRequired] LongId SkuId, [property: JsonRequired] LongId DefinitionId,
    [property: JsonRequired] string DefinitionVersion, [property: JsonRequired] int Packages);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BranchRequestInputDto([property: JsonRequired] OrderContextDto Context,
    [property: JsonRequired] BranchRequestLineInputDto[] Lines, string? Note = null) {
    internal BranchOrderRequestInput Domain() {
        if (Context is null || Lines is null || Lines.Length is < 1 or > 100 || Lines.Any(line => line is null))
            throw new DomainValidationException("invalid_branch_order_request", "Choose a branch and explicit packages");
        return BranchOrderRequestRules.Copy(new(Context.Domain(), [.. Lines.Select(line => new BranchOrderRequestLine(line.SkuId,
            line.DefinitionId, CatalogInputs.PositiveVersion(line.DefinitionVersion), line.Packages))], Note));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BranchRequestPurposeDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record BranchRequestPackagesQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] OrderContextDto Context, int Offset = 0, int Limit = 25, string? Search = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BranchRequestPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] BranchRequestInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BranchRequestSubmitDto([property: JsonRequired] BranchRequestInputDto Input,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BranchRequestHistoryQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string View, LongId? BeforeRequestId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BranchRequestDecisionPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Decision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BranchRequestDecideDto([property: JsonRequired] string Decision,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BranchRequestRecoveryInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

public sealed record BranchRequestLabelsDto(string BuyingOrganization, string Branch, string? Division,
    string FulfillmentOrganization, string Shop, string? Warehouse, string Channel);
public sealed record BranchRequestLineDto(LongId SkuId, LongId DefinitionId, string DefinitionVersion, int Packages,
    string SkuCode, string FamilyName, string PackageLabel, string PackageKind, string BaseUnitCode,
    int BaseUnitsPerPackage, AccessScopeDto Owner);
public sealed record BranchRequestPreviewDto(BranchRequestInputDto Input, BranchRequestLineDto[] Lines,
    AccessScopeDto Owner, BranchRequestLabelsDto Labels, string Binding, DateTimeOffset ObservedAt);
public sealed record BranchRequestDecisionDto(LongId Id, string Action, string Reason, DateTimeOffset RecordedAt);
public sealed record BranchRequestViewDto(LongId Id, string Number, string Version, string State,
    BranchRequestInputDto Input, BranchRequestLineDto[] Lines, AccessScopeDto Owner,
    BranchRequestLabelsDto Labels, LongId CreatedBy, DateTimeOffset RecordedAt, BranchRequestDecisionDto? Decision);
public sealed record BranchRequestPageDto(BranchRequestViewDto[] Items, LongId? NextRequestId);
public sealed record BranchRequestDecisionPreviewDto(BranchRequestViewDto Request, string Decision,
    bool CanDecide, string Binding, DateTimeOffset ObservedAt);
public sealed record BranchRequestResultDto(LongId RequestId, LongId? DecisionId, string Number,
    string Version, string State, DateTimeOffset RecordedAt);
public sealed record BranchRequestRecoveryDto(string State, BranchRequestResultDto? Result);

public static class BranchOrderRequestEndpoints {
    public static void MapBranchOrderRequestEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/ordering/requests").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/contexts/query", async (BranchRequestPurposeDto request, HttpContext http,
            [FromServices] BranchOrderRequestService service, CancellationToken ct) => {
            Purpose(request.Purpose, "branch-request-review");
            var rows = await service.ContextsAsync(Actor(http), ct);
            return TypedResults.Ok(new OrderContextsDto([.. rows.Select(row => new OrderContextOptionDto(
                OrderContextDto.From(row.Context), row.OrganizationName, row.BranchName, row.DivisionName))]));
        }).WithName("BranchRequestContexts");
        group.MapPost("/packages/query", async (BranchRequestPackagesQueryDto request, HttpContext http,
            [FromServices] BranchOrderRequestService service, CancellationToken ct) => {
            Purpose(request.Purpose, "branch-request-review"); if (request.Context is null) throw Invalid();
            return TypedResults.Ok(CatalogResponses.From(await service.PackagesAsync(Actor(http), request.Context.Domain(),
                request.Offset, request.Limit, request.Search, ct)));
        }).WithName("BranchRequestPackages");
        group.MapPost("/preview/query", async (BranchRequestPreviewInputDto request, HttpContext http,
            [FromServices] BranchOrderRequestService service, CancellationToken ct) => {
            Purpose(request.Purpose, "branch-request-review"); if (request.Input is null) throw Invalid();
            return TypedResults.Ok(Preview(await service.PreviewAsync(Actor(http), request.Input.Domain(), ct)));
        }).WithName("PreviewBranchRequest");
        group.MapPost("", async (BranchRequestSubmitDto request, HttpContext http,
            [FromServices] BranchOrderRequestService service,
            [FromHeader(Name = "If-Match")] string expectedVersion,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) => {
            if (request.Input is null || AccessRequestMetadata.ExpectedVersion(http.Request) != 0) throw Invalid();
            var result = Result(await service.SubmitAsync(Actor(http), request.Input.Domain(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct));
            http.Response.Headers.ETag = '"' + result.Version + '"'; return TypedResults.Ok(result);
        }).WithName("SubmitBranchRequest");
        group.MapPost("/query", async (BranchRequestHistoryQueryDto request, HttpContext http,
            [FromServices] BranchOrderRequestService service, CancellationToken ct) => {
            Purpose(request.Purpose, "branch-request-history");
            var incoming = request.View switch { "mine" => false, "incoming" => true, _ => throw Invalid() };
            var page = await service.ListAsync(Actor(http), incoming, request.BeforeRequestId?.Value, ct);
            return TypedResults.Ok(new BranchRequestPageDto([.. page["items"].AsBsonArray.Select(View)],
                page["nextRequestId"].IsBsonNull ? null : new LongId(page["nextRequestId"].AsInt64)));
        }).WithName("ListBranchRequests");
        group.MapPost("/{id}/query", async (string id, BranchRequestPurposeDto request, HttpContext http,
            [FromServices] BranchOrderRequestService service, CancellationToken ct) => {
            Purpose(request.Purpose, "branch-request-history"); var row = View(await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct));
            http.Response.Headers.ETag = '"' + row.Version + '"'; return TypedResults.Ok(row);
        }).WithName("ReadBranchRequest");
        group.MapPost("/{id}/decisions/preview/query", async (string id, BranchRequestDecisionPreviewInputDto request,
            HttpContext http, [FromServices] BranchOrderRequestService service, CancellationToken ct) => {
            Purpose(request.Purpose, "branch-request-decision");
            var row = await service.PreviewDecisionAsync(Actor(http), LongId.Parse(id).Value, Decision(request.Decision), ct);
            return TypedResults.Ok(new BranchRequestDecisionPreviewDto(View(row["request"]), row["decision"].AsString,
                row["canDecide"].AsBoolean, row["binding"].AsString, Time(row["observedAt"])));
        }).WithName("PreviewBranchRequestDecision");
        group.MapPost("/{id}/decisions", async (string id, BranchRequestDecideDto request, HttpContext http,
            [FromServices] BranchOrderRequestService service,
            [FromHeader(Name = "If-Match")] string expectedVersion,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) => {
            var result = Result(await service.DecideAsync(Actor(http), LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), Decision(request.Decision), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct));
            http.Response.Headers.ETag = '"' + result.Version + '"'; return TypedResults.Ok(result);
        }).WithName("DecideBranchRequest");
        group.MapPost("/operations/query", async (BranchRequestRecoveryInputDto request, HttpContext http,
            [FromServices] BranchOrderRequestService service, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery"); var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new BranchRequestRecoveryDto(row["state"].AsString,
                row["result"].IsBsonNull ? null : Result(row["result"]))) ;
        }).WithName("RecoverBranchRequest");
    }
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()!.Actor;
    private static void Purpose(string? actual, string expected) { if (actual != expected) throw Invalid(); }
    private static BranchOrderRequestDecision Decision(string? value) => value switch {
        "accept" => BranchOrderRequestDecision.Accept, "reject" => BranchOrderRequestDecision.Reject, _ => throw Invalid() };
    private static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static DateTimeOffset Time(BsonValue value) => DateTimeOffset.FromUnixTimeMilliseconds(value.AsBsonDateTime.MillisecondsSinceEpoch);
    private static BranchRequestInputDto Input(BsonValue value) => new(
        new OrderContextDto(new(value["context"]["organizationId"].AsInt64), new(value["context"]["branchId"].AsInt64),
            value["context"]["divisionId"].IsBsonNull ? null : new LongId(value["context"]["divisionId"].AsInt64)),
        [.. value["lines"].AsBsonArray.Select(line => new BranchRequestLineInputDto(new(line["skuId"].AsInt64),
            new(line["definitionId"].AsInt64), Number(line["definitionVersion"].AsInt64), line["packages"].AsInt32))],
        value["note"].IsBsonNull ? null : value["note"].AsString);
    private static AccessScopeDto Owner(BsonValue value) => new(value["kind"].AsString switch {
        "fulfillmentShop" => GrantScopeKind.FulfillmentShop, "fulfillmentWarehouse" => GrantScopeKind.FulfillmentWarehouse,
        _ => throw Invalid() }, new(value["organizationId"].AsInt64), ShopId: new(value["shopId"].AsInt64),
        WarehouseId: value["warehouseId"].IsBsonNull ? null : new LongId(value["warehouseId"].AsInt64),
        ChannelId: new(value["channelId"].AsInt64));
    private static BranchRequestLabelsDto Labels(BsonValue value) => new(value["buyingOrganization"].AsString,
        value["branch"].AsString, value["division"].IsBsonNull ? null : value["division"].AsString,
        value["fulfillmentOrganization"].AsString, value["shop"].AsString,
        value["warehouse"].IsBsonNull ? null : value["warehouse"].AsString, value["channel"].AsString);
    private static BranchRequestLineDto Line(BsonValue value) => new(new(value["skuId"].AsInt64),
        new(value["definitionId"].AsInt64), Number(value["definitionVersion"].AsInt64), value["packages"].AsInt32,
        value["skuCode"].AsString, value["familyName"].AsString, value["packageLabel"].AsString,
        value["packageKind"].AsString, value["baseUnitCode"].AsString, value["baseUnitsPerPackage"].AsInt32, Owner(value["owner"]));
    private static BranchRequestPreviewDto Preview(BsonValue value) => new(Input(value["input"]),
        [.. value["lines"].AsBsonArray.Select(Line)], Owner(value["owner"]), Labels(value["labels"]),
        value["binding"].AsString, Time(value["observedAt"]));
    private static BranchRequestViewDto View(BsonValue value) => new(new(value["id"].AsInt64), value["number"].AsString,
        Number(value["version"].AsInt64), value["state"].AsString, Input(value["input"]),
        [.. value["lines"].AsBsonArray.Select(Line)], Owner(value["owner"]), Labels(value["labels"]),
        new(value["createdBy"].AsInt64), Time(value["recordedAt"]), value["decision"].IsBsonNull ? null :
            new BranchRequestDecisionDto(new(value["decision"]["id"].AsInt64), value["decision"]["action"].AsString,
                value["decision"]["reason"].AsString, Time(value["decision"]["recordedAt"])));
    private static BranchRequestResultDto Result(BsonValue value) => new(new(value["requestId"].AsInt64),
        value.AsBsonDocument.TryGetValue("decisionId", out var id) ? new LongId(id.AsInt64) : null,
        value["number"].AsString, Number(value["version"].AsInt64), value["state"].AsString, Time(value["recordedAt"]));
    private static DomainValidationException Invalid() => new("invalid_branch_order_request", "Choose a current branch and reviewed request");
}
