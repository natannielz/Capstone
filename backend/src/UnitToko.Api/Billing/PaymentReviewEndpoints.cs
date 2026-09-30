using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Billing;

namespace UnitToko.Api.Billing;

public sealed record PaymentDuplicateQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] PaymentEvidenceReferenceDto Reference,
    [property: JsonRequired] LongId? BeforeId);
public sealed record PaymentDuplicateMatchDto(LongId DeclarationId, string Revision, LongId SubmissionId, DateTimeOffset SubmittedAt, ImmutableArray<string> Signals);
public sealed record PaymentDuplicatePageDto(LongId DeclarationId, string Revision, LongId SubmissionId, ImmutableArray<PaymentDuplicateMatchDto> Items, bool HasMore);
public sealed record PaymentReviewQueueEntryDto(PaymentDraftDto Declaration, string ReviewRevision, string State, LongId? AssigneeId,
    DateTimeOffset? RecordedAt, string OcrState, ImmutableArray<string> Discrepancies);
public sealed record PaymentReviewQueuePageDto(ImmutableArray<PaymentReviewQueueEntryDto> Items, bool HasMore, DateTimeOffset ObservedAt);

public static class PaymentReviewEndpoints
{
    public static void MapPaymentReviewEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/billing/payment-reviews").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (InvoiceQueryDto request, HttpContext http, PaymentDraftService service, CancellationToken ct) => {
            Purpose(request.Purpose); Private(http);
            var page = await service.ListReviewsAsync(http.Features.Get<ResolvedActor>()!.Actor, request.BeforeId?.Value, new(request.Purpose, http.TraceIdentifier), ct);
            return TypedResults.Ok(new PaymentDraftPageDto([.. page.Items.Select(PaymentDraftEndpoints.View)], page.HasMore));
        }).WithName("ListPaymentReviews");
        group.MapPost("/queue/query", async (InvoiceQueryDto request, HttpContext http, PaymentDraftService service, CancellationToken ct) => {
            Purpose(request.Purpose); Private(http);
            var page = await service.ListReviewQueueAsync(http.Features.Get<ResolvedActor>()!.Actor, request.BeforeId?.Value, new(request.Purpose, http.TraceIdentifier), ct);
            return TypedResults.Ok(new PaymentReviewQueuePageDto([.. page.Items.Select(row => new PaymentReviewQueueEntryDto(PaymentDraftEndpoints.View(row.Declaration),
                OrderDraftInputDto.Number(row.ReviewRevision), row.State, row.AssigneeId is { } assignee ? new(assignee) : null, row.RecordedAt, row.OcrState, row.Discrepancies))], page.HasMore, page.ObservedAt));
        }).WithName("ListPaymentReviewQueue");
        group.MapPost("/{id}/query", async (string id, OrderReadDto request, HttpContext http, PaymentDraftService service, CancellationToken ct) => {
            Purpose(request.Purpose); Private(http);
            var row = await service.ReadReviewAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, new(request.Purpose, http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row.Revision); return TypedResults.Ok(PaymentDraftEndpoints.View(row));
        }).WithName("ReadPaymentReview");
        group.MapPost("/{id}/duplicates/query", async (string id, PaymentDuplicateQueryDto request, HttpContext http, PaymentDraftService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose); Private(http);
            var reference = request.Reference is { } value ? new EvidenceRef(value.Id.Value, LongId.Parse(value.Version).Value, value.Sha256) : throw PaymentDraftEndpoints.Invalid();
            var page = await service.ReadDuplicatesAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), reference, request.BeforeId?.Value, new(request.Purpose, http.TraceIdentifier), ct);
            return TypedResults.Ok(new PaymentDuplicatePageDto(new(page.DeclarationId), page.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), new(page.SubmissionId),
                [.. page.Items.Select(item => new PaymentDuplicateMatchDto(new(item.DeclarationId), item.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), new(item.SubmissionId), item.SubmittedAt, [.. item.Signals]))], page.HasMore));
        }).WithName("ReadPaymentDuplicates");
        group.MapPost("/{id}/ocr/query", async (string id, PaymentEvidenceReadDto request, HttpContext http, PaymentEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Purpose != "payment-ocr-view") throw PaymentDraftEndpoints.Invalid(); Private(http);
            return TypedResults.Ok(PaymentOcrEndpoints.View(await service.ReadReviewExtractionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), Reference(request), new(request.Purpose, http.TraceIdentifier), ct)));
        }).WithName("ReadPaymentReviewOcr");
        group.MapPost("/{id}/evidence/{reservationId}/original/query", async (string id, string reservationId, PaymentEvidenceReadDto request, HttpContext http, PaymentEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Purpose != "payment-evidence-view" || http.Request.Headers.ContainsKey("Range")) throw PaymentDraftEndpoints.Invalid();
            var reference = Reference(request);
            var file = await service.OpenReviewReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(reservationId).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), reference, new(request.Purpose, http.TraceIdentifier), ct);
            Private(http); http.Response.Headers.XContentTypeOptions = "nosniff"; http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            http.Response.Headers["Referrer-Policy"] = "no-referrer"; http.Response.ContentLength = file.Length;
            return Results.Stream(file.Content, file.Mime, $"bukti-{reference.Id}-{reference.Version}.{(file.Mime == "image/png" ? "png" : "jpg")}", enableRangeProcessing: false);
        }).Produces<byte[]>(200, "image/png", "image/jpeg").WithName("ReadPaymentReviewOriginal");
    }
    private static EvidenceRef Reference(PaymentEvidenceReadDto request) => request.Reference is { } value
        ? new(value.Id.Value, LongId.Parse(value.Version).Value, value.Sha256) : throw PaymentDraftEndpoints.Invalid();
    private static void Purpose(string value) { if (value != "payment-review") throw PaymentDraftEndpoints.Invalid(); }
    private static void Private(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache"; }
}
