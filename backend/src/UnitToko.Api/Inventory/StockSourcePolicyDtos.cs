using System.Text.Json.Serialization;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
namespace UnitToko.Api.Inventory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourcePolicyPreviewRequestDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourcePolicyReviewRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Action)
{
    internal StockSourceSetupDecision Decision() => Action switch
    {
        "approve" => StockSourceSetupDecision.Approve, "reject" => StockSourceSetupDecision.Reject,
        "withdraw" => StockSourceSetupDecision.Withdraw, _ => throw StockSourceSetupInputs.Invalid()
    };
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourcePolicyReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockSourcePolicyHistoryDto([property: JsonRequired] string Purpose, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourcePolicyInputDto([property: JsonRequired] string Writer, [property: JsonRequired] string? ExternalTenant,
    [property: JsonRequired] string FeedIntervalMilliseconds, [property: JsonRequired] string ReconciliationProcedureReference,
    [property: JsonRequired] string OutageProcedureReference)
{
    internal StockSourceOperatingPolicyInput ToDomain() => new(Writer switch
        { "unitToko" => StockMovementWriter.UnitToko, "externalSystem" => StockMovementWriter.ExternalSystem, _ => throw StockSourceSetupInputs.Invalid() },
        ExternalTenant, StockSourceSetupInputs.Number(FeedIntervalMilliseconds, true), ReconciliationProcedureReference, OutageProcedureReference);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourcePolicyProposeDto([property: JsonRequired] string ExpectedSuspensionVersion,
    [property: JsonRequired] string ExpectedPolicyVersion, [property: JsonRequired] string ConfigurationHash,
    [property: JsonRequired] StockSourcePolicyInputDto Input, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourcePolicyRecoveryDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId PhysicalPoolId,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

public sealed record StockSourcePolicyMappingDto(LongId AliasId, string AliasVersion, string PoolVersion, AccessScopeDto Location,
    bool Enabled, LongId ProposalId, LongId DecisionId);
public sealed record StockSourcePolicyConfigurationDto(StockSourceSettingsDto Settings, bool Active, DateTimeOffset SourceApprovedAt,
    LongId CreationProposalId, LongId? RevisionProposalId, StockSourcePolicyMappingDto[] Mappings);
public sealed record StockSourcePolicyBindingDto(string PoolVersion, string SuspensionVersion, string PolicyVersion, string ConfigurationHash);
public sealed record StockSourcePolicyPreviewDto(LongId PhysicalPoolId, string PoolVersion, string SuspensionVersion, string PolicyVersion,
    string ConfigurationHash, StockSourcePolicyConfigurationDto Configuration);
public sealed record StockSourcePolicyCommandDto(LongId ProposalId, string ProposalVersion, string Status, LongId PhysicalPoolId,
    string? BasePolicyVersion, LongId? DecisionId, LongId? PolicyId, string? PolicyVersion);
public sealed record StockSourcePolicyDetailDto(LongId Id, string ProposalVersion, string Status, LongId PhysicalPoolId,
    string PoolVersion, string SuspensionVersion, string BasePolicyVersion, string ConfigurationHash,
    StockSourcePolicyConfigurationDto Configuration, StockSourcePolicyInputDto Input, DateTimeOffset ProposedAt, string Reason,
    StockSourceSetupDecisionDto? Decision, LongId? DecisionId, LongId? PolicyId, string? PolicyVersion);
public sealed record StockSourcePolicyReviewDto(string Action, bool BindingCurrent, StockSourcePolicyBindingDto Current,
    StockSourcePolicyDetailDto Proposal);
public sealed record StockSourcePolicyLatestDto(bool BindingCurrent, StockSourcePolicyDetailDto? Policy);
public sealed record StockSourcePolicyPageDto(string Total, StockSourcePolicyDetailDto[] Items);
public sealed record StockSourcePolicyReceiptDto(string State, StockSourcePolicyCommandDto? Result);