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
public sealed record PaymentVerificationPreviewQueryDto([property: JsonRequired] LongId ReceiptId, string? ReviewSource = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentVerificationResolutionDto([property: JsonRequired] string Field, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentVerificationCommandDto([property: JsonRequired] LongId ReceiptId, [property: JsonRequired] string Binding,
    [property: JsonRequired] string PayerVerificationReference, [property: JsonRequired] bool ImageReviewed, [property: JsonRequired] bool BankReviewed,
    [property: JsonRequired] bool PayerVerified, [property: JsonRequired] PaymentVerificationResolutionDto[] Resolutions, [property: JsonRequired] string Reason);
public sealed record PaymentVerificationPreviewDto(string Binding, LongId DeclarationId, string DeclarationRevision, LongId ReceiptId, string ReviewRevision,
    LongId BuyingOrganizationId, string BuyerName, string DeclaredPayerName, string Amount, string BookedDate, string TransactionId,
    string AccountNumber, string AccountHolder, string BankCode, string PayerVerificationProcedureReference,
    ImmutableArray<PaymentReviewField> Fields, ImmutableArray<PaymentDiscrepancy> Discrepancies, string ReviewSource);
public sealed record PaymentVerificationResultDto(LongId VerificationId, LongId DeclarationId, LongId ReceiptId, string ReviewRevision, DateTimeOffset RecordedAt, string Operation);
public sealed record PaymentVerificationDetailDto(PaymentVerificationResultDto Result, string DeclarationRevision, PaymentEvidenceReferenceDto Evidence,
    LongId VerifiedBy, string PayerVerificationReference, string Reason, ImmutableArray<PaymentDiscrepancyResolution> Resolutions, string ReviewSource);
public sealed record PaymentVerificationReadDto(string State, PaymentVerificationDetailDto? Verification);
public sealed record PaymentVerificationRecoveryDto(string Status, PaymentVerificationResultDto? Result);
public sealed record PaymentVerificationReceiptDto(LongId ReceiptId, string Amount, string BookedDate, string TransactionId);
public sealed record PaymentVerificationReceiptPageDto(PaymentVerificationReceiptDto[] Items, LongId? NextAfterId);

public static class PaymentVerificationEndpoints
{
    public static void MapPaymentVerificationEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/billing/payment-reviews/{id}/verification").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/receipts/query", async (string id, ReceivingAccountQueryDto request, HttpContext http, PaymentVerificationService service, CancellationToken ct) => {
            if (request.Purpose != "payment-review") throw PaymentVerificationPolicy.Invalid(); Private(http);
            var page = await service.ReceiptCandidatesAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.AfterId?.Value, ct);
            return TypedResults.Ok(new PaymentVerificationReceiptPageDto([.. page.Items.Select(row => new PaymentVerificationReceiptDto(
                new(row["_id"].AsInt64), new UnitToko.Modules.Accounting.Money(row["amount"].AsDecimal).ToString(), row["bookedDate"].AsString, row["transactionId"].AsString))],
                page.NextAfterId is { } next ? new(next) : null));
        }).WithName("ListPaymentVerificationReceipts");
        group.MapPost("/preview", async (string id, PaymentVerificationPreviewQueryDto request, HttpContext http, PaymentVerificationService service, CancellationToken ct) => {
            Private(http); var preview = await service.PreviewAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.ReceiptId.Value, ct, request.ReviewSource);
            return TypedResults.Ok(Preview(preview));
        }).WithName("PreviewPaymentVerification");
        group.MapPost("/query", async (string id, OrderReadDto request, HttpContext http, PaymentVerificationService service, CancellationToken ct) => {
            if (request.Purpose != "payment-review") throw PaymentVerificationPolicy.Invalid(); Private(http);
            var row = await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct);
            return TypedResults.Ok(new PaymentVerificationReadDto(row is null ? "not_verified" : "verified", row is null ? null : Detail(row)));
        }).WithName("ReadPaymentVerification");
        group.MapPost("", async (string id, PaymentVerificationCommandDto request, HttpContext http, PaymentVerificationService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (AccessRequestMetadata.ExpectedVersion(http.Request) != 0 || request.Resolutions is null || request.Resolutions.Any(item => item is null)) throw PaymentVerificationPolicy.Invalid(); Private(http);
            var input = new PaymentVerificationInput(request.Binding, request.PayerVerificationReference, request.ImageReviewed, request.BankReviewed, request.PayerVerified,
                [.. request.Resolutions.Select(item => new PaymentDiscrepancyResolution(item.Field, item.Reason))]);
            return TypedResults.Ok(Result(await service.VerifyAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.ReceiptId.Value, input,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("VerifyPaymentDeclaration");
        group.MapPost("/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, PaymentVerificationService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw PaymentVerificationPolicy.Invalid(); Private(http);
            var row = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Key, ct);
            if (row is not null && row["declarationId"].AsInt64 != LongId.Parse(id).Value) throw PaymentVerificationPolicy.Invalid();
            return TypedResults.Ok(new PaymentVerificationRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverPaymentVerification");
    }
    private static string Number(BsonValue value) => OrderDraftInputDto.Number(value.AsInt64);
    private static PaymentVerificationPreviewDto Preview(PaymentVerificationPreview value) => new(value.Binding, new(value.Declaration["declarationId"].AsInt64), Number(value.Declaration["revision"]),
        new(value.Receipt["_id"].AsInt64), Number(value.Review["revision"]), new(value.Declaration["party"]["buyingOrganizationId"].AsInt64),
        value.Declaration["invoices"][0]["buyerName"].AsString, value.Declaration["input"]["payerName"].AsString,
        new UnitToko.Modules.Accounting.Money(value.Receipt["amount"].AsDecimal).ToString(), value.Receipt["bookedDate"].AsString, value.Receipt["transactionId"].AsString,
        value.Account["details"]["accountNumber"].AsString, value.Account["details"]["holderName"].AsString, value.Account["details"]["bankCode"].AsString,
        value.Policy["payerVerificationProcedureReference"].AsString,
        [.. value.Review["fields"].AsBsonArray.Select(field => new PaymentReviewField(field["field"].AsString, field["value"].IsBsonNull ? null : field["value"].AsString))], value.Discrepancies, value.Review.GetValue("kind", "ocr").AsString);
    private static PaymentVerificationResultDto Result(BsonDocument row) => new(new(row["verificationId"].AsInt64), new(row["declarationId"].AsInt64), new(row["receiptId"].AsInt64),
        Number(row["reviewRevision"]), new(row["recordedAt"].ToUniversalTime()), row["operation"].AsString);
    private static PaymentVerificationDetailDto Detail(BsonDocument row) {
        var source = row["source"]; var reference = source["evidence"];
        return new(new(new(row["_id"].AsInt64), new(row["declarationId"].AsInt64), new(row["receiptId"].AsInt64), Number(row["review"]["revision"]), new(row["recordedAt"].ToUniversalTime()), row["operation"].AsString),
            Number(source["revision"]), new(new(reference["id"].AsInt64), Number(reference["version"]), reference["sha256"].AsString),
            new(row["actorId"].AsInt64), row["payerVerificationReference"].AsString, row["reason"].AsString,
            [.. row["resolutions"].AsBsonArray.Select(item => new PaymentDiscrepancyResolution(item["field"].AsString, item["reason"].AsString))], row["review"].AsBsonDocument.GetValue("kind", "ocr").AsString);
    }
    private static void Private(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache"; }
}
