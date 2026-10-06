using System.Globalization;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Api.Inventory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceActivationExpectedDto(
    [property: JsonRequired] string PoolRevision, [property: JsonRequired] string PoolFence,
    [property: JsonRequired] string SuspensionRevision, [property: JsonRequired] LongId PolicyId,
    [property: JsonRequired] string PolicyRevision, [property: JsonRequired] string ActivationRevision,
    [property: JsonRequired] string ConfigurationHash)
{
    internal StockSourceActivationExpectation ToDomain() {
        var value = new StockSourceActivationExpectation(StockSourceActivationInputs.Number(PoolRevision), StockSourceActivationInputs.Number(PoolFence, true),
            StockSourceActivationInputs.Number(SuspensionRevision, true), PolicyId.Value, StockSourceActivationInputs.Number(PolicyRevision),
            StockSourceActivationInputs.Number(ActivationRevision, true), ConfigurationHash);
        value.Validate(); return value;
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceActivationHandoffDto([property: JsonRequired] string Reference, [property: JsonRequired] string ObservedAt,
    [property: JsonRequired] string PhysicalPackages, [property: JsonRequired] string CarriedValueMinorUnits,
    [property: JsonRequired] bool EmptyPoolConfirmed, [property: JsonRequired] bool PreviousWriterStopped, [property: JsonRequired] bool NoUnrecordedStock)
{
    internal StockSourceEmptyHandoff ToDomain() {
        if (!DateTimeOffset.TryParseExact(ObservedAt, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var observedAt)) throw StockSourceActivationInputs.Invalid();
        var value = new StockSourceEmptyHandoff(Reference, observedAt, StockSourceActivationInputs.Number(PhysicalPackages, true),
            StockSourceActivationInputs.Number(CarriedValueMinorUnits, true), EmptyPoolConfirmed, PreviousWriterStopped, NoUnrecordedStock);
        StockSourceActivationRules.Validate(value); return value;
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceActivationProposeDto([property: JsonRequired] StockSourceActivationExpectedDto Expected,
    [property: JsonRequired] StockSourceActivationHandoffDto Handoff, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceActivationDecideDto([property: JsonRequired] StockSourceActivationExpectedDto Expected,
    [property: JsonRequired] StockSourceActivationHandoffDto? Handoff, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceActivationReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockSourceActivationQueryDto([property: JsonRequired] string Purpose, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceActivationReviewQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId PhysicalPoolId,
    [property: JsonRequired] string Action, [property: JsonRequired] StockSourceActivationHandoffDto? Handoff);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceActivationRecoveryDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId PhysicalPoolId,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
public sealed record StockSourceActivationObligationDto(string Domain, string ProducerRevision, bool Complete, string OpenCount);
public sealed record StockSourceActivationPreviewDto(LongId PhysicalPoolId, StockSourceActivationExpectedDto Expected,
    StockSourcePolicyConfigurationDto Configuration, string RequiredHandoffReference, StockSourceActivationObligationDto[] Obligations, bool Active);
public sealed record StockSourceActivationCommandDto(LongId ProposalId, string ProposalVersion, string Status, LongId PhysicalPoolId,
    LongId? DecisionId, LongId? ActivationId, string RecordedAt);
public sealed record StockSourceActivationDecisionDto(string Action, string RecordedAt, string Reason, StockSourceActivationHandoffDto? Handoff);
public sealed record StockSourceActivationDetailDto(LongId Id, string ProposalVersion, string Status, LongId PhysicalPoolId,
    StockSourceActivationExpectedDto Expected, StockSourcePolicyConfigurationDto Configuration, StockSourceActivationHandoffDto Handoff,
    StockSourceActivationObligationDto[] Obligations, string ProposedAt, string Reason, StockSourceActivationDecisionDto? Decision,
    LongId? DecisionId, LongId? ActivationId);
public sealed record StockSourceActivationReviewDto(LongId ProposalId, LongId PhysicalPoolId, string Action,
    StockSourceActivationExpectedDto Expected, StockSourceActivationHandoffDto? Handoff, string Binding, StockSourceActivationDetailDto Proposal);
public sealed record StockSourceActivationPageDto(string Total, StockSourceActivationDetailDto[] Items);
public sealed record StockSourceActivationReceiptDto(string State, StockSourceActivationCommandDto? Result);

internal static class StockSourceActivationInputs
{
    internal static long Number(string? text, bool zero = false) {
        var number = StockSourceSetupInputs.Number(text, zero);
        return number < long.MaxValue - 2 ? number : throw Invalid();
    }
    internal static void Purpose(string actual, string expected) { if (actual != expected) throw new DomainValidationException("invalid_read_purpose", "Use the required source activation purpose"); }
    internal static StockSourceSetupDecision Action(string value) => value switch { "approve" => StockSourceSetupDecision.Approve,
        "reject" => StockSourceSetupDecision.Reject, _ => throw Invalid() };
    internal static DomainValidationException Invalid() => new("invalid_stock_source_activation", "Provide explicit current empty-pool handoff facts");
}
internal static class StockSourceActivationResponses
{
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static string Time(BsonValue value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static LongId? Optional(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static StockSourceActivationExpectedDto Expected(BsonDocument row) => new(Number(row["poolRevision"]), Number(row["poolFence"]),
        Number(row["suspensionRevision"]), new(row["policyId"].AsInt64), Number(row["policyRevision"]), Number(row["activationRevision"]), row["configurationHash"].AsString);
    private static StockSourceActivationHandoffDto Handoff(BsonDocument row) => new(row["reference"].AsString, Time(row["observedAt"]),
        Number(row["physicalPackages"]), Number(row["carriedValueMinorUnits"]), row["emptyPoolConfirmed"].AsBoolean,
        row["previousWriterStopped"].AsBoolean, row["noUnrecordedStock"].AsBoolean);
    private static StockSourceActivationObligationDto[] Obligations(BsonValue value) => [.. value.AsBsonArray.Select(row =>
        new StockSourceActivationObligationDto(row["domain"].AsString, Number(row["producerRevision"]), row["complete"].AsBoolean, Number(row["openCount"])))];
    private static StockSourcePolicyConfigurationDto Configuration(long poolId, BsonDocument expected, BsonDocument value) =>
        StockSourcePolicyResponses.Preview(new BsonDocument { { "poolId", poolId }, { "poolRevision", expected["poolRevision"] },
            { "suspensionRevision", expected["suspensionRevision"] }, { "policyRevision", expected["policyRevision"] },
            { "configurationHash", expected["configurationHash"] }, { "configuration", value } }).Configuration;
    internal static StockSourceActivationPreviewDto Preview(BsonDocument row) => new(new(row["poolId"].AsInt64), Expected(row["expected"].AsBsonDocument),
        Configuration(row["poolId"].AsInt64, row["expected"].AsBsonDocument, row["configuration"].AsBsonDocument), row["requiredHandoffReference"].AsString,
        Obligations(row["obligations"]), row["active"].AsBoolean);
    internal static StockSourceActivationCommandDto Command(BsonDocument row) => new(new(row["proposalId"].AsInt64), Number(row["revision"]), row["status"].AsString,
        new(row["poolId"].AsInt64), Optional(row["decisionId"]), Optional(row["activationId"]), Time(row["recordedAt"]));
    internal static StockSourceActivationDetailDto Detail(BsonDocument view) {
        var proposal = view["proposal"].AsBsonDocument; var decision = view["decision"].IsBsonNull ? null : view["decision"].AsBsonDocument;
        return new(new(proposal["_id"].AsInt64), Number(proposal["revision"]), view["status"].AsString, new(proposal["poolId"].AsInt64),
            Expected(proposal["expected"].AsBsonDocument), Configuration(proposal["poolId"].AsInt64, proposal["expected"].AsBsonDocument, proposal["configuration"].AsBsonDocument),
            Handoff(proposal["handoff"].AsBsonDocument), Obligations(proposal["obligations"]), Time(proposal["recordedAt"]), proposal["reason"].AsString,
            decision is null ? null : new(decision["decision"].AsString, Time(decision["recordedAt"]), decision["reason"].AsString,
                decision["handoff"].IsBsonNull ? null : Handoff(decision["handoff"].AsBsonDocument)),
            decision is null ? null : new LongId(decision["_id"].AsInt64), decision is null ? null : Optional(decision["activationId"]));
    }
    internal static StockSourceActivationReviewDto Review(BsonDocument row, long poolId) => new(new(row["proposalId"].AsInt64), new(poolId), row["decision"].AsString,
        Expected(row["expected"].AsBsonDocument), row["handoff"].IsBsonNull ? null : Handoff(row["handoff"].AsBsonDocument), row["binding"].AsString,
        Detail(new BsonDocument { { "proposal", row["proposal"] }, { "decision", BsonNull.Value }, { "status", "proposed" } }));
}
