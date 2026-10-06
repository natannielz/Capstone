using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace UnitToko.Api.Inventory;

internal static class StockSourceActivationOpenApi
{
    internal static void Configure(OpenApiDocument document) {
        var schemas = document.Components?.Schemas ?? throw new InvalidOperationException("Activation schemas were not generated");
        Set(nameof(StockSourceActivationReadDto), "purpose", Choice("inventory-source-activation-preview", "inventory-source-activation-history"));
        Set(nameof(StockSourceActivationQueryDto), "purpose", Choice("inventory-source-activation-history"));
        Set(nameof(StockSourceActivationReviewQueryDto), "purpose", Choice("inventory-source-activation-review"));
        Set(nameof(StockSourceActivationRecoveryDto), "purpose", Choice("operation-recovery"));
        Set(nameof(StockSourceActivationRecoveryDto), "operation", Choice("stock.source.activation.propose", "stock.source.activation.decide"));
        Set(nameof(StockSourceActivationRecoveryDto), "key", new() { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 128, Pattern = @"^[A-Za-z0-9_.:-]+$(?![\s\S])" });
        Set(nameof(StockSourceActivationReceiptDto), "state", Choice("not_confirmed", "committed"));
        foreach (var type in new[] { nameof(StockSourceActivationCommandDto), nameof(StockSourceActivationDetailDto) }) {
            Set(type, "status", Choice("proposed", "activated", "rejected")); Set(type, "proposalVersion", Choice("1"));
        }
        foreach (var type in new[] { nameof(StockSourceActivationReviewQueryDto), nameof(StockSourceActivationReviewDto), nameof(StockSourceActivationDecisionDto) })
            Set(type, "action", Choice("approve", "reject"));
        foreach (var property in new[] { "poolRevision", "poolFence", "suspensionRevision", "policyRevision", "activationRevision" })
            Set(nameof(StockSourceActivationExpectedDto), property, Number(property is "poolFence" or "suspensionRevision" or "activationRevision"));
        Set(nameof(StockSourceActivationExpectedDto), "configurationHash", Hash());
        foreach (var type in new[] { nameof(StockSourceActivationReviewDto), nameof(StockSourceActivationDecideDto) }) Set(type, "binding", Hash());
        Set(nameof(StockSourceActivationHandoffDto), "reference", new() { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 160 });
        Set(nameof(StockSourceActivationHandoffDto), "observedAt", Time());
        foreach (var property in new[] { "physicalPackages", "carriedValueMinorUnits" }) Set(nameof(StockSourceActivationHandoffDto), property, Choice("0"));
        foreach (var property in new[] { "emptyPoolConfirmed", "previousWriterStopped", "noUnrecordedStock" })
            Set(nameof(StockSourceActivationHandoffDto), property, new() { Type = JsonSchemaType.Boolean, Enum = [JsonValue.Create(true)!] });
        foreach (var type in new[] { nameof(StockSourceActivationProposeDto), nameof(StockSourceActivationDecideDto) })
            Set(type, "reason", new() { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 1000 });
        Set(nameof(StockSourceActivationObligationDto), "domain", Choice("Orders", "Reservations", "Fulfillment", "Procurement", "AfterSales", "SourceReconciliation", "InventoryOperations"));
        Set(nameof(StockSourceActivationObligationDto), "producerRevision", Number());
        Set(nameof(StockSourceActivationObligationDto), "complete", new() { Type = JsonSchemaType.Boolean, Enum = [JsonValue.Create(true)!] });
        Set(nameof(StockSourceActivationObligationDto), "openCount", Choice("0"));
        Set(nameof(StockSourceActivationPreviewDto), "active", new() { Type = JsonSchemaType.Boolean, Enum = [JsonValue.Create(false)!] });
        Set(nameof(StockSourceActivationPageDto), "total", Number(true));
        Set(nameof(StockSourceActivationQueryDto), "offset", new() { Type = JsonSchemaType.Integer, Minimum = "0", Maximum = "10000", Default = JsonValue.Create(0) });
        Set(nameof(StockSourceActivationQueryDto), "limit", new() { Type = JsonSchemaType.Integer, Minimum = "1", Maximum = "100", Default = JsonValue.Create(25) });
        foreach (var (type, property) in new[] { (nameof(StockSourceActivationCommandDto), "recordedAt"), (nameof(StockSourceActivationDecisionDto), "recordedAt"),
            (nameof(StockSourceActivationDetailDto), "proposedAt") }) Set(type, property, Time());
        void Set(string type, string property, OpenApiSchema schema) {
            if (schemas[type].Properties is not { } properties || !properties.ContainsKey(property)) throw new InvalidOperationException("Activation property was not generated");
            properties[property] = schema;
        }
    }
    private static OpenApiSchema Number(bool zero = false) => new() { Type = JsonSchemaType.String, MaxLength = 19,
        Pattern = zero ? @"^(0|[1-9][0-9]{0,18})$(?![\s\S])" : @"^[1-9][0-9]{0,18}$(?![\s\S])" };
    private static OpenApiSchema Hash() => new() { Type = JsonSchemaType.String, MinLength = 64, MaxLength = 64, Pattern = @"^[0-9a-f]{64}$(?![\s\S])" };
    private static OpenApiSchema Time() => new() { Type = JsonSchemaType.String, Format = "date-time", MinLength = 24, MaxLength = 24,
        Pattern = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$(?![\s\S])" };
    private static OpenApiSchema Choice(params string[] values) => new() { Type = JsonSchemaType.String,
        Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList() };
}
