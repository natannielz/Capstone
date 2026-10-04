using System.Globalization;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Procurement;
using R = UnitToko.Api.Procurement.ProcurementRequestEndpoints;

namespace UnitToko.Api.Procurement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementAdvanceReturnLineInputDto([property: JsonRequired] LongId SettlementId,
    [property: JsonRequired] Money Amount);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementAdvanceReturnInputDto([property: JsonRequired] ProcurementLocationDto Location,
    [property: JsonRequired] LongId ReceiptId, [property: JsonRequired] LongId PolicyId,
    [property: JsonRequired] string PolicyRevision, [property: JsonRequired] string PostingDate,
    [property: JsonRequired] string Reason, [property: JsonRequired] ProcurementAdvanceReturnLineInputDto[] Lines)
{
    internal ProcurementAdvanceReturnInput Domain()
    {
        if (Location is null || Lines is null || Lines.Any(line => line is null) ||
            !DateOnly.TryParseExact(PostingDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw ProcurementAdvanceReturnRules.Invalid();
        return ProcurementAdvanceReturnRules.Copy(new(Location.Domain(), ReceiptId.Value, PolicyId.Value,
            LongId.Parse(PolicyRevision).Value, date, Reason,
            [.. Lines.Select(line => new ProcurementAdvanceReturnLineInput(line.SettlementId.Value, line.Amount))]));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementAdvanceReturnQueryDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementAdvanceReturnPoliciesQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforePolicyId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementAdvanceReturnReceiptsQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeReceiptId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementAdvanceReturnAdvancesQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ProcurementLocationDto Location, [property: JsonRequired] LongId ReceiptId,
    LongId? BeforeSettlementId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementAdvanceReturnHistoryQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeProposalId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementAdvanceReturnPreviewInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ProcurementAdvanceReturnInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementAdvanceReturnProposeDto([property: JsonRequired] ProcurementAdvanceReturnInputDto Input,
    [property: JsonRequired] string Binding);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementAdvanceReturnDecisionPreviewInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Decision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementAdvanceReturnDecideDto([property: JsonRequired] string Decision,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementAdvanceReturnRecoveryInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

public sealed record ProcurementAdvanceReturnSourceLineDto(LongId SettlementId, string SettlementHash,
    LongId DisbursementId, Money SettlementAmount, Money ReturnAmount, ProcurementStockValuationAccountDto SourceAdvanceAccount);
public sealed record ProcurementAdvanceReturnSourceDto(LongId ReceiptId, string ReceiptHash, Money ReceiptAmount,
    string BookedDate, string? BankReference, LongId BankAccountId, LongId BankAccountVersionId,
    ProcurementLocationDto Location, LongId SupplierId, string SupplierName, LongId OrderId,
    string CommercialReference, LongId BookId, ProcurementStockValuationAccountDto SuspenseAccount,
    ProcurementAdvanceReturnSourceLineDto[] Lines);
public sealed record ProcurementAdvanceReturnCapacityLineDto(LongId SettlementId, ProcurementSupplierAdvanceStateDto Advance);
public sealed record ProcurementAdvanceReturnCapacityDto(ProcurementAdvanceReturnCapacityLineDto[] Lines);
public sealed record ProcurementAdvanceReturnPolicyDto(LongId Id, string Revision, bool Active,
    ProcurementLocationDto Location, string Currency, DateTimeOffset ApprovedAt, DateTimeOffset ExpiresAt,
    string Treatment, LongId BookId, LongId ChartVersionId, ProcurementStockValuationAccountDto Suspense,
    ProcurementStockValuationAccountDto SupplierAdvance, string OperatingOwnerReference, string ApprovalReference);
public sealed record ProcurementAdvanceReturnAccountingDto(LongId BookId, LongId ChartVersionId, LongId PeriodId,
    string PeriodMonth, ProcurementStockValuationAccountDto Suspense, ProcurementStockValuationAccountDto SupplierAdvance);
public sealed record ProcurementAdvanceReturnPreviewDto(ProcurementAdvanceReturnInputDto Input,
    ProcurementAdvanceReturnSourceDto Source, ProcurementAdvanceReturnCapacityDto Capacity,
    ProcurementAdvanceReturnPolicyDto Policy, ProcurementAdvanceReturnAccountingDto Accounting,
    Money Amount, string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementAdvanceReturnProposalDto(LongId Id, string Version, ProcurementAdvanceReturnInputDto Input,
    ProcurementAdvanceReturnSourceDto Source, ProcurementAdvanceReturnCapacityDto Capacity,
    ProcurementAdvanceReturnPolicyDto Policy, ProcurementAdvanceReturnAccountingDto Accounting,
    Money Amount, DateTimeOffset RecordedAt, string Reason);
[JsonDerivedType(typeof(ProcurementAdvanceReturnProposalResultDto))]
[JsonDerivedType(typeof(ProcurementAdvanceReturnDecisionResultDto))]
public abstract record ProcurementAdvanceReturnResultDto;
public sealed record ProcurementAdvanceReturnProposalResultDto(LongId ProposalId, string Version, string Status,
    DateTimeOffset RecordedAt) : ProcurementAdvanceReturnResultDto;
public sealed record ProcurementAdvanceReturnDecisionResultDto(LongId ProposalId, LongId DecisionId, string Version,
    string Status, LongId ReceiptId, LongId[] ReturnLineIds, LongId? JournalId, DateTimeOffset RecordedAt) : ProcurementAdvanceReturnResultDto;
public sealed record ProcurementAdvanceReturnReadDto(ProcurementAdvanceReturnProposalDto Proposal,
    ProcurementAdvanceReturnDecisionResultDto? Decision, string? DecisionReason);
public sealed record ProcurementAdvanceReturnDecisionPreviewDto(ProcurementAdvanceReturnProposalDto Proposal,
    string Decision, ProcurementAdvanceReturnSourceDto Source, ProcurementAdvanceReturnCapacityDto Capacity,
    ProcurementAdvanceReturnPolicyDto Policy, ProcurementAdvanceReturnAccountingDto Accounting,
    string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementAdvanceReturnHistoryDto(ProcurementLocationDto Location, ProcurementAdvanceReturnReadDto[] Items,
    LongId? NextProposalId);
public sealed record ProcurementAdvanceReturnRecoveryDto(string State, ProcurementAdvanceReturnResultDto? Result);
public sealed record ProcurementAdvanceReturnPoliciesDto(ProcurementAdvanceReturnPolicyDto[] Items, LongId? NextPolicyId);
public sealed record ProcurementAdvanceReturnReceiptDto(LongId ReceiptId, Money ReceiptAmount, string BookedDate,
    string? BankReference, LongId BankAccountId, LongId BankAccountVersionId, string ReceiptHash);
public sealed record ProcurementAdvanceReturnReceiptsDto(ProcurementLocationDto Location,
    ProcurementAdvanceReturnReceiptDto[] Items, LongId? NextReceiptId);
public sealed record ProcurementAdvanceReturnAdvancesDto(ProcurementLocationDto Location, LongId ReceiptId,
    ProcurementSupplierAdvanceChoiceDto[] Items, LongId? NextSettlementId);

internal static class ProcurementAdvanceReturnDtos
{
    private static LongId Id(BsonValue value) => new(value.AsInt64);
    private static LongId? OptionalId(BsonValue value) => value.IsBsonNull ? null : Id(value);
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static string? OptionalText(BsonValue value) => value.IsBsonNull ? null : value.AsString;
    private static Money Amount(BsonValue value) => Money.Parse(value.AsString);
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime());
    private static ProcurementStockValuationAccountDto Account(BsonValue value) =>
        new(value["mappingKey"].AsString, Id(value["accountId"]), value["accountCode"].AsString);
    internal static ProcurementAdvanceReturnInputDto Input(BsonValue row) => new(R.Location(row["location"]), Id(row["receiptId"]),
        Id(row["policyId"]), Number(row["policyRevision"]), row["postingDate"].AsString, row["reason"].AsString,
        [.. row["lines"].AsBsonArray.Select(line => new ProcurementAdvanceReturnLineInputDto(Id(line["settlementId"]), Amount(line["amount"])))]);
    internal static ProcurementAdvanceReturnSourceDto Source(BsonValue row) => new(Id(row["receiptId"]), row["receiptHash"].AsString,
        Amount(row["receiptAmount"]), row["bookedDate"].AsString, OptionalText(row["bankReference"]), Id(row["bankAccountId"]),
        Id(row["bankAccountVersionId"]), R.Location(row["location"]), Id(row["supplierId"]), row["supplierName"].AsString,
        Id(row["orderId"]), row["commercialReference"].AsString, Id(row["bookId"]), Account(row["suspenseAccount"]),
        [.. row["lines"].AsBsonArray.Select(line => new ProcurementAdvanceReturnSourceLineDto(Id(line["settlementId"]),
            line["settlementHash"].AsString, Id(line["disbursementId"]), Amount(line["settlementAmount"]),
            Amount(line["returnAmount"]), Account(line["sourceAdvanceAccount"])))]);
    internal static ProcurementAdvanceReturnCapacityDto Capacity(BsonValue row) => new(
        [.. row["lines"].AsBsonArray.Select(line => new ProcurementAdvanceReturnCapacityLineDto(Id(line["settlementId"]),
            ProcurementSupplierPayableDtos.AdvanceBalance(line["advance"])))]);
    internal static ProcurementAdvanceReturnPolicyDto Policy(BsonValue row) => new(Id(row["id"]), Number(row["revision"]),
        row["active"].AsBoolean, R.Location(row["location"]), row["currency"].AsString, Date(row["approvedAt"]), Date(row["expiresAt"]),
        row["treatment"].AsString, Id(row["bookId"]), Id(row["chartVersionId"]), Account(row["suspense"]), Account(row["supplierAdvance"]),
        row["operatingOwnerReference"].AsString, row["approvalReference"].AsString);
    internal static ProcurementAdvanceReturnAccountingDto Accounting(BsonValue row) => new(Id(row["bookId"]), Id(row["chartVersionId"]),
        Id(row["periodId"]), row["periodMonth"].AsString, Account(row["suspense"]), Account(row["supplierAdvance"]));
    internal static ProcurementAdvanceReturnPreviewDto Preview(BsonValue row) => new(Input(row["input"]), Source(row["source"]),
        Capacity(row["capacity"]), Policy(row["policy"]), Accounting(row["accounting"]), Amount(row["amount"]), row["binding"].AsString,
        Date(row["observedAt"]));
    internal static ProcurementAdvanceReturnProposalDto Proposal(BsonValue row) => new(Id(row["id"]), Number(row["version"]),
        Input(row["input"]), Source(row["source"]), Capacity(row["capacity"]), Policy(row["policy"]), Accounting(row["accounting"]),
        Amount(row["amount"]), Date(row["recordedAt"]), row["reason"].AsString);
    internal static ProcurementAdvanceReturnDecisionResultDto DecisionResult(BsonValue row) => new(Id(row["proposalId"]),
        Id(row["decisionId"]), Number(row["version"]), row["status"].AsString, Id(row["receiptId"]),
        [.. row["returnLineIds"].AsBsonArray.Select(Id)], OptionalId(row["journalId"]), Date(row["recordedAt"]));
    internal static ProcurementAdvanceReturnResultDto Result(BsonValue row) => row["status"].AsString switch {
        "proposed" => new ProcurementAdvanceReturnProposalResultDto(Id(row["proposalId"]), Number(row["version"]),
            row["status"].AsString, Date(row["recordedAt"])),
        "returned" or "rejected" => DecisionResult(row),
        _ => throw ProcurementAdvanceReturnRules.Invalid()
    };
    internal static ProcurementAdvanceReturnReadDto Read(BsonValue row) => new(Proposal(row["proposal"]),
        row["decision"].IsBsonNull ? null : DecisionResult(row["decision"]), OptionalText(row["decisionReason"]));
    internal static ProcurementAdvanceReturnDecisionPreviewDto DecisionPreview(BsonValue row) => new(Proposal(row["proposal"]),
        row["decision"].AsString, Source(row["source"]), Capacity(row["capacity"]), Policy(row["policy"]), Accounting(row["accounting"]),
        row["binding"].AsString, Date(row["observedAt"]));
    internal static ProcurementAdvanceReturnHistoryDto History(BsonValue row) => new(R.Location(row["location"]),
        [.. row["items"].AsBsonArray.Select(Read)], OptionalId(row["nextProposalId"]));
    internal static ProcurementAdvanceReturnRecoveryDto Recovery(BsonValue row) => new(row["state"].AsString,
        row["result"].IsBsonNull ? null : Result(row["result"]));
    internal static ProcurementAdvanceReturnPoliciesDto Policies(BsonValue row) => new([.. row["items"].AsBsonArray.Select(Policy)], OptionalId(row["nextPolicyId"]));
    internal static ProcurementAdvanceReturnReceiptsDto Receipts(BsonValue row) => new(R.Location(row["location"]),
        [.. row["items"].AsBsonArray.Select(item => new ProcurementAdvanceReturnReceiptDto(Id(item["receiptId"]),
            Amount(item["receiptAmount"]), item["bookedDate"].AsString, OptionalText(item["bankReference"]),
            Id(item["bankAccountId"]), Id(item["bankAccountVersionId"]), item["receiptHash"].AsString))], OptionalId(row["nextReceiptId"]));
    internal static ProcurementAdvanceReturnAdvancesDto Advances(BsonValue row) => new(R.Location(row["location"]),
        Id(row["receiptId"]), [.. row["items"].AsBsonArray.Select(ProcurementSupplierPayableDtos.AdvanceChoice)], OptionalId(row["nextSettlementId"]));
}
