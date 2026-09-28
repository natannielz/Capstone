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

public static class StockSourceRevisionEndpoints
{
    public static void MapStockSourceRevisionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/inventory/sources").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/pools/query", async (StockSourceCurrentQueryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-current");
            var page = await service.ListSourcesAsync(Actor(http), StockSourceSetupInputs.Location(request.Location), request.Offset, request.Limit, ct);
            return TypedResults.Ok(new StockSourceCurrentPageDto(page.Total.ToString(CultureInfo.InvariantCulture), [.. page.Items.Select(StockSourceRevisionResponses.Current)]));
        }).WithName("StockSourceCurrentList");
        group.MapPost("/pools/{poolId}/query", async (string poolId, StockSourceCurrentQueryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-current");
            var view = await service.ReadCurrentSourceAsync(Actor(http), StockSourceSetupInputs.Location(request.Location), LongId.Parse(poolId).Value, ct);
            var result = StockSourceRevisionResponses.Current(view);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(view["pool"]["revision"].AsInt64);
            return TypedResults.Ok(result);
        }).WithName("StockSourceCurrentRead");
        group.MapPost("/pools/{poolId}/revisions", async (string poolId, StockSourceSetupProposeDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) =>
        {
            var result = await service.ProposeRevisionAsync(Actor(http), LongId.Parse(poolId).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                request.ToDomain(), new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(StockSourceRevisionResponses.Command(result));
        }).WithName("ProposeStockSourceRevision");
        group.MapPost("/revisions/query", async (StockSourceRevisionQueryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-revisions");
            var page = await service.ListRevisionsAsync(Actor(http), StockSourceSetupInputs.Location(request.Location), request.PhysicalPoolId?.Value,
                request.Offset, request.Limit, ct);
            return TypedResults.Ok(new StockSourceRevisionPageDto(page.Total.ToString(CultureInfo.InvariantCulture), [.. page.Items.Select(StockSourceRevisionResponses.Detail)]));
        }).WithName("StockSourceRevisionHistory");
        group.MapPost("/revisions/{id}/query", async Task<StockSourceRevisionDetailDto?> (string id, StockSourceRevisionReadDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-revisions");
            var view = await service.ReadRevisionAsync(Actor(http), StockSourceSetupInputs.Location(request.Location), LongId.Parse(id).Value, ct);
            if (view is null) return null;
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return StockSourceRevisionResponses.Detail(view);
        }).WithName("StockSourceRevisionRead");
        foreach (var (suffix, action, name) in new[]
        {
            ("approve", StockSourceSetupDecision.Approve, "ApproveStockSourceRevision"),
            ("reject", StockSourceSetupDecision.Reject, "RejectStockSourceRevision"),
            ("withdraw", StockSourceSetupDecision.Withdraw, "WithdrawStockSourceRevision")
        })
            group.MapPost("/revisions/{id}/" + suffix, async (string id, StockSourceSetupDecideDto request, HttpContext http,
                [FromServices] StockSourceSetupService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
                [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) =>
            {
                try
                {
                    var result = await service.DecideRevisionAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                        action, request.ReviewReference, new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
                    http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
                    return TypedResults.Ok(StockSourceRevisionResponses.Command(result));
                }
                catch (AuthorizationDeniedException exception) when (exception.Code == "approval_revision_invalid")
                { throw new PersistenceConflictException("stock_source_revision_changed", "The proposed source revision changed"); }
            }).WithName(name);
        group.MapPost("/revision-operations/query", async (StockSourceRevisionRecoveryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "operation-recovery");
            if (string.IsNullOrEmpty(request.Key) || request.Key.Length > 128 || request.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')))
                throw new DomainValidationException("invalid_idempotency_key", "Use a canonical request key");
            var result = await service.RecoverRevisionAsync(Actor(http), StockSourceSetupInputs.Location(request.Location), request.Operation, request.Key, http.TraceIdentifier, ct);
            return TypedResults.Ok(new StockSourceRevisionReceiptDto(result is null ? "not_confirmed" : "committed", result is null ? null : StockSourceRevisionResponses.Command(result)));
        }).WithName("StockSourceRevisionReceipt");
    }
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
}
