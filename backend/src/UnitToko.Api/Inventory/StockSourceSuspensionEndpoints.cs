using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
namespace UnitToko.Api.Inventory;

public static class StockSourceSuspensionEndpoints
{
    public static void MapStockSourceSuspensionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/inventory/sources").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/pools/{poolId}/suspension-preview/query", async (string poolId, StockSourceSuspensionPreviewRequestDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-suspension-preview");
            var row = await service.PreviewSuspensionAsync(Actor(http), LongId.Parse(poolId).Value, ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row["poolRevision"].AsInt64);
            return TypedResults.Ok(StockSourceSuspensionResponses.Preview(row));
        }).WithName("StockSourceSuspensionPreview");
        group.MapPost("/pools/{poolId}/suspend", async (string poolId, StockSourceSuspensionRequestDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) =>
        {
            var expected = new StockSourceSuspensionExpectation(AccessRequestMetadata.ExpectedVersion(http.Request),
                StockSourceSetupInputs.Number(request.ExpectedSuspensionVersion, true), request.ConfigurationHash);
            expected.Validate();
            var row = await service.SuspendAsync(Actor(http), LongId.Parse(poolId).Value, expected,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
            return TypedResults.Ok(StockSourceSuspensionResponses.Command(row));
        }).WithName("SuspendStockSource");
        group.MapPost("/pools/{poolId}/suspension/query", async Task<StockSourceSuspensionDetailDto?> (string poolId, StockSourceSuspensionReadDto request,
            HttpContext http, [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-suspension-history");
            var row = await service.ReadSuspensionAsync(Actor(http), LongId.Parse(poolId).Value, ct);
            return row is null ? null : StockSourceSuspensionResponses.Detail(row);
        }).WithName("StockSourceSuspensionLatest");
        group.MapPost("/pools/{poolId}/suspensions/query", async (string poolId, StockSourceSuspensionHistoryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-suspension-history");
            var page = await service.ListSuspensionsAsync(Actor(http), LongId.Parse(poolId).Value, request.Offset, request.Limit, ct);
            return TypedResults.Ok(new StockSourceSuspensionPageDto(page.Total.ToString(CultureInfo.InvariantCulture), [.. page.Items.Select(StockSourceSuspensionResponses.Detail)]));
        }).WithName("StockSourceSuspensionHistory");
        group.MapPost("/pools/{poolId}/suspensions/{id}/query", async Task<StockSourceSuspensionDetailDto?> (string poolId, string id,
            StockSourceSuspensionReadDto request, HttpContext http, [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-suspension-history");
            var row = await service.ReadSuspensionDetailAsync(Actor(http), LongId.Parse(poolId).Value, LongId.Parse(id).Value, ct);
            return row is null ? null : StockSourceSuspensionResponses.Detail(row);
        }).WithName("StockSourceSuspensionDetail");
        group.MapPost("/suspension-operations/query", async (StockSourceSuspensionRecoveryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "operation-recovery");
            if (request.Operation != "stock.source.suspend" || string.IsNullOrEmpty(request.Key) || request.Key.Length > 128 ||
                request.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')))
                throw new DomainValidationException("invalid_stock_source_suspension_recovery", "Use the original suspension command reference");
            var row = await service.RecoverSuspensionAsync(Actor(http), request.PhysicalPoolId.Value, request.Key, http.TraceIdentifier, ct);
            return TypedResults.Ok(new StockSourceSuspensionReceiptDto(row is null ? "not_confirmed" : "committed", row is null ? null : StockSourceSuspensionResponses.Command(row)));
        }).WithName("StockSourceSuspensionReceipt");
    }
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
}