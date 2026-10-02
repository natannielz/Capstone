using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;

namespace UnitToko.Api.Billing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RefundPayoutTransferDto([property: JsonRequired] DateOnly TransferDate, [property: JsonRequired] string TransferReference);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RefundPayoutInputDto([property: JsonRequired] LongId BeneficiaryId, [property: JsonRequired] string ExpectedBeneficiaryRevision,
    [property: JsonRequired] LongId PayingAccountId, [property: JsonRequired] LongId PayingAccountVersionId, [property: JsonRequired] Money Amount,
    [property: JsonRequired] DateOnly TransferDate, [property: JsonRequired] string TransferReference) {
    internal RefundPayoutInput Domain() => RefundPayoutPolicy.Copy(new(BeneficiaryId.Value, PaymentAllocationInputDto.Version(ExpectedBeneficiaryRevision),
        PayingAccountId.Value, PayingAccountVersionId.Value, Amount, TransferDate, TransferReference));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RefundPayoutDraftCommandDto([property: JsonRequired] string Binding, [property: JsonRequired] RefundPayoutInputDto Input, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RefundPayoutRecordPreviewDto([property: JsonRequired] LongId ReservationId, [property: JsonRequired] PaymentEvidenceReferenceDto Reference);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RefundPayoutRecordCommandDto([property: JsonRequired] LongId ReservationId, [property: JsonRequired] PaymentEvidenceReferenceDto Reference,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
public sealed record RefundPayoutDraftResultDto(LongId DraftId, string Revision, LongId ApprovalId, string State, string Amount, DateTimeOffset CreatedAt);
public sealed record RefundPayoutResultDto(LongId PayoutId, LongId DraftId, LongId ApprovalId, string State, string Amount, DateTimeOffset RecordedAt);
public sealed record RefundPayoutViewDto(LongId ApprovalId, LongId CaseId, LongId ReturnId, LongId InvoiceId, string Amount,
    LongId BeneficiaryId, string BeneficiaryRevision, string BeneficiaryName, string BeneficiaryBankCode, string BeneficiaryAccountSuffix,
    LongId PayingAccountId, LongId PayingAccountVersionId, string PayingBankCode, string PayingAccountSuffix,
    string PayoutRecordingProcedureReference, string BeneficiaryVerificationProcedureReference, RefundPayoutInputDto Input,
    RefundPayoutResultDto? Payout, RefundPayoutDraftResultDto? Draft, PaymentEvidenceReferenceDto? Evidence, LongId? ReservationId, RefundSettlementResultDto? Settlement);
public sealed record RefundPayoutPreviewDto(RefundPayoutViewDto View, string Binding);
public sealed record RefundPayoutDraftRecoveryDto(string Status, RefundPayoutDraftResultDto? Result);
public sealed record RefundPayoutRecoveryDto(string Status, RefundPayoutResultDto? Result);
public sealed record ApprovedRefundPayoutDto(ReturnFinancialApprovalResultDto? Approval, RefundPayoutResultDto? Payout, RefundPayoutDraftResultDto? Draft, RefundSettlementResultDto? Settlement);

public static class RefundPayoutEndpoints {
    public static void MapRefundPayoutEndpoints(this WebApplication app) {
        app.MapPost("/api/v1/billing/cases/{id}/returns/{returnId}/refund-payout/query", async (string id, string returnId, OrderReadDto request,
            HttpContext http, RefundPayoutService service, CancellationToken ct) => {
            Purpose(request.Purpose, "refund-payout"); A.Private(http);
            var row = await service.ReadApprovedRefundAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, ct);
            return TypedResults.Ok(new ApprovedRefundPayoutDto(row["approval"].IsBsonNull ? null : ReturnFinancialApprovalEndpoints.Result(row["approval"].AsBsonDocument),
                row["payout"].IsBsonNull ? null : Result(row["payout"].AsBsonDocument), row["draft"].IsBsonNull ? null : Draft(row["draft"].AsBsonDocument),
                row["settlement"].IsBsonNull ? null : RefundSettlementEndpoints.Result(row["settlement"].AsBsonDocument)));
        }).RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>().WithName("ReadApprovedRefundPayout");
        var approvals = app.MapGroup("/api/v1/billing/refund-approvals/{id}/payout-drafts").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        var drafts = app.MapGroup("/api/v1/billing/refund-payout-drafts/{id}").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var group in new[] { approvals, drafts }) foreach (var status in new[] { 400, 401, 403, 409, 413, 415, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        approvals.MapPost("/preview", async (string id, RefundPayoutTransferDto request, HttpContext http, RefundPayoutService service, CancellationToken ct) => {
            A.Private(http); var preview = await service.PreviewDraftFromTransferAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.TransferDate, request.TransferReference, ct);
            return TypedResults.Ok(new RefundPayoutPreviewDto(View(preview.View), preview.Binding));
        }).WithName("PreviewRefundPayoutDraft");
        approvals.MapPost("", async (string id, RefundPayoutDraftCommandDto request, HttpContext http, RefundPayoutService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            if (request.Input is null) throw RefundPayoutPolicy.Invalid(); A.Private(http);
            return TypedResults.Ok(Draft(await service.CreateDraftAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Binding, request.Input.Domain(),
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("CreateRefundPayoutDraft");
        approvals.MapPost("/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, RefundPayoutService service, CancellationToken ct) => {
            RecoveryPurpose(request.Purpose); A.Private(http); var row = await service.RecoverDraftAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Key, ct);
            return TypedResults.Ok(new RefundPayoutDraftRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Draft(row)));
        }).WithName("RecoverRefundPayoutDraft");
        drafts.MapPost("/query", async (string id, OrderReadDto request, HttpContext http, RefundPayoutService service, CancellationToken ct) => {
            Purpose(request.Purpose, "refund-payout"); A.Private(http); return TypedResults.Ok(View(await service.ReadDraftAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct)));
        }).WithName("ReadRefundPayoutDraft");
        drafts.MapPost("/preview", async (string id, RefundPayoutRecordPreviewDto request, HttpContext http, RefundPayoutService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            A.Private(http); var preview = await service.PreviewRecordAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), request.ReservationId.Value, Reference(request.Reference), ct);
            return TypedResults.Ok(new RefundPayoutPreviewDto(View(preview.View), preview.Binding));
        }).WithName("PreviewRefundPayoutRecord");
        drafts.MapPost("/record", async (string id, RefundPayoutRecordCommandDto request, HttpContext http, RefundPayoutEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            A.Private(http); return TypedResults.Ok(Result(await service.RecordAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), request.ReservationId.Value, Reference(request.Reference), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("RecordRefundPayout");
        drafts.MapPost("/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, RefundPayoutService service, CancellationToken ct) => {
            RecoveryPurpose(request.Purpose); A.Private(http); var row = await service.RecoverRecordAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Key, ct);
            return TypedResults.Ok(new RefundPayoutRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverRefundPayoutRecord");
        MapEvidence(drafts);
        app.MapRefundSettlementEndpoints();
    }
    private static void Purpose(string value, string expected) { if (value != expected) throw RefundPayoutPolicy.Invalid(); }
    private static void RecoveryPurpose(string value) => Purpose(value, "operation-recovery");
    private static EvidenceRef Reference(PaymentEvidenceReferenceDto value) {
        if (value is null) throw RefundPayoutPolicy.Invalid(); return new(value.Id.Value, LongId.Parse(value.Version).Value, value.Sha256);
    }
    internal static RefundPayoutDraftResultDto Draft(BsonDocument row) => new(new(row["draftId"].AsInt64), A.Number(row["revision"].AsInt64), new(row["approvalId"].AsInt64), row["state"].AsString, A.MoneyText(row["amount"]), new(row["createdAt"].ToUniversalTime()));
    internal static RefundPayoutResultDto Result(BsonDocument row) => new(new(row["payoutId"].AsInt64), new(row["draftId"].AsInt64), new(row["approvalId"].AsInt64), row["state"].AsString, A.MoneyText(row["amount"]), new(row["recordedAt"].ToUniversalTime()));
    private static RefundPayoutViewDto View(BsonDocument row) {
        var input = row["input"]; var hasDraft = row.TryGetValue("draft", out var draft) && !draft.IsBsonNull;
        return new(new(row["approvalId"].AsInt64), new(row["caseId"].AsInt64), new(row["returnId"].AsInt64), new(row["invoiceId"].AsInt64), A.MoneyText(row["amount"]),
            new(row["beneficiaryId"].AsInt64), A.Number(row["beneficiaryRevision"].AsInt64), row["beneficiaryName"].AsString, row["beneficiaryBankCode"].AsString, row["beneficiaryAccountSuffix"].AsString,
            new(row["payingAccountId"].AsInt64), new(row["payingAccountVersionId"].AsInt64), row["payingBankCode"].AsString, row["payingAccountSuffix"].AsString,
            row["payoutRecordingProcedureReference"].AsString, row["beneficiaryVerificationProcedureReference"].AsString,
            new(new(input["beneficiaryId"].AsInt64), A.Number(input["expectedBeneficiaryRevision"].AsInt64), new(input["payingAccountId"].AsInt64), new(input["payingAccountVersionId"].AsInt64), new(input["amount"].AsDecimal),
                DateOnly.ParseExact(input["transferDate"].AsString, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), input["transferReference"].AsString),
            row["payout"].IsBsonNull ? null : Result(row["payout"].AsBsonDocument), hasDraft ? Draft(draft!.AsBsonDocument) : null,
            row.TryGetValue("evidence", out var evidence) && !evidence.IsBsonNull ? new(new(evidence["id"].AsInt64), A.Number(evidence["version"].AsInt64), evidence["sha256"].AsString) : null,
            row.TryGetValue("reservationId", out var reservation) && !reservation.IsBsonNull ? new(reservation.AsInt64) : null,
            row["settlement"].IsBsonNull ? null : RefundSettlementEndpoints.Result(row["settlement"].AsBsonDocument));
    }
    private static PaymentUploadReservationDto Reservation(ReservationResult row) => new(new(row.ReservationId), A.Number(row.Revision), row.ExpiresAt);
    private static PaymentEvidenceReferenceDto Reference(EvidenceRef row) => new(new(row.Id), A.Number(row.Version), row.Sha256);
    private static PaymentEvidenceStatusDto Status(SafeStatus row) => new(new(row.ReservationId), row.State, A.Number(row.Revision), row.ExpiresAt,
        row.Reference is null ? null : Reference(row.Reference), row.ScanStatus, row.DecoderStatus, row.SafeFailure);
    private static void MapEvidence(RouteGroupBuilder drafts) {
        drafts.MapPost("/evidence/query", async (string id, InvoiceQueryDto request, HttpContext http, RefundPayoutEvidenceService service, CancellationToken ct) => {
            Purpose(request.Purpose, "refund-payout-evidence"); A.Private(http); return TypedResults.Ok(new PaymentEvidencePageDto([.. (await service.ListAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.BeforeId?.Value, ct)).Select(Status)]));
        }).WithName("ListRefundPayoutEvidence");
        drafts.MapPost("/evidence", async (string id, PaymentEvidenceReserveDto request, HttpContext http, RefundPayoutEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            A.Private(http); return TypedResults.Ok(Reservation(await service.ReserveAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ReserveRefundPayoutEvidence");
        drafts.MapPost("/evidence/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, RefundPayoutEvidenceService service, CancellationToken ct) => {
            RecoveryPurpose(request.Purpose); A.Private(http); var row = await service.RecoverReservationAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Key, ct);
            return TypedResults.Ok(new PaymentUploadRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Reservation(row)));
        }).WithName("RecoverRefundPayoutEvidenceReservation");
        drafts.MapPost("/evidence/{reservationId}/query", async (string id, string reservationId, OrderReadDto request, HttpContext http, RefundPayoutEvidenceService service, CancellationToken ct) => {
            Purpose(request.Purpose, "refund-payout-evidence"); A.Private(http); return TypedResults.Ok(Status(await service.StatusAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(reservationId).Value, ct)));
        }).WithName("ReadRefundPayoutEvidenceStatus");
        drafts.MapPost("/evidence/{reservationId}/content", async (string id, string reservationId, HttpContext http, RefundPayoutEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (!string.Equals(http.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) || http.Request.Headers.ContainsKey("Content-Encoding"))
                throw new BadHttpRequestException("Use unencoded original bytes", StatusCodes.Status415UnsupportedMediaType);
            A.Private(http); using var source = new PaymentEvidenceBodyStream(http.Request.Body);
            var result = await service.UploadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(reservationId).Value, AccessRequestMetadata.ExpectedVersion(http.Request), source,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), "Upload original refund payout evidence", http.TraceIdentifier), ct);
            if (source.Exceeded) throw new BadHttpRequestException("Upload too large", StatusCodes.Status413PayloadTooLarge);
            return TypedResults.Ok(new PaymentUploadOutcomeDto(result.State, result.Evidence is null ? null : Reference(result.Evidence.Reference)));
        }).WithMetadata(PaymentEvidenceBodyStream.Metadata).Accepts<byte[]>("application/octet-stream").WithName("UploadRefundPayoutEvidence");
        drafts.MapPost("/evidence/{reservationId}/recover", async (string id, string reservationId, PaymentEvidenceRecoverDto request, HttpContext http, RefundPayoutEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            Purpose(request.Purpose, "upload-recovery"); A.Private(http); var result = await service.RecoverUploadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(reservationId).Value,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            return TypedResults.Ok(new PaymentUploadOutcomeDto(result.State, result.Evidence is null ? null : Reference(result.Evidence.Reference)));
        }).WithName("RecoverRefundPayoutEvidenceUpload");
        drafts.MapPost("/evidence/{reservationId}/original/query", async (string id, string reservationId, PaymentEvidenceReadDto request, HttpContext http, RefundPayoutEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose, "refund-payout-evidence-view"); if (http.Request.Headers.ContainsKey("Range")) throw RefundPayoutPolicy.Invalid();
            var reference = Reference(request.Reference); var file = await service.OpenReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(reservationId).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), reference, new(request.Purpose, http.TraceIdentifier), ct);
            A.Private(http); http.Response.Headers.XContentTypeOptions = "nosniff"; http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            http.Response.Headers["Referrer-Policy"] = "no-referrer"; http.Response.ContentLength = file.Length;
            return Results.Stream(file.Content, file.Mime, $"bukti-refund-{reference.Id}-{reference.Version}.{(file.Mime == "image/png" ? "png" : "jpg")}", enableRangeProcessing: false);
        }).Produces<byte[]>(200, "image/png", "image/jpeg").WithName("ReadRefundPayoutEvidenceOriginal");
    }
}
