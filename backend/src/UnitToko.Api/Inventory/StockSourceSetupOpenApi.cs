using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace UnitToko.Api.Inventory;

internal static class StockSourceSetupOpenApi
{
    internal static void Configure(OpenApiDocument document)
    {
        var schemas = document.Components?.Schemas ?? throw new InvalidOperationException("Source setup schemas were not generated");
        foreach (var name in new[] { nameof(StockSourceSetupProposeDto), nameof(StockSourceSetupDetailDto) })
        {
            Set(name, "maximumAgeMilliseconds", new() { Type = JsonSchemaType.String, Pattern = "^[1-9][0-9]{0,18}$",
                Description = "Exact positive Int64 milliseconds; the service additionally checks the supported TimeSpan range." });
            Set(name, "safetyBufferBaseUnits", new() { Type = JsonSchemaType.String, Pattern = "^(0|[1-9][0-9]{0,18})$",
                Description = "Exact nonnegative Int64 base-unit quantity." });
            Set(name, "mode", Choice("webLedger", "externalLedger"));
            Set(name, "control", Choice("exclusiveWebAllocation", "sharedWebLedger", "externalHold"));
        }
        Set(nameof(StockSourceSetupRecoveryDto), "operation", Choice("stock.source.propose", "stock.source.approve", "stock.source.reject", "stock.source.withdraw"));
        foreach (var name in new[] { nameof(StockSourceSetupCommandDto), nameof(StockSourceSetupDetailDto) })
            Set(name, "status", Choice("proposed", "approved", "rejected", "withdrawn"));
        Set(nameof(StockSourceSetupDecisionDto), "status", Choice("approved", "rejected", "withdrawn"));
        Set(nameof(StockSourceSetupReceiptDto), "state", Choice("not_confirmed", "committed"));
        foreach (var path in document.Paths.Where(path => path.Key == "/api/v1/inventory/sources/{id}/query"))
            foreach (var operation in (path.Value.Operations ?? []).Values)
                if (operation.Responses?.TryGetValue("200", out var response) == true &&
                    response.Content?.TryGetValue("application/json", out var content) == true && content.Schema is { } schema)
                    content.Schema = new OpenApiSchema { AnyOf = [schema, new OpenApiSchema { Type = JsonSchemaType.Null }] };

        void Set(string name, string property, OpenApiSchema schema)
        {
            if (schemas[name].Properties is not { } properties || !properties.ContainsKey(property))
                throw new InvalidOperationException("Source setup property was not generated");
            properties[property] = schema;
        }
    }
    private static OpenApiSchema Choice(params string[] values) => new()
        { Type = JsonSchemaType.String, Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList() };
}
