using System.Globalization;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Api.Inventory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockSourceCurrentQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] AccessScopeDto Location, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockSourceRevisionQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] AccessScopeDto Location, LongId? PhysicalPoolId = null, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceRevisionReadDto([property: JsonRequired] string Purpose, [property: JsonRequired] AccessScopeDto Location);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceRevisionRecoveryDto([property: JsonRequired] string Purpose, [property: JsonRequired] AccessScopeDto Location,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

public sealed record StockSourceSettingsDto(AccessScopeDto Location, string PhysicalKey, string Label, string Mode, string Control,
    string MaximumAgeMilliseconds, string SafetyBufferBaseUnits, string? ExternalNamespace, string? ReservationProtocolReference,
    string DiscrepancyOwnerReference, string HandoffReference);
public sealed record StockSourceCurrentDto(LongId PhysicalPoolId, string PoolVersion, string PoolState, StockSourceSettingsDto Settings,
    DateTimeOffset ApprovedAt, LongId CreationProposalId, LongId? RevisionProposalId);
public sealed record StockSourceCurrentPageDto(string Total, StockSourceCurrentDto[] Items);
public sealed record StockSourceRevisionCommandDto(LongId ProposalId, string ProposalVersion, string Status, LongId PhysicalPoolId,
    string? BasePoolVersion, string? AppliedPoolVersion, LongId? DecisionId);
public sealed record StockSourceRevisionReceiptDto(string State, StockSourceRevisionCommandDto? Result);
public sealed record StockSourceRevisionDetailDto(LongId Id, string ProposalVersion, string Status, LongId PhysicalPoolId,
    string BasePoolVersion, StockSourceSettingsDto BaseSettings, StockSourceSettingsDto ProposedSettings,
    DateTimeOffset ProposedAt, string Reason, StockSourceSetupDecisionDto? Decision, string? AppliedPoolVersion);
public sealed record StockSourceRevisionPageDto(string Total, StockSourceRevisionDetailDto[] Items);

internal static class StockSourceRevisionResponses
{
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static DateTimeOffset Time(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static string Status(BsonValue? value) => value is null ? "proposed" : value.AsString switch
        { "approve" => "approved", "reject" => "rejected", "withdraw" => "withdrawn", _ => throw StockSourceSetupInputs.Invalid() };
    internal static StockSourceSettingsDto Settings(BsonDocument input)
    {
        var location = input["location"].AsBsonDocument;
        var scope = new StockLocation(new(location["organizationId"].AsInt64), new(location["shopId"].AsInt64),
            new(location["warehouseId"].AsInt64), new(location["channelId"].AsInt64)).ToScope();
        return new(AccessScopeDto.From(scope), input["physicalKey"].AsString, input["label"].AsString,
            input["mode"].AsString switch { "WebLedger" => "webLedger", "ExternalLedger" => "externalLedger", _ => throw StockSourceSetupInputs.Invalid() },
            input["control"].AsString switch { "ExclusiveWebAllocation" => "exclusiveWebAllocation", "SharedWebLedger" => "sharedWebLedger",
                "ExternalHold" => "externalHold", _ => throw StockSourceSetupInputs.Invalid() },
            Number(input["maximumAgeMilliseconds"]), Number(input["safetyBufferBaseUnits"]),
            input["externalNamespace"].IsBsonNull ? null : input["externalNamespace"].AsString,
            input["reservationProtocolReference"].IsBsonNull ? null : input["reservationProtocolReference"].AsString,
            input["discrepancyOwnerReference"].AsString, input["handoffReference"].AsString);
    }
    internal static StockSourceCurrentDto Current(BsonDocument view)
    {
        var pool = view["pool"].AsBsonDocument;
        return new(new(pool["_id"].AsInt64), Number(pool["revision"]), pool["active"].AsBoolean ? "active" : "inactive",
            Settings(view["input"].AsBsonDocument), Time(view["approvedAt"]), new(view["creationProposalId"].AsInt64),
            view["revisionProposalId"].IsBsonNull ? null : new(view["revisionProposalId"].AsInt64));
    }
    internal static StockSourceRevisionCommandDto Command(BsonDocument result) => new(new(result["proposalId"].AsInt64), "1",
        Status(result.TryGetValue("status", out var status) ? status : null), new(result["poolId"].AsInt64),
        result.TryGetValue("basePoolRevision", out var basis) ? Number(basis) : null,
        result.TryGetValue("poolRevision", out var applied) && !applied.IsBsonNull ? Number(applied) : null,
        result.TryGetValue("decisionId", out var decision) ? new(decision.AsInt64) : null);
    internal static StockSourceRevisionDetailDto Detail(BsonDocument view)
    {
        var proposal = view["proposal"].AsBsonDocument;
        var decision = view["decision"].IsBsonNull ? null : view["decision"].AsBsonDocument;
        return new(new(proposal["_id"].AsInt64), "1", Status(decision?["decision"]), new(proposal["poolId"].AsInt64),
            Number(proposal["basePoolRevision"]), Settings(proposal["baseInput"].AsBsonDocument), Settings(proposal["input"].AsBsonDocument),
            Time(proposal["proposedAt"]), proposal["reason"].AsString,
            decision is null ? null : new(Status(decision["decision"]), Time(decision["decidedAt"]), decision["reviewReference"].AsString, decision["reason"].AsString),
            decision is not null && !decision["poolRevision"].IsBsonNull ? Number(decision["poolRevision"]) : null);
    }
}
