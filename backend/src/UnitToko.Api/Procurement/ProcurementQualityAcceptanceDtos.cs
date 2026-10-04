using System.Globalization;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Procurement;
using R = UnitToko.Api.Procurement.ProcurementRequestEndpoints;

namespace UnitToko.Api.Procurement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementQualityAcceptanceInputDto(
    [property: JsonRequired] ProcurementLocationDto Location, [property: JsonRequired] LongId ReceiptId,
    [property: JsonRequired] string ReceiptRevision,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int AcceptedPackages,
    [property: JsonRequired] LongId PolicyId, [property: JsonRequired] string PolicyRevision,
    [property: JsonRequired] DateTimeOffset ObservedAt, [property: JsonRequired] string QualityReference,
    [property: JsonRequired] string PutawayReference, [property: JsonRequired] string SegregationReference,
    [property: JsonRequired] string AcceptedCondition, [property: JsonRequired] string LotReference,
    [property: JsonRequired] string? ExpiresOn, [property: JsonRequired] string? NonExpiringReason,
    [property: JsonRequired] string Reason)
{
    internal ProcurementQualityAcceptanceInput Domain() => ProcurementQualityAcceptanceRules.Copy(new ProcurementQualityAcceptanceInput(
        Location is null ? throw ProcurementQualityAcceptanceRules.Invalid() : Location.Domain(), ReceiptId.Value,
        LongId.Parse(ReceiptRevision).Value, AcceptedPackages, PolicyId.Value, LongId.Parse(PolicyRevision).Value,
        ObservedAt, QualityReference, PutawayReference, SegregationReference, AcceptedCondition, LotReference,
        ProcurementQualityAcceptanceDtos.InputDate(ExpiresOn), NonExpiringReason, Reason));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementQualityAcceptanceObservationDto(
    [property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired, JsonNumberHandling(JsonNumberHandling.Strict)] int AcceptedPackages,
    [property: JsonRequired] string QualityReference, [property: JsonRequired] string PutawayReference,
    [property: JsonRequired] string SegregationReference, [property: JsonRequired] string AcceptedCondition,
    [property: JsonRequired] string LotReference, [property: JsonRequired] string? ExpiresOn,
    [property: JsonRequired] string? NonExpiringReason)
{
    internal ProcurementQualityAcceptanceObservation Domain() => ProcurementQualityAcceptanceRules.Copy(new ProcurementQualityAcceptanceObservation(
        ObservedAt, AcceptedPackages, QualityReference, PutawayReference, SegregationReference, AcceptedCondition,
        LotReference, ProcurementQualityAcceptanceDtos.InputDate(ExpiresOn), NonExpiringReason));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementQualityAcceptanceQueryDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementQualityAcceptanceReceiptsQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeReceiptId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementQualityAcceptancePoliciesQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] LongId ReceiptId, LongId? BeforePolicyId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementQualityAcceptanceHistoryQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeProposalId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementQualityAcceptancePreviewInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] ProcurementQualityAcceptanceInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementQualityAcceptanceProposeDto([property: JsonRequired] ProcurementQualityAcceptanceInputDto Input,
    [property: JsonRequired] string Binding);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementQualityAcceptanceDecisionPreviewInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Decision, [property: JsonRequired] ProcurementQualityAcceptanceObservationDto? Observation);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementQualityAcceptanceDecideDto([property: JsonRequired] string Decision,
    [property: JsonRequired] ProcurementQualityAcceptanceObservationDto? Observation,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementQualityAcceptanceRecoveryInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

public sealed record ProcurementQualityAcceptanceBalanceDto(LongId BalanceId, LongId LotId, LongId PoolId,
    string OnHand, string Reserved, string Held, string Expired, string Revision);
public sealed record ProcurementQualityAcceptanceCapacityDto(string Version, string AcceptedPackages, string RemainingPackages,
    LongId? LastAllocationId, string? LastHash);
public sealed record ProcurementQualityAcceptancePolicyDto(LongId Id, string Revision, bool Active, ProcurementLocationDto Location,
    string QualityProcedureReference, string PutawayProcedureReference, bool AllowNonExpiring, string? NonExpiringProcedureReference,
    string MaximumObservationAgeMilliseconds, string ApprovalReference, DateTimeOffset ApprovedAt, DateTimeOffset ExpiresAt);
public sealed record ProcurementQualityAcceptanceSupplierDto(LongId Id, string DisplayName);
public sealed record ProcurementQualityAcceptanceOrderReferenceDto(string Reference);
public sealed record ProcurementQualityAcceptanceOrderDto(LongId Id, string Version, LongId ApprovalId,
    ProcurementQualityAcceptanceSupplierDto Supplier, ProcurementQualityAcceptanceOrderReferenceDto CommercialReference,
    ProcurementPackageDto[] Lines);
public sealed record ProcurementQualityAcceptanceSourceDto(ProcurementQualityAcceptanceOrderDto Order, ProcurementStockReceiptInspectionDto Inspection);
public sealed record ProcurementQualityAcceptanceReceiptDto(LongId Id, string Revision, ProcurementStockReceiptInputDto Input,
    ProcurementQualityAcceptanceSourceDto Source, LongId LotId, LongId BalanceId, string State, DateTimeOffset RecordedAt);
public sealed record ProcurementQualityAcceptanceReceiptChoiceDto(ProcurementQualityAcceptanceReceiptDto Receipt,
    ProcurementQualityAcceptanceBalanceDto HeldBalance, ProcurementQualityAcceptanceCapacityDto Capacity);
public sealed record ProcurementQualityAcceptanceReceiptsDto(ProcurementLocationDto Location,
    ProcurementQualityAcceptanceReceiptChoiceDto[] Items, LongId? NextReceiptId);
public sealed record ProcurementQualityAcceptancePoliciesDto(LongId ReceiptId, ProcurementQualityAcceptancePolicyDto[] Items, LongId? NextPolicyId);
public sealed record ProcurementQualityAcceptancePreviewDto(ProcurementQualityAcceptanceInputDto Input, ProcurementQualityAcceptanceReceiptDto Receipt,
    ProcurementQualityAcceptanceBalanceDto HeldBalance, ProcurementQualityAcceptancePolicyDto Policy,
    ProcurementQualityAcceptanceCapacityDto Capacity, string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementQualityAcceptanceProposalDto(LongId Id, string Version, ProcurementQualityAcceptanceInputDto Input,
    ProcurementQualityAcceptanceReceiptDto Receipt, ProcurementQualityAcceptanceBalanceDto HeldBalance, ProcurementQualityAcceptancePolicyDto Policy,
    ProcurementQualityAcceptanceCapacityDto Capacity, DateTimeOffset RecordedAt, string Reason);
public sealed record ProcurementQualityAcceptanceDecisionPreviewDto(ProcurementQualityAcceptanceProposalDto Proposal, string Decision,
    ProcurementQualityAcceptanceObservationDto? Observation, ProcurementQualityAcceptanceBalanceDto HeldBalance,
    ProcurementQualityAcceptancePolicyDto Policy, ProcurementQualityAcceptanceCapacityDto Capacity, string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementQualityAcceptanceResultDto(LongId ProposalId, LongId? DecisionId, LongId? AllocationId,
    string Version, string Status, DateTimeOffset RecordedAt);
public sealed record ProcurementQualityAcceptanceReadDto(ProcurementQualityAcceptanceProposalDto Proposal,
    ProcurementQualityAcceptanceResultDto? Decision, string? DecisionReason, ProcurementQualityAcceptanceObservationDto? CheckerObservation);
public sealed record ProcurementQualityAcceptanceHistoryDto(ProcurementLocationDto Location, ProcurementQualityAcceptanceReadDto[] Items, LongId? NextProposalId);
public sealed record ProcurementQualityAcceptanceRecoveryDto(string State, ProcurementQualityAcceptanceResultDto? Result);

internal static class ProcurementQualityAcceptanceDtos
{
    internal static DateOnly? InputDate(string? value) => value is null ? null :
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date :
        throw ProcurementQualityAcceptanceRules.Invalid();
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static string? Text(BsonValue value) => value.IsBsonNull ? null : value.AsString;
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    // Quality readers need physical provenance, not the order's commercial amounts or payment terms.
    internal static ProcurementQualityAcceptanceReceiptDto Receipt(BsonValue row) {
        var receipt = ProcurementStockReceiptEndpoints.Receipt(row);
        var order = receipt.Source.Order; var inspection = receipt.Source.Inspection;
        var line = order.Lines.Single(item => item.SkuId == inspection.Line.RequestSkuId &&
            item.DefinitionId == inspection.Line.DefinitionId && item.DefinitionVersion == inspection.Line.DefinitionVersion);
        return new(receipt.Id, receipt.Revision, receipt.Input,
            new(new(order.Id, order.Version, order.ApprovalId, new(order.Supplier.Id, order.Supplier.DisplayName),
                new(order.CommercialReference.Reference), [new(line.SkuId, line.SkuRevision, line.DefinitionId, line.DefinitionVersion,
                    line.SkuCode, line.FamilyName, line.PackageLabel, line.PackageKind, line.BaseUnitCode, line.BaseUnitsPerPackage)]), inspection),
            receipt.LotId, receipt.BalanceId, receipt.State, receipt.RecordedAt);
    }
    internal static ProcurementQualityAcceptanceInputDto Input(BsonValue row) => new(R.Location(row["location"]),
        new(row["receiptId"].AsInt64), Number(row["receiptRevision"]), row["acceptedPackages"].AsInt32,
        new(row["policyId"].AsInt64), Number(row["policyRevision"]), Date(row["observedAt"]), row["qualityReference"].AsString,
        row["putawayReference"].AsString, row["segregationReference"].AsString, row["acceptedCondition"].AsString,
        row["lotReference"].AsString, Text(row["expiresOn"]), Text(row["nonExpiringReason"]), row["reason"].AsString);
    internal static ProcurementQualityAcceptanceObservationDto Observation(BsonValue row) => new(Date(row["observedAt"]),
        row["acceptedPackages"].AsInt32, row["qualityReference"].AsString, row["putawayReference"].AsString,
        row["segregationReference"].AsString, row["acceptedCondition"].AsString, row["lotReference"].AsString,
        Text(row["expiresOn"]), Text(row["nonExpiringReason"]));
    internal static ProcurementQualityAcceptanceBalanceDto Balance(BsonValue row) => new(new(row["balanceId"].AsInt64),
        new(row["lotId"].AsInt64), new(row["poolId"].AsInt64), Number(row["onHand"]), Number(row["reserved"]),
        Number(row["held"]), Number(row["expired"]), Number(row["revision"]));
    internal static ProcurementQualityAcceptanceCapacityDto Capacity(BsonValue row) => new(Number(row["version"]),
        Number(row["acceptedPackages"]), Number(row["remainingPackages"]), Id(row["lastAllocationId"]), Text(row["lastHash"]));
    internal static ProcurementQualityAcceptancePolicyDto Policy(BsonValue row) => new(new(row["id"].AsInt64), Number(row["revision"]),
        row["active"].AsBoolean, R.Location(row["location"]), row["qualityProcedureReference"].AsString,
        row["putawayProcedureReference"].AsString, row["allowNonExpiring"].AsBoolean, Text(row["nonExpiringProcedureReference"]),
        Number(row["maximumObservationAgeMilliseconds"]), row["approvalReference"].AsString, Date(row["approvedAt"]), Date(row["expiresAt"]));
    internal static ProcurementQualityAcceptanceReceiptsDto Receipts(BsonDocument row) => new(R.Location(row["location"]),
        [.. row["items"].AsBsonArray.Select(item => new ProcurementQualityAcceptanceReceiptChoiceDto(
            Receipt(item["receipt"]), Balance(item["heldBalance"]), Capacity(item["capacity"])))], Id(row["nextReceiptId"]));
    internal static ProcurementQualityAcceptancePoliciesDto Policies(BsonDocument row) => new(new(row["receiptId"].AsInt64),
        [.. row["items"].AsBsonArray.Select(Policy)], Id(row["nextPolicyId"]));
    internal static ProcurementQualityAcceptancePreviewDto Preview(BsonDocument row) => new(Input(row["input"]),
        Receipt(row["receipt"]), Balance(row["heldBalance"]), Policy(row["policy"]),
        Capacity(row["capacity"]), row["binding"].AsString, Date(row["observedAt"]));
    internal static ProcurementQualityAcceptanceProposalDto Proposal(BsonValue row) => new(new(row["id"].AsInt64), Number(row["version"]),
        Input(row["input"]), Receipt(row["receipt"]), Balance(row["heldBalance"]), Policy(row["policy"]),
        Capacity(row["capacity"]), Date(row["recordedAt"]), row["reason"].AsString);
    internal static ProcurementQualityAcceptanceDecisionPreviewDto DecisionPreview(BsonDocument row) => new(Proposal(row["proposal"]),
        row["decision"].AsString, row["observation"].IsBsonNull ? null : Observation(row["observation"]), Balance(row["heldBalance"]),
        Policy(row["policy"]), Capacity(row["capacity"]), row["binding"].AsString, Date(row["observedAt"]));
    internal static ProcurementQualityAcceptanceResultDto Result(BsonValue row) => new(new(row["proposalId"].AsInt64),
        Id(row["decisionId"]), Id(row["allocationId"]), Number(row["version"]), row["status"].AsString, Date(row["recordedAt"]));
    internal static ProcurementQualityAcceptanceReadDto Read(BsonValue row) => new(Proposal(row["proposal"]),
        row["decision"].IsBsonNull ? null : Result(row["decision"]), Text(row["decisionReason"]),
        row["checkerObservation"].IsBsonNull ? null : Observation(row["checkerObservation"]));
    internal static ProcurementQualityAcceptanceHistoryDto History(BsonDocument row) => new(R.Location(row["location"]),
        [.. row["items"].AsBsonArray.Select(Read)], Id(row["nextProposalId"]));
}
