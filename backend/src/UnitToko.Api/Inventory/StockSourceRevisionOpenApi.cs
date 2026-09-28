using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace UnitToko.Api.Inventory;

internal static class StockSourceRevisionOpenApi
{
    internal static void Configure(OpenApiDocument document)
    {
        var schemas = document.Components?.Schemas ?? throw new InvalidOperationException("Source revision schemas were not generated");
        Set(nameof(StockSourceCurrentQueryDto), "purpose", Choice("inventory-source-current"));
        Set(nameof(StockSourceRevisionQueryDto), "purpose", Choice("inventory-source-revisions"));
        Set(nameof(StockSourceRevisionReadDto), "purpose", Choice("inventory-source-revisions"));
        Set(nameof(StockSourceRevisionRecoveryDto), "purpose", Choice("operation-recovery"));
        Set(nameof(StockSourceRevisionRecoveryDto), "operation", Choice("stock.source.revision.propose", "stock.source.revision.approve", "stock.source.revision.reject", "stock.source.revision.withdraw"));
        Set(nameof(StockSourceRevisionReceiptDto), "state", Choice("not_confirmed", "committed"));
        Set(nameof(StockSourceCurrentDto), "poolState", Choice("inactive", "active"));
        Set(nameof(StockSourceCurrentDto), "poolVersion", Number());
        foreach (var type in new[] { nameof(StockSourceRevisionCommandDto), nameof(StockSourceRevisionDetailDto) })
        {
            Set(type, "status", Choice("proposed", "approved", "rejected", "withdrawn"));
            Set(type, "proposalVersion", Choice("1"));
            Set(type, "basePoolVersion", Number(nullable: type == nameof(StockSourceRevisionCommandDto)));
            Set(type, "appliedPoolVersion", Number(nullable: true));
        }
        Set(nameof(StockSourceSettingsDto), "mode", Choice("webLedger", "externalLedger"));
        Set(nameof(StockSourceSettingsDto), "control", Choice("exclusiveWebAllocation", "sharedWebLedger", "externalHold"));
        Set(nameof(StockSourceSettingsDto), "maximumAgeMilliseconds", Number());
        Set(nameof(StockSourceSettingsDto), "safetyBufferBaseUnits", Number(zero: true));
        Set(nameof(StockSourceCurrentPageDto), "total", Number(zero: true));
        Set(nameof(StockSourceRevisionPageDto), "total", Number(zero: true));
        foreach (var path in document.Paths.Where(path => path.Key == "/api/v1/inventory/sources/revisions/{id}/query"))
            foreach (var operation in (path.Value.Operations ?? []).Values)
                if (operation.Responses?.TryGetValue("200", out var response) == true &&
                    response.Content?.TryGetValue("application/json", out var content) == true && content.Schema is { } schema)
                    content.Schema = new OpenApiSchema { AnyOf = [schema, new OpenApiSchema { Type = JsonSchemaType.Null }] };
        void Set(string type, string property, OpenApiSchema schema)
        {
            if (schemas[type].Properties is not { } properties || !properties.ContainsKey(property))
                throw new InvalidOperationException("Source revision property was not generated");
            properties[property] = schema;
        }
    }
    private static OpenApiSchema Number(bool zero = false, bool nullable = false) => new()
    {
        Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String,
        Pattern = zero ? @"^(0|[1-9][0-9]{0,18})$(?![\s\S])" : @"^[1-9][0-9]{0,18}$(?![\s\S])",
        MaxLength = 19, Description = "Exact decimal Int64 string; runtime range and workflow version rules also apply."
    };
    private static OpenApiSchema Choice(params string[] values) => new()
        { Type = JsonSchemaType.String, Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList() };
}
