using System.Globalization;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Api.Inventory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceAliasProposeDto([property: JsonRequired] AccessScopeDto RequestedLocation,
    [property: JsonRequired] string ExpectedAliasVersion, [property: JsonRequired] bool Enabled, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceAliasReadDto([property: JsonRequired] string Purpose, [property: JsonRequired] AccessScopeDto RequestedLocation);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockSourceAliasHistoryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] AccessScopeDto RequestedLocation, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceAliasRecoveryDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId PhysicalPoolId,
    [property: JsonRequired] AccessScopeDto RequestedLocation, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

public sealed record StockSourceAliasCommandDto(LongId ProposalId, string ProposalVersion, string Status, LongId PhysicalPoolId,
    string PoolVersion, string? BaseAliasVersion, string? AppliedAliasVersion, LongId? DecisionId);
public sealed record StockSourceAliasReceiptDto(string State, StockSourceAliasCommandDto? Result);
public sealed record StockSourceAliasDetailDto(LongId Id, string ProposalVersion, string Status, LongId PhysicalPoolId,
    string PoolVersion, AccessScopeDto OwnerLocation, AccessScopeDto RequestedLocation, string PhysicalKey,
    string BaseAliasVersion, bool Enabled, DateTimeOffset ProposedAt, string Reason, StockSourceSetupDecisionDto? Decision, string? AppliedAliasVersion);
public sealed record StockSourceAliasPageDto(string Total, StockSourceAliasDetailDto[] Items);
public sealed record StockSourceAliasCurrentDto(StockSourceCurrentDto Source, AccessScopeDto RequestedLocation,
    string BindingState, StockSourceAliasDetailDto? Mapping);

internal static class StockSourceAliasResponses
{
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static DateTimeOffset Time(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static string Status(BsonValue? value) => value is null ? "proposed" : value.AsString switch
        { "approve" => "approved", "reject" => "rejected", "withdraw" => "withdrawn", _ => throw StockSourceSetupInputs.Invalid() };
    private static AccessScopeDto Location(BsonValue value)
    {
        var location = value.AsBsonDocument;
        return AccessScopeDto.From(new StockLocation(new(location["organizationId"].AsInt64), new(location["shopId"].AsInt64),
            new(location["warehouseId"].AsInt64), new(location["channelId"].AsInt64)).ToScope());
    }
    internal static StockSourceAliasCommandDto Command(BsonDocument result) => new(new(result["proposalId"].AsInt64), "1",
        Status(result.TryGetValue("status", out var status) ? status : null), new(result["poolId"].AsInt64), Number(result["poolRevision"]),
        result.TryGetValue("baseAliasRevision", out var basis) ? Number(basis) : null,
        result.TryGetValue("aliasRevision", out var applied) && !applied.IsBsonNull ? Number(applied) : null,
        result.TryGetValue("decisionId", out var decision) ? new(decision.AsInt64) : null);
    internal static StockSourceAliasDetailDto Detail(BsonDocument view)
    {
        var proposal = view["proposal"].AsBsonDocument;
        var decision = view["decision"].IsBsonNull ? null : view["decision"].AsBsonDocument;
        return new(new(proposal["_id"].AsInt64), "1", Status(decision?["decision"]), new(proposal["poolId"].AsInt64),
            Number(proposal["poolRevision"]), Location(proposal["ownerLocation"]), Location(proposal["requestedLocation"]),
            proposal["physicalKey"].AsString, Number(proposal["baseAliasRevision"]), proposal["enabled"].AsBoolean,
            Time(proposal["proposedAt"]), proposal["reason"].AsString,
            decision is null ? null : new(Status(decision["decision"]), Time(decision["decidedAt"]), decision["reviewReference"].AsString, decision["reason"].AsString),
            decision is not null && !decision["aliasRevision"].IsBsonNull ? Number(decision["aliasRevision"]) : null);
    }
    internal static StockSourceAliasCurrentDto Current(BsonDocument view, StockLocation requested)
    {
        var mapping = view["mapping"].IsBsonNull ? null : Detail(view["mapping"].AsBsonDocument);
        var state = mapping is null ? "absent" : !mapping.Enabled ? "disabled" : view["bindingCurrent"].AsBoolean ? "current" : "stale";
        return new(StockSourceRevisionResponses.Current(view["currentSource"].AsBsonDocument), AccessScopeDto.From(requested.ToScope()), state, mapping);
    }
}
