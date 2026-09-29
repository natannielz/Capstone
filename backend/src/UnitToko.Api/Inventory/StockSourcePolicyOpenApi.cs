using System.Text.Json.Nodes;
using Microsoft.OpenApi;
namespace UnitToko.Api.Inventory;

internal static class StockSourcePolicyOpenApi
{
    internal static void Configure(OpenApiDocument document)
    {
        var schemas = document.Components?.Schemas ?? throw new InvalidOperationException("Operating-policy schemas were not generated");
        Set(nameof(StockSourcePolicyPreviewRequestDto), "purpose", Choice("inventory-source-policy-preview"));
        Set(nameof(StockSourcePolicyReviewRequestDto), "purpose", Choice("inventory-source-policy-review"));
        foreach (var type in new[] { nameof(StockSourcePolicyReadDto), nameof(StockSourcePolicyHistoryDto) })
            Set(type, "purpose", Choice("inventory-source-policy-history"));
        Set(nameof(StockSourcePolicyRecoveryDto), "purpose", Choice("operation-recovery"));
        Set(nameof(StockSourcePolicyRecoveryDto), "operation", Choice("stock.source.policy.propose", "stock.source.policy.approve", "stock.source.policy.reject", "stock.source.policy.withdraw"));
        Set(nameof(StockSourcePolicyRecoveryDto), "key", new() { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 128,
            Pattern = @"^[A-Za-z0-9_.:-]+$(?![\s\S])" });
        Set(nameof(StockSourcePolicyReceiptDto), "state", Choice("not_confirmed", "committed"));
        foreach (var type in new[] { nameof(StockSourcePolicyReviewRequestDto), nameof(StockSourcePolicyReviewDto) })
            Set(type, "action", Choice("approve", "reject", "withdraw"));
        Set(nameof(StockSourcePolicyInputDto), "writer", Choice("unitToko", "externalSystem"));
        Set(nameof(StockSourcePolicyInputDto), "feedIntervalMilliseconds", Number(zero: true));
        foreach (var type in new[] { nameof(StockSourcePolicyCommandDto), nameof(StockSourcePolicyDetailDto) })
        {
            Set(type, "status", Choice("proposed", "approved", "rejected", "withdrawn"));
            Set(type, "proposalVersion", Choice("1"));
            Set(type, "basePolicyVersion", Number(zero: true, nullable: type == nameof(StockSourcePolicyCommandDto)));
            Set(type, "policyVersion", Number(nullable: true));
        }
        foreach (var type in new[] { nameof(StockSourcePolicyPreviewDto), nameof(StockSourcePolicyBindingDto), nameof(StockSourcePolicyDetailDto) })
        {
            Set(type, "poolVersion", Number());
            Set(type, "suspensionVersion", Number(zero: true));
            Set(type, "configurationHash", Hash());
        }
        foreach (var type in new[] { nameof(StockSourcePolicyPreviewDto), nameof(StockSourcePolicyBindingDto) })
            Set(type, "policyVersion", Number(zero: true));
        Set(nameof(StockSourcePolicyProposeDto), "expectedSuspensionVersion", Number(zero: true));
        Set(nameof(StockSourcePolicyProposeDto), "expectedPolicyVersion", Number(zero: true));
        Set(nameof(StockSourcePolicyProposeDto), "configurationHash", Hash());
        Set(nameof(StockSourcePolicyMappingDto), "aliasVersion", Number());
        Set(nameof(StockSourcePolicyMappingDto), "poolVersion", Number());
        Set(nameof(StockSourcePolicyPageDto), "total", Number(zero: true));
        Set(nameof(StockSourcePolicyHistoryDto), "offset", new() { Type = JsonSchemaType.Integer, Minimum = "0", Maximum = "10000", Default = JsonValue.Create(0) });
        Set(nameof(StockSourcePolicyHistoryDto), "limit", new() { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "100", Default = JsonValue.Create(25) });
        foreach (var path in document.Paths.Where(path => path.Key == "/api/v1/inventory/sources/pools/{poolId}/policy-proposals/{id}/query"))
            foreach (var operation in (path.Value.Operations ?? []).Values)
                if (operation.Responses?.TryGetValue("200", out var response) == true &&
                    response.Content?.TryGetValue("application/json", out var content) == true && content.Schema is { } schema)
                    content.Schema = new OpenApiSchema { AnyOf = [schema, new OpenApiSchema { Type = JsonSchemaType.Null }] };
        void Set(string type, string property, OpenApiSchema schema)
        {
            if (schemas[type].Properties is not { } properties || !properties.ContainsKey(property))
                throw new InvalidOperationException("Operating-policy property was not generated");
            properties[property] = schema;
        }
    }
    private static OpenApiSchema Number(bool zero = false, bool nullable = false) => new()
    {
        Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String,
        Pattern = zero ? @"^(0|[1-9][0-9]{0,18})$(?![\s\S])" : @"^[1-9][0-9]{0,18}$(?![\s\S])",
        MaxLength = 19, Description = "Exact decimal Int64 string; runtime range and workflow version rules also apply."
    };
    private static OpenApiSchema Hash() => new()
        { Type = JsonSchemaType.String, Pattern = @"^[0-9a-f]{64}$(?![\s\S])", MinLength = 64, MaxLength = 64 };
    private static OpenApiSchema Choice(params string[] values) => new()
        { Type = JsonSchemaType.String, Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList() };
}