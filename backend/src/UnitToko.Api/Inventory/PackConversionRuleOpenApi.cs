using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using UnitToko.Modules.Contracts;

namespace UnitToko.Api.Inventory;

internal static class PackConversionRuleOpenApi
{
    internal static void Configure(OpenApiDocument document) {
        var schemas = document.Components?.Schemas ?? throw new InvalidOperationException("Conversion rule schemas were not generated");
        Set(nameof(PackConversionReadDto), "purpose", Choice("pack-conversion-rule-warehouses", "pack-conversion-rule-history"));
        Set(nameof(PackConversionPackageQueryDto), "purpose", Choice("pack-conversion-rule-source", "pack-conversion-rule-destination"));
        Set(nameof(PackConversionPackageQueryDto), "role", Choice("source", "destination"));
        Set(nameof(PackConversionPageQueryDto), "purpose", Choice("pack-conversion-rule-history"));
        Set(nameof(PackConversionPreviewQueryDto), "purpose", Choice("pack-conversion-rule-preview"));
        Set(nameof(PackConversionReviewQueryDto), "purpose", Choice("pack-conversion-rule-review"));
        Set(nameof(PackConversionAdmissionQueryDto), "purpose", Choice("pack-conversion-admission"));
        Set(nameof(PackConversionReceiptQueryDto), "purpose", Choice("operation-recovery"));
        Set(nameof(PackConversionReceiptQueryDto), "operation", Choice("inventory.pack-conversion.rule.propose", "inventory.pack-conversion.rule.decide"));
        Set(nameof(PackConversionReceiptQueryDto), "key", new() { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 128, Pattern = @"^[A-Za-z0-9_.:-]+$(?![\s\S])" });
        Set(nameof(PackConversionReceiptDto), "state", Choice("not_confirmed", "committed"));
        Set(nameof(PackConversionCommandDto), "state", Choice("pending", "approve", "reject"));
        Set(nameof(PackConversionDetailDto), "status", Choice("pending", "approved", "rejected"));
        foreach (var type in new[] { nameof(PackConversionReviewQueryDto), nameof(PackConversionDecisionDto), nameof(PackConversionReviewDto) })
            Set(type, "action", Choice("approve", "reject"));
        foreach (var type in new[] { nameof(PackConversionProposeDto), nameof(PackConversionDecideDto), nameof(PackConversionPreviewDto), nameof(PackConversionReviewDto) })
            Set(type, "binding", new() { Type = JsonSchemaType.String, MinLength = 64, MaxLength = 64, Pattern = @"^[0-9a-f]{64}$(?![\s\S])" });
        foreach (var type in new[] { nameof(PackConversionProposeDto), nameof(PackConversionDecideDto) })
            Set(type, "reason", new() { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 1000 });
        foreach (var property in new[] { "sourcePackagesPerStep", "destinationPackagesPerStep" })
            Set(nameof(PackConversionRuleInputDto), property, Quantity());
        Set(nameof(PackConversionAdmissionQueryDto), "steps", Quantity());
        Set(nameof(PackConversionPackageQueryDto), "offset", new() { Type = JsonSchemaType.Integer, Minimum = "0", Maximum = "10000", Default = JsonValue.Create(0) });
        Set(nameof(PackConversionPackageQueryDto), "limit", new() { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "100", Default = JsonValue.Create(25) });
        Set(nameof(PackConversionPackageQueryDto), "search", new() { Type = JsonSchemaType.String | JsonSchemaType.Null, MaxLength = 100 });
        foreach (var type in new[] { nameof(PackConversionCommandDto), nameof(PackConversionDetailDto) }) Set(type, "revision", Choice("1"));
        Set(nameof(PackConversionRuleDto), "version", Choice("1"));
        Set(nameof(PackConversionAdmissionDto), "ruleVersion", Number());
        Set(nameof(PackConversionAdmissionDto), "state", Choice("blocked"));
        foreach (var property in new[] { "sourcePackages", "destinationPackages", "baseUnits" }) {
            Set(nameof(PackConversionPreviewDto), property, Number()); Set(nameof(PackConversionAdmissionDto), property, Number());
        }
        Set(nameof(PackConversionAdmissionDto), "availableSourcePackages", Number(true));
        foreach (var type in new[] { nameof(PackConversionPreviewDto), nameof(PackConversionCommandDto), nameof(PackConversionDetailDto),
            nameof(PackConversionReviewDto), nameof(PackConversionProposalPageDto), nameof(PackConversionRuleDto), nameof(PackConversionRulePageDto), nameof(PackConversionAdmissionDto) })
            Set(type, "physicalConversionAvailable", new() { Type = JsonSchemaType.Boolean, Enum = [JsonValue.Create(false)!] });
        void Set(string type, string property, OpenApiSchema schema) {
            if (schemas[type].Properties is not { } properties || !properties.ContainsKey(property))
                throw new InvalidOperationException("Conversion rule property was not generated");
            properties[property] = schema;
        }
    }
    private static OpenApiSchema Quantity() => new() { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = DomainValues.MaximumLineQuantity.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    private static OpenApiSchema Number(bool zero = false) => new() { Type = JsonSchemaType.String,
        Pattern = zero ? @"^(0|[1-9][0-9]{0,18})$(?![\s\S])" : @"^[1-9][0-9]{0,18}$(?![\s\S])", MaxLength = 19 };
    private static OpenApiSchema Choice(params string[] values) => new() { Type = JsonSchemaType.String,
        Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList() };
}
