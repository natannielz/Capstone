using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;

namespace UnitToko.Api.Inventory;

public static class PackConversionExecutionEndpoints
{
    public static void MapPackConversionExecutionEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/inventory/pack-conversions/executions").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        group.AddEndpointFilter(async (context, next) => {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store, max-age=0";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });
        foreach (var status in new[] { 400, 401, 403, 404, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/sources/query", async (PackConversionExecutionSourcesQueryDto request, HttpContext http,
            [FromServices] PackConversionService service, CancellationToken ct) => {
            Purpose(request.Purpose, "pack-conversion-sources");
            return TypedResults.Ok(PackConversionExecutionResponses.Sources(await service.SourcesAsync(Actor(http), StockCountInputs.Location(request.Location),
                request.RuleId.Value, request.RuleVersion.Value, request.BeforeSourceBalanceId?.Value ?? 0, request.Limit, ct)));
        }).WithName("PackConversionExecutionSources");
        group.MapPost("/preview/query", async (PackConversionExecutionPreviewQueryDto request, HttpContext http,
            [FromServices] PackConversionService service, CancellationToken ct) => {
            Purpose(request.Purpose, "pack-conversion-preview");
            var row = await service.PreviewAsync(Actor(http), request.Input?.ToDomain() ?? throw PackConversionExecutionPolicy.Invalid(), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row["expected"]["sourceBalanceRevision"].AsInt64);
            return TypedResults.Ok(PackConversionExecutionResponses.Preview(row));
        }).WithName("PackConversionExecutionPreview");
        group.MapPost("", async (PackConversionExecutionExecuteDto request, HttpContext http,
            [FromServices] PackConversionService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            var expected = request.Expected?.ToDomain() ?? throw PackConversionExecutionPolicy.Invalid();
            if (AccessRequestMetadata.ExpectedVersion(http.Request) != expected.SourceBalanceRevision) throw PackConversionExecutionPolicy.Invalid();
            var row = await service.ExecuteAsync(Actor(http), request.Input?.ToDomain() ?? throw PackConversionExecutionPolicy.Invalid(), expected,
                request.Binding, new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(PackConversionExecutionResponses.Command(row));
        }).WithName("ExecutePackConversion");
        group.MapPost("/query", async (PackConversionExecutionHistoryQueryDto request, HttpContext http,
            [FromServices] PackConversionService service, CancellationToken ct) => {
            Purpose(request.Purpose, "pack-conversion-history");
            return TypedResults.Ok(PackConversionExecutionResponses.History(await service.ListAsync(Actor(http), StockCountInputs.Location(request.Location),
                request.BeforeConversionId?.Value ?? 0, request.Limit, ct)));
        }).WithName("PackConversionExecutionHistory");
        group.MapPost("/{id}/query", async (string id, PackConversionExecutionReadDto request, HttpContext http,
            [FromServices] PackConversionService service, CancellationToken ct) => {
            Purpose(request.Purpose, "pack-conversion-history");
            var row = await service.ReadExecutionAsync(Actor(http), LongId.Parse(id).Value, ct);
            if (row is null) return Results.NotFound();
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return Results.Ok(PackConversionExecutionResponses.Detail(row));
        }).WithName("PackConversionExecutionDetail").Produces<PackConversionExecutionDetailDto>(200);
        group.MapPost("/operations/query", async (PackConversionExecutionReceiptQueryDto request, HttpContext http,
            [FromServices] PackConversionService service, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery");
            var row = await service.RecoverAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new PackConversionExecutionReceiptDto(row is null ? "not_confirmed" : "committed",
                row is null ? null : PackConversionExecutionResponses.Command(row)));
        }).WithName("PackConversionExecutionReceipt");
    }
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
    private static void Purpose(string actual, string expected) {
        if (actual != expected) throw new DomainValidationException("invalid_read_purpose", "Use the stated pack-conversion purpose");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record PackConversionExecutionInputDto([property: JsonRequired] AccessScopeDto Location,
    [property: JsonRequired] LongId RuleId, [property: JsonRequired] LongId RuleVersion,
    [property: JsonRequired] LongId SourceBalanceId, [property: JsonRequired] int Steps) {
    internal PackConversionInput ToDomain() => PackConversionExecutionPolicy.Copy(new PackConversionInput(StockCountInputs.Location(Location),
        RuleId.Value, RuleVersion.Value, SourceBalanceId.Value, Steps));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackConversionExecutionExpectedDto([property: JsonRequired] LongId SourceBalanceRevision,
    [property: JsonRequired] LongId PoolRevision, [property: JsonRequired] string PoolFence, [property: JsonRequired] LongId LotValueRevision) {
    internal PackConversionExpectation ToDomain() => PackConversionExecutionPolicy.Copy(new PackConversionExpectation(SourceBalanceRevision.Value,
        PoolRevision.Value, PoolFence == "0" ? 0 : PoolFence is null ? throw PackConversionExecutionPolicy.Invalid() : LongId.Parse(PoolFence).Value, LotValueRevision.Value));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackConversionExecutionReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record PackConversionExecutionSourcesQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] AccessScopeDto Location,
    [property: JsonRequired] LongId RuleId, [property: JsonRequired] LongId RuleVersion, LongId? BeforeSourceBalanceId = null, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record PackConversionExecutionHistoryQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] AccessScopeDto Location,
    LongId? BeforeConversionId = null, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackConversionExecutionPreviewQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] PackConversionExecutionInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackConversionExecutionExecuteDto([property: JsonRequired] PackConversionExecutionInputDto Input,
    [property: JsonRequired] PackConversionExecutionExpectedDto Expected, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PackConversionExecutionReceiptQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);

