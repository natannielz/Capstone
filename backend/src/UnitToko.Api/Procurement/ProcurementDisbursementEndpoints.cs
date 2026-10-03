using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Billing;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Procurement;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;
using R = UnitToko.Api.Procurement.ProcurementRequestEndpoints;

namespace UnitToko.Api.Procurement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementDisbursementInputDto([property: JsonRequired] ProcurementLocationDto Location,
    [property: JsonRequired] LongId FundingRequestId, [property: JsonRequired] LongId FundingDecisionId, [property: JsonRequired] LongId AuthorizationId,
    [property: JsonRequired] LongId SupplierId, [property: JsonRequired] LongId BeneficiaryVersionId, [property: JsonRequired] string BeneficiaryVersion,
    [property: JsonRequired] LongId PayingAccountId, [property: JsonRequired] LongId PayingAccountVersionId,
    [property: JsonRequired] LongId PolicyId, [property: JsonRequired] string PolicyRevision, [property: JsonRequired] Money Amount,
    [property: JsonRequired] DateOnly TransferDate, [property: JsonRequired] string TransferReference) {
    internal ProcurementDisbursementInput Domain() {
        if (Location is null) throw ProcurementDisbursementRules.Invalid();
        return ProcurementDisbursementRules.Copy(new(Location.Domain(), FundingRequestId.Value, FundingDecisionId.Value, AuthorizationId.Value,
            SupplierId.Value, BeneficiaryVersionId.Value, PaymentAllocationInputDto.Version(BeneficiaryVersion), PayingAccountId.Value,
            PayingAccountVersionId.Value, PolicyId.Value, PaymentAllocationInputDto.Version(PolicyRevision), Amount, TransferDate, TransferReference));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementDisbursementReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementDisbursementListInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementLocationDto Location, LongId? BeforeId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementDisbursementOptionsInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId AuthorizationId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementDisbursementPreviewInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] ProcurementDisbursementInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementDisbursementDraftCommandDto([property: JsonRequired] ProcurementDisbursementInputDto Input, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementDisbursementRecordPreviewInputDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] LongId ReservationId, [property: JsonRequired] PaymentEvidenceReferenceDto Reference);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementDisbursementRecordCommandDto([property: JsonRequired] LongId ReservationId,
    [property: JsonRequired] PaymentEvidenceReferenceDto Reference, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcurementDisbursementRecoveryInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Operation, [property: JsonRequired] string Key);
public sealed record ProcurementDisbursementAuthorizationDto(LongId Id, LongId RequestId, LongId DecisionId, LongId OrderId, string OrderVersion,
    LongId OrderApprovalId, string Route, PurchaseOrderSupplierDto Supplier, Money AuthorizedAmount, string BusinessDate, string NeededBy, DateTimeOffset ApprovedAt);
public sealed record ProcurementDisbursementCapacityDto(LongId AuthorizationId, Money AuthorizedAmount, Money RecordedAmount, Money RemainingAmount, string Version);
public sealed record ProcurementDisbursementAuthorizationChoiceDto(ProcurementDisbursementAuthorizationDto Authorization, ProcurementDisbursementCapacityDto Capacity);
public sealed record ProcurementDisbursementAuthorizationsDto(ProcurementLocationDto Location, ProcurementDisbursementAuthorizationChoiceDto[] Items, LongId? NextAuthorizationId);
public sealed record ProcurementDisbursementPolicyDto(LongId Id, ProcurementLocationDto Location, string Revision, LongId PayingAccountId,
    LongId PayingAccountVersionId, bool AllowPartialRecordings, bool ReserveUnverified, string PayoutRecordingProcedureReference,
    string BeneficiaryVerificationProcedureReference, string OperatingOwnerReference, string ApprovalReference, DateTimeOffset ApprovedAt, DateTimeOffset ExpiresAt, bool Active);
public sealed record ProcurementDisbursementAccountDto(LongId Id, LongId VersionId, string Version, string BankCode, string BankName, string MaskedAccount, string Currency);
public sealed record ProcurementDisbursementBeneficiaryDto(LongId SupplierId, LongId BeneficiaryId, LongId VersionId, string Version, string BankCode, string MaskedAccount, string Currency);
public sealed record ProcurementDisbursementOptionDto(ProcurementDisbursementPolicyDto Policy, ProcurementDisbursementAccountDto PayingAccount);
public sealed record ProcurementDisbursementOptionsDto(ProcurementDisbursementAuthorizationDto Authorization, ProcurementDisbursementCapacityDto Capacity,
    ProcurementDisbursementBeneficiaryDto? Beneficiary, ProcurementDisbursementOptionDto[] Items);
