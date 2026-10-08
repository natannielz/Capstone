using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Procurement;
using P = UnitToko.Api.Billing.PaymentAllocationEndpoints;

namespace UnitToko.Api.Procurement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseArrivalInputDto([property: JsonRequired] LongId OrderId, [property: JsonRequired] string OrderVersion,
    [property: JsonRequired] LongId ApprovalId, [property: JsonRequired] LongId SkuId, [property: JsonRequired] LongId DefinitionId,
    [property: JsonRequired] string ExpectedRevision, [property: JsonRequired] DateTimeOffset? ExpectedAt, [property: JsonRequired] string ConfirmationReference)
{
    internal PurchaseArrivalInput Domain() => PurchaseArrivalRules.Copy(new(OrderId.Value, LongId.Parse(OrderVersion).Value, ApprovalId.Value,
        SkuId.Value, DefinitionId.Value, LongId.Parse(ExpectedRevision).Value, ExpectedAt, ConfirmationReference));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseArrivalQueryDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseArrivalPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] PurchaseArrivalInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseArrivalSubmitDto([property: JsonRequired] PurchaseArrivalInputDto Input, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseArrivalDecisionPreviewDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Decision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseArrivalDecisionDto([property: JsonRequired] string Decision, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseArrivalRecoveryInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Action, [property: JsonRequired] string Key);
public sealed record PurchaseArrivalViewDto(LongId OrderId, string OrderVersion, LongId ApprovalId, LongId SkuId, LongId DefinitionId,
    string Revision, int OrderedPackages, string State, LongId? ProposalId, DateTimeOffset? ProposedAt, DateTimeOffset? ExpectedAt,
    string? ConfirmationReference, string? Reason, DateTimeOffset? RecordedAt);
public sealed record PurchaseArrivalPreviewDto(PurchaseArrivalViewDto Current, PurchaseArrivalInputDto Input, string Binding, string? Decision);
public sealed record PurchaseArrivalResultDto(LongId EventId, string EventHash, LongId OrderId, LongId SkuId, string Revision, string State);
public sealed record PurchaseArrivalRecoveryDto(string Status, PurchaseArrivalResultDto? Result);

public static class PurchaseArrivalEndpoints
{
    public static void MapPurchaseArrivalEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/procurement/arrivals").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/{orderId}/{skuId}/query", async (string orderId, string skuId, PurchaseArrivalQueryDto request, HttpContext http, [FromServices] PurchaseOrderService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(View(await service.ReadArrivalAsync(Actor(http), LongId.Parse(orderId).Value, LongId.Parse(skuId).Value, ct)));
        }).WithName("PurchaseArrivalRead");
        group.MapPost("/preview/query", async (PurchaseArrivalPreviewInputDto request, HttpContext http, [FromServices] PurchaseOrderService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw PurchaseArrivalRules.Invalid();
            return TypedResults.Ok(Preview(await service.PreviewArrivalAsync(Actor(http), request.Input.Domain(), ct)));
        }).WithName("PurchaseArrivalPreview");
        group.MapPost("/proposals", async (PurchaseArrivalSubmitDto request, HttpContext http, [FromServices] PurchaseOrderService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) => {
            if (request.Input is null) throw PurchaseArrivalRules.Invalid();
            return TypedResults.Ok(Result(await service.ProposeArrivalAsync(Actor(http), request.Input.Domain(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ProposePurchaseArrival");
        group.MapPost("/proposals/{id}/decision-preview/query", async (string id, PurchaseArrivalDecisionPreviewDto request, HttpContext http,
            [FromServices] PurchaseOrderService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Preview(await service.PreviewArrivalDecisionAsync(Actor(http), LongId.Parse(id).Value, request.Decision, ct)));
        }).WithName("PurchaseArrivalDecisionPreview");
        group.MapPost("/proposals/{id}/decisions", async (string id, PurchaseArrivalDecisionDto request, HttpContext http,
            [FromServices] PurchaseOrderService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            return TypedResults.Ok(Result(await service.DecideArrivalAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), request.Decision,
                request.Binding, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("DecidePurchaseArrival");
        group.MapPost("/operations/query", async (PurchaseArrivalRecoveryInputDto request, HttpContext http, [FromServices] PurchaseOrderService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw PurchaseArrivalRules.Invalid();
            var result = await service.RecoverArrivalAsync(Actor(http), request.Action, request.Key, ct);
            return TypedResults.Ok(new PurchaseArrivalRecoveryDto(result is null ? "not_confirmed" : "confirmed", result is null ? null : Result(result)));
        }).WithName("RecoverPurchaseArrival");
    }
    private static ActorContext Actor(HttpContext http) { P.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string value) { if (value != "purchase-arrival-review") throw PurchaseArrivalRules.Invalid(); }
    private static string Number(BsonValue v) => v.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static LongId? Id(BsonValue v) => v.IsBsonNull ? null : new(v.AsInt64);
    private static DateTimeOffset? Date(BsonValue v) => v.IsBsonNull ? null : new(v.ToUniversalTime(), TimeSpan.Zero);
    private static string? Text(BsonValue v) => v.IsBsonNull ? null : v.AsString;
    internal static PurchaseArrivalViewDto View(BsonDocument v) => new(new(v["orderId"].AsInt64), Number(v["orderVersion"]), new(v["approvalId"].AsInt64),
        new(v["skuId"].AsInt64), new(v["definitionId"].AsInt64), Number(v["revision"]), v["orderedPackages"].AsInt32, v["state"].AsString,
        Id(v["proposalId"]), Date(v["proposedAt"]), Date(v["expectedAt"]), Text(v["confirmationReference"]), Text(v["reason"]), Date(v["recordedAt"]));
    private static PurchaseArrivalInputDto Input(BsonValue v) => new(new(v["orderId"].AsInt64), Number(v["orderVersion"]), new(v["approvalId"].AsInt64),
        new(v["skuId"].AsInt64), new(v["definitionId"].AsInt64), Number(v["expectedRevision"]), Date(v["expectedAt"]), v["confirmationReference"].AsString);
    private static PurchaseArrivalPreviewDto Preview(BsonDocument v) => new(View(v), Input(v["input"]), v["binding"].AsString, v.TryGetValue("decision", out var decision) ? decision.AsString : null);
    private static PurchaseArrivalResultDto Result(BsonDocument v) => new(new(v["eventId"].AsInt64), v["eventHash"].AsString,
        new(v["orderId"].AsInt64), new(v["skuId"].AsInt64), Number(v["revision"]), v["state"].AsString);
}
