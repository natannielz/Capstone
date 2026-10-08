using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;

namespace UnitToko.Api.Catalog;

public sealed record CatalogBulkSkuQueryDto([property: JsonRequired] IReadOnlyList<string>? Codes);

public static class CatalogBulkEndpoints
{
    public static void MapCatalogBulkEndpoints(this WebApplication app)
    {
        var endpoint = app.MapPost("/api/v1/catalog/variants/exact-skus/query", async (CatalogBulkSkuQueryDto request, HttpContext http,
            [FromServices] CatalogDefinitionQueryService service, CancellationToken ct) => {
            if (request.Codes is null || request.Codes.Count is < 1 or > 100)
                throw new DomainValidationException("invalid_bulk_sku_query", "Choose up to 100 distinct internal SKU codes");
            var items = await service.ReadExactSkuCodesAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Codes.ToImmutableArray(), ct);
            return TypedResults.Ok(items.Select(CatalogResponses.From).ToArray());
        }).RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>()
            .WithName("CatalogPublishedExactSkuQuery");
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            endpoint.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
    }
}
