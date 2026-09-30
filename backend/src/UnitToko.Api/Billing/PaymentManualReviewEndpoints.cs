using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Billing;

namespace UnitToko.Api.Billing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentManualReviewSaveDto([property: JsonRequired] string Binding, [property: JsonRequired] bool ImageReviewed,
    [property: JsonRequired] PaymentCorrectionFieldDto[] Fields, [property: JsonRequired] string Reason);
public sealed record PaymentManualReviewViewDto(string Binding, LongId DeclarationId, string DeclarationRevision, LongId SubmissionId,
    PaymentEvidenceReferenceDto Evidence, string ReviewRevision, LongId FailureId, string FailureCode, DateTimeOffset FailedAt,
    string ProcedureReference, ImmutableArray<PaymentReviewField> Fields, LongId? ReviewedBy, DateTimeOffset? RecordedAt);
public sealed record PaymentManualReviewResultDto(LongId DeclarationId, LongId SubmissionId, string ReviewRevision, LongId ManualReviewId, DateTimeOffset RecordedAt);
public sealed record PaymentManualReviewRecoveryDto(string Status, PaymentManualReviewResultDto? Result);

public static class PaymentManualReviewEndpoints
{
    public static void MapPaymentManualReviewEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/billing/payment-reviews/{id}/manual-review").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (string id, OrderReadDto request, HttpContext http, PaymentVerificationService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Purpose != "payment-review") throw PaymentManualReviewPolicy.Invalid(); Private(http);
            var value = await service.PreviewManualReviewAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct);
            if (AccessRequestMetadata.ExpectedVersion(http.Request) != value.Source["revision"].AsInt64) throw PaymentManualReviewPolicy.Invalid();
            var reference = value.Source["evidence"]; http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(value.ReviewRevision);
            return TypedResults.Ok(new PaymentManualReviewViewDto(value.Binding, new(value.Source["declarationId"].AsInt64), Number(value.Source["revision"]), new(value.Source["submissionId"].AsInt64),
                new(new(reference["id"].AsInt64), Number(reference["version"]), reference["sha256"].AsString), OrderDraftInputDto.Number(value.ReviewRevision),
                new(value.Failure["failureId"].AsInt64), value.Failure["safeFailure"].AsString, new(value.Failure["failedAt"].ToUniversalTime()), value.Policy["procedureReference"].AsString,
                value.Latest is null ? [.. PaymentFieldExtractor.Fields.Select(name => new PaymentReviewField(name, null))] : PaymentCorrectionBsonFields(value.Latest["fields"].AsBsonArray),
                value.Latest is null ? null : new(value.Latest["actorId"].AsInt64), value.Latest is null ? null : new(value.Latest["recordedAt"].ToUniversalTime())));
        }).WithName("ReadPaymentManualReview");
        group.MapPost("", async (string id, PaymentManualReviewSaveDto request, HttpContext http, PaymentVerificationService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Fields is null || request.Fields.Any(field => field is null)) throw PaymentManualReviewPolicy.Invalid(); Private(http);
            return TypedResults.Ok(Result(await service.RecordManualReviewAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                new(request.Binding, request.ImageReviewed, [.. request.Fields.Select(field => new PaymentReviewField(field.Field, field.Value))]),
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("RecordPaymentManualReview");
        group.MapPost("/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, PaymentVerificationService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw PaymentManualReviewPolicy.Invalid(); Private(http);
            var value = await service.RecoverManualReviewAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Key, ct);
            return TypedResults.Ok(new PaymentManualReviewRecoveryDto(value is null ? "not_confirmed" : "committed", value is null ? null : Result(value)));
        }).WithName("RecoverPaymentManualReview");
    }
    private static string Number(BsonValue value) => OrderDraftInputDto.Number(value.AsInt64);
    private static ImmutableArray<PaymentReviewField> PaymentCorrectionBsonFields(BsonArray fields) => [.. fields.Select(field => new PaymentReviewField(field["field"].AsString, field["value"].IsBsonNull ? null : field["value"].AsString))];
    private static PaymentManualReviewResultDto Result(BsonDocument row) => new(new(row["declarationId"].AsInt64), new(row["submissionId"].AsInt64), Number(row["reviewRevision"]), new(row["manualReviewId"].AsInt64), new(row["recordedAt"].ToUniversalTime()));
    private static void Private(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache"; }
}
