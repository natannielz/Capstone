using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Api.Inventory;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;

namespace UnitToko.Api.Catalog;

public static class ScanEndpoints
{
    public static void MapScanEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/catalog/scans/query", async (ScanRequestDto body, HttpContext http, [FromServices] ScanResolver resolver, CancellationToken ct) => {
            var actor = http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
            var value = await resolver.ResolveAsync(actor, body.Domain(), http.TraceIdentifier, ct);
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(new ScanResolutionDto(value.Kind, value.Reason, value.Purpose,
                value.DocumentId is { } document ? new(document) : null, Number(value.DocumentRevision),
                value.AuxiliaryId is { } auxiliary ? new(auxiliary) : null, Number(value.AuxiliaryRevision), value.SourceReview,
                [.. value.Candidates.Select(item => new ScanCandidateDto(item.TargetReference, item.SkuId, item.DefinitionId,
                    Number(item.DefinitionVersion)!, item.SkuCode, item.PackageLabel, item.PackageKind, item.BaseUnitCode, item.BaseUnitsPerPackage,
                    OperationalScanSelectionDto.From(item.Selection ?? throw ScanRules.Invalid()), item.CurrentPrimaryCode))]));
        }).RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>().WithName("ResolveOperationalScan");
    }
    private static string? Number(long? value) => value?.ToString(CultureInfo.InvariantCulture);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ScanRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] string CodeKind,
    [property: JsonRequired] string RawCode, [property: JsonRequired] string? SourceNamespace, [property: JsonRequired] string? SourceAccount,
    [property: JsonRequired] AccessScopeDto? Location, [property: JsonRequired] LongId? DocumentId,
    [property: JsonRequired] LongId? DocumentRevision, [property: JsonRequired] LongId? AuxiliaryId,
    [property: JsonRequired] LongId? AuxiliaryRevision, [property: JsonRequired] string? SourceReview)
{
    internal ScanRequest Domain() => new(Purpose, CodeKind, RawCode, SourceNamespace, SourceAccount,
        Location is null ? null : StockCountInputs.Location(Location), DocumentId?.Value, DocumentRevision?.Value,
        AuxiliaryId?.Value, AuxiliaryRevision?.Value, SourceReview);
}
public sealed record ScanCandidateDto(string TargetReference, LongId SkuId, LongId DefinitionId, string DefinitionVersion,
    string SkuCode, string PackageLabel, string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage,
    OperationalScanSelectionDto Selection, string? CurrentPrimaryCode);
public sealed record ScanResolutionDto(string Kind, string Reason, string Purpose, LongId? DocumentId, string? DocumentRevision,
    LongId? AuxiliaryId, string? AuxiliaryRevision, string? SourceReview, IReadOnlyList<ScanCandidateDto> Candidates);
