using System.Globalization;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
namespace UnitToko.Api.Inventory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceSuspensionPreviewRequestDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceSuspensionReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record StockSourceSuspensionHistoryDto([property: JsonRequired] string Purpose, int Offset = 0, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceSuspensionRequestDto([property: JsonRequired] string ExpectedSuspensionVersion,
    [property: JsonRequired] string ConfigurationHash, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StockSourceSuspensionRecoveryDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId PhysicalPoolId,
    [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

public sealed record StockSourceSuspensionPreviewDto(LongId PhysicalPoolId, string PoolVersion, string SuspensionVersion,
    bool Active, string ConfigurationHash, AccessScopeDto[] AffectedLocations);
public sealed record StockSourceSuspensionCommandDto(LongId SuspensionId, LongId PhysicalPoolId, string PoolVersion, string SuspensionVersion, string Status);
public sealed record StockSourceSuspensionDetailDto(LongId Id, LongId PhysicalPoolId, string PoolVersion, string SuspensionVersion,
    string PhysicalKey, AccessScopeDto OwnerLocation, AccessScopeDto[] AffectedLocations, bool WasActive, DateTimeOffset SuspendedAt, string Reason);
public sealed record StockSourceSuspensionPageDto(string Total, StockSourceSuspensionDetailDto[] Items);
public sealed record StockSourceSuspensionReceiptDto(string State, StockSourceSuspensionCommandDto? Result);

internal static class StockSourceSuspensionResponses
{
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static AccessScopeDto Location(BsonValue value)
    {
        var row = value.AsBsonDocument;
        return AccessScopeDto.From(new StockLocation(new(row["organizationId"].AsInt64), new(row["shopId"].AsInt64),
            new(row["warehouseId"].AsInt64), new(row["channelId"].AsInt64)).ToScope());
    }
    internal static StockSourceSuspensionPreviewDto Preview(BsonDocument row) => new(new(row["poolId"].AsInt64), Number(row["poolRevision"]),
        Number(row["suspensionRevision"]), row["active"].AsBoolean, row["configurationHash"].AsString,
        [.. row["affectedLocations"].AsBsonArray.Select(Location)]);
    internal static StockSourceSuspensionCommandDto Command(BsonDocument row) => new(new(row["suspensionId"].AsInt64), new(row["poolId"].AsInt64),
        Number(row["poolRevision"]), Number(row["suspensionRevision"]), "suspended");
    internal static StockSourceSuspensionDetailDto Detail(BsonDocument row)
    {
        var locations = row["configuration"]["mappings"].AsBsonArray.Select(alias => alias["requestedLocation"]).Append(row["ownerLocation"])
            .OrderBy(location => location["channelId"].AsInt64).Select(Location).ToArray();
        return new(new(row["_id"].AsInt64), new(row["poolId"].AsInt64), Number(row["poolRevision"]), Number(row["suspensionRevision"]),
            row["physicalKey"].AsString, Location(row["ownerLocation"]), locations, row["configuration"]["source"]["active"].AsBoolean,
            new(row["suspendedAt"].ToUniversalTime(), TimeSpan.Zero), row["reason"].AsString);
    }
}