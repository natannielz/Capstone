using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;

namespace UnitToko.Api.Inventory;

public static class StockSourceReconciliationEndpoints
{
    private const string PurposeValue = "stock-source-reconciliation-review";
    public static void MapStockSourceReconciliationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/inventory/source-reconciliations").RequireAuthorization()
            .AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (StockSourceReconciliationTargetsDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            Purpose(request.Purpose); Private(http);
            var page = await service.ReconciliationTargetsAsync(Actor(http), StockSourceSetupInputs.Location(request.Location), request.Offset, request.Limit, ct);
            return TypedResults.Ok(new StockSourceReconciliationTargetPageDto(page.Total.ToString(CultureInfo.InvariantCulture),
                [.. page.Items.Select(StockSourceReconciliationResponses.Target)]));
        }).WithName("StockSourceReconciliationTargets");
        group.MapPost("/pools/{poolId}/baseline/query", async (string poolId, StockSourceReconciliationReadDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            Purpose(request.Purpose); Private(http);
            var row = await service.ReadReconciliationBaselineAsync(Actor(http), LongId.Parse(poolId).Value, ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row["poolRevision"].AsInt64);
            return TypedResults.Ok(StockSourceReconciliationResponses.Baseline(row));
        }).WithName("StockSourceReconciliationBaseline");
        group.MapPost("/proposals", async (StockSourceReconciliationProposeDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) =>
        {
            Private(http);
            var input = request.Input?.ToDomain() ?? throw StockSourceReconciliationRules.Invalid();
            var result = await service.ProposeReconciliationAsync(Actor(http), input, request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(StockSourceReconciliationResponses.Result(result));
        }).WithName("StockSourceReconciliationPropose");
        group.MapPost("/proposals/{id}/review/query", async (string id, StockSourceReconciliationPreviewDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            Purpose(request.Purpose); Private(http);
            var row = await service.PreviewReconciliationDecisionAsync(Actor(http), LongId.Parse(id).Value, Decision(request.Decision),
                request.Observation?.ToDomain(), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(StockSourceReconciliationResponses.Review(row));
        }).WithName("StockSourceReconciliationReview");
        group.MapPost("/proposals/{id}/decide", async (string id, StockSourceReconciliationDecideDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) =>
        {
            Private(http);
            var row = await service.DecideReconciliationAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                Decision(request.Decision), request.Observation?.ToDomain(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(StockSourceReconciliationResponses.Result(row));
        }).WithName("StockSourceReconciliationDecide");
        group.MapPost("/pools/{poolId}/proposals/query", async (string poolId, StockSourceReconciliationHistoryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            Purpose(request.Purpose); Private(http);
            var page = await service.ListReconciliationsAsync(Actor(http), LongId.Parse(poolId).Value, request.Offset, request.Limit, ct);
            return TypedResults.Ok(new StockSourceReconciliationPageDto(page.Total.ToString(CultureInfo.InvariantCulture),
                [.. page.Items.Select(StockSourceReconciliationResponses.Detail)]));
        }).WithName("StockSourceReconciliationHistory");
        group.MapPost("/proposals/{id}/query", async Task<StockSourceReconciliationDetailDto?> (string id,
            StockSourceReconciliationReadDto request, HttpContext http, [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            Purpose(request.Purpose); Private(http);
            var row = await service.ReadReconciliationAsync(Actor(http), LongId.Parse(id).Value, ct);
            if (row is null) return null;
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return StockSourceReconciliationResponses.Detail(row);
        }).WithName("StockSourceReconciliationDetail");
        group.MapPost("/operations/query", async (StockSourceReconciliationRecoveryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "operation-recovery"); Private(http);
            var row = await service.RecoverReconciliationAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new StockSourceReconciliationReceiptDto(row is null ? "not_confirmed" : "committed",
                row is null ? null : StockSourceReconciliationResponses.Result(row)));
        }).WithName("StockSourceReconciliationReceipt");
    }
    private static void Purpose(string actual) => StockSourceSetupInputs.Purpose(actual, PurposeValue);
    private static StockSourceReconciliationDecision Decision(string value) => value switch {
        "confirm" => StockSourceReconciliationDecision.Confirm, "reject" => StockSourceReconciliationDecision.Reject,
        _ => throw StockSourceReconciliationRules.Invalid() };
    private static void Private(HttpContext http) => http.Response.Headers.CacheControl = "private, no-store";
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ??
        throw new AuthorizationDeniedException("unauthenticated");
}
