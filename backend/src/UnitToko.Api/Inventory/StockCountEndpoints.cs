using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Api.Catalog;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;

namespace UnitToko.Api.Inventory;

public static class StockCountEndpoints
{
    public static void MapStockCountEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/inventory/stock-counts")
            .RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));

        group.MapPost("/warehouses/query", async (StockCountReadDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, CancellationToken ct) =>
        {
            var purpose = request.Purpose switch
            {
                "inventory-stock-count" => StockCountChoicePurpose.Count,
                "inventory-stock-history" => StockCountChoicePurpose.History,
                _ => throw new DomainValidationException("invalid_read_purpose", "Choose counting or history")
            };
            return TypedResults.Ok(StockCountResponses.Warehouses(await service.StockWarehouseOptionsAsync(Actor(http), purpose,
                new(request.Purpose, http.TraceIdentifier), ct)));
        }).WithName("StockCountWarehouses");
        group.MapPost("/packages/query", async (StockCountPackageQueryDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, CancellationToken ct) =>
        {
            Purpose(request.Purpose, "inventory-stock-count");
            return TypedResults.Ok(StockCountResponses.Packages(await service.CountablePackagesAsync(Actor(http),
                StockCountInputs.Location(request.Location), request.Offset, request.Limit, request.Search,
                new(request.Purpose, http.TraceIdentifier), ct)));
        }).WithName("StockCountPackages");
        group.MapPost("/sources/query", async (StockCountSourceQueryDto request, HttpContext http,
            [FromServices] StockCountService service, CancellationToken ct) =>
        {
            Purpose(request.Purpose, "inventory-stock-count");
            return TypedResults.Ok(StockCountResponses.Sources(await service.SourcesAsync(Actor(http),
                StockCountInputs.Location(request.Location), request.Offset, request.Limit, ct)));
        }).WithName("StockCountSources");
        group.MapPost("", async (StockCountRecordDto request, HttpContext http,
            [FromServices] StockCountService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) =>
        {
            var reason = request.Reason?.Trim();
            if (!StockCountPolicy.CanonicalText(reason, 1000))
                throw new DomainValidationException("invalid_reason", "A bounded count reason is required");
            var result = await service.RecordCountAsync(Actor(http), request.ToDomain(),
                new(AccessRequestMetadata.IdempotencyKey(http.Request), reason!, http.TraceIdentifier), ct,
                OperationalScanSelectionDto.DomainAll(request.ScanSelections));
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(StockCountResponses.Observation(result));
        }).WithName("RecordStockCount");
        group.MapPost("/query", async (StockCountPageQueryDto request, HttpContext http,
            [FromServices] StockCountService service, CancellationToken ct) =>
        {
            Purpose(request.Purpose, "inventory-stock-history");
            return TypedResults.Ok(StockCountResponses.Page(await service.ListAsync(Actor(http),
                StockCountInputs.Location(request.Location), request.Offset, request.Limit, ct)));
        }).WithName("StockCountHistory");
        group.MapPost("/{id}/query", async Task<StockCountObservationDto?> (string id, StockCountReadDto request,
            HttpContext http, [FromServices] StockCountService service, CancellationToken ct) =>
        {
            Purpose(request.Purpose, "inventory-stock-history");
            var result = await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct);
            if (result is null) return null;
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return StockCountResponses.Observation(result);
        }).WithName("StockCountRead");
        group.MapPost("/operations/query", async (StockCountReceiptQueryDto request, HttpContext http,
            [FromServices] StockCountService service, CancellationToken ct) =>
        {
            Purpose(request.Purpose, "operation-recovery");
            if (request.Operation != "inventory.stock-count.record")
                throw new DomainValidationException("invalid_operation", "Choose stock-count recording");
            if (string.IsNullOrEmpty(request.Key) || request.Key.Length > 128 ||
                request.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')))
                throw new DomainValidationException("invalid_idempotency_key", "Use a canonical request key");
            return TypedResults.Ok(StockCountResponses.Receipt(await service.RecoverAsync(Actor(http), request.Key, ct)));
        }).WithName("StockCountReceipt");
    }
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ??
        throw new AuthorizationDeniedException("unauthenticated");
    private static void Purpose(string actual, string expected)
    {
        if (actual != expected) throw new DomainValidationException("invalid_read_purpose", "Use the required stock-count purpose");
    }
}
