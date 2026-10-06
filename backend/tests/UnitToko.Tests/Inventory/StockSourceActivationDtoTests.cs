using System.Text.Json;
using UnitToko.Api.Inventory;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Inventory;

public sealed class StockSourceActivationDtoTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Handoff = """{"reference":"synthetic-handoff","observedAt":"2026-10-06T08:00:00.000Z","physicalPackages":"0","carriedValueMinorUnits":"0","emptyPoolConfirmed":true,"previousWriterStopped":true,"noUnrecordedStock":true}""";
    [Fact]
    public void Required_explicit_zero_handoff_rejects_missing_boolean_unknown_private_fields_numeric_tokens_and_noncanonical_time() {
        var value = JsonSerializer.Deserialize<StockSourceActivationHandoffDto>(Handoff, Json); Assert.NotNull(value);
        Assert.Equal(0L, value.ToDomain().PhysicalPackages); Assert.Equal(TimeSpan.Zero, value.ToDomain().ObservedAt.Offset);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StockSourceActivationHandoffDto>(Handoff.Replace(",\"emptyPoolConfirmed\":true", "", StringComparison.Ordinal), Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StockSourceActivationHandoffDto>(Handoff.Replace("\"physicalPackages\":\"0\"", "\"physicalPackages\":0", StringComparison.Ordinal), Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StockSourceActivationHandoffDto>(Handoff.Replace("\"reference\":", "\"actorId\":\"1\",\"reference\":", StringComparison.Ordinal), Json));
        foreach (var changed in new[] { Handoff.Replace(".000Z", "Z", StringComparison.Ordinal), Handoff.Replace(".000Z", ".000+00:00", StringComparison.Ordinal),
            Handoff.Replace("\"physicalPackages\":\"0\"", "\"physicalPackages\":\"00\"", StringComparison.Ordinal),
            Handoff.Replace("\"previousWriterStopped\":true", "\"previousWriterStopped\":false", StringComparison.Ordinal) })
            Assert.Throws<DomainValidationException>(() => JsonSerializer.Deserialize<StockSourceActivationHandoffDto>(changed, Json)!.ToDomain());
    }
}
