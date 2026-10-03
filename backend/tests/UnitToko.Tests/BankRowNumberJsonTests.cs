using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using UnitToko.Api.Billing;
using UnitToko.Api.Procurement;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests;

public sealed class BankRowNumberJsonTests
{
    [Theory]
    [InlineData(nameof(ProcurementSettlementInputDto), "bankRowNumber", "bankImportId")]
    [InlineData(nameof(RefundSettlementInputDto), "bankRowNumber", "bankImportId")]
    [InlineData(nameof(BankReceiptPreviewInputDto), "rowNumber", "importId")]
    [InlineData(nameof(BankReceiptConfirmDto), "rowNumber", "importId")]
    public void Web_binding_accepts_integer_bank_rows_but_rejects_quoted_numbers(string contract, string rowProperty, string importProperty)
    {
        // Match Minimal API Web defaults and Program's unmapped-member setting. LongId
        // and Money retain their actual type-level converters; do not make globals strict.
        var options = new JsonOptions().SerializerOptions;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        Assert.True(options.NumberHandling.HasFlag(JsonNumberHandling.AllowReadingFromString));
        var importId = new LongId(9007199254740993);
        object input = contract switch {
            nameof(ProcurementSettlementInputDto) => new ProcurementSettlementInputDto(new(8301), "1", importId, 2,
                new("BNI", "0000123456789", "Synthetic supplier", "IDR"), "Synthetic beneficiary verification", "Synthetic debit verification"),
            nameof(RefundSettlementInputDto) => new RefundSettlementInputDto(importId, 2,
                "Synthetic beneficiary verification", "Synthetic debit verification"),
            nameof(BankReceiptPreviewInputDto) => new BankReceiptPreviewInputDto(importId, 2),
            nameof(BankReceiptConfirmDto) => new BankReceiptConfirmDto(importId, 2, new string('a', 64),
                new("0000123456789", "0001", "2026-10-03", new(6000m)), "Synthetic independent source", true, "Synthetic review"),
            _ => throw new ArgumentOutOfRangeException(nameof(contract))
        };
        var type = input.GetType();
        var json = JsonSerializer.SerializeToNode(input, type, options)!.AsObject();
        Assert.Equal(2, json[rowProperty]!.GetValue<int>());
        Assert.Equal("9007199254740993", json[importProperty]!.GetValue<string>());
        Assert.Equal(input, JsonSerializer.Deserialize(json.ToJsonString(), type, options));

        json[rowProperty] = "2";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json.ToJsonString(), type, options));
    }
}
