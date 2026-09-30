using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Billing;

namespace UnitToko.Api.Billing;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentDraftInputDto([property: JsonRequired] LongId[] InvoiceIds, [property: JsonRequired] Money DeclaredAmount,
    [property: JsonRequired] DateOnly TransferDate, [property: JsonRequired] string PayerName, [property: JsonRequired] string? BankReference, [property: JsonRequired] string? Note)
{
    internal PaymentDraftInput Domain() {
        if (InvoiceIds is null) throw PaymentDraftEndpoints.Invalid();
        return new([.. InvoiceIds], DeclaredAmount, TransferDate, PayerName, BankReference, Note);
    }
    internal static PaymentDraftInputDto From(PaymentDraftInput value) => new([.. value.InvoiceIds], value.DeclaredAmount, value.TransferDate, value.PayerName, value.BankReference, value.Note);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentDraftSaveDto([property: JsonRequired] LongId? DeclarationId, [property: JsonRequired] PaymentDraftInputDto Input, [property: JsonRequired] string Reason);
public sealed record PaymentSubmissionSummaryDto(LongId Id, string ReviewRevision, LongId ReservationId, PaymentEvidenceReferenceDto Evidence, DateTimeOffset SubmittedAt);
public sealed record PaymentDraftDto(LongId Id, string Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string State, PaymentDraftInputDto Input, ImmutableArray<PaymentInvoiceReference> Invoices, PaymentSubmissionSummaryDto? Submission);
public sealed record PaymentDraftPageDto(ImmutableArray<PaymentDraftDto> Items, bool HasMore);
public sealed record PaymentDraftResultDto(LongId DeclarationId, LongId VersionId, string Revision, DateTimeOffset RecordedAt);
public sealed record PaymentDraftRecoveryDto(string Status, PaymentDraftResultDto? Result);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentSubmissionDto([property: JsonRequired] LongId ReservationId, [property: JsonRequired] PaymentEvidenceReferenceDto Evidence, [property: JsonRequired] string Reason);
public sealed record PaymentSubmissionResultDto(LongId DeclarationId, LongId SubmissionId, string DeclarationRevision, string ReviewRevision, string State, DateTimeOffset SubmittedAt);
public sealed record PaymentSubmissionRecoveryDto(string Status, PaymentSubmissionResultDto? Result);
public sealed record PaymentFollowUpDto(LongId DeclarationId, string DeclarationRevision, LongId SubmissionId, PaymentEvidenceReferenceDto Evidence,
    string ReviewRevision, string State, DateTimeOffset SubmittedAt, DateTimeOffset? RecordedAt, PaymentNotificationStatus Notification);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentReplacementRequestDto([property: JsonRequired] string ReviewRevision, [property: JsonRequired] LongId ActionId,
    [property: JsonRequired] string ActionHash, [property: JsonRequired] string Binding)
{
    internal PaymentReplacementRequest Domain() => new(LongId.Parse(ReviewRevision).Value, ActionId.Value, ActionHash, Binding);
    internal static PaymentReplacementRequestDto From(PaymentReplacementRequest value) => new(OrderDraftInputDto.Number(value.ReviewRevision), new(value.ActionId), value.ActionHash, value.Binding);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentReplacementSaveDto([property: JsonRequired] PaymentReplacementRequestDto Request, [property: JsonRequired] string Reason);
public sealed record PaymentReplacementResultDto(LongId ParentDeclarationId, string ParentDeclarationRevision, LongId DeclarationId, LongId VersionId, string Revision, DateTimeOffset RecordedAt);
public sealed record PaymentReplacementViewDto(LongId ParentDeclarationId, string ParentDeclarationRevision, LongId SubmissionId, PaymentEvidenceReferenceDto Evidence,
    PaymentDraftInputDto Input, PaymentReplacementRequestDto Request, PaymentReplacementResultDto? Existing);
public sealed record PaymentReplacementRecoveryDto(string Status, PaymentReplacementResultDto? Result);
public sealed record PaymentReplacementOriginDto(LongId ParentDeclarationId, string ParentDeclarationRevision);
public sealed record PaymentReplacementOriginViewDto(PaymentReplacementOriginDto? Origin);

public static class PaymentDraftEndpoints
{
    public static void MapPaymentDraftEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/billing/payment-declarations").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (InvoiceQueryDto request, HttpContext http, PaymentDraftService service, CancellationToken ct) => {
            Purpose(request.Purpose); var page = await service.ListAsync(http.Features.Get<ResolvedActor>()!.Actor, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new PaymentDraftPageDto([.. page.Items.Select(View)], page.HasMore));
        }).WithName("ListPaymentDeclarations");
        group.MapPost("/{id}/query", async (string id, OrderReadDto request, HttpContext http, PaymentDraftService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row.Revision); return TypedResults.Ok(View(row));
        }).WithName("ReadPaymentDeclaration");
        group.MapPost("/{id}/follow-up/query", async (string id, OrderReadDto request, HttpContext http, PaymentDraftService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Purpose != "payment-follow-up") throw Invalid();
            http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache";
            var row = await service.ReadFollowUpAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row.ReviewRevision);
            return TypedResults.Ok(new PaymentFollowUpDto(new(row.DeclarationId), OrderDraftInputDto.Number(row.DeclarationRevision), new(row.SubmissionId),
                new(new(row.Evidence.Id), OrderDraftInputDto.Number(row.Evidence.Version), row.Evidence.Sha256),
                row.ReviewRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), row.State, row.SubmittedAt, row.RecordedAt, row.Notification));
        }).WithName("ReadOwnPaymentFollowUp");
        group.MapPost("/{id}/replacement/query", async (string id, OrderReadDto request, HttpContext http, PaymentDraftService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Purpose != "payment-replacement") throw Invalid(); PrivateReplacement(http);
            var view = await service.PreviewReplacementAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), ct);
            var source = view.Source; var evidence = source["evidence"]; http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(source["revision"].AsInt64);
            return TypedResults.Ok(new PaymentReplacementViewDto(new(source["declarationId"].AsInt64), OrderDraftInputDto.Number(source["revision"].AsInt64), new(source["submissionId"].AsInt64),
                new(new(evidence["id"].AsInt64), OrderDraftInputDto.Number(evidence["version"].AsInt64), evidence["sha256"].AsString),
                PaymentDraftInputDto.From(view.Input), PaymentReplacementRequestDto.From(view.Request), view.Existing is null ? null : ReplacementResult(view.Existing)));
        }).WithName("PreviewOwnPaymentReplacement");
        group.MapPost("/{id}/replacement", async (string id, PaymentReplacementSaveDto request, HttpContext http, PaymentDraftService service,
            [FromHeader(Name = "If-Match")] string expected, [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            if (request.Request is null) throw Invalid(); PrivateReplacement(http);
            return TypedResults.Ok(ReplacementResult(await service.CreateReplacementAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), request.Request.Domain(), new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("CreateOwnPaymentReplacement");
        group.MapPost("/{id}/replacement/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, PaymentDraftService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw Invalid(); PrivateReplacement(http);
            var row = await service.RecoverReplacementAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Key, ct);
            return TypedResults.Ok(new PaymentReplacementRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : ReplacementResult(row)));
        }).WithName("RecoverOwnPaymentReplacement");
        group.MapPost("/{id}/replacement-origin/query", async (string id, OrderReadDto request, HttpContext http, PaymentDraftService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Purpose != "payment-replacement-origin") throw Invalid(); PrivateReplacement(http);
            var row = await service.ReadReplacementOriginAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), ct);
            return TypedResults.Ok(new PaymentReplacementOriginViewDto(row is null ? null : new(new(row.ParentDeclarationId), OrderDraftInputDto.Number(row.ParentDeclarationRevision))));
        }).WithName("ReadOwnPaymentReplacementOrigin");
        group.MapPost("", async (PaymentDraftSaveDto request, HttpContext http, PaymentDraftService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Input is null) throw Invalid();
            return TypedResults.Ok(Result(await service.SaveAsync(http.Features.Get<ResolvedActor>()!.Actor, request.DeclarationId?.Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), request.Input.Domain(), new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("SavePaymentDeclaration");
        group.MapPost("/operations/query", async (OrderDraftRecoveryDto request, HttpContext http, PaymentDraftService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw Invalid(); var row = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Key, ct);
            return TypedResults.Ok(new PaymentDraftRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverPaymentDeclaration");
        group.MapPost("/{id}/submit", async (string id, PaymentSubmissionDto request, HttpContext http, PaymentEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Evidence is null) throw Invalid();
            var row = await service.SubmitAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), request.ReservationId.Value,
                new(request.Evidence.Id.Value, LongId.Parse(request.Evidence.Version).Value, request.Evidence.Sha256), new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            return TypedResults.Ok(Submitted(row));
        }).WithName("SubmitPaymentDeclaration");
        group.MapPost("/{id}/submission/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, PaymentEvidenceService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw Invalid();
            var row = await service.RecoverSubmissionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Key, ct);
            return TypedResults.Ok(new PaymentSubmissionRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Submitted(row)));
        }).WithName("RecoverPaymentSubmission");
    }
    internal static void Purpose(string value) { if (value != "payment-declarations") throw Invalid(); }
    internal static DomainValidationException Invalid() => new("invalid_payment_declaration", "Use the current authorized payment declaration workflow");
    internal static PaymentDraftDto View(PaymentDraftView row) => new(row.Id, OrderDraftInputDto.Number(row.Revision), row.CreatedAt, row.UpdatedAt, row.State, PaymentDraftInputDto.From(row.Input), row.Invoices,
        row.Submission is { } submitted ? new(submitted.Id, OrderDraftInputDto.Number(submitted.ReviewRevision), submitted.ReservationId,
            new(new(submitted.Evidence.Id), OrderDraftInputDto.Number(submitted.Evidence.Version), submitted.Evidence.Sha256), submitted.SubmittedAt) : null);
    private static PaymentSubmissionResultDto Submitted(BsonDocument row) => new(new(row["declarationId"].AsInt64), new(row["submissionId"].AsInt64),
        OrderDraftInputDto.Number(row["declarationRevision"].AsInt64), OrderDraftInputDto.Number(row["reviewRevision"].AsInt64), row["state"].AsString,
        DateTimeOffset.FromUnixTimeMilliseconds(row["submittedAt"].AsBsonDateTime.MillisecondsSinceEpoch));
    private static PaymentDraftResultDto Result(BsonDocument row) => new(new(row["declarationId"].AsInt64), new(row["versionId"].AsInt64),
        OrderDraftInputDto.Number(row["revision"].AsInt64), DateTimeOffset.FromUnixTimeMilliseconds(row["recordedAt"].AsBsonDateTime.MillisecondsSinceEpoch));
    private static PaymentReplacementResultDto ReplacementResult(BsonDocument row) => new(new(row["parentDeclarationId"].AsInt64), OrderDraftInputDto.Number(row["parentDeclarationRevision"].AsInt64),
        new(row["declarationId"].AsInt64), new(row["versionId"].AsInt64), OrderDraftInputDto.Number(row["revision"].AsInt64), new(row["recordedAt"].ToUniversalTime()));
    private static void PrivateReplacement(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache"; }
}
