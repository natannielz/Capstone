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

public static class StockSourceSetupEndpoints
{
    public static void MapStockSourceSetupEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/inventory/sources").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));

        group.MapPost("", async (StockSourceSetupProposeDto request, HttpContext http, [FromServices] StockSourceSetupService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) =>
        {
            var result = await service.ProposeAsync(Actor(http), request.ToDomain(),
                new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(StockSourceSetupResponses.Command(result));
        }).WithName("ProposeStockSourceSetup");

        foreach (var (suffix, action, name) in new[]
        {
            ("approve", StockSourceSetupDecision.Approve, "ApproveStockSourceSetup"),
            ("reject", StockSourceSetupDecision.Reject, "RejectStockSourceSetup"),
            ("withdraw", StockSourceSetupDecision.Withdraw, "WithdrawStockSourceSetup")
        })
        {
            group.MapPost("/{id}/" + suffix, async (string id, StockSourceSetupDecideDto request, HttpContext http,
                [FromServices] StockSourceSetupService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
                [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) =>
            {
                try
                {
                    var result = await service.DecideAsync(Actor(http), LongId.Parse(id).Value,
                        AccessRequestMetadata.ExpectedVersion(http.Request), action, request.ReviewReference,
                        new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
                    http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
                    return TypedResults.Ok(StockSourceSetupResponses.Command(result));
                }
                catch (AuthorizationDeniedException exception) when (exception.Code == "approval_revision_invalid")
                { throw new PersistenceConflictException("stock_source_revision_changed", "The proposed setup revision changed"); }
            }).WithName(name);
        }

        group.MapPost("/warehouses/query", async (StockSourceWarehouseQueryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            var choices = await service.WarehousesAsync(Actor(http), request.Purpose, http.TraceIdentifier, ct);
            return TypedResults.Ok(new StockSourceWarehousePageDto([.. choices.Select(choice =>
                new StockSourceWarehouseDto(AccessScopeDto.From(choice.Location.ToScope()), choice.OrganizationName, choice.ShopName,
                    choice.WarehouseName, choice.ChannelName))]));
        }).WithName("StockSourceWarehouseChoices");
        group.MapPost("/query", async (StockSourceSetupQueryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-setup");
            var page = await service.ListAsync(Actor(http), StockSourceSetupInputs.Location(request.Location), request.Offset, request.Limit, ct);
            return TypedResults.Ok(new StockSourceSetupPageDto(page.Total.ToString(CultureInfo.InvariantCulture),
                [.. page.Items.Select(StockSourceSetupResponses.Detail)]));
        }).WithName("StockSourceSetupHistory");
        group.MapPost("/{id}/query", async Task<StockSourceSetupDetailDto?> (string id, StockSourceSetupQueryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-setup");
            var row = await service.ReadAsync(Actor(http), StockSourceSetupInputs.Location(request.Location), LongId.Parse(id).Value, ct);
            if (row is null) return null;
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return StockSourceSetupResponses.Detail(row);
        }).WithName("StockSourceSetupRead");
        group.MapPost("/operations/query", async (StockSourceSetupRecoveryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "operation-recovery");
            if (string.IsNullOrEmpty(request.Key) || request.Key.Length > 128 ||
                request.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')))
                throw new DomainValidationException("invalid_idempotency_key", "Use a canonical request key");
            var result = await service.RecoverAsync(Actor(http), StockSourceSetupInputs.Location(request.Location),
                request.Operation, request.Key, http.TraceIdentifier, ct);
            return TypedResults.Ok(new StockSourceSetupReceiptDto(result is null ? "not_confirmed" : "committed",
                result is null ? null : StockSourceSetupResponses.Command(result)));
        }).WithName("StockSourceSetupReceipt");
    }
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ??
        throw new AuthorizationDeniedException("unauthenticated");
}
