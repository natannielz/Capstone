using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Procurement;

namespace UnitToko.Api.Billing;

// P01 keeps image evidence mandatory for the primary payment submission. Payment PDF
// admission is not a supplementary association; only supplier PDF extraction is connected.
public static class PdfDocumentOcrEndpoints
{
    public static void MapPdfDocumentOcrEndpoints(this WebApplication app)
    {
        MapFormat(app, "/api/v1/billing/payment-declarations/{id}/evidence-format/query", "payment-evidence-format-view", false, false);
        MapFormat(app, "/api/v1/billing/payment-reviews/{id}/evidence-format/query", "payment-review-evidence-format-view", true, false);
        MapFormat(app, "/api/v1/procurement/supplier-documents/drafts/{id}/evidence-format/query", "supplier-document-evidence-format-view", false, true);
        Add(app.MapPost("/api/v1/procurement/supplier-documents/drafts/{id}/pdf-ocr/query",
            async (string id, PaymentEvidenceReadDto request, HttpContext http, SupplierDocumentEvidenceService service,
                [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
                var reference = Reference(request, "supplier-document-pdf-ocr-view");
                var view = await service.ReadPdfExtractionAsync(http.Features.Get<ResolvedActor>()!.Actor,
                    LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), reference,
                    new(request.Purpose, http.TraceIdentifier), ct);
                Private(http); return TypedResults.Ok(PdfDocumentOcrDtos.View(view));
            }).WithName("ReadSupplierDocumentPdfOcr"));
    }
    public sealed record EvidenceSubmissionFormatDto(string SourceId, string SourceRevision, PaymentEvidenceReferenceDto Evidence, string Kind);
    private static void MapFormat(WebApplication app, string route, string purpose, bool review, bool supplier)
    {
        Add(app.MapPost(route, async (string id, PaymentEvidenceReadDto request, HttpContext http, PaymentEvidenceService payment,
            SupplierDocumentEvidenceService documents, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            var reference = Reference(request, purpose); var actor = http.Features.Get<ResolvedActor>()!.Actor;
            var revision = AccessRequestMetadata.ExpectedVersion(http.Request); var sourceId = LongId.Parse(id).Value;
            var metadata = new UnitToko.Platform.Authorization.AuthorizationReadMetadata(request.Purpose, http.TraceIdentifier);
            var view = supplier ? await documents.ReadSubmissionFormatAsync(actor, sourceId, revision, reference, metadata, ct)
                : await payment.ReadSubmissionFormatAsync(actor, sourceId, revision, reference, metadata, review, ct);
            Private(http); return TypedResults.Ok(new EvidenceSubmissionFormatDto(OrderDraftInputDto.Number(view.SourceId), OrderDraftInputDto.Number(view.SourceRevision),
                new(new(view.Evidence.Id), OrderDraftInputDto.Number(view.Evidence.Version), view.Evidence.Sha256), view.Kind));
        }).WithName(supplier ? "ReadSupplierDocumentEvidenceFormat" : review ? "ReadPaymentReviewEvidenceFormat" : "ReadPaymentEvidenceFormat"));
    }
    private static EvidenceRef Reference(PaymentEvidenceReadDto request, string purpose)
    {
        if (request.Purpose != purpose || request.Reference is null) throw PaymentDraftEndpoints.Invalid();
        return new(request.Reference.Id.Value, LongId.Parse(request.Reference.Version).Value, request.Reference.Sha256);
    }
    private static void Private(HttpContext http)
    {
        http.Response.Headers.CacheControl = "private, no-store, max-age=0";
        http.Response.Headers.Pragma = "no-cache";
        http.Response.Headers.XContentTypeOptions = "nosniff";
    }
    private static void Add(RouteHandlerBuilder route)
    {
        route.RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            route.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
    }
}
