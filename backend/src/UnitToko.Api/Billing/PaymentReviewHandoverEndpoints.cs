using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Identity;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Billing;

namespace UnitToko.Api.Billing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentHandoverInputDto(string DelegationId, string DelegationRevision, string DelegationEventId, string OriginalAssigneeId, string DeclarationRevision) {
    public PaymentReviewHandoverInput Domain() => new(LongId.Parse(DelegationId).Value, LongId.Parse(DelegationRevision).Value,
        LongId.Parse(DelegationEventId).Value, LongId.Parse(OriginalAssigneeId).Value, LongId.Parse(DeclarationRevision).Value);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentHandoverQueryDto(string Purpose, string? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentHandoverSaveDto([property: JsonRequired] string Binding, [property: JsonRequired] PaymentHandoverInputDto Handover, [property: JsonRequired] string Reason);
public sealed record PaymentHandoverChoiceDto(LongId DelegationId, string DelegationRevision, LongId DelegationEventId, string State, DateTimeOffset ExpiresAt);
public sealed record PaymentHandoverPageDto(LongId DeclarationId, string DeclarationRevision, LongId SubmissionId, string Binding,
    string ReviewRevision, LongId? AssigneeId, bool Open, bool CanClaim, PaymentHandoverChoiceDto[] Items, LongId? NextId, DateTimeOffset ObservedAt);
public sealed record PaymentHandoverResultDto(LongId DeclarationId, LongId SubmissionId, string ReviewRevision, LongId ActionId,
    string Action, string State, LongId AssigneeId, DateTimeOffset RecordedAt, string Operation, PaymentHandoverInputDto Handover);
public sealed record PaymentHandoverRecoveryDto(string State, PaymentHandoverResultDto? Result);
public static class PaymentReviewHandoverEndpoints
{
    public static void MapPaymentReviewHandoverEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/billing/payment-reviews/{id}/handover").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var code in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(code, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (string id, PaymentHandoverQueryDto request, HttpContext http, PaymentVerificationService service, CancellationToken ct) => {
            if (request.Purpose != "payment-handover") throw PaymentReviewHandoverPolicy.Invalid(); Private(http);
            var page = await service.ReadHandoversAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value,
                request.BeforeId is null ? null : LongId.Parse(request.BeforeId).Value, ct);
            return TypedResults.Ok(new PaymentHandoverPageDto(new(page.DeclarationId), Number(page.DeclarationRevision), new(page.SubmissionId), page.Binding,
                Number(page.ReviewRevision), page.AssigneeId is { } assigned ? new(assigned) : null, page.Open, page.CanClaim,
                page.Items.Select(item => new PaymentHandoverChoiceDto(new(item.DelegationId), Number(item.DelegationRevision), new(item.DelegationEventId), item.State, item.ExpiresAt)).ToArray(),
                page.NextId is { } next ? new(next) : null, page.ObservedAt));
        }).WithName("ReadPaymentReviewHandovers");
        group.MapPost("", async (string id, PaymentHandoverSaveDto request, HttpContext http, PaymentVerificationService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Private(http); if (request.Handover is null) throw PaymentReviewHandoverPolicy.Invalid();
            return TypedResults.Ok(Result(await service.ClaimHandoverAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), request.Binding, request.Handover.Domain(),
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ClaimPaymentReviewHandover");
        group.MapPost("/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, PaymentVerificationService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw PaymentReviewHandoverPolicy.Invalid(); Private(http);
            var saved = await service.RecoverHandoverAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Key, ct);
            return TypedResults.Ok(new PaymentHandoverRecoveryDto(saved is null ? "not_confirmed" : "committed", saved is null ? null : Result(saved)));
        }).WithName("RecoverPaymentReviewHandover");
    }
    private static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static PaymentHandoverResultDto Result(BsonDocument row) { var input = row["handover"];
        return new(new(row["declarationId"].AsInt64), new(row["submissionId"].AsInt64), Number(row["reviewRevision"].AsInt64), new(row["actionId"].AsInt64),
            row["action"].AsString, row["state"].AsString, new(row["assigneeId"].AsInt64), new(row["recordedAt"].ToUniversalTime()), row["operation"].AsString,
            new(Number(input["delegationId"].AsInt64), Number(input["delegationRevision"].AsInt64), Number(input["delegationEventId"].AsInt64),
                Number(input["originalAssigneeId"].AsInt64), Number(input["declarationRevision"].AsInt64))); }
    private static void Private(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache"; }
}
