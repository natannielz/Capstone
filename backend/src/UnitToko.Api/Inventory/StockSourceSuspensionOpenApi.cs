using System.Text.Json.Nodes;
using Microsoft.OpenApi;
namespace UnitToko.Api.Inventory;

internal static class StockSourceSuspensionOpenApi
{
    internal static void Configure(OpenApiDocument document)
    {
        var schemas = document.Components?.Schemas ?? throw new InvalidOperationException("Source suspension schemas were not generated");
        Set(nameof(StockSourceSuspensionPreviewRequestDto), "purpose", Choice("inventory-source-suspension-preview"));
        Set(nameof(StockSourceSuspensionReadDto), "purpose", Choice("inventory-source-suspension-history"));
        Set(nameof(StockSourceSuspensionHistoryDto), "purpose", Choice("inventory-source-suspension-history"));
        Set(nameof(StockSourceSuspensionRecoveryDto), "purpose", Choice("operation-recovery"));
        Set(nameof(StockSourceSuspensionRecoveryDto), "operation", Choice("stock.source.suspend"));
        Set(nameof(StockSourceSuspensionReceiptDto), "state", Choice("not_confirmed", "committed"));
        Set(nameof(StockSourceSuspensionCommandDto), "status", Choice("suspended"));
        Set(nameof(StockSourceSuspensionRequestDto), "expectedSuspensionVersion", Number(zero: true));
        Set(nameof(StockSourceSuspensionPageDto), "total", Number(zero: true));
        foreach (var type in new[] { nameof(StockSourceSuspensionPreviewDto), nameof(StockSourceSuspensionCommandDto), nameof(StockSourceSuspensionDetailDto) })
        {
            Set(type, "poolVersion", Number());
            Set(type, "suspensionVersion", Number(zero: type == nameof(StockSourceSuspensionPreviewDto)));
        }
        foreach (var type in new[] { nameof(StockSourceSuspensionPreviewDto), nameof(StockSourceSuspensionRequestDto) })
            Set(type, "configurationHash", new OpenApiSchema { Type = JsonSchemaType.String, Pattern = @"^[0-9a-f]{64}$(?![\s\S])", MinLength = 64, MaxLength = 64 });
        foreach (var path in document.Paths.Where(path => path.Key is "/api/v1/inventory/sources/pools/{poolId}/suspension/query" or "/api/v1/inventory/sources/pools/{poolId}/suspensions/{id}/query"))
            foreach (var operation in (path.Value.Operations ?? []).Values)
                if (operation.Responses?.TryGetValue("200", out var response) == true &&
                    response.Content?.TryGetValue("application/json", out var content) == true && content.Schema is { } schema)
                    content.Schema = new OpenApiSchema { AnyOf = [schema, new OpenApiSchema { Type = JsonSchemaType.Null }] };
        void Set(string type, string property, OpenApiSchema schema)
        {
            if (schemas[type].Properties is not { } properties || !properties.ContainsKey(property))
                throw new InvalidOperationException("Source suspension property was not generated");
            properties[property] = schema;
        }
    }
    private static OpenApiSchema Number(bool zero = false) => new()
    {
        Type = JsonSchemaType.String,
        Pattern = zero ? @"^(0|[1-9][0-9]{0,18})$(?![\s\S])" : @"^[1-9][0-9]{0,18}$(?![\s\S])",
        MaxLength = 19, Description = "Exact decimal Int64 string; runtime range and workflow version rules also apply."
    };
    private static OpenApiSchema Choice(params string[] values) => new()
        { Type = JsonSchemaType.String, Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList() };
}
