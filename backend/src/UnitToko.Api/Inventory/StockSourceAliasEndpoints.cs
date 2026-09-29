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

public static class StockSourceAliasEndpoints
{
    public static void MapStockSourceAliasEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/inventory/sources").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/pools/{poolId}/aliases/query", async (string poolId, StockSourceAliasReadDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-alias");
            var location = StockSourceSetupInputs.Location(request.RequestedLocation);
            var view = await service.ReadAliasAsync(Actor(http), LongId.Parse(poolId).Value, location, ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(view["currentSource"]["pool"]["revision"].AsInt64);
            return TypedResults.Ok(StockSourceAliasResponses.Current(view, location));
        }).WithName("StockSourceAliasCurrent");
        group.MapPost("/pools/{poolId}/aliases", async (string poolId, StockSourceAliasProposeDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) =>
        {
            var result = await service.ProposeAliasAsync(Actor(http), LongId.Parse(poolId).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                StockSourceSetupInputs.Location(request.RequestedLocation), StockSourceSetupInputs.Number(request.ExpectedAliasVersion, true), request.Enabled,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(StockSourceAliasResponses.Command(result));
        }).WithName("ProposeStockSourceAlias");
        group.MapPost("/pools/{poolId}/alias-proposals/query", async (string poolId, StockSourceAliasHistoryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-alias-history");
            var page = await service.ListAliasProposalsAsync(Actor(http), LongId.Parse(poolId).Value,
                StockSourceSetupInputs.Location(request.RequestedLocation), request.Offset, request.Limit, ct);
            return TypedResults.Ok(new StockSourceAliasPageDto(page.Total.ToString(CultureInfo.InvariantCulture), [.. page.Items.Select(StockSourceAliasResponses.Detail)]));
        }).WithName("StockSourceAliasHistory");
        group.MapPost("/pools/{poolId}/alias-proposals/{id}/query", async Task<StockSourceAliasDetailDto?> (string poolId, string id,
            StockSourceAliasHistoryDto request, HttpContext http, [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-alias-history");
            if (request.Offset is < 0 or > 10000 || request.Limit is < 1 or > 100)
                throw new DomainValidationException("invalid_page", "Page offset or size is outside the supported range");
            var view = await service.ReadAliasProposalAsync(Actor(http), LongId.Parse(poolId).Value,
                StockSourceSetupInputs.Location(request.RequestedLocation), LongId.Parse(id).Value, ct);
            if (view is null) return null;
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return StockSourceAliasResponses.Detail(view);
        }).WithName("StockSourceAliasProposal");
        foreach (var (suffix, action, name) in new[]
        {
            ("approve", StockSourceSetupDecision.Approve, "ApproveStockSourceAlias"),
            ("reject", StockSourceSetupDecision.Reject, "RejectStockSourceAlias"),
            ("withdraw", StockSourceSetupDecision.Withdraw, "WithdrawStockSourceAlias")
        })
            group.MapPost("/alias-proposals/{id}/" + suffix, async (string id, StockSourceSetupDecideDto request, HttpContext http,
                [FromServices] StockSourceSetupService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
                [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) =>
            {
                try
                {
                    var result = await service.DecideAliasAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), action,
                        request.ReviewReference, new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
                    http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
                    return TypedResults.Ok(StockSourceAliasResponses.Command(result));
                }
                catch (AuthorizationDeniedException exception) when (exception.Code == "approval_revision_invalid")
                { throw new PersistenceConflictException("stock_source_alias_changed", "The channel mapping proposal changed; review it again"); }
            }).WithName(name);
        group.MapPost("/alias-operations/query", async (StockSourceAliasRecoveryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "operation-recovery");
            if (string.IsNullOrEmpty(request.Key) || request.Key.Length > 128 || request.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')))
                throw new DomainValidationException("invalid_idempotency_key", "Use a canonical request key");
            var result = await service.RecoverAliasAsync(Actor(http), request.PhysicalPoolId.Value,
                StockSourceSetupInputs.Location(request.RequestedLocation), request.Operation, request.Key, http.TraceIdentifier, ct);
            return TypedResults.Ok(new StockSourceAliasReceiptDto(result is null ? "not_confirmed" : "committed", result is null ? null : StockSourceAliasResponses.Command(result)));
        }).WithName("StockSourceAliasReceipt");
    }
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
}
