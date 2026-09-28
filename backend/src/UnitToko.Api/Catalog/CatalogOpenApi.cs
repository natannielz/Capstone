using Microsoft.OpenApi;

namespace UnitToko.Api.Catalog;

internal static class CatalogOpenApi
{
    internal static void Configure(OpenApiDocument document)
    {
        // Reuse the scalar ID contract so arrays retain the same exact Int64 string bounds.
        var schemas = document.Components?.Schemas ?? throw new InvalidOperationException("Catalog schemas were not generated");
        var identifier = schemas[nameof(CatalogAssortmentVersionDto)].Properties?["id"] ??
            throw new InvalidOperationException("Catalog identifier schema was not generated");
        foreach (var (name, property) in new[]
        {
            (nameof(CatalogPublicationSaveDto), "assortmentIds"),
            (nameof(CatalogFamilyCreateDto), "imageAssetIds"),
            (nameof(CatalogFamilyEditDto), "imageAssetIds"),
            (nameof(CatalogDefinitionInputDto), "imageAssetIds")
        })
        {
            if (schemas[name].Properties?[property] is not OpenApiSchema array)
                throw new InvalidOperationException("Catalog identifier array schema was not generated");
            array.Items = identifier;
        }
        foreach (var path in document.Paths.Where(path => path.Key == "/api/v1/catalog/variants/{publicId}/query"))
            foreach (var operation in (path.Value.Operations ?? []).Values)
                if (operation.Responses?.TryGetValue("200", out var response) == true &&
                    response.Content?.TryGetValue("application/json", out var content) == true && content.Schema is { } schema)
                {
                    // A hidden/missing product is JSON null; shared DTOs and page items stay non-null.
                    content.Schema = new OpenApiSchema { AnyOf = [schema, new OpenApiSchema { Type = JsonSchemaType.Null }] };
                }
    }
}