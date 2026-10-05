using System.Text.Json;
using UnitToko.Api.Fulfillment;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Ordering;
/// <summary>UNEXECUTED isolated API contract draft; current actual routes/generated types remain frozen.</summary>
public sealed class AfterSalesReturnedLotWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Json = """{"binding":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","observedLotLabel":"LOT-000012-FISIK-PRIVAT","confirmPhysicalOriginalLotMatch":true,"lines":[{"receiptId":"9007199254740993","skuId":"9007199254740994","originalLotId":"9007199254740995","verifiedBaseUnits":"12"}],"reason":"Pencocokan manual oleh petugas independen"}""";
    [Fact]
    public void Explicit_private_physical_label_attestation_and_lossless_source_ids_map_only_to_the_immutable_manual_finding()
    {
        var dto = JsonSerializer.Deserialize<ReturnedLotVerificationRequestDto>(Json, Web)!; var input = dto.Domain();
        Assert.Equal("LOT-000012-FISIK-PRIVAT", input.VerificationReference); Assert.True(input.ConfirmPhysicalOriginalLotMatch);
        Assert.Equal(9007199254740993L, Assert.Single(input.Lines).ReceiptId); Assert.Equal(9007199254740995L, input.Lines[0].OriginalLotId); Assert.Equal(12L, input.Lines[0].VerifiedBaseUnits);
        Assert.Throws<DomainValidationException>(() => (dto with { ObservedLotLabel = " " }).Domain());
        Assert.Throws<DomainValidationException>(() => (dto with { ConfirmPhysicalOriginalLotMatch = false }).Domain());
        Assert.Throws<DomainValidationException>(() => (dto with { Lines = [] }).Domain());
        Assert.Throws<DomainValidationException>(() => (dto with { Lines = [.. dto.Lines, .. dto.Lines] }).Domain());
    }
    [Fact]
    public void Required_explicit_fields_reject_numeric_ids_null_lines_and_extra_release_money_destination_fields()
    {
        foreach (var bad in new[] {
            Json.Replace("\"receiptId\":\"9007199254740993\"", "\"receiptId\":9007199254740993"),
            Json.Replace("\"verifiedBaseUnits\":\"12\"", "\"verifiedBaseUnits\":12"),
            Json.Replace("\"observedLotLabel\":\"LOT-000012-FISIK-PRIVAT\",", ""),
            Json.Replace("\"confirmPhysicalOriginalLotMatch\":true,", ""),
            Json.Replace("\"reason\":", "\"released\":true,\"reason\":"),
            Json.Replace("\"reason\":", "\"physicalDestinationPoolId\":\"2\",\"reason\":"),
            Json.Replace("\"reason\":", "\"refundAmount\":\"100\",\"reason\":")
        }) Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ReturnedLotVerificationRequestDto>(bad, Web));
        var absent = JsonSerializer.Deserialize<ReturnedLotVerificationRequestDto>(Json.Replace("\"lines\":[{", "\"lines\":[null,{"), Web)!;
        Assert.Throws<DomainValidationException>(() => absent.Domain());
    }
    [Fact]
    public void Recovery_returns_only_exact_original_owner_result_and_quarantine_state_without_private_physical_label()
    {
        var dto = new ReturnedLotVerificationRecoveryDto("committed", new(new(9007199254740993), new(9007199254740994), new(9007199254740995), "1",
            new(2026, 10, 6, 1, 2, 3, TimeSpan.Zero), "verified_original_lot_quarantined", "fulfillment.case.return_lot_verify"));
        var json = JsonSerializer.SerializeToElement(dto, Web); var result = json.GetProperty("result");
        Assert.Equal("9007199254740993", result.GetProperty("returnId").GetString());
        Assert.Equal(new[] { "caseId", "invoiceId", "operation", "returnId", "revision", "state", "verifiedAt" }, result.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("observedLotLabel", json.GetRawText(), StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("reason", json.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }
}
