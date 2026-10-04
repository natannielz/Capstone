using System.Text.Json;
using UnitToko.Api.Procurement;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Procurement;

namespace UnitToko.Tests.Procurement;

public sealed class ProcurementSupplierPayableDomainTests
{
    [Fact]
    public void Web_output_unions_preserve_exact_subtype_fields_and_lossless_ids_and_money_without_discriminators()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var id = new LongId(9007199254740993L);
        var location = new ProcurementLocationDto(new(1), new(2), new(3), new(4));
        var account = new ProcurementStockValuationAccountDto("receipt_accrual", new(11), "L2100");
        var payableAccount = new ProcurementStockValuationAccountDto("supplier_payable", new(12), "L2200");
        var advanceAccount = new ProcurementStockValuationAccountDto("supplier_advance", new(13), "A1201");
        var at = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);
        var hash = new string('a', 64);
        ProcurementSupplierPayableSourceDto recognition = new ProcurementSupplierPayableRecognitionSourceDto(
            "recognize", id, hash, new(5), new(6), new(7), new(8), new(9), 1, 1, new(10), "PO-1", "INV-1",
            new(14), "Synthetic supplier", location, new(11250), new(10000), new(1000), new(250), account, new(15));
        ProcurementSupplierPayableSourceDto application = new ProcurementSupplierPayableApplicationSourceDto(
            "apply_advance", id, hash, new(16), hash, new(17), new(18), new(19), new(10), "PO-1", "INV-1",
            new(14), "Synthetic supplier", location, new(4000), payableAccount, advanceAccount, new(15), new(15));
        ProcurementSupplierPayableCapacityDto recognitionCapacity = new ProcurementSupplierPayableRecognitionCapacityDto(id, new(11250), false, null);
        ProcurementSupplierPayableCapacityDto applicationCapacity = new ProcurementSupplierPayableApplicationCapacityDto(
            new(id, "1", new(11250), new(4000), new(7250), new(20), hash),
            new ProcurementSupplierAdvanceBalanceDto(new(16), "1", new(6000), new(4000), new(2000), new(20), hash));
        var input = new ProcurementSupplierPayableInputDto("recognize", location, id, null, null, null,
            new(21), "1", "2026-10-03", "Explicit synthetic review");
        var policy = new ProcurementSupplierPayablePolicyDto(new(21), "1", true, location, "IDR", at, at.AddDays(1),
            "receipt_accrual_to_payable", "verified_advance_to_payable", new(15), new(22), account, payableAccount, advanceAccount,
            "Synthetic operating owner", "Synthetic independent approval");
        var accounting = new ProcurementSupplierPayableAccountingDto(new(15), new(22), new(23), "2026-10", account, payableAccount, advanceAccount);

        var recognized = JsonSerializer.SerializeToElement(new ProcurementSupplierPayablePreviewDto(input, recognition,
            recognitionCapacity, policy, accounting, new(11250), hash, at), options);
        Keys(recognized, "input", "source", "capacity", "policy", "accounting", "amount", "binding", "observedAt");
        var source = recognized.GetProperty("source");
        Keys(source, "kind", "valuationAllocationId", "valuationAllocationHash", "valuationDecisionId", "valuationJournalId", "receiptId",
            "invoiceDocumentId", "invoiceMatchId", "matchLineNumber", "packages", "orderId", "commercialReference", "invoiceDocumentReference",
            "supplierId", "supplierName", "location", "amount", "netAmount", "taxAmount", "chargeAmount", "sourceAccount", "sourceBookId");
        Assert.Equal("9007199254740993", source.GetProperty("valuationAllocationId").GetString());
        Assert.Equal("11250.00", source.GetProperty("amount").GetString());
        Keys(recognized.GetProperty("capacity"), "valuationAllocationId", "amount", "recognized", "payableId");
        Assert.Equal(JsonValueKind.Null, recognized.GetProperty("capacity").GetProperty("payableId").ValueKind);

        var applied = JsonSerializer.SerializeToElement(new ProcurementSupplierPayablePreviewDto(input with {
            Kind = "apply_advance", ValuationAllocationId = null, PayableId = id, SettlementId = new(16), Amount = new(4000)
        }, application, applicationCapacity, policy, accounting, new(4000), hash, at), options);
        source = applied.GetProperty("source");
        Keys(source, "kind", "payableId", "payableHash", "settlementId", "settlementHash", "disbursementId", "settlementJournalId",
            "valuationAllocationId", "orderId", "commercialReference", "invoiceDocumentReference", "supplierId", "supplierName", "location",
            "amount", "sourcePayableAccount", "sourceAdvanceAccount", "sourceBookId", "advanceBookId");
        Assert.Equal("9007199254740993", source.GetProperty("payableId").GetString());
        Assert.Equal("4000.00", source.GetProperty("amount").GetString());
        var capacity = applied.GetProperty("capacity"); Keys(capacity, "payable", "advance");
        Keys(capacity.GetProperty("payable"), "payableId", "version", "totalAmount", "appliedAmount", "remainingAmount", "lastApplicationId", "lastHash");
        Keys(capacity.GetProperty("advance"), "settlementId", "version", "totalAmount", "appliedAmount", "remainingAmount", "lastApplicationId", "lastHash");
        Assert.Equal("7250.00", capacity.GetProperty("payable").GetProperty("remainingAmount").GetString());
        Assert.Equal("2000.00", capacity.GetProperty("advance").GetProperty("remainingAmount").GetString());

        var proposed = JsonSerializer.SerializeToElement(new ProcurementSupplierPayableRecoveryDto("confirmed",
            new ProcurementSupplierPayableProposalResultDto(id, "1", "proposed", at)), options);
        Keys(proposed, "state", "result"); Keys(proposed.GetProperty("result"), "proposalId", "version", "status", "recordedAt");
        Assert.Equal("9007199254740993", proposed.GetProperty("result").GetProperty("proposalId").GetString());
        var decided = JsonSerializer.SerializeToElement(new ProcurementSupplierPayableRecoveryDto("confirmed",
            new ProcurementSupplierPayableDecisionResultDto(id, new(24), "1", "approved", new(25), null, new(26), at)), options);
        Keys(decided.GetProperty("result"), "proposalId", "decisionId", "version", "status", "payableId", "applicationId", "journalId", "recordedAt");
        Assert.Equal("26", decided.GetProperty("result").GetProperty("journalId").GetString());
        Assert.Equal(JsonValueKind.Null, decided.GetProperty("result").GetProperty("applicationId").ValueKind);

        static void Keys(JsonElement value, params string[] expected) =>
            Assert.Equal(expected.Order(StringComparer.Ordinal), value.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Recognition_cannot_supply_a_derived_amount_and_advance_application_requires_one_exact_positive_amount()
    {
        var source = new ProcurementSupplierPayableInput(ProcurementSupplierPayableKind.Recognize, ProcurementRequestTestData.Location,
            5, null, null, null, 1, 1, new(2026, 10, 3), "Synthetic reviewed recognition");
        Assert.Equal(source, ProcurementSupplierPayableRules.Copy(source));
        var application = source with { Kind = ProcurementSupplierPayableKind.ApplyAdvance, ValuationAllocationId = null, PayableId = 7,
            SettlementId = 8, Amount = new Money(4000) };
        Assert.Equal(application, ProcurementSupplierPayableRules.Copy(application));
        foreach (var invalid in new[] { source with { Amount = new Money(11250) }, source with { PayableId = 7 }, source with { ValuationAllocationId = null },
            application with { Amount = null }, application with { Amount = new Money(0) }, application with { SettlementId = null },
            application with { ValuationAllocationId = 5 }, application with { PolicyRevision = 0 }, application with { Reason = " " } })
            Assert.Throws<DomainValidationException>(() => ProcurementSupplierPayableRules.Copy(invalid));
    }
}
