using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Reporting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Reporting;

namespace UnitToko.Api.Reporting;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingReportReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingReportKeyDto([property: JsonRequired] LongId OrganizationId,
    [property: JsonRequired] LongId ShopId, [property: JsonRequired] LongId ChannelId,
    [property: JsonRequired] string Currency)
{
    internal OperationalBookKey Domain() => new(OrganizationId.Value, ShopId.Value, ChannelId.Value, Currency);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingReportQueryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] AccountingReportKeyDto Key, [property: JsonRequired] string EndDate);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingReportExportDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] LongId ReadId, [property: JsonRequired] string SnapshotHash);

/// <summary>A scoped posted-journal view and original-snapshot CSV; never a posting or approved close.</summary>
public static class AccountingReportEndpoints
{
    public static void MapAccountingReportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/accounting/reports")
            .RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));

        group.MapPost("/books/query", async (AccountingReportReadDto request, HttpContext http,
            [FromServices] AccountingReportService service, CancellationToken ct) =>
        {
            var row = await service.BooksAsync(Actor(http), Metadata(request.Purpose, http), ct);
            NoStore(http);
            return TypedResults.Ok(row);
        }).WithName("ReadAccountingReportBooks");

        group.MapPost("/query", async (AccountingReportQueryDto request, HttpContext http,
            [FromServices] AccountingReportService service, CancellationToken ct) =>
        {
            if (request.Key is null)
                throw new DomainValidationException("invalid_accounting_report_key", "Select the authorized operational book");
            var row = await service.ReadAsync(Actor(http), request.Key.Domain(), request.EndDate,
                Metadata(request.Purpose, http), ct);
            NoStore(http);
            return TypedResults.Ok(row);
        }).WithName("ReadAccountingReportSnapshot");

        group.MapPost("/export", async (AccountingReportExportDto request, HttpContext http,
            [FromServices] AccountingReportService service, CancellationToken ct) =>
        {
            var row = await service.ExportAsync(Actor(http), request.ReadId.Value, request.SnapshotHash,
                Metadata(request.Purpose, http), ct);
            NoStore(http);
            http.Response.Headers["X-Read-Id"] = row.ReadId.ToString();
            http.Response.Headers["X-Snapshot-Read-Id"] = row.SnapshotReadId.ToString();
            http.Response.Headers["X-Snapshot-Hash"] = row.SnapshotHash;
            return TypedResults.File(row.Bytes, "text/csv; charset=utf-8", row.FileName);
        }).WithName("ExportAccountingReportSnapshot");
    }

    private static AccountingReportReadMetadata Metadata(string purpose, HttpContext http) => new(purpose, http.TraceIdentifier);
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ??
        throw new AuthorizationDeniedException("unauthenticated");
    private static void NoStore(HttpContext http)
    {
        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers.XContentTypeOptions = "nosniff";
    }
}