public sealed record PackConversionExecutionStockDto(LongId BalanceId, LongId PoolId, LongId LotId, string OnHand, string Reserved, string Held, string Expired, string Revision);
public sealed record PackConversionExecutionSourceDto(LongId SourceBalanceId, string SourceBalanceRevision, LongId RuleId, string RuleVersion,
    StockCountPackageOptionDto Source, StockCountPackageOptionDto Destination, string ReceiptReference, string LotReference, LongId LotId,
    string? ExpiresOn, string AvailableSourcePackages, string RemainingPackages, string RemainingValue, string MaximumSteps, string Policy);
public sealed record PackConversionExecutionSourcesDto(PackConversionExecutionSourceDto[] Items, string UnavailableCount, LongId? NextSourceBalanceId);
public sealed record PackConversionExecutionPreviewDto(PackConversionExecutionInputDto Input, PackConversionExecutionExpectedDto Expected,
    StockCountPackageOptionDto Source, StockCountPackageOptionDto Destination, PackConversionExecutionStockDto StockBefore,
    PackConversionExecutionStockDto StockAfter, string RemainingPackages, string RemainingValue, string SourcePackages, string DestinationPackages,
    string BaseUnits, string Policy, string Binding, DateTimeOffset ObservedAt);
public sealed record PackConversionExecutionCommandDto(LongId ConversionId, string Revision, string Status, string SourcePackages,
    string DestinationPackages, string BaseUnits, LongId DestinationBalanceId, LongId DestinationLotId, LongId MovementId, LongId ValueTransitionId, string TransferredValue);
public sealed record PackConversionExecutionDetailDto(LongId Id, string Revision, string Status, PackConversionExecutionInputDto Input,
    StockCountPackageOptionDto Source, StockCountPackageOptionDto Destination, string ReceiptReference, string SourceLotReference,
    PackConversionExecutionStockDto StockBefore, PackConversionExecutionStockDto StockAfter, PackConversionExecutionStockDto DestinationAfter,
    string SourcePackages, string DestinationPackages, string BaseUnits, string Policy, string TransferredValue, DateTimeOffset RecordedAt, string Reason);
public sealed record PackConversionExecutionHistoryDto(PackConversionExecutionDetailDto[] Items, LongId? NextConversionId);
public sealed record PackConversionExecutionReceiptDto(string State, PackConversionExecutionCommandDto? Result);

