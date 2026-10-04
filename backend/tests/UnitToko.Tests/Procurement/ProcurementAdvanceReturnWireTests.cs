using System.Text.Json;
using MongoDB.Bson;
using UnitToko.Api.Procurement;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Procurement;

public sealed class ProcurementAdvanceReturnWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Legacy_advance_output_stays_exact_while_returned_capacity_has_its_own_lossless_shape()
    {
        var id = new LongId(9007199254740993L); var hash = new string('a', 64);
        ProcurementSupplierAdvanceStateDto legacy = new ProcurementSupplierAdvanceBalanceDto(id, "1", new(6000), new(4000), new(2000), new(7), hash);
        var value = JsonSerializer.SerializeToElement(legacy, Web);
        Keys(value, "settlementId", "version", "totalAmount", "appliedAmount", "remainingAmount", "lastApplicationId", "lastHash");
        Assert.Equal("9007199254740993", value.GetProperty("settlementId").GetString());
        Assert.Equal("2000.00", value.GetProperty("remainingAmount").GetString());
        ProcurementSupplierAdvanceStateDto current = new ProcurementSupplierAdvanceReturnedBalanceDto(2, id, "2", new(6000),
            new(1000), new(3000), new(2000), "return", new(8), hash);
        value = JsonSerializer.SerializeToElement(current, Web);
        Keys(value, "stateVersion", "settlementId", "version", "totalAmount", "appliedAmount", "returnedAmount", "remainingAmount", "lastEffectKind", "lastEffectId", "lastHash");
        Assert.Equal(2, value.GetProperty("stateVersion").GetInt32());
        Assert.Equal("3000.00", value.GetProperty("returnedAmount").GetString());
        Assert.Equal("8", value.GetProperty("lastEffectId").GetString());
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<ProcurementSupplierAdvanceStateDto>(value.GetRawText(), Web));
    }

    [Fact]
    public void Actual_return_input_requires_lossless_ids_money_explicit_lines_and_exact_date_without_caller_supplier()
    {
        var json = """{"location":{"organizationId":"100","shopId":"101","warehouseId":"102","channelId":"103"},"receiptId":"9007199254740993","policyId":"6","policyRevision":"1","postingDate":"2026-10-04","reason":"Explicit local review","lines":[{"settlementId":"7","amount":"0.10"},{"settlementId":"8","amount":"0.20"}]}""";
        var input = JsonSerializer.Deserialize<ProcurementAdvanceReturnInputDto>(json, Web)!;
        Assert.Equal(9007199254740993L, input.Domain().ReceiptId);
        Assert.Equal("0.30", UnitToko.Modules.Procurement.ProcurementAdvanceReturnRules.Total(input.Domain()).ToString());
        foreach (var wrong in new[] { json.Replace("\"receiptId\":\"9007199254740993\"", "\"receiptId\":9007199254740993"),
            json.Replace("\"amount\":\"0.10\"", "\"amount\":0.10"),
            json.Replace("\"reason\":", "\"supplierId\":\"1\",\"reason\":"), json.Replace("\"lines\":", "\"unexpected\":true,\"lines\":") })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProcurementAdvanceReturnInputDto>(wrong, Web));
        Assert.Throws<DomainValidationException>(() => JsonSerializer.Deserialize<ProcurementAdvanceReturnInputDto>(
            json.Replace("\"amount\":\"0.10\"", "\"amount\":\"0.101\""), Web));
        Assert.Throws<DomainValidationException>(() => (input with { PostingDate = "2026-10-4" }).Domain());
        Assert.Throws<DomainValidationException>(() => (input with { Lines = null! }).Domain());
        Assert.Throws<DomainValidationException>(() => (input with { Lines = [null!] }).Domain());
    }

    [Fact]
    public void Return_recovery_serializes_actual_result_subtype_and_preserves_null_bank_reference()
    {
        var at = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var proposed = JsonSerializer.SerializeToElement(new ProcurementAdvanceReturnRecoveryDto("confirmed",
            new ProcurementAdvanceReturnProposalResultDto(new(9007199254740993), "1", "proposed", at)), Web);
        Keys(proposed.GetProperty("result"), "proposalId", "version", "status", "recordedAt");
        var decided = JsonSerializer.SerializeToElement(new ProcurementAdvanceReturnRecoveryDto("confirmed",
            new ProcurementAdvanceReturnDecisionResultDto(new(1), new(2), "1", "returned", new(3), [new(4), new(5)], new(6), at)), Web);
        Keys(decided.GetProperty("result"), "proposalId", "decisionId", "version", "status", "receiptId", "returnLineIds", "journalId", "recordedAt");
        Assert.Equal(new[] { "4", "5" }, decided.GetProperty("result").GetProperty("returnLineIds").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("6", decided.GetProperty("result").GetProperty("journalId").GetString());
        var source = new BsonDocument { { "receiptId", 3L }, { "receiptHash", new string('a',64) }, { "receiptAmount", "3000.00" },
            { "bookedDate", "2026-10-04" }, { "bankReference", BsonNull.Value }, { "bankAccountId", 4L }, { "bankAccountVersionId", 5L },
            { "location", UnitToko.Platform.Procurement.ProcurementRequestBson.Location(ProcurementRequestTestData.Location) },
            { "supplierId", 6L }, { "supplierName", "Synthetic supplier" }, { "orderId", 7L }, { "commercialReference", "PO-0001" },
            { "bookId", 8L }, { "suspenseAccount", new BsonDocument { { "mappingKey", "unidentified_cash" }, { "accountId", 9L }, { "accountCode", "L2900" } } },
            { "lines", new BsonArray() } };
        Assert.Null(ProcurementAdvanceReturnDtos.Source(source).BankReference);
    }

    private static void Keys(JsonElement value, params string[] expected) => Assert.Equal(expected.Order(StringComparer.Ordinal),
        value.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
}
