using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Api.Inventory;

internal static class PackConversionExecutionOpenApi
{
    internal static void Configure(OpenApiDocument document) {
        var schemas = document.Components?.Schemas ?? throw new InvalidOperationException("Conversion execution schemas were not generated");
        Set(nameof(PackConversionExecutionReadDto), "purpose", Choice("pack-conversion-history"));
        Set(nameof(PackConversionExecutionSourcesQueryDto), "purpose", Choice("pack-conversion-sources"));
        Set(nameof(PackConversionExecutionHistoryQueryDto), "purpose", Choice("pack-conversion-history"));
        Set(nameof(PackConversionExecutionPreviewQueryDto), "purpose", Choice("pack-conversion-preview"));
        Set(nameof(PackConversionExecutionReceiptQueryDto), "purpose", Choice("operation-recovery"));
        Set(nameof(PackConversionExecutionReceiptQueryDto), "operation", Choice("inventory.pack-conversion.execute"));
        Set(nameof(PackConversionExecutionReceiptQueryDto), "key", new() { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 128, Pattern = @"^[A-Za-z0-9_.:-]+$(?![\s\S])" });
        Set(nameof(PackConversionExecutionInputDto), "steps", new() { Type = JsonSchemaType.Integer, Minimum = "1",
            Maximum = DomainValues.MaximumLineQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        foreach (var type in new[] { nameof(PackConversionExecutionSourcesQueryDto), nameof(PackConversionExecutionHistoryQueryDto) })
            Set(type, "limit", new() { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "100", Default = JsonValue.Create(25) });
        Set(nameof(PackConversionExecutionExpectedDto), "poolFence", Number(true));
        foreach (var type in new[] { nameof(PackConversionExecutionExecuteDto), nameof(PackConversionExecutionPreviewDto) })
            Set(type, "binding", new() { Type = JsonSchemaType.String, MinLength = 64, MaxLength = 64, Pattern = @"^[0-9a-f]{64}$(?![\s\S])" });
        Set(nameof(PackConversionExecutionExecuteDto), "reason", new() { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 1000 });
        foreach (var type in new[] { nameof(PackConversionExecutionSourceDto), nameof(PackConversionExecutionPreviewDto), nameof(PackConversionExecutionDetailDto) })
            Set(type, "policy", Choice(LotValuePolicy.ProportionalFloorPolicy));
        foreach (var type in new[] { nameof(PackConversionExecutionCommandDto), nameof(PackConversionExecutionDetailDto) }) {
            Set(type, "revision", Choice("1")); Set(type, "status", Choice("converted"));
            foreach (var property in new[] { "sourcePackages", "destinationPackages", "baseUnits" }) Set(type, property, Number());
            Set(type, "transferredValue", Money());
        }
        Set(nameof(PackConversionExecutionReceiptDto), "state", Choice("not_confirmed", "committed"));
        foreach (var property in new[] { "onHand", "reserved", "held", "expired" }) Set(nameof(PackConversionExecutionStockDto), property, Number(true));
        Set(nameof(PackConversionExecutionStockDto), "revision", Number());
        foreach (var property in new[] { "availableSourcePackages", "remainingPackages", "maximumSteps" }) Set(nameof(PackConversionExecutionSourceDto), property, Number(true));
        Set(nameof(PackConversionExecutionSourceDto), "sourceBalanceRevision", Number()); Set(nameof(PackConversionExecutionSourceDto), "ruleVersion", Number());
        Set(nameof(PackConversionExecutionSourceDto), "remainingValue", Money()); Set(nameof(PackConversionExecutionPreviewDto), "remainingValue", Money());
        Set(nameof(PackConversionExecutionSourcesDto), "unavailableCount", Number(true));
        foreach (var property in new[] { "remainingPackages", "sourcePackages", "destinationPackages", "baseUnits" })
            Set(nameof(PackConversionExecutionPreviewDto), property, Number());
        void Set(string type, string property, OpenApiSchema schema) {
            if (schemas[type].Properties is not { } properties || !properties.ContainsKey(property)) throw new InvalidOperationException("Conversion execution property was not generated");
            properties[property] = schema;
        }
    }
    private static OpenApiSchema Number(bool zero = false) => new() { Type = JsonSchemaType.String, MaxLength = 19,
        Pattern = zero ? @"^(0|[1-9][0-9]{0,18})$(?![\s\S])" : @"^[1-9][0-9]{0,18}$(?![\s\S])" };
    private static OpenApiSchema Money() => new() { Type = JsonSchemaType.String, MaxLength = 19, Pattern = @"^(0|[1-9][0-9]{0,14})\.[0-9]{2}$(?![\s\S])" };
    private static OpenApiSchema Choice(params string[] values) => new() { Type = JsonSchemaType.String, Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList() };
}
