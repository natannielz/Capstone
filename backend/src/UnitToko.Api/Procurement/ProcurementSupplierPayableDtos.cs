using System.Globalization;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Procurement;
using R = UnitToko.Api.Procurement.ProcurementRequestEndpoints;

namespace UnitToko.Api.Procurement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSupplierPayableInputDto(
    [property: JsonRequired] string Kind, [property: JsonRequired] ProcurementLocationDto Location,
    [property: JsonRequired] LongId? ValuationAllocationId, [property: JsonRequired] LongId? PayableId,
    [property: JsonRequired] LongId? SettlementId, [property: JsonRequired] Money? Amount,
    [property: JsonRequired] LongId PolicyId, [property: JsonRequired] string PolicyRevision,
    [property: JsonRequired] string PostingDate, [property: JsonRequired] string Reason)
{
    internal ProcurementSupplierPayableInput Domain()
    {
        if (Location is null || !DateOnly.TryParseExact(PostingDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var postingDate)) throw ProcurementSupplierPayableRules.Invalid();
        return ProcurementSupplierPayableRules.Copy(new(
        Kind switch { "recognize" => ProcurementSupplierPayableKind.Recognize,
            "apply_advance" => ProcurementSupplierPayableKind.ApplyAdvance, _ => throw ProcurementSupplierPayableRules.Invalid() },
        Location.Domain(), ValuationAllocationId?.Value, PayableId?.Value, SettlementId?.Value, Amount,
        PolicyId.Value, LongId.Parse(PolicyRevision).Value,
        postingDate, Reason));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSupplierPayableQueryDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSupplierPayablePoliciesQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforePolicyId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSupplierPayableValuationsQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeValuationAllocationId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSupplierPayableListQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforePayableId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSupplierPayableAdvancesQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ProcurementLocationDto Location, [property: JsonRequired] LongId PayableId, LongId? BeforeSettlementId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSupplierPayableHistoryQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeProposalId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSupplierPayablePreviewInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ProcurementSupplierPayableInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSupplierPayableProposeDto([property: JsonRequired] ProcurementSupplierPayableInputDto Input,
    [property: JsonRequired] string Binding);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSupplierPayableDecisionPreviewInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Decision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSupplierPayableDecideDto([property: JsonRequired] string Decision,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementSupplierPayableRecoveryInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

// These unions are output-only. Native subtype serialization retains exact public fields without a new wire discriminator.
[JsonDerivedType(typeof(ProcurementSupplierPayableRecognitionSourceDto))]
[JsonDerivedType(typeof(ProcurementSupplierPayableApplicationSourceDto))]
public abstract record ProcurementSupplierPayableSourceDto;
public sealed record ProcurementSupplierPayableRecognitionSourceDto(string Kind, LongId ValuationAllocationId,
    string ValuationAllocationHash, LongId ValuationDecisionId, LongId ValuationJournalId, LongId ReceiptId,
    LongId InvoiceDocumentId, LongId InvoiceMatchId, int MatchLineNumber, int Packages, LongId OrderId,
    string CommercialReference, string InvoiceDocumentReference, LongId SupplierId, string SupplierName,
    ProcurementLocationDto Location, Money Amount, Money NetAmount, Money TaxAmount, Money ChargeAmount,
    ProcurementStockValuationAccountDto SourceAccount, LongId SourceBookId) : ProcurementSupplierPayableSourceDto;
public sealed record ProcurementSupplierPayableApplicationSourceDto(string Kind, LongId PayableId, string PayableHash,
    LongId SettlementId, string SettlementHash, LongId DisbursementId, LongId SettlementJournalId,
    LongId ValuationAllocationId, LongId OrderId, string CommercialReference, string InvoiceDocumentReference,
    LongId SupplierId, string SupplierName, ProcurementLocationDto Location, Money Amount,
    ProcurementStockValuationAccountDto SourcePayableAccount, ProcurementStockValuationAccountDto SourceAdvanceAccount,
    LongId SourceBookId, LongId AdvanceBookId) : ProcurementSupplierPayableSourceDto;
[JsonDerivedType(typeof(ProcurementSupplierPayableRecognitionCapacityDto))]
[JsonDerivedType(typeof(ProcurementSupplierPayableApplicationCapacityDto))]
public abstract record ProcurementSupplierPayableCapacityDto;
public sealed record ProcurementSupplierPayableRecognitionCapacityDto(LongId ValuationAllocationId, Money Amount,
    bool Recognized, LongId? PayableId) : ProcurementSupplierPayableCapacityDto;
public sealed record ProcurementSupplierPayableBalanceDto(LongId PayableId, string Version, Money TotalAmount,
    Money AppliedAmount, Money RemainingAmount, LongId? LastApplicationId, string? LastHash);
public sealed record ProcurementSupplierAdvanceBalanceDto(LongId SettlementId, string Version, Money TotalAmount,
    Money AppliedAmount, Money RemainingAmount, LongId? LastApplicationId, string? LastHash);
public sealed record ProcurementSupplierPayableApplicationCapacityDto(ProcurementSupplierPayableBalanceDto Payable,
    ProcurementSupplierAdvanceBalanceDto Advance) : ProcurementSupplierPayableCapacityDto;
public sealed record ProcurementSupplierPayablePolicyDto(LongId Id, string Revision, bool Active, ProcurementLocationDto Location,
    string Currency, DateTimeOffset ApprovedAt, DateTimeOffset ExpiresAt, string SourceTreatment, string ApplicationTreatment,
    LongId BookId, LongId ChartVersionId, ProcurementStockValuationAccountDto ReceiptAccrual,
    ProcurementStockValuationAccountDto SupplierPayable, ProcurementStockValuationAccountDto SupplierAdvance,
    string OperatingOwnerReference, string ApprovalReference);
public sealed record ProcurementSupplierPayableAccountingDto(LongId BookId, LongId ChartVersionId, LongId PeriodId,
    string PeriodMonth, ProcurementStockValuationAccountDto ReceiptAccrual, ProcurementStockValuationAccountDto SupplierPayable,
    ProcurementStockValuationAccountDto SupplierAdvance);
public sealed record ProcurementSupplierPayablePreviewDto(ProcurementSupplierPayableInputDto Input,
    ProcurementSupplierPayableSourceDto Source, ProcurementSupplierPayableCapacityDto Capacity,
    ProcurementSupplierPayablePolicyDto Policy, ProcurementSupplierPayableAccountingDto Accounting,
    Money Amount, string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementSupplierPayableProposalDto(LongId Id, string Version, ProcurementSupplierPayableInputDto Input,
    ProcurementSupplierPayableSourceDto Source, ProcurementSupplierPayableCapacityDto Capacity,
    ProcurementSupplierPayablePolicyDto Policy, ProcurementSupplierPayableAccountingDto Accounting,
    Money Amount, DateTimeOffset RecordedAt, string Reason);
[JsonDerivedType(typeof(ProcurementSupplierPayableProposalResultDto))]
[JsonDerivedType(typeof(ProcurementSupplierPayableDecisionResultDto))]
public abstract record ProcurementSupplierPayableResultDto;
public sealed record ProcurementSupplierPayableProposalResultDto(LongId ProposalId, string Version, string Status,
    DateTimeOffset RecordedAt) : ProcurementSupplierPayableResultDto;
public sealed record ProcurementSupplierPayableDecisionResultDto(LongId ProposalId, LongId DecisionId, string Version,
    string Status, LongId? PayableId, LongId? ApplicationId, LongId? JournalId, DateTimeOffset RecordedAt) : ProcurementSupplierPayableResultDto;
public sealed record ProcurementSupplierPayableReadDto(ProcurementSupplierPayableProposalDto Proposal,
    ProcurementSupplierPayableDecisionResultDto? Decision, string? DecisionReason);
public sealed record ProcurementSupplierPayableDecisionPreviewDto(ProcurementSupplierPayableProposalDto Proposal, string Decision,
    ProcurementSupplierPayableSourceDto Source, ProcurementSupplierPayableCapacityDto Capacity,
    ProcurementSupplierPayablePolicyDto Policy, ProcurementSupplierPayableAccountingDto Accounting,
    string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementSupplierPayableHistoryDto(ProcurementLocationDto Location, ProcurementSupplierPayableReadDto[] Items,
    LongId? NextProposalId);
public sealed record ProcurementSupplierPayableRecoveryDto(string State, ProcurementSupplierPayableResultDto? Result);
public sealed record ProcurementSupplierPayableItemDto(LongId Id, LongId ValuationAllocationId, LongId OrderId,
    string CommercialReference, string InvoiceDocumentReference, LongId SupplierId, string SupplierName,
    ProcurementLocationDto Location, Money Amount, LongId JournalId, DateTimeOffset RecordedAt);
public sealed record ProcurementSupplierAdvanceItemDto(LongId SettlementId, LongId DisbursementId, LongId OrderId,
    string CommercialReference, LongId SupplierId, string SupplierName, ProcurementLocationDto Location,
    Money Amount, LongId JournalId, DateTimeOffset RecordedAt);
public sealed record ProcurementSupplierPayableChoiceDto(ProcurementSupplierPayableItemDto Payable, ProcurementSupplierPayableBalanceDto Capacity);
public sealed record ProcurementSupplierAdvanceChoiceDto(ProcurementSupplierAdvanceItemDto Advance, ProcurementSupplierAdvanceBalanceDto Capacity);
public sealed record ProcurementSupplierPayableValuationChoiceDto(ProcurementSupplierPayableRecognitionSourceDto Source,
    ProcurementSupplierPayableRecognitionCapacityDto Capacity);
public sealed record ProcurementSupplierPayableValuationsDto(ProcurementSupplierPayableValuationChoiceDto[] Items, LongId? NextValuationAllocationId);
public sealed record ProcurementSupplierPayablesDto(ProcurementSupplierPayableChoiceDto[] Items, LongId? NextPayableId);
public sealed record ProcurementSupplierAdvancesDto(ProcurementSupplierAdvanceChoiceDto[] Items, LongId? NextSettlementId);
public sealed record ProcurementSupplierPayablePoliciesDto(ProcurementSupplierPayablePolicyDto[] Items, LongId? NextPolicyId);

internal static class ProcurementSupplierPayableDtos
{
    private static LongId Id(BsonValue value) => new(value.AsInt64);
    private static LongId? OptionalId(BsonValue value) => value.IsBsonNull ? null : Id(value);
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static string? OptionalText(BsonValue value) => value.IsBsonNull ? null : value.AsString;
    private static Money Amount(BsonValue value) => Money.Parse(value.AsString);
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime());
    private static ProcurementStockValuationAccountDto Account(BsonValue row) => new(row["mappingKey"].AsString,
        Id(row["accountId"]), row["accountCode"].AsString);
    internal static ProcurementSupplierPayableInputDto Input(BsonValue row) => new(row["kind"].AsString, R.Location(row["location"]),
        OptionalId(row["valuationAllocationId"]), OptionalId(row["payableId"]), OptionalId(row["settlementId"]),
        row["amount"].IsBsonNull ? null : Amount(row["amount"]), Id(row["policyId"]), Number(row["policyRevision"]),
        row["postingDate"].AsString, row["reason"].AsString);
    internal static ProcurementSupplierPayableSourceDto Source(BsonValue row) => row["kind"].AsString switch {
        "recognize" => new ProcurementSupplierPayableRecognitionSourceDto(row["kind"].AsString, Id(row["valuationAllocationId"]),
            row["valuationAllocationHash"].AsString, Id(row["valuationDecisionId"]), Id(row["valuationJournalId"]), Id(row["receiptId"]),
            Id(row["invoiceDocumentId"]), Id(row["invoiceMatchId"]), row["matchLineNumber"].AsInt32, row["packages"].AsInt32,
            Id(row["orderId"]), row["commercialReference"].AsString, row["invoiceDocumentReference"].AsString, Id(row["supplierId"]),
            row["supplierName"].AsString, R.Location(row["location"]), Amount(row["amount"]), Amount(row["netAmount"]),
            Amount(row["taxAmount"]), Amount(row["chargeAmount"]), Account(row["sourceAccount"]), Id(row["sourceBookId"])),
        "apply_advance" => new ProcurementSupplierPayableApplicationSourceDto(row["kind"].AsString, Id(row["payableId"]),
            row["payableHash"].AsString, Id(row["settlementId"]), row["settlementHash"].AsString, Id(row["disbursementId"]),
            Id(row["settlementJournalId"]), Id(row["valuationAllocationId"]), Id(row["orderId"]), row["commercialReference"].AsString,
            row["invoiceDocumentReference"].AsString, Id(row["supplierId"]), row["supplierName"].AsString, R.Location(row["location"]),
            Amount(row["amount"]), Account(row["sourcePayableAccount"]), Account(row["sourceAdvanceAccount"]),
            Id(row["sourceBookId"]), Id(row["advanceBookId"])),
        _ => throw ProcurementSupplierPayableRules.Invalid()
    };
    internal static ProcurementSupplierPayableBalanceDto PayableBalance(BsonValue row) => new(Id(row["payableId"]), Number(row["version"]),
        Amount(row["totalAmount"]), Amount(row["appliedAmount"]), Amount(row["remainingAmount"]), OptionalId(row["lastApplicationId"]), OptionalText(row["lastHash"]));
    internal static ProcurementSupplierAdvanceBalanceDto AdvanceBalance(BsonValue row) => new(Id(row["settlementId"]), Number(row["version"]),
        Amount(row["totalAmount"]), Amount(row["appliedAmount"]), Amount(row["remainingAmount"]), OptionalId(row["lastApplicationId"]), OptionalText(row["lastHash"]));
    internal static ProcurementSupplierPayableCapacityDto Capacity(BsonValue row) => row.AsBsonDocument.Contains("valuationAllocationId")
        ? new ProcurementSupplierPayableRecognitionCapacityDto(Id(row["valuationAllocationId"]), Amount(row["amount"]), row["recognized"].AsBoolean, OptionalId(row["payableId"]))
        : new ProcurementSupplierPayableApplicationCapacityDto(PayableBalance(row["payable"]), AdvanceBalance(row["advance"]));
    internal static ProcurementSupplierPayablePolicyDto Policy(BsonValue row) => new(Id(row["id"]), Number(row["revision"]), row["active"].AsBoolean,
        R.Location(row["location"]), row["currency"].AsString, Date(row["approvedAt"]), Date(row["expiresAt"]),
        row["sourceTreatment"].AsString, row["applicationTreatment"].AsString, Id(row["bookId"]), Id(row["chartVersionId"]),
        Account(row["receiptAccrual"]), Account(row["supplierPayable"]), Account(row["supplierAdvance"]),
        row["operatingOwnerReference"].AsString, row["approvalReference"].AsString);
    internal static ProcurementSupplierPayableAccountingDto Accounting(BsonValue row) => new(Id(row["bookId"]), Id(row["chartVersionId"]),
        Id(row["periodId"]), row["periodMonth"].AsString, Account(row["receiptAccrual"]), Account(row["supplierPayable"]), Account(row["supplierAdvance"]));
    internal static ProcurementSupplierPayablePreviewDto Preview(BsonValue row) => new(Input(row["input"]), Source(row["source"]),
        Capacity(row["capacity"]), Policy(row["policy"]), Accounting(row["accounting"]), Amount(row["amount"]),
        row["binding"].AsString, Date(row["observedAt"]));
    internal static ProcurementSupplierPayableProposalDto Proposal(BsonValue row) => new(Id(row["id"]), Number(row["version"]), Input(row["input"]),
        Source(row["source"]), Capacity(row["capacity"]), Policy(row["policy"]), Accounting(row["accounting"]),
        Amount(row["amount"]), Date(row["recordedAt"]), row["reason"].AsString);
    internal static ProcurementSupplierPayableDecisionResultDto DecisionResult(BsonValue row) => new(Id(row["proposalId"]), Id(row["decisionId"]),
        Number(row["version"]), row["status"].AsString, OptionalId(row["payableId"]), OptionalId(row["applicationId"]),
        OptionalId(row["journalId"]), Date(row["recordedAt"]));
    internal static ProcurementSupplierPayableResultDto Result(BsonValue row) => row["status"].AsString switch {
        "proposed" => new ProcurementSupplierPayableProposalResultDto(Id(row["proposalId"]), Number(row["version"]), row["status"].AsString, Date(row["recordedAt"])),
        "approved" or "rejected" => DecisionResult(row),
        _ => throw ProcurementSupplierPayableRules.Invalid()
    };
    internal static ProcurementSupplierPayableReadDto Read(BsonValue row) => new(Proposal(row["proposal"]),
        row["decision"].IsBsonNull ? null : DecisionResult(row["decision"]), OptionalText(row["decisionReason"]));
    internal static ProcurementSupplierPayableDecisionPreviewDto DecisionPreview(BsonValue row) => new(Proposal(row["proposal"]),
        row["decision"].AsString, Source(row["source"]), Capacity(row["capacity"]), Policy(row["policy"]), Accounting(row["accounting"]),
        row["binding"].AsString, Date(row["observedAt"]));
    internal static ProcurementSupplierPayableHistoryDto History(BsonValue row) => new(R.Location(row["location"]),
        [.. row["items"].AsBsonArray.Select(Read)], OptionalId(row["nextProposalId"]));
    internal static ProcurementSupplierPayableRecoveryDto Recovery(BsonValue row) => new(row["state"].AsString,
        row["result"].IsBsonNull ? null : Result(row["result"]));
    internal static ProcurementSupplierPayableChoiceDto PayableChoice(BsonValue row)
    {
        var value = row["payable"];
        return new(new(Id(value["id"]), Id(value["valuationAllocationId"]), Id(value["orderId"]), value["commercialReference"].AsString,
            value["invoiceDocumentReference"].AsString, Id(value["supplierId"]), value["supplierName"].AsString, R.Location(value["location"]),
            Amount(value["amount"]), Id(value["journalId"]), Date(value["recordedAt"])), PayableBalance(row["capacity"]));
    }
    internal static ProcurementSupplierAdvanceChoiceDto AdvanceChoice(BsonValue row)
    {
        var value = row["advance"];
        return new(new(Id(value["settlementId"]), Id(value["disbursementId"]), Id(value["orderId"]), value["commercialReference"].AsString,
            Id(value["supplierId"]), value["supplierName"].AsString, R.Location(value["location"]), Amount(value["amount"]),
            Id(value["journalId"]), Date(value["recordedAt"])), AdvanceBalance(row["capacity"]));
    }
    internal static ProcurementSupplierPayableValuationsDto Valuations(BsonValue row) => new(
        [.. row["items"].AsBsonArray.Select(value => new ProcurementSupplierPayableValuationChoiceDto(
            (ProcurementSupplierPayableRecognitionSourceDto)Source(value["source"]),
            (ProcurementSupplierPayableRecognitionCapacityDto)Capacity(value["capacity"])))], OptionalId(row["nextValuationAllocationId"]));
    internal static ProcurementSupplierPayablesDto Payables(BsonValue row) => new([.. row["items"].AsBsonArray.Select(PayableChoice)], OptionalId(row["nextPayableId"]));
    internal static ProcurementSupplierAdvancesDto Advances(BsonValue row) => new([.. row["items"].AsBsonArray.Select(AdvanceChoice)], OptionalId(row["nextSettlementId"]));
    internal static ProcurementSupplierPayablePoliciesDto Policies(BsonValue row) => new([.. row["items"].AsBsonArray.Select(Policy)], OptionalId(row["nextPolicyId"]));
}