internal static class PackConversionExecutionResponses
{
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static DateTimeOffset Time(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static LongId? OptionalId(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static StockCountPackageOptionDto Package(BsonValue value) {
        var sku = value["sku"]; var definition = value["definition"];
        return new(new(sku["_id"].AsInt64), Number(sku["revision"].AsInt64), new(definition["_id"].AsInt64), Number(definition["version"].AsInt64),
            sku["sku"].AsString, value["family"]["name"].AsString, definition["packageLabel"].AsString, definition["packageKind"].AsString,
            definition["baseUnitCode"].AsString, definition["baseUnitsPerPackage"].AsInt32);
    }
    private static PackConversionExecutionInputDto Input(BsonValue value) => new(AccessScopeDto.From(new StockLocation(
        new(value["location"]["organizationId"].AsInt64), new(value["location"]["shopId"].AsInt64),
        new(value["location"]["warehouseId"].AsInt64), new(value["location"]["channelId"].AsInt64)).ToScope()),
        new(value["ruleId"].AsInt64), new(value["ruleVersion"].AsInt64), new(value["sourceBalanceId"].AsInt64), value["steps"].AsInt32);
    private static PackConversionExecutionExpectedDto Expected(BsonValue value) => new(new(value["sourceBalanceRevision"].AsInt64), new(value["poolRevision"].AsInt64),
        Number(value["poolFence"].AsInt64), new(value["lotValueRevision"].AsInt64));
    private static PackConversionExecutionStockDto Stock(BsonValue value) => new(new(value["_id"].AsInt64), new(value["physicalPoolId"].AsInt64), new(value["lotId"].AsInt64),
        Number(value["onHand"].AsInt64), Number(value["reserved"].AsInt64), Number(value["held"].AsInt64), Number(value["expired"].AsInt64), Number(value["version"].AsInt64));
    internal static PackConversionExecutionSourcesDto Sources(BsonDocument value) => new([.. value["items"].AsBsonArray.Select(row =>
        new PackConversionExecutionSourceDto(new(row["sourceBalanceId"].AsInt64), Number(row["sourceBalanceRevision"].AsInt64), new(row["ruleId"].AsInt64), Number(row["ruleVersion"].AsInt64),
            Package(row["catalog"]["source"]), Package(row["catalog"]["destination"]), row["receiptReference"].AsString, row["lotReference"].AsString, new(row["lotId"].AsInt64),
            row["expiresOn"].IsBsonNull ? null : row["expiresOn"].AsString, Number(row["availableSourcePackages"].AsInt64), Number(row["remainingPackages"].AsInt64),
            row["remainingValue"].AsString, Number(row["maximumSteps"].AsInt64), row["policy"].AsString))], Number(value["unavailableCount"].AsInt64), OptionalId(value["nextSourceBalanceId"]));
    internal static PackConversionExecutionPreviewDto Preview(BsonDocument row) => new(Input(row["input"]), Expected(row["expected"]),
        Package(row["rule"]["catalog"]["source"]), Package(row["rule"]["catalog"]["destination"]), Stock(row["before"]), Stock(row["after"]),
        Number(row["sourceValue"]["remainingPackages"].AsInt64), row["sourceValue"]["remainingValue"].AsString,
        Number(row["quantities"]["sourcePackages"].AsInt64), Number(row["quantities"]["destinationPackages"].AsInt64), Number(row["quantities"]["baseUnits"].AsInt64),
        row["policy"].AsString, row["binding"].AsString, Time(row["observedAt"]));
    internal static PackConversionExecutionCommandDto Command(BsonDocument row) => new(new(row["conversionId"].AsInt64), Number(row["revision"].AsInt64), row["status"].AsString,
        Number(row["sourcePackages"].AsInt64), Number(row["destinationPackages"].AsInt64), Number(row["baseUnits"].AsInt64), new(row["destinationBalanceId"].AsInt64),
        new(row["destinationLotId"].AsInt64), new(row["movementId"].AsInt64), new(row["valueTransitionId"].AsInt64), row["transferredValue"].AsString);
    internal static PackConversionExecutionDetailDto Detail(BsonDocument view) {
        var row = view["conversion"];
        return new(new(row["_id"].AsInt64), "1", "converted", Input(row["input"]), Package(row["catalog"]["source"]), Package(row["catalog"]["destination"]),
            view["receiptReference"].AsString, view["sourceLotReference"].AsString, Stock(row["before"]), Stock(row["after"]), Stock(row["destinationAfter"]),
            Number(row["quantities"]["sourcePackages"].AsInt64), Number(row["quantities"]["destinationPackages"].AsInt64), Number(row["quantities"]["baseUnits"].AsInt64),
            row["policy"].AsString, row["transferredValue"].AsString, Time(row["recordedAt"]), row["reason"].AsString);
    }
    internal static PackConversionExecutionHistoryDto History(BsonDocument row) => new([.. row["items"].AsBsonArray.Select(value => Detail(value.AsBsonDocument))], OptionalId(row["nextConversionId"]));
}
