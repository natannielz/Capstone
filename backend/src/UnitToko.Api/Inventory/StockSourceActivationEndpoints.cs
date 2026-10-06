using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;

namespace UnitToko.Api.Inventory;

public static class StockSourceActivationEndpoints
{
    public static void MapStockSourceActivationEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/inventory/sources").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/pools/{poolId}/activation-preview/query", async (string poolId, StockSourceActivationReadDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) => {
            StockSourceActivationInputs.Purpose(request.Purpose, "inventory-source-activation-preview");
            return TypedResults.Ok(StockSourceActivationResponses.Preview(await service.PreviewActivationAsync(Actor(http), LongId.Parse(poolId).Value, ct)));
        }).WithName("StockSourceActivationPreview");
        group.MapPost("/pools/{poolId}/activation-proposals", async (string poolId, StockSourceActivationProposeDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            var expected = request.Expected?.ToDomain() ?? throw StockSourceActivationInputs.Invalid();
            if (AccessRequestMetadata.ExpectedVersion(http.Request) != expected.PoolRevision) throw StockSourceActivationInputs.Invalid();
            var result = await service.ProposeActivationAsync(Actor(http), LongId.Parse(poolId).Value, expected,
                request.Handoff?.ToDomain() ?? throw StockSourceActivationInputs.Invalid(),
                new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1); return TypedResults.Ok(StockSourceActivationResponses.Command(result));
        }).WithName("ProposeStockSourceActivation");
        group.MapPost("/pools/{poolId}/activation-proposals/query", async (string poolId, StockSourceActivationQueryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) => {
            StockSourceActivationInputs.Purpose(request.Purpose, "inventory-source-activation-history");
            var page = await service.ListActivationsAsync(Actor(http), LongId.Parse(poolId).Value, request.Offset, request.Limit, ct);
            return TypedResults.Ok(new StockSourceActivationPageDto(page.Total.ToString(CultureInfo.InvariantCulture), [.. page.Items.Select(StockSourceActivationResponses.Detail)]));
        }).WithName("StockSourceActivationHistory");
        group.MapPost("/pools/{poolId}/activation-proposals/{id}/query", async Task<StockSourceActivationDetailDto?> (string poolId, string id, StockSourceActivationReadDto request,
            HttpContext http, [FromServices] StockSourceSetupService service, CancellationToken ct) => {
            StockSourceActivationInputs.Purpose(request.Purpose, "inventory-source-activation-history");
            var result = await service.ReadActivationAsync(Actor(http), LongId.Parse(poolId).Value, LongId.Parse(id).Value, ct);
            if (result is null) return null; http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1); return StockSourceActivationResponses.Detail(result);
        }).WithName("StockSourceActivationRead");
        group.MapPost("/activation-proposals/{id}/review/query", async (string id, StockSourceActivationReviewQueryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) => {
            StockSourceActivationInputs.Purpose(request.Purpose, "inventory-source-activation-review");
            var result = await service.PreviewActivationDecisionAsync(Actor(http), LongId.Parse(id).Value, StockSourceActivationInputs.Action(request.Action), request.Handoff?.ToDomain(), ct);
            if (result["poolId"] != request.PhysicalPoolId.Value) throw new AuthorizationDeniedException("resource_unavailable");
            return TypedResults.Ok(StockSourceActivationResponses.Review(result, request.PhysicalPoolId.Value));
        }).WithName("StockSourceActivationReview");
        foreach (var (suffix, action, name) in new[] { ("approve", StockSourceSetupDecision.Approve, "ApproveStockSourceActivation"),
            ("reject", StockSourceSetupDecision.Reject, "RejectStockSourceActivation") }) {
            group.MapPost("/activation-proposals/{id}/" + suffix, async (string id, StockSourceActivationDecideDto request, HttpContext http,
                [FromServices] StockSourceSetupService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
                [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
                var result = await service.DecideActivationAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), action,
                    request.Handoff?.ToDomain(), request.Expected?.ToDomain() ?? throw StockSourceActivationInputs.Invalid(), request.Binding,
                    new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
                http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1); return TypedResults.Ok(StockSourceActivationResponses.Command(result));
            }).WithName(name);
        }
        group.MapPost("/activation-operations/query", async (StockSourceActivationRecoveryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) => {
            StockSourceActivationInputs.Purpose(request.Purpose, "operation-recovery");
            if (request.Operation is not ("stock.source.activation.propose" or "stock.source.activation.decide") || string.IsNullOrEmpty(request.Key) ||
                request.Key.Length > 128 || request.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw StockSourceActivationInputs.Invalid();
            var result = await service.RecoverActivationAsync(Actor(http), request.PhysicalPoolId.Value, request.Operation, request.Key, http.TraceIdentifier, ct);
            return TypedResults.Ok(new StockSourceActivationReceiptDto(result is null ? "not_confirmed" : "committed", result is null ? null : StockSourceActivationResponses.Command(result)));
        }).WithName("StockSourceActivationReceipt");
    }
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
}
