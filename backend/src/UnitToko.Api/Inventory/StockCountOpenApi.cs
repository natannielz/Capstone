using Microsoft.OpenApi;

namespace UnitToko.Api.Inventory;

internal static class StockCountOpenApi
{
    internal static void Configure(OpenApiDocument document)
    {
        foreach (var path in document.Paths.Where(path => path.Key == "/api/v1/inventory/stock-counts/{id}/query"))
            foreach (var operation in (path.Value.Operations ?? []).Values)
                if (operation.Responses?.TryGetValue("200", out var response) == true &&
                    response.Content?.TryGetValue("application/json", out var content) == true && content.Schema is { } schema)
                    content.Schema = new OpenApiSchema { AnyOf = [schema, new OpenApiSchema { Type = JsonSchemaType.Null }] };
    }
}
