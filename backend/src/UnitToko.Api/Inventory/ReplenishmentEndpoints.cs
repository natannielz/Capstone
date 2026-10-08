using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Inventory;

namespace UnitToko.Api.Inventory;

public static class ReplenishmentEndpoints
{
    public static void MapReplenishmentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/inventory/replenishment").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/warehouses/query", async (StockCountReadDto body, HttpContext http, [FromServices] CatalogAuthoringService catalog, CancellationToken ct) => {
            Purpose(body.Purpose);
            return TypedResults.Ok(StockCountResponses.Warehouses(await catalog.ReplenishmentWarehousesAsync(Actor(http), new(body.Purpose, http.TraceIdentifier), ct)));
        }).WithName("ReplenishmentWarehouses");
        group.MapPost("/packages/query", async (StockCountPackageQueryDto body, HttpContext http, [FromServices] CatalogAuthoringService catalog, CancellationToken ct) => {
            Purpose(body.Purpose);
            return TypedResults.Ok(StockCountResponses.Packages(await catalog.ReplenishmentPackagesAsync(Actor(http), StockCountInputs.Location(body.Location),
                body.Offset, body.Limit, body.Search, new(body.Purpose, http.TraceIdentifier), ct)));
        }).WithName("ReplenishmentPackages");
        group.MapPost("/sources/query", async (StockCountSourceQueryDto body, HttpContext http, [FromServices] ReplenishmentService service, CancellationToken ct) => {
            Purpose(body.Purpose);
            if (body.Offset != 0 || body.Limit != 25) throw Invalid();
            return TypedResults.Ok(new ReplenishmentSourcesDto([.. (await service.SourcesAsync(Actor(http), StockCountInputs.Location(body.Location), ct))
                .Select(item => new ReplenishmentSourceDto(new(item.Id), Number(item.Revision)!, item.Label, item.PhysicalKey, item.Active))]));
        }).WithName("ReplenishmentSources");
        group.MapPost("", async (ReplenishmentRecordDto body, HttpContext http, [FromServices] ReplenishmentService service,
            [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) => {
            var result = await service.EvaluateAsync(Actor(http), body.ToDomain(), new(AccessRequestMetadata.IdempotencyKey(http.Request), body.Reason, http.TraceIdentifier), ct);
            return TypedResults.Ok(View(result));
        }).WithName("EvaluateReplenishment");
        group.MapPost("/history/query", async (ReplenishmentHistoryQueryDto body, HttpContext http, [FromServices] ReplenishmentService service, CancellationToken ct) => {
            Purpose(body.Purpose);
            var page = await service.HistoryAsync(Actor(http), StockCountInputs.Location(body.Location), body.Before?.Value, body.Limit, ct);
            return TypedResults.Ok(new ReplenishmentHistoryDto([.. page.Items.Select(View)], Number(page.NextBefore)));
        }).WithName("ReplenishmentHistory");
        group.MapPost("/{id}/query", async (string id, StockCountReadDto body, HttpContext http, [FromServices] ReplenishmentService service, CancellationToken ct) => {
            Purpose(body.Purpose); var result = await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct);
            return TypedResults.Ok(result is null ? null : View(result));
        }).WithName("ReadReplenishment");
        group.MapPost("/operations/query", async (StockCountReceiptQueryDto body, HttpContext http, [FromServices] ReplenishmentService service, CancellationToken ct) => {
            if (body.Purpose != "operation-recovery" || body.Operation != "inventory.replenishment.evaluate") throw Invalid();
            var result = await service.RecoverAsync(Actor(http), body.Key, ct);
            return TypedResults.Ok(new ReplenishmentRecoveryDto(result is null ? "not_confirmed" : "committed", result is null ? null : View(result)));
        }).WithName("RecoverReplenishment");
    }
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
    private static void Purpose(string purpose) { if (purpose != "inventory-replenishment") throw Invalid(); }
    internal static DomainValidationException Invalid() => new("invalid_replenishment_review", "Choose exact source and packaging with explicit valid planning inputs");
    internal static string? Number(long? value) => value?.ToString(CultureInfo.InvariantCulture);
    internal static ReplenishmentValuesDto Values(ReplenishmentInputs values) => new(values.DailyDemandPerDay?.ToString(CultureInfo.InvariantCulture), values.LeadTimeDays,
        Number(values.SafetyStockPackages), Number(values.ReorderPointPackages), Number(values.PurchaseMinimumPackages)!, Number(values.PurchaseIncrementPackages)!);
    private static ReplenishmentViewDto View(ReplenishmentEvaluation result) {
        var advice = result.Advice; var definition = result.Definition;
        return new(new(result.Id), AccessScopeDto.From(result.Input.Location), new(result.Input.PoolId), Number(result.Input.ExpectedPoolRevision)!,
            new(result.Input.SkuId), Number(result.Input.ExpectedSkuRevision)!, new(result.Input.DefinitionId), Number(definition.DefinitionVersion)!,
            definition.SkuCode, definition.PackageLabel, definition.PackageKind, definition.BaseUnitCode, definition.BaseUnitsPerPackage,
            Values(result.Input.Inputs), result.Input.InputReference, result.Reason, result.SourceHash, advice.RuleVersion, advice.ObservedAt, advice.Horizon,
            result.Stock.CurrentAuthority, Number(result.Stock.UsableBeforeReservations), Number(result.Stock.ReservedPackages), Number(result.Stock.AdditionalUnreservedCommitments),
            Number(advice.AvailableAfterCommitments), advice.ThresholdAlert, Number(advice.EligibleSupplyPackages), Number(advice.InventoryPosition),
            Number(advice.RequiredStockPackages), Number(advice.SuggestedPurchasePackages), [.. advice.Warnings],
            [.. result.Supplies.Select(supply => new ReplenishmentSupplyDto(supply.Reference, Number(supply.Packages)!, supply.ExpectedAt,
                supply.Verified, advice.SupplyDecisions.Single(item => item.Reference == supply.Reference).State))]);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReplenishmentValuesDto([property: JsonRequired] string? DailyDemandPerDay, [property: JsonRequired] int? LeadTimeDays,
    [property: JsonRequired] string? SafetyStockPackages, [property: JsonRequired] string? ReorderPointPackages,
    [property: JsonRequired] string PurchaseMinimumPackages, [property: JsonRequired] string PurchaseIncrementPackages)
{
    private static long? Quantity(string? value, bool required = false) {
        if (value is null && !required) return null;
        if (value is null || value.Length > 19 || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < 0 || value != parsed.ToString(CultureInfo.InvariantCulture) || required && parsed == 0) throw ReplenishmentEndpoints.Invalid();
        return parsed;
    }
    internal ReplenishmentInputs ToDomain() {
        decimal? demand = null;
        if (DailyDemandPerDay is { } value) {
            if (value.Length > 60 || !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) ||
                parsed < 0 || value != value.Trim() || value.Length == 0 || value[0] == '.' || value[^1] == '.') throw ReplenishmentEndpoints.Invalid();
            demand = parsed;
        }
        return new(demand, LeadTimeDays, Quantity(SafetyStockPackages), Quantity(ReorderPointPackages),
            Quantity(PurchaseMinimumPackages, true)!.Value, Quantity(PurchaseIncrementPackages, true)!.Value);
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReplenishmentRecordDto([property: JsonRequired] AccessScopeDto Location, [property: JsonRequired] LongId PoolId,
    [property: JsonRequired] LongId ExpectedPoolRevision, [property: JsonRequired] LongId SkuId, [property: JsonRequired] LongId ExpectedSkuRevision,
    [property: JsonRequired] LongId DefinitionId, [property: JsonRequired] ReplenishmentValuesDto Inputs,
    [property: JsonRequired] string InputReference, [property: JsonRequired] string Reason)
{
    internal ReplenishmentReviewInput ToDomain() => new(StockCountInputs.Location(Location), PoolId.Value, ExpectedPoolRevision.Value,
        SkuId.Value, ExpectedSkuRevision.Value, DefinitionId.Value, Inputs?.ToDomain() ?? throw ReplenishmentEndpoints.Invalid(), InputReference);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReplenishmentHistoryQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] AccessScopeDto Location,
    [property: JsonRequired] LongId? Before, [property: JsonRequired] int Limit);
public sealed record ReplenishmentHistoryDto(IReadOnlyList<ReplenishmentViewDto> Items, string? NextBefore);
public sealed record ReplenishmentSourceDto(LongId Id, string Revision, string Label, string PhysicalKey, bool Active);
public sealed record ReplenishmentSourcesDto(IReadOnlyList<ReplenishmentSourceDto> Items);
public sealed record ReplenishmentSupplyDto(string Reference, string Packages, DateTimeOffset? ExpectedAt, bool Verified, string State);
public sealed record ReplenishmentRecoveryDto(string State, ReplenishmentViewDto? Evaluation);
public sealed record ReplenishmentViewDto(LongId Id, AccessScopeDto Location, LongId PoolId, string PoolRevision, LongId SkuId, string SkuRevision,
    LongId DefinitionId, string DefinitionVersion, string SkuCode, string PackageLabel, string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage,
    ReplenishmentValuesDto Inputs, string InputReference, string Reason, string SourceHash, string RuleVersion, DateTimeOffset ObservedAt, DateTimeOffset? Horizon,
    bool CurrentAuthority, string? UsableBeforeReservations, string? ReservedPackages, string? AdditionalUnreservedCommitments,
    string? AvailableAfterCommitments, bool? ThresholdAlert, string? EligibleSupplyPackages, string? InventoryPosition,
    string? RequiredStockPackages, string? SuggestedPurchasePackages, IReadOnlyList<string> Warnings, IReadOnlyList<ReplenishmentSupplyDto> Supplies);
