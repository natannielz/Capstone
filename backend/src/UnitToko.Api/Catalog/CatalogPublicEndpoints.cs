using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Catalog;

namespace UnitToko.Api.Catalog;

public sealed record PublicCatalogAttributeDto(string Key, string Value);
/// <summary>One package of a guest product. There is deliberately no price, stock, owner or internal id.</summary>
public sealed record PublicCatalogPackageDto(string PublicId, string Code, string PackageLabel, string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage,
    PublicCatalogAttributeDto[] Attributes, int MinimumOrderPackages, int OrderIncrementPackages);
/// <summary>What anyone may see before signing in: one sample product and the packages it publishes now. No price, stock, owner or internal id.</summary>
public sealed record PublicCatalogItemDto(string PublicId, string Name, string Description, string CategoryCode, string CategoryName,
    PublicCatalogPackageDto[] Packages);
public sealed record PublicCatalogCategoryDto(string Code, string Name, string Count);
public sealed record PublicCatalogPageDto(string Total, PublicCatalogItemDto[] Items, PublicCatalogCategoryDto[] Categories);

public static class CatalogPublicEndpoints
{
    public static void MapCatalogPublicEndpoints(this WebApplication app)
    {
        // Anonymous and read-only: no session, lease or audit is involved, so traffic causes no writes.
        var open = app.MapGroup("/api/v1/public/catalog").AllowAnonymous();
        foreach (var status in new[] { 400, 404, 503 })
            open.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        open.MapGet("", async (HttpContext http, [FromServices] CatalogDefinitionQueryService service, [FromQuery] string? q, [FromQuery] string? kategori,
            [FromQuery] string? urut, [FromQuery] int? offset, [FromQuery] int? limit, CancellationToken ct) =>
        {
            var sort = urut switch { null or "" or "terbaru" => PublicCatalogSort.Added, "nama" => PublicCatalogSort.Name,
                _ => throw new DomainValidationException("public_catalog_query_invalid", "Choose a supported order") };
            var page = await service.QueryPublicAsync(offset ?? 0, limit ?? 24, q, kategori, sort, ct);
            Uncached(http);
            return TypedResults.Ok(new PublicCatalogPageDto(page.Total.ToString(CultureInfo.InvariantCulture), [.. page.Items.Select(Item)],
                [.. page.Categories.Select(item => new PublicCatalogCategoryDto(item.Code, item.Name, item.Count.ToString(CultureInfo.InvariantCulture)))]));
        }).WithName("PublicCatalogQuery");
        open.MapGet("/{publicId}", async Task<Microsoft.AspNetCore.Http.HttpResults.Results<Microsoft.AspNetCore.Http.HttpResults.Ok<PublicCatalogItemDto>,
            Microsoft.AspNetCore.Http.HttpResults.NotFound>> (string publicId, HttpContext http, [FromServices] CatalogDefinitionQueryService service, CancellationToken ct) =>
        {
            var item = await service.ReadPublicItemAsync(publicId, ct);
            Uncached(http);
            return item is null ? TypedResults.NotFound() : TypedResults.Ok(Item(item));
        }).WithName("PublicCatalogRead");
    }

    private static PublicCatalogItemDto Item(PublicCatalogItem item) => new(item.PublicId, item.Name, item.Description, item.CategoryCode, item.CategoryName,
        [.. item.Packages.Select(package => new PublicCatalogPackageDto(package.PublicId, package.Code, package.PackageLabel, package.PackageKind, package.BaseUnitCode,
            package.BaseUnitsPerPackage, [.. package.Attributes.Select(pair => new PublicCatalogAttributeDto(pair.Key, pair.Value))],
            package.MinimumOrderPackages, package.OrderIncrementPackages))]);
    /// <summary>A withdrawn sample must disappear on the very next request, so no browser or shared cache may keep a guest answer.
    /// no-store matches every other API response and leaves no stale copy anywhere to revalidate.</summary>
    private static void Uncached(HttpContext http) => http.Response.Headers.CacheControl = "no-store";
}
