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
public sealed record PaymentReviewActionSaveDto([property: JsonRequired] string Binding, [property: JsonRequired] string Action, [property: JsonRequired] string Reason, string? PublicExplanation = null);
public sealed record PaymentReviewActionViewDto(string Binding, LongId DeclarationId, string DeclarationRevision, LongId SubmissionId, PaymentEvidenceReferenceDto Evidence,
    string ReviewRevision, string State, LongId? AssigneeId, LongId? RecordedBy, DateTimeOffset? RecordedAt, string? Reason, string? PublicExplanation, bool Verified, DateTimeOffset SubmittedAt, DateTimeOffset ObservedAt);
public sealed record PaymentReviewActionResultDto(LongId DeclarationId, LongId SubmissionId, string ReviewRevision, LongId ActionId, string Action, string State, LongId? AssigneeId, DateTimeOffset RecordedAt, string Operation);
public sealed record PaymentReviewActionRecoveryDto(string Status, PaymentReviewActionResultDto? Result);
public static class PaymentReviewActionEndpoints
{
    public static void MapPaymentReviewActionEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/billing/payment-reviews/{id}/workflow").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (string id, OrderReadDto request, HttpContext http, PaymentVerificationService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Purpose != "payment-review") throw PaymentReviewActionPolicy.Invalid(); Private(http);
            var view = await service.ReadReviewActionsAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct); var source = view.Source; var last = view.Latest;
            if (AccessRequestMetadata.ExpectedVersion(http.Request) != source["revision"].AsInt64) throw PaymentReviewActionPolicy.Invalid();
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(view.ReviewRevision); var evidence = source["evidence"];
            return TypedResults.Ok(new PaymentReviewActionViewDto(view.Binding, new(source["declarationId"].AsInt64), Number(source["revision"]), new(source["submissionId"].AsInt64),
                new(new(evidence["id"].AsInt64), Number(evidence["version"]), evidence["sha256"].AsString), view.ReviewRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                last?["state"].AsString ?? "pending", last is null || last["assigneeId"].IsBsonNull ? null : new(last["assigneeId"].AsInt64),
                last is null ? null : new(last["actorId"].AsInt64), last is null ? null : new(last["recordedAt"].ToUniversalTime()), last?["reason"].AsString,
                last?.GetValue("publicExplanation", BsonNull.Value) is { IsString: true } explanation ? explanation.AsString : null, view.Verified, view.SubmittedAt, view.ObservedAt));
        }).WithName("ReadPaymentReviewWorkflow");
        group.MapPost("", async (string id, PaymentReviewActionSaveDto request, HttpContext http, PaymentVerificationService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Private(http); return TypedResults.Ok(Result(await service.RecordReviewActionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), new(request.Binding, request.Action, request.PublicExplanation), new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("RecordPaymentReviewAction");
        group.MapPost("/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, PaymentVerificationService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw PaymentReviewActionPolicy.Invalid(); Private(http);
            var row = await service.RecoverReviewActionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Key, ct);
            return TypedResults.Ok(new PaymentReviewActionRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverPaymentReviewAction");
    }
    private static string Number(BsonValue value) => OrderDraftInputDto.Number(value.AsInt64);
    private static PaymentReviewActionResultDto Result(BsonDocument row) => new(new(row["declarationId"].AsInt64), new(row["submissionId"].AsInt64), Number(row["reviewRevision"]),
        new(row["actionId"].AsInt64), row["action"].AsString, row["state"].AsString, row["assigneeId"].IsBsonNull ? null : new(row["assigneeId"].AsInt64), new(row["recordedAt"].ToUniversalTime()), row["operation"].AsString);
    private static void Private(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache"; }
}
