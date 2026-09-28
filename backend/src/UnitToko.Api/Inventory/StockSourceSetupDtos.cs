using System.Globalization;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Api.Inventory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceSetupProposeDto(
    [property: JsonRequired] AccessScopeDto Location, [property: JsonRequired] string PhysicalKey,
    [property: JsonRequired] string Label, [property: JsonRequired] string Mode, [property: JsonRequired] string Control,
    [property: JsonRequired] string MaximumAgeMilliseconds, [property: JsonRequired] string SafetyBufferBaseUnits,
    [property: JsonRequired] string DiscrepancyOwnerReference, [property: JsonRequired] string HandoffReference,
    [property: JsonRequired] string Reason, string? ExternalNamespace = null, string? ReservationProtocolReference = null)
{
    internal StockSourceSetupInput ToDomain()
    {
        var input = new StockSourceSetupInput(StockSourceSetupInputs.Location(Location), PhysicalKey, Label,
            Mode switch { "webLedger" => StockAuthorityMode.WebLedger, "externalLedger" => StockAuthorityMode.ExternalLedger, _ => throw StockSourceSetupInputs.Invalid() },
            Control switch { "exclusiveWebAllocation" => StockPoolControl.ExclusiveWebAllocation,
                "sharedWebLedger" => StockPoolControl.SharedWebLedger, "externalHold" => StockPoolControl.ExternalHold, _ => throw StockSourceSetupInputs.Invalid() },
            StockSourceSetupInputs.Number(MaximumAgeMilliseconds, false), StockSourceSetupInputs.Number(SafetyBufferBaseUnits, true),
            ExternalNamespace, ReservationProtocolReference, DiscrepancyOwnerReference, HandoffReference);
        StockSourceSetupPolicy.Validate(input); return input;
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceSetupDecideDto([property: JsonRequired] string ReviewReference, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockSourceSetupQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] AccessScopeDto Location, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceSetupRecoveryDto([property: JsonRequired] string Purpose, [property: JsonRequired] AccessScopeDto Location,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
public sealed record StockSourceSetupCommandDto(LongId ProposalId, string ProposalVersion, string Status, LongId? DecisionId, LongId? PhysicalPoolId);
public sealed record StockSourceSetupReceiptDto(string State, StockSourceSetupCommandDto? Result);
public sealed record StockSourceSetupDecisionDto(string Status, DateTimeOffset DecidedAt, string ReviewReference, string Reason);
public sealed record StockSourceSetupDetailDto(LongId Id, string ProposalVersion, string Status, AccessScopeDto Location,
    string PhysicalKey, string Label, string Mode, string Control, string MaximumAgeMilliseconds, string SafetyBufferBaseUnits,
    string? ExternalNamespace, string? ReservationProtocolReference, string DiscrepancyOwnerReference, string HandoffReference,
    DateTimeOffset ProposedAt, string Reason, StockSourceSetupDecisionDto? Decision, LongId? PhysicalPoolId);
public sealed record StockSourceSetupPageDto(string Total, StockSourceSetupDetailDto[] Items);

internal static class StockSourceSetupInputs
{
    internal static StockLocation Location(AccessScopeDto? input)
    {
        if (input is null) throw Invalid();
        var scope = input.ToDomain();
        StockCountPolicy.Location(scope);
        return new(new(scope.OrganizationId!.Value), new(scope.ShopId!.Value), new(scope.WarehouseId!.Value), new(scope.ChannelId!.Value));
    }
    internal static long Number(string? text, bool zero)
    {
        if (text is null || text.Length is < 1 or > 19 || text.Any(c => c is < '0' or > '9') ||
            text.Length > 1 && text[0] == '0' || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < (zero ? 0 : 1)) throw Invalid();
        return value;
    }
    internal static string Reason(string? value) => StockSourceSetupPolicy.Text(value, 1000) ? value! : throw Invalid();
    internal static void Purpose(string actual, string expected)
    { if (actual != expected) throw new DomainValidationException("invalid_read_purpose", "Use the required source-setup purpose"); }
    internal static DomainValidationException Invalid() => new("invalid_stock_source_setup", "Provide complete explicit source-setup facts");
}

internal static class StockSourceSetupResponses
{
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Status(BsonDocument? decision) => decision is null ? "proposed" : decision["decision"].AsString switch
        { "approve" => "approved", "reject" => "rejected", "withdraw" => "withdrawn", _ => throw StockSourceSetupInputs.Invalid() };
    internal static StockSourceSetupCommandDto Command(BsonDocument row)
    {
        var status = row.TryGetValue("status", out var value) ? value.AsString switch
            { "approve" => "approved", "reject" => "rejected", "withdraw" => "withdrawn", _ => throw StockSourceSetupInputs.Invalid() } : "proposed";
        return new(new(row["proposalId"].AsInt64), "1", status,
            row.TryGetValue("decisionId", out var decision) ? new(decision.AsInt64) : null,
            row.TryGetValue("poolId", out var pool) && !pool.IsBsonNull ? new(pool.AsInt64) : null);
    }
    internal static StockSourceSetupDetailDto Detail(BsonDocument view)
    {
        var proposal = view["proposal"].AsBsonDocument; var input = proposal["input"].AsBsonDocument;
        var location = input["location"].AsBsonDocument;
        var scope = new StockLocation(new(location["organizationId"].AsInt64), new(location["shopId"].AsInt64),
            new(location["warehouseId"].AsInt64), new(location["channelId"].AsInt64)).ToScope();
        var decision = view["decision"].IsBsonNull ? null : view["decision"].AsBsonDocument;
        var mode = input["mode"].AsString switch { "WebLedger" => "webLedger", "ExternalLedger" => "externalLedger", _ => throw StockSourceSetupInputs.Invalid() };
        var control = input["control"].AsString switch { "ExclusiveWebAllocation" => "exclusiveWebAllocation",
            "SharedWebLedger" => "sharedWebLedger", "ExternalHold" => "externalHold", _ => throw StockSourceSetupInputs.Invalid() };
        return new(new(proposal["_id"].AsInt64), "1", Status(decision), AccessScopeDto.From(scope),
            input["physicalKey"].AsString, input["label"].AsString, mode, control, Number(input["maximumAgeMilliseconds"].AsInt64),
            Number(input["safetyBufferBaseUnits"].AsInt64), input["externalNamespace"].IsBsonNull ? null : input["externalNamespace"].AsString,
            input["reservationProtocolReference"].IsBsonNull ? null : input["reservationProtocolReference"].AsString,
            input["discrepancyOwnerReference"].AsString, input["handoffReference"].AsString,
            new(proposal["proposedAt"].ToUniversalTime(), TimeSpan.Zero), proposal["reason"].AsString,
            decision is null ? null : new(Status(decision), new(decision["decidedAt"].ToUniversalTime(), TimeSpan.Zero),
                decision["reviewReference"].AsString, decision["reason"].AsString),
            decision is not null && !decision["poolId"].IsBsonNull ? new(decision["poolId"].AsInt64) : null);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceWarehouseQueryDto([property: JsonRequired] string Purpose);
public sealed record StockSourceWarehouseDto(AccessScopeDto Location, string OrganizationName, string ShopName, string WarehouseName, string ChannelName);
public sealed record StockSourceWarehousePageDto(StockSourceWarehouseDto[] Items);