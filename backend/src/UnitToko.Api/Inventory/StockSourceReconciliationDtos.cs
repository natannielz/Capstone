using System.Globalization;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Api.Inventory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceReconciliationReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockSourceReconciliationTargetsDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] AccessScopeDto Location, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockSourceReconciliationHistoryDto([property: JsonRequired] string Purpose, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceObservedBalanceDto([property: JsonRequired] LongId BalanceId, [property: JsonRequired] string Packages)
{
    internal StockSourceObservedBalance ToDomain() => new(BalanceId.Value, StockSourceSetupInputs.Number(Packages, true));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceReconciliationObservationDto([property: JsonRequired] DateTimeOffset ObservedAt,
    [property: JsonRequired] string SourceReference, [property: JsonRequired] StockSourceObservedBalanceDto[] Entries)
{
    internal StockSourceReconciliationObservation ToDomain()
    {
        if (Entries is null) throw StockSourceReconciliationRules.Invalid();
        var result = new StockSourceReconciliationObservation(ObservedAt, SourceReference,
            Entries.Select(value => value?.ToDomain() ?? throw StockSourceReconciliationRules.Invalid()).ToImmutableArray());
        StockSourceReconciliationRules.Validate(result); return result;
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceReconciliationInputDto([property: JsonRequired] LongId PoolId,
    [property: JsonRequired] string PoolRevision, [property: JsonRequired] string PoolFence,
    [property: JsonRequired] LongId PolicyId, [property: JsonRequired] string PolicyRevision,
    [property: JsonRequired] string ConfigurationHash, [property: JsonRequired] StockSourceReconciliationObservationDto Observation)
{
    internal StockSourceReconciliationInput ToDomain()
    {
        var result = new StockSourceReconciliationInput(PoolId.Value, StockSourceSetupInputs.Number(PoolRevision, false),
            StockSourceSetupInputs.Number(PoolFence, true), PolicyId.Value, StockSourceSetupInputs.Number(PolicyRevision, false),
            ConfigurationHash, Observation?.ToDomain() ?? throw StockSourceReconciliationRules.Invalid());
        StockSourceReconciliationRules.Validate(result); return result;
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceReconciliationProposeDto([property: JsonRequired] StockSourceReconciliationInputDto Input,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceReconciliationPreviewDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Decision, [property: JsonRequired] StockSourceReconciliationObservationDto? Observation);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceReconciliationDecideDto([property: JsonRequired] string Decision,
    [property: JsonRequired] StockSourceReconciliationObservationDto? Observation,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceReconciliationRecoveryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

public sealed record StockSourceReconciliationTargetDto(LongId PoolId, string Label, string PhysicalKey,
    AccessScopeDto OwnerLocation, string OrganizationName, string ShopName, string WarehouseName, string[] ChannelNames);
public sealed record StockSourceReconciliationTargetPageDto(string Total, StockSourceReconciliationTargetDto[] Items);
public sealed record StockSourceReconciliationBalanceDto(LongId BalanceId, LongId ReceiptId, LongId LotId,
    AccessScopeDto Location, LongId SkuId, string SkuCode, LongId DefinitionId, string DefinitionVersion,
    string PackageLabel, string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage, string LotReference,
    string? ExpiresOn, string OnHand, string Reserved, string Held, string Expired, string Version);
public sealed record StockSourceReconciliationBaselineDto(LongId PoolId, string PoolRevision, string PoolFence,
    string PoolLabel, string PhysicalKey, AccessScopeDto OwnerLocation, LongId PolicyId, string PolicyRevision,
    string ReconciliationProcedureReference, string ConfigurationHash, string MaximumAgeMilliseconds,
    DateTimeOffset LatestSourceAt, StockSourceReconciliationBalanceDto[] Balances,
    DateTimeOffset CapturedAt, string BaselineHash, string Binding);
public sealed record StockSourceReconciliationDifferenceDto(LongId BalanceId, string BaselinePackages, string ObservedPackages);
public sealed record StockSourceReconciliationProposalDto(LongId Id, string Version, LongId PoolId,
    StockSourceReconciliationBaselineDto Baseline, StockSourceReconciliationObservationDto Observation,
    StockSourceReconciliationDifferenceDto[] Differences, LongId RecordedBy, DateTimeOffset RecordedAt, string Reason, string Status);
public sealed record StockSourceReconciliationDecisionDto(LongId Id, string Decision,
    StockSourceReconciliationObservationDto? Observation, LongId RecordedBy, DateTimeOffset RecordedAt, string Reason);
public sealed record StockSourceReconciliationDetailDto(StockSourceReconciliationProposalDto Proposal,
    StockSourceReconciliationDecisionDto? Decision);
public sealed record StockSourceReconciliationPageDto(string Total, StockSourceReconciliationDetailDto[] Items);
public sealed record StockSourceReconciliationReviewDto(StockSourceReconciliationProposalDto Proposal, string Decision,
    StockSourceReconciliationBaselineDto? Baseline, StockSourceReconciliationDifferenceDto[] CheckerDifferences,
    bool CanConfirm, string Binding, DateTimeOffset ObservedAt);
public sealed record StockSourceReconciliationResultDto(LongId ProposalId, LongId? DecisionId, LongId PoolId,
    string Revision, string Status, DateTimeOffset RecordedAt);
public sealed record StockSourceReconciliationReceiptDto(string State, StockSourceReconciliationResultDto? Result);

internal static class StockSourceReconciliationResponses
{
    private static string N(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static DateTimeOffset At(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static AccessScopeDto L(BsonValue value) => AccessScopeDto.From(new StockLocation(
        new(value["organizationId"].AsInt64), new(value["shopId"].AsInt64),
        new(value["warehouseId"].AsInt64), new(value["channelId"].AsInt64)).ToScope());
    internal static StockSourceReconciliationTargetDto Target(BsonDocument row) => new(new(row["poolId"].AsInt64),
        row["label"].AsString, row["physicalKey"].AsString, L(row["ownerLocation"]), row["organizationName"].AsString,
        row["shopName"].AsString, row["warehouseName"].AsString, [.. row["channelNames"].AsBsonArray.Select(value => value.AsString)]);
    internal static StockSourceReconciliationObservationDto Observation(BsonValue value) => new(At(value["observedAt"]),
        value["sourceReference"].AsString, [.. value["entries"].AsBsonArray.Select(entry =>
            new StockSourceObservedBalanceDto(new(entry["balanceId"].AsInt64), N(entry["packages"]))) ]);
    internal static StockSourceReconciliationBaselineDto Baseline(BsonValue value) => new(new(value["poolId"].AsInt64),
        N(value["poolRevision"]), N(value["poolFence"]), value["poolLabel"].AsString, value["physicalKey"].AsString,
        L(value["ownerLocation"]), new(value["policyId"].AsInt64), N(value["policyRevision"]),
        value["reconciliationProcedureReference"].AsString, value["configurationHash"].AsString,
        N(value["maximumAgeMilliseconds"]), At(value["latestSourceAt"]),
        [.. value["balances"].AsBsonArray.Select(row => new StockSourceReconciliationBalanceDto(new(row["balanceId"].AsInt64),
            new(row["receiptId"].AsInt64), new(row["lotId"].AsInt64), L(row["location"]), new(row["skuId"].AsInt64),
            row["skuCode"].AsString, new(row["definitionId"].AsInt64), N(row["definitionVersion"]), row["packageLabel"].AsString,
            row["packageKind"].AsString, row["baseUnitCode"].AsString, row["baseUnitsPerPackage"].AsInt32,
            row["lotReference"].AsString, row["expiresOn"].IsBsonNull ? null : row["expiresOn"].AsString,
            N(row["onHand"]), N(row["reserved"]), N(row["held"]), N(row["expired"]), N(row["version"])))],
        At(value["capturedAt"]), value["baselineHash"].AsString, value["binding"].AsString);
    private static StockSourceReconciliationDifferenceDto[] Differences(BsonValue value) =>
        [.. value.AsBsonArray.Select(row => new StockSourceReconciliationDifferenceDto(new(row["balanceId"].AsInt64),
            N(row["baselinePackages"]), N(row["observedPackages"])))];
    internal static StockSourceReconciliationProposalDto Proposal(BsonValue value) => new(new(value["id"].AsInt64),
        N(value["version"]), new(value["poolId"].AsInt64), Baseline(value["baseline"]), Observation(value["observation"]),
        Differences(value["differences"]), new(value["recordedBy"].AsInt64), At(value["recordedAt"]),
        value["reason"].AsString, value["status"].AsString);
    internal static StockSourceReconciliationDetailDto Detail(BsonDocument value) => new(Proposal(value["proposal"]),
        value["decision"].IsBsonNull ? null : new(new(value["decision"]["id"].AsInt64), value["decision"]["decision"].AsString,
            value["decision"]["observation"].IsBsonNull ? null : Observation(value["decision"]["observation"]),
            new(value["decision"]["recordedBy"].AsInt64), At(value["decision"]["recordedAt"]), value["decision"]["reason"].AsString));
    internal static StockSourceReconciliationReviewDto Review(BsonDocument value) => new(Proposal(value["proposal"]),
        value["decision"].AsString, value["baseline"].IsBsonNull ? null : Baseline(value["baseline"]),
        Differences(value["checkerDifferences"]), value["canConfirm"].AsBoolean, value["binding"].AsString, At(value["observedAt"]));
    internal static StockSourceReconciliationResultDto Result(BsonDocument value) => new(new(value["proposalId"].AsInt64),
        value["decisionId"].IsBsonNull ? null : new(value["decisionId"].AsInt64), new(value["poolId"].AsInt64),
        N(value["revision"]), value["status"].AsString, At(value["recordedAt"]));
}
