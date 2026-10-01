using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Fulfillment;
using UnitToko.Platform.Fulfillment;

namespace UnitToko.Api.Fulfillment;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnResolutionPreviewDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Action);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnResolutionRequestDto([property: JsonRequired] string Binding, [property: JsonRequired] string Action, [property: JsonRequired] string PublicNote, [property: JsonRequired] string Reason);
public sealed record ReturnResolutionRequestViewDto(DateTimeOffset RequestedAt, string Action, string PublicNote, string State);
public sealed record ReturnResolutionDto(LongId CaseId, LongId InvoiceId, LongId ReturnId, string Revision, string Binding, ReturnInspectionAssessmentDto Inspection, ReturnResolutionRequestViewDto? Request);
public sealed record ReturnResolutionResultDto(LongId ReturnId, LongId CaseId, LongId InvoiceId, string Revision, DateTimeOffset RequestedAt, string Action, string State, string Operation);
public sealed record ReturnResolutionRecoveryDto(string Status, ReturnResolutionResultDto? Result);
public static class AfterSalesResolutionEndpoints
{
    public static void MapAfterSalesResolutionEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/fulfillment/cases/{id}/returns/{returnId}/resolution").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (string id, string returnId, OrderReadDto request, HttpContext http, AfterSalesCaseService service, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Private(http); ReadPurpose(http, request.Purpose); var value = await service.ReadResolutionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, ct: ct);
            http.Response.Headers.ETag = "\"" + Number(value.Revision) + "\""; return TypedResults.Ok(View(value));
        }).WithName("ReadAfterSalesResolutionRequest");
        group.MapPost("/preview/query", async (string id, string returnId, ReturnResolutionPreviewDto request, HttpContext http, AfterSalesCaseService service, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Private(http); ReadPurpose(http, request.Purpose); _ = ReturnResolutionPolicy.Permission(request.Action);
            var value = await service.ReadResolutionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, request.Action, ct);
            http.Response.Headers.ETag = "\"" + Number(value.Revision) + "\""; return TypedResults.Ok(View(value));
        }).WithName("PreviewAfterSalesResolutionRequest");
        group.MapPost("", async (string id, string returnId, ReturnResolutionRequestDto request, HttpContext http, AfterSalesCaseService service, [FromHeader(Name = "If-Match")] string expected,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            Private(http); return TypedResults.Ok(Result(await service.RequestResolutionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                request.Binding, new(request.Action, request.PublicNote), new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("RequestAfterSalesResolution");
        group.MapPost("/operations/query", async (string id, string returnId, OrderDraftRecoveryDto request, HttpContext http, AfterSalesCaseService service, CancellationToken ct) => {
            Private(http); if (request.Purpose != "operation-recovery") throw ReturnResolutionPolicy.Invalid();
            var value = await service.RecoverResolutionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, request.Key, ct);
            return TypedResults.Ok(new ReturnResolutionRecoveryDto(value is null ? "not_confirmed" : "committed", value is null ? null : Result(value)));
        }).WithName("RecoverAfterSalesResolutionRequest");
    }
    private static void ReadPurpose(HttpContext http, string purpose) { if (purpose != "after-sales-resolution" || AccessRequestMetadata.ExpectedVersion(http.Request) != 2) throw ReturnResolutionPolicy.Invalid(); }
    private static ReturnResolutionDto View(AfterSalesResolutionView value) => new(value.CaseId, value.InvoiceId, value.ReturnId, Number(value.Revision), value.Binding,
        new(value.Inspection.InspectedAt, value.Inspection.PublicNote, [.. value.Inspection.Lines.Select(line => new ReturnInspectionAssessmentLineDto(line.ReceiptId, line.SkuId, Number(line.InspectedBaseUnits), line.Identity, line.Condition))]),
        value.Request is null ? null : new(value.Request.RequestedAt, value.Request.Action, value.Request.PublicNote, value.Request.State));
    private static ReturnResolutionResultDto Result(BsonDocument value) => new(new(value["returnId"].AsInt64), new(value["caseId"].AsInt64), new(value["invoiceId"].AsInt64), Number(value["revision"].AsInt64),
        new(value["requestedAt"].ToUniversalTime()), value["action"].AsString, value["state"].AsString, value["operation"].AsString);
    private static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static void Private(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache"; }
}
