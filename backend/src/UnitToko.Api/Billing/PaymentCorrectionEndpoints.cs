using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Billing;

namespace UnitToko.Api.Billing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentCorrectionBindingDto([property: JsonRequired] string DeclarationRevision, [property: JsonRequired] LongId SubmissionId,
    [property: JsonRequired] PaymentEvidenceReferenceDto Evidence, [property: JsonRequired] LongId ExtractionId) {
    internal PaymentCorrectionBinding Domain() => Evidence is null ? throw PaymentReviewCorrectionPolicy.Invalid() :
        new(LongId.Parse(DeclarationRevision).Value, SubmissionId.Value, new(Evidence.Id.Value, LongId.Parse(Evidence.Version).Value, Evidence.Sha256), ExtractionId.Value);
    internal static PaymentCorrectionBindingDto From(PaymentCorrectionBinding binding) => new(OrderDraftInputDto.Number(binding.DeclarationRevision), new(binding.SubmissionId),
        new(new(binding.Evidence.Id), OrderDraftInputDto.Number(binding.Evidence.Version), binding.Evidence.Sha256), new(binding.ExtractionId));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentCorrectionFieldDto([property: JsonRequired] string Field, [property: JsonRequired] string? Value);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentCorrectionQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] PaymentCorrectionBindingDto Binding);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentCorrectionSaveDto([property: JsonRequired] PaymentCorrectionBindingDto Binding, [property: JsonRequired] PaymentCorrectionFieldDto[] Fields, [property: JsonRequired] string Reason);
public sealed record PaymentCorrectionVersionDto(LongId Id, string Revision, LongId ActorId, DateTimeOffset RecordedAt, string Reason, ImmutableArray<PaymentReviewField> BeforeFields, ImmutableArray<PaymentReviewField> Fields);
public sealed record PaymentCorrectionViewDto(LongId DeclarationId, PaymentCorrectionBindingDto Binding, string ReviewRevision,
    ImmutableArray<PaymentReviewField> OriginalFields, ImmutableArray<PaymentReviewField> Fields, ImmutableArray<PaymentCorrectionVersionDto> Recent);
public sealed record PaymentCorrectionResultDto(LongId DeclarationId, LongId SubmissionId, string ReviewRevision, LongId CorrectionId, DateTimeOffset RecordedAt);
public sealed record PaymentCorrectionRecoveryDto(string Status, PaymentCorrectionResultDto? Result);

public static class PaymentCorrectionEndpoints
{
    public static void MapPaymentCorrectionEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/billing/payment-reviews/{id}/corrections").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (string id, PaymentCorrectionQueryDto request, HttpContext http, PaymentEvidenceService service, CancellationToken ct) => {
            if (request.Purpose != "payment-ocr-view" || request.Binding is null) throw PaymentReviewCorrectionPolicy.Invalid(); Private(http);
            var view = await service.ReadCorrectionsAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Binding.Domain(), new(request.Purpose, http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(view.ReviewRevision);
            return TypedResults.Ok(new PaymentCorrectionViewDto(new(view.DeclarationId), PaymentCorrectionBindingDto.From(view.Binding), OrderDraftInputDto.Number(view.ReviewRevision), view.OriginalFields, view.Fields,
                [.. view.Recent.Select(row => new PaymentCorrectionVersionDto(new(row.Id), OrderDraftInputDto.Number(row.Revision), new(row.ActorId), row.RecordedAt, row.Reason, row.BeforeFields, row.Fields))]));
        }).WithName("ReadPaymentCorrections");
        group.MapPost("", async (string id, PaymentCorrectionSaveDto request, HttpContext http, PaymentEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Binding is null || request.Fields is null || request.Fields.Any(field => field is null)) throw PaymentReviewCorrectionPolicy.Invalid(); Private(http);
            return TypedResults.Ok(Result(await service.CorrectReviewAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), request.Binding.Domain(),
                [.. request.Fields.Select(field => new PaymentReviewField(field.Field, field.Value))], new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("CorrectPaymentReview");
        group.MapPost("/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, PaymentEvidenceService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw PaymentReviewCorrectionPolicy.Invalid(); Private(http);
            var row = await service.RecoverReviewCorrectionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Key, ct);
            return TypedResults.Ok(new PaymentCorrectionRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverPaymentCorrection");
    }
    private static PaymentCorrectionResultDto Result(BsonDocument row) => new(new(row["declarationId"].AsInt64), new(row["submissionId"].AsInt64),
        OrderDraftInputDto.Number(row["reviewRevision"].AsInt64), new(row["correctionId"].AsInt64), new(row["recordedAt"].ToUniversalTime()));
    private static void Private(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache"; }
}
