using System.Collections.Immutable;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Billing;

namespace UnitToko.Api.Billing;

public sealed record PaymentOcrFieldDto(string Field, string? Value, string? Confidence, ImmutableArray<string>? BoundingBox, ImmutableArray<string> Flags);
public sealed record PaymentOcrResultDto(LongId Id, DateTimeOffset ExtractedAt, ExtractionProvenance Provenance, ImmutableArray<PaymentOcrFieldDto> Fields, ImmutableArray<string> QualityFlags);
public sealed record PaymentOcrViewDto(LongId DeclarationId, LongId SubmissionId, PaymentEvidenceReferenceDto Evidence, string State, PaymentOcrResultDto? Result);

public static class PaymentOcrEndpoints
{
    public static void MapPaymentOcrEndpoints(this WebApplication app) {
        var endpoint = app.MapPost("/api/v1/billing/payment-declarations/{id}/ocr/query", async (string id, PaymentEvidenceReadDto request, HttpContext http, PaymentEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Purpose != "payment-ocr-view" || request.Reference is null) throw PaymentDraftEndpoints.Invalid();
            var reference = new EvidenceRef(request.Reference.Id.Value, LongId.Parse(request.Reference.Version).Value, request.Reference.Sha256);
            var result = await service.ReadExtractionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                reference, new(request.Purpose, http.TraceIdentifier), ct);
            http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache";
            return TypedResults.Ok(new PaymentOcrViewDto(new(result.DeclarationId), new(result.SubmissionId), request.Reference, result.State,
                result.Extraction is not { } extraction ? null : new(new(result.ResultId!.Value), result.ExtractedAt!.Value, extraction.Provenance,
                    [.. extraction.Fields.Select(field => new PaymentOcrFieldDto(field.Field, field.Value, field.Confidence?.ToString(CultureInfo.InvariantCulture),
                        field.BoundingBox is null ? null : [.. field.BoundingBox.Select(value => value.ToString(CultureInfo.InvariantCulture))], [.. field.Flags]))], [.. extraction.QualityFlags])));
        }).RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>().WithName("ReadPaymentOcr");
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) endpoint.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
    }
}