public sealed record ProcurementDisbursementPreviewDto(ProcurementDisbursementInputDto Input, ProcurementDisbursementAuthorizationDto Authorization,
    ProcurementDisbursementCapacityDto Capacity, ProcurementDisbursementBeneficiaryDto Beneficiary, ProcurementDisbursementAccountDto PayingAccount,
    ProcurementDisbursementPolicyDto Policy, string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementDisbursementRecordedDto(LongId Id, Money Amount, DateTimeOffset RecordedAt, LongId ReservationId, PaymentEvidenceReferenceDto Evidence);
public sealed record ProcurementDisbursementDraftDto(LongId Id, string Version, ProcurementDisbursementInputDto Input, ProcurementDisbursementAuthorizationDto Authorization,
    ProcurementDisbursementCapacityDto Capacity, ProcurementDisbursementBeneficiaryDto Beneficiary, ProcurementDisbursementAccountDto PayingAccount,
    ProcurementDisbursementPolicyDto Policy, string Reason, DateTimeOffset CreatedAt, string State, ProcurementDisbursementRecordedDto? Disbursement);
public sealed record ProcurementDisbursementHistoryDto(ProcurementLocationDto Location, ProcurementDisbursementDraftDto[] Items, LongId? NextDraftId);
public sealed record ProcurementDisbursementRecordPreviewDto(ProcurementDisbursementDraftDto Draft, ProcurementDisbursementCapacityDto Capacity, string Binding, DateTimeOffset ObservedAt);
public sealed record ProcurementDisbursementResultDto(LongId DraftId, string DraftVersion, LongId AuthorizationId, LongId? DisbursementId, Money Amount, string Status);
public sealed record ProcurementDisbursementRecoveryDto(string Status, ProcurementDisbursementResultDto? Result);

public static class ProcurementDisbursementEndpoints {
    public static void MapProcurementDisbursementEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/procurement/disbursements").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 415, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (ProcurementDisbursementReadDto request, HttpContext http, ProcurementDisbursementService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.TargetsAsync(Actor(http), ct);
            return TypedResults.Ok(new ProcurementTargetsDto([.. row["items"].AsBsonArray.Select(R.Target)]));
        }).WithName("ProcurementDisbursementTargets");
        group.MapPost("/authorizations/query", async (ProcurementDisbursementListInputDto request, HttpContext http, ProcurementDisbursementService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.AuthorizationsAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementDisbursementAuthorizationsDto(R.Location(row["location"]),
                [.. row["items"].AsBsonArray.Select(item => new ProcurementDisbursementAuthorizationChoiceDto(Authorization(item["authorization"]), Capacity(item["capacity"])))], Id(row["nextAuthorizationId"])));
        }).WithName("ProcurementDisbursementAuthorizations");
        group.MapPost("/options/query", async (ProcurementDisbursementOptionsInputDto request, HttpContext http, ProcurementDisbursementService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.OptionsAsync(Actor(http), request.AuthorizationId.Value, ct);
            return TypedResults.Ok(new ProcurementDisbursementOptionsDto(Authorization(row["authorization"]), Capacity(row["capacity"]),
                row["beneficiary"].IsBsonNull ? null : Beneficiary(row["beneficiary"]),
                [.. row["items"].AsBsonArray.Select(item => new ProcurementDisbursementOptionDto(Policy(item["policy"]), Account(item["payingAccount"]))) ]));
        }).WithName("ProcurementDisbursementOptions");
        group.MapPost("/preview/query", async (ProcurementDisbursementPreviewInputDto request, HttpContext http, ProcurementDisbursementService service, CancellationToken ct) => {
            Purpose(request.Purpose); if (request.Input is null) throw ProcurementDisbursementRules.Invalid();
            return TypedResults.Ok(Preview(await service.PreviewAsync(Actor(http), request.Input.Domain(), ct)));
        }).WithName("PreviewProcurementDisbursementDraft");
        group.MapPost("/drafts", async (ProcurementDisbursementDraftCommandDto request, HttpContext http, ProcurementDisbursementService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            if (request.Input is null) throw ProcurementDisbursementRules.Invalid();
            return TypedResults.Ok(Result(await service.CreateDraftAsync(Actor(http), request.Input.Domain(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("CreateProcurementDisbursementDraft");
        group.MapPost("/drafts/query", async (ProcurementDisbursementListInputDto request, HttpContext http, ProcurementDisbursementService service, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.ListAsync(Actor(http), Location(request.Location), request.BeforeId?.Value, ct);
            return TypedResults.Ok(new ProcurementDisbursementHistoryDto(R.Location(row["location"]), [.. row["items"].AsBsonArray.Select(Draft)], Id(row["nextDraftId"])));
        }).WithName("ProcurementDisbursementHistory");
        group.MapPost("/operations/query", async (ProcurementDisbursementRecoveryInputDto request, HttpContext http, ProcurementDisbursementService service, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery"); var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new ProcurementDisbursementRecoveryDto(row is null ? "not_confirmed" : "confirmed", row is null ? null : Result(row)));
        }).WithName("RecoverProcurementDisbursement");
        var drafts = group.MapGroup("/drafts/{id}");
        drafts.MapPost("/query", async (string id, ProcurementDisbursementReadDto request, HttpContext http, ProcurementDisbursementService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Draft(await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("ReadProcurementDisbursementDraft");
        drafts.MapPost("/record-preview/query", async (string id, ProcurementDisbursementRecordPreviewInputDto request, HttpContext http, ProcurementDisbursementService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose); var row = await service.PreviewRecordAsync(Actor(http), LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), request.ReservationId.Value, Reference(request.Reference), ct);
            return TypedResults.Ok(new ProcurementDisbursementRecordPreviewDto(Draft(row["draft"]), Capacity(row["capacity"]), row["binding"].AsString, Date(row["observedAt"])));
        }).WithName("PreviewProcurementDisbursementRecord");
        drafts.MapPost("/records", async (string id, ProcurementDisbursementRecordCommandDto request, HttpContext http, ProcurementDisbursementEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            return TypedResults.Ok(Result(await service.RecordAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                request.ReservationId.Value, Reference(request.Reference), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("RecordProcurementDisbursement");
        MapEvidence(drafts);
    }
    private static ActorContext Actor(HttpContext http) { A.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static void Purpose(string value, string expected = "disbursement-review") { if (value != expected) throw ProcurementDisbursementRules.Invalid(); }
    private static ProcurementLocation Location(ProcurementLocationDto value) => value is null ? throw ProcurementDisbursementRules.Invalid() : value.Domain();
    private static string Number(BsonValue value) => value.AsInt64.ToString(CultureInfo.InvariantCulture);
    private static LongId? Id(BsonValue value) => value.IsBsonNull ? null : new(value.AsInt64);
    private static Money Amount(BsonValue value) => Money.Parse(value.AsString);
    private static DateTimeOffset Date(BsonValue value) => new(value.ToUniversalTime(), TimeSpan.Zero);
    private static EvidenceRef Reference(PaymentEvidenceReferenceDto value) => value is null ? throw ProcurementDisbursementRules.Invalid() : new(value.Id.Value, LongId.Parse(value.Version).Value, value.Sha256);
    private static PaymentEvidenceReferenceDto Reference(EvidenceRef value) => new(new(value.Id), Number(value.Version), value.Sha256);
    private static PaymentEvidenceReferenceDto Evidence(BsonValue value) => new(new(value["id"].AsInt64), Number(value["version"]), value["sha256"].AsString);
    private static ProcurementDisbursementInputDto Input(BsonValue row) => new(R.Location(row["location"]), new(row["fundingRequestId"].AsInt64),
        new(row["fundingDecisionId"].AsInt64), new(row["authorizationId"].AsInt64), new(row["supplierId"].AsInt64), new(row["beneficiaryVersionId"].AsInt64), Number(row["beneficiaryVersion"]),
        new(row["payingAccountId"].AsInt64), new(row["payingAccountVersionId"].AsInt64), new(row["policyId"].AsInt64), Number(row["policyRevision"]), Amount(row["amount"]),
        DateOnly.ParseExact(row["transferDate"].AsString, "yyyy-MM-dd", CultureInfo.InvariantCulture), row["transferReference"].AsString);
    private static ProcurementDisbursementAuthorizationDto Authorization(BsonValue row) => new(new(row["id"].AsInt64), new(row["requestId"].AsInt64), new(row["decisionId"].AsInt64),
        new(row["orderId"].AsInt64), Number(row["orderVersion"]), new(row["orderApprovalId"].AsInt64), row["route"].AsString, PurchaseOrderEndpoints.Supplier(row["supplier"]),
        Amount(row["authorizedAmount"]), row["businessDate"].AsString, row["neededBy"].AsString, Date(row["approvedAt"]));
    private static ProcurementDisbursementCapacityDto Capacity(BsonValue row) => new(new(row["authorizationId"].AsInt64), Amount(row["authorizedAmount"]), Amount(row["recordedAmount"]), Amount(row["remainingAmount"]), Number(row["version"]));
    private static ProcurementDisbursementPolicyDto Policy(BsonValue row) => new(new(row["id"].AsInt64), R.Location(row["location"]), Number(row["revision"]),
        new(row["payingAccountId"].AsInt64), new(row["payingAccountVersionId"].AsInt64), row["allowPartialRecordings"].AsBoolean, row["reserveUnverified"].AsBoolean,
        row["payoutRecordingProcedureReference"].AsString, row["beneficiaryVerificationProcedureReference"].AsString, row["operatingOwnerReference"].AsString,
        row["approvalReference"].AsString, Date(row["approvedAt"]), Date(row["expiresAt"]), row["active"].AsBoolean);
    private static ProcurementDisbursementAccountDto Account(BsonValue row) => new(new(row["id"].AsInt64), new(row["versionId"].AsInt64), Number(row["version"]),
        row["bankCode"].AsString, row["bankName"].AsString, row["maskedAccount"].AsString, row["currency"].AsString);
    private static ProcurementDisbursementBeneficiaryDto Beneficiary(BsonValue row) => new(new(row["supplierId"].AsInt64), new(row["beneficiaryId"].AsInt64), new(row["versionId"].AsInt64),
        Number(row["version"]), row["bankCode"].AsString, row["maskedAccount"].AsString, row["currency"].AsString);
    private static ProcurementDisbursementPreviewDto Preview(BsonValue row) => new(Input(row["input"]), Authorization(row["authorization"]), Capacity(row["capacity"]),
        Beneficiary(row["beneficiary"]), Account(row["payingAccount"]), Policy(row["policy"]), row["binding"].AsString, Date(row["observedAt"]));
    private static ProcurementDisbursementDraftDto Draft(BsonValue row) => new(new(row["id"].AsInt64), Number(row["version"]), Input(row["input"]), Authorization(row["authorization"]),
        Capacity(row["capacity"]), Beneficiary(row["beneficiary"]), Account(row["payingAccount"]), Policy(row["policy"]), row["reason"].AsString, Date(row["createdAt"]), row["state"].AsString,
        row["disbursement"].IsBsonNull ? null : new(new(row["disbursement"]["id"].AsInt64), Amount(row["disbursement"]["amount"]), Date(row["disbursement"]["recordedAt"]),
            new(row["disbursement"]["reservationId"].AsInt64), Evidence(row["disbursement"]["evidence"])));
    private static ProcurementDisbursementResultDto Result(BsonValue row) => new(new(row["draftId"].AsInt64), Number(row["draftVersion"]), new(row["authorizationId"].AsInt64),
        Id(row["disbursementId"]), Amount(row["amount"]), row["status"].AsString);
    private static PaymentUploadReservationDto Reservation(ReservationResult row) => new(new(row.ReservationId), Number(row.Revision), row.ExpiresAt);
    private static PaymentEvidenceStatusDto Status(SafeStatus row) => new(new(row.ReservationId), row.State, Number(row.Revision), row.ExpiresAt,
        row.Reference is null ? null : Reference(row.Reference), row.ScanStatus, row.DecoderStatus, row.SafeFailure);
    private static void MapEvidence(RouteGroupBuilder drafts) {
        drafts.MapPost("/evidence/query", async (string id, InvoiceQueryDto request, HttpContext http, ProcurementDisbursementEvidenceService service, CancellationToken ct) => {
            Purpose(request.Purpose, "procurement-disbursement-evidence");
            return TypedResults.Ok(new PaymentEvidencePageDto([.. (await service.ListAsync(Actor(http), LongId.Parse(id).Value, request.BeforeId?.Value, ct)).Select(Status)]));
        }).WithName("ListProcurementDisbursementEvidence");
        drafts.MapPost("/evidence", async (string id, PaymentEvidenceReserveDto request, HttpContext http, ProcurementDisbursementEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            return TypedResults.Ok(Reservation(await service.ReserveAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ReserveProcurementDisbursementEvidence");
        drafts.MapPost("/evidence/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, ProcurementDisbursementEvidenceService service, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery"); var row = await service.RecoverReservationAsync(Actor(http), LongId.Parse(id).Value, request.Key, ct);
            return TypedResults.Ok(new PaymentUploadRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Reservation(row)));
        }).WithName("RecoverProcurementDisbursementEvidenceReservation");
        drafts.MapPost("/evidence/{reservationId}/query", async (string id, string reservationId, ProcurementDisbursementReadDto request, HttpContext http, ProcurementDisbursementEvidenceService service, CancellationToken ct) => {
            Purpose(request.Purpose, "procurement-disbursement-evidence"); return TypedResults.Ok(Status(await service.StatusAsync(Actor(http), LongId.Parse(id).Value, LongId.Parse(reservationId).Value, ct)));
        }).WithName("ReadProcurementDisbursementEvidenceStatus");
        drafts.MapPost("/evidence/{reservationId}/content", async (string id, string reservationId, HttpContext http, ProcurementDisbursementEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (!string.Equals(http.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) || http.Request.Headers.ContainsKey("Content-Encoding"))
                throw new BadHttpRequestException("Use unencoded original bytes", StatusCodes.Status415UnsupportedMediaType);
            var actor = Actor(http); using var source = new PaymentEvidenceBodyStream(http.Request.Body);
            var result = await service.UploadAsync(actor, LongId.Parse(id).Value, LongId.Parse(reservationId).Value, AccessRequestMetadata.ExpectedVersion(http.Request), source,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), "Upload original procurement disbursement evidence", http.TraceIdentifier), ct);
            if (source.Exceeded) throw new BadHttpRequestException("Upload too large", StatusCodes.Status413PayloadTooLarge);
            return TypedResults.Ok(new PaymentUploadOutcomeDto(result.State, result.Evidence is null ? null : Reference(result.Evidence.Reference)));
        }).WithMetadata(PaymentEvidenceBodyStream.Metadata).Accepts<byte[]>("application/octet-stream").WithName("UploadProcurementDisbursementEvidence");
        drafts.MapPost("/evidence/{reservationId}/recover", async (string id, string reservationId, PaymentEvidenceRecoverDto request, HttpContext http, ProcurementDisbursementEvidenceService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            Purpose(request.Purpose, "upload-recovery"); var result = await service.RecoverUploadAsync(Actor(http), LongId.Parse(id).Value, LongId.Parse(reservationId).Value,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct);
            return TypedResults.Ok(new PaymentUploadOutcomeDto(result.State, result.Evidence is null ? null : Reference(result.Evidence.Reference)));
        }).WithName("RecoverProcurementDisbursementEvidenceUpload");
        drafts.MapPost("/evidence/{reservationId}/original/query", async (string id, string reservationId, PaymentEvidenceReadDto request, HttpContext http, ProcurementDisbursementEvidenceService service,
            [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            Purpose(request.Purpose, "procurement-disbursement-evidence-view"); if (http.Request.Headers.ContainsKey("Range")) throw ProcurementDisbursementRules.Invalid();
            var reference = Reference(request.Reference); var file = await service.OpenReadAsync(Actor(http), LongId.Parse(id).Value, LongId.Parse(reservationId).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), reference, new(request.Purpose, http.TraceIdentifier), ct);
            http.Response.Headers.XContentTypeOptions = "nosniff"; http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            http.Response.Headers["Referrer-Policy"] = "no-referrer"; http.Response.ContentLength = file.Length;
            return Results.Stream(file.Content, file.Mime, $"bukti-pengadaan-{reference.Id}-{reference.Version}.{(file.Mime == "image/png" ? "png" : "jpg")}", enableRangeProcessing: false);
        }).Produces<byte[]>(200, "image/png", "image/jpeg").WithName("ReadProcurementDisbursementEvidenceOriginal");
    }
}
