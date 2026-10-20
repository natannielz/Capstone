using System.Collections.Immutable;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;

namespace UnitToko.Api.Catalog;

public sealed record CatalogVariantSkuQueryDto([property: System.Text.Json.Serialization.JsonRequired] string[] SkuIds);
public sealed record CatalogBuyerVariantListDto(CatalogBuyerVariantDto[] Items);

public static class CatalogCartEndpoints
{
    /// <summary>The cart's read of its own packages: current names, package facts and prices, so a changed price is shown and confirmed, never replaced silently.</summary>
    public static void MapCatalogCartEndpoints(this WebApplication app)
    {
        var buyer = app.MapGroup("/api/v1/catalog/variants").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            buyer.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        buyer.MapPost("/by-sku/query", async (CatalogVariantSkuQueryDto request, HttpContext http, [FromServices] CatalogDefinitionQueryService service, CancellationToken ct) =>
        {
            if (request.SkuIds is null || request.SkuIds.Length is 0 or > 100)
                throw new DomainValidationException("invalid_cart_sku_query", "Choose up to 100 distinct packages");
            var ids = request.SkuIds.Select(value => DomainValues.ParseId(value)).ToImmutableArray();
            var actor = http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
            var items = await service.ReadBuyerSkusAsync(actor, ids, ct);
            return TypedResults.Ok(new CatalogBuyerVariantListDto([.. items.Select(CatalogBuyerIdentifierResponses.From)]));
        }).WithName("CatalogBuyerVariantsBySku");
    }
}
