using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace UnitToko.Api.Inventory;

internal static class StockSourceAliasOpenApi
{
    internal static void Configure(OpenApiDocument document)
    {
        var schemas = document.Components?.Schemas ?? throw new InvalidOperationException("Source alias schemas were not generated");
        Set(nameof(StockSourceAliasReadDto), "purpose", Choice("inventory-source-alias"));
        Set(nameof(StockSourceAliasHistoryDto), "purpose", Choice("inventory-source-alias-history"));
        Set(nameof(StockSourceAliasRecoveryDto), "purpose", Choice("operation-recovery"));
        Set(nameof(StockSourceAliasRecoveryDto), "operation", Choice("stock.source.alias.propose", "stock.source.alias.approve", "stock.source.alias.reject", "stock.source.alias.withdraw"));
        Set(nameof(StockSourceAliasReceiptDto), "state", Choice("not_confirmed", "committed"));
        Set(nameof(StockSourceAliasCurrentDto), "bindingState", Choice("absent", "disabled", "stale", "current"));
        Set(nameof(StockSourceAliasProposeDto), "expectedAliasVersion", Number(zero: true));
        Set(nameof(StockSourceAliasPageDto), "total", Number(zero: true));
        foreach (var type in new[] { nameof(StockSourceAliasCommandDto), nameof(StockSourceAliasDetailDto) })
        {
            Set(type, "status", Choice("proposed", "approved", "rejected", "withdrawn"));
            Set(type, "proposalVersion", Choice("1"));
            Set(type, "poolVersion", Number());
            Set(type, "baseAliasVersion", Number(zero: true, nullable: type == nameof(StockSourceAliasCommandDto)));
            Set(type, "appliedAliasVersion", Number(nullable: true));
        }
        foreach (var path in document.Paths.Where(path => path.Key == "/api/v1/inventory/sources/pools/{poolId}/alias-proposals/{id}/query"))
            foreach (var operation in (path.Value.Operations ?? []).Values)
                if (operation.Responses?.TryGetValue("200", out var response) == true &&
                    response.Content?.TryGetValue("application/json", out var content) == true && content.Schema is { } schema)
                    content.Schema = new OpenApiSchema { AnyOf = [schema, new OpenApiSchema { Type = JsonSchemaType.Null }] };
        void Set(string type, string property, OpenApiSchema schema)
        {
            if (schemas[type].Properties is not { } properties || !properties.ContainsKey(property))
                throw new InvalidOperationException("Source alias property was not generated");
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
