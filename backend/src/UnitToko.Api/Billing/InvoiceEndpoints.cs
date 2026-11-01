using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Billing;

namespace UnitToko.Api.Billing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvoiceReceiptDto([property: JsonRequired] LongId ShipmentId, [property: JsonRequired] LongId ReceiptId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvoiceQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId? BeforeId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvoicePreviewRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] InvoiceReceiptDto[] Receipts);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvoiceIssueDto([property: JsonRequired] InvoiceReceiptDto[] Receipts, [property: JsonRequired] string Review, [property: JsonRequired] string Reason);
public sealed record InvoiceLineDto(LongId OrderId, LongId ShipmentId, LongId ReceiptId, LongId SkuId, string SkuCode, string ProductName,
    string PackageLabel, int BaseUnitsPerPackage, string BaseUnitCode, string AcceptedBaseUnits, Money Net, Money Tax, Money Charges);
public sealed record InvoicePreviewDto(string Review, string BuyerName, string SellerName, DateOnly IssueDate, DateOnly DueDate, string Grouping,
    DateOnly PeriodFrom, DateOnly PeriodTo, string TermOrigin, int TermDays, string DayRule, string TaxDocumentProcess, Money Net, Money Tax, Money Charges, Money Total, ImmutableArray<InvoiceLineDto> Lines);
public sealed record InvoiceDto(LongId Id, LongId NumberEntity, LongId NumberChannel, int NumberYear, string Number, DateTimeOffset IssuedAt, InvoicePreviewDto Detail, string DocumentHash, int DocumentLength,
    Money Paid, Money Credited, Money Outstanding, string PaymentState, string Aging);
public sealed record InvoicePageDto(ImmutableArray<InvoiceDto> Items, bool HasMore);
public sealed record InvoiceRecoveryDto(string Status, InvoiceDto? Invoice);

public static class InvoiceEndpoints
{
    public static void MapInvoiceEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/billing/invoices").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/candidates/query", async (InvoiceQueryDto request, HttpContext http, InvoiceService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(await service.CandidatesAsync(http.Features.Get<ResolvedActor>()!.Actor, request.BeforeId?.Value, ct));
        }).WithName("ListInvoiceCandidates");
        group.MapPost("/preview/query", async (InvoicePreviewRequestDto request, HttpContext http, InvoiceService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Preview(await service.PreviewAsync(http.Features.Get<ResolvedActor>()!.Actor, Selected(request.Receipts), ct)));
        }).WithName("PreviewInvoice");
        group.MapPost("", async (InvoiceIssueDto request, HttpContext http, InvoiceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (AccessRequestMetadata.ExpectedVersion(http.Request) != 0) throw Invalid();
            return TypedResults.Ok(View(await service.IssueAsync(http.Features.Get<ResolvedActor>()!.Actor, Selected(request.Receipts), request.Review,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("IssueInvoice");
        group.MapPost("/query", async (InvoiceQueryDto request, HttpContext http, InvoiceService service, CancellationToken ct) => {
            Purpose(request.Purpose); var result = await service.ListAsync(http.Features.Get<ResolvedActor>()!.Actor, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new InvoicePageDto([.. result.Items.Select(View)], result.HasMore));
        }).WithName("ListInvoices");
        group.MapPost("/{id}/query", async (string id, OrderReadDto request, HttpContext http, InvoiceService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(View(await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct)));
        }).WithName("ReadInvoice");
        group.MapPost("/operations/query", async (OrderDraftRecoveryDto request, HttpContext http, InvoiceService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw Invalid(); var invoice = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Key, ct);
            return TypedResults.Ok(new InvoiceRecoveryDto(invoice is null ? "not_confirmed" : "committed", invoice is null ? null : View(invoice)));
        }).WithName("RecoverInvoice");
        group.MapPost("/{id}/download", async (string id, OrderReadDto request, HttpContext http, InvoiceService service, CancellationToken ct) => {
            Purpose(request.Purpose); var file = await service.DownloadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct);
            http.Response.Headers.CacheControl = "private, no-store";
            http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return TypedResults.File(Encoding.UTF8.GetBytes(file.Content), "text/html; charset=utf-8", file.FileName);
        }).WithName("DownloadInvoice").Produces<byte[]>(200, "text/html");
    }
    internal static InvoiceDto View(InvoiceView row) {
        var balance = row.Balance ?? throw Invalid();
        return new(row.Id, row.NumberEntity, row.NumberChannel, row.NumberYear, row.Number.ToString(CultureInfo.InvariantCulture), row.IssuedAt,
            Preview(row.Detail), row.DocumentHash, row.DocumentLength, balance.AllocatedPayment, balance.ApprovedCredit, balance.Outstanding, balance.PaymentState, balance.Aging);
    }
    private static InvoicePreviewDto Preview(InvoiceIssuePreview row) => new(row.Review, row.BuyerName, row.SellerName, row.IssueDate, row.DueDate, row.Grouping, row.PeriodFrom, row.PeriodTo,
        row.TermOrigin, row.TermDays, row.DayRule, row.TaxDocumentProcess, row.Net, row.Tax, row.Charges, row.Total,
        [.. row.Lines.Select(line => new InvoiceLineDto(line.OrderId, line.ShipmentId, line.ReceiptId, line.SkuId, line.SkuCode, line.ProductName, line.PackageLabel, line.BaseUnitsPerPackage,
            line.BaseUnitCode, line.AcceptedBaseUnits.ToString(CultureInfo.InvariantCulture), line.Net, line.Tax, line.Charges))]);
    private static ImmutableArray<InvoiceReceiptSelection> Selected(InvoiceReceiptDto[] values) {
        if (values is null || values.Any(value => value is null)) throw Invalid(); return InvoicePolicy.Copy(values.Select(value => new InvoiceReceiptSelection(value.ShipmentId, value.ReceiptId)));
    }
    private static void Purpose(string value) { if (value != "invoices") throw Invalid(); }
    private static DomainValidationException Invalid() => new("invalid_invoice", "Use the current authorized invoice workflow");
}
