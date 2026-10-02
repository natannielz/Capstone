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
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;

namespace UnitToko.Api.Billing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RefundSettlementInputDto([property: JsonRequired] LongId BankImportId, [property: JsonRequired] int BankRowNumber,
    [property: JsonRequired] string BeneficiaryVerificationReference, [property: JsonRequired] string TransferVerificationReference) {
    internal RefundSettlementInput Domain() => RefundSettlementPolicy.Copy(new(BankImportId.Value, BankRowNumber, BeneficiaryVerificationReference, TransferVerificationReference));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RefundSettlementCommandDto([property: JsonRequired] RefundSettlementInputDto Input, [property: JsonRequired] string Binding,
    [property: JsonRequired] bool Attested, [property: JsonRequired] string Reason);
public sealed record RefundSettlementReceiptDto(LongId ReceiptId, string Version, string Paid, string Pending, string ReservedForRefund, string Available);
public sealed record RefundSettlementResultDto(LongId SettlementId, LongId PayoutId, LongId FinancialApprovalId, LongId CaseId, LongId ReturnId, LongId InvoiceId,
    string Amount, string Operation, string State, DateTimeOffset RecordedAt, ImmutableArray<RefundSettlementReceiptDto> Receipts);
public sealed record RefundSettlementViewDto(RefundPayoutResultDto Payout, LongId CaseId, LongId ReturnId, LongId InvoiceId, string Amount,
    string DraftRevision, LongId DraftId, LongId ReservationId, PaymentEvidenceReferenceDto Evidence, RefundSettlementResultDto? Settlement, string CandidateInstructions,
    string BeneficiaryName, string BeneficiaryBankCode, string BeneficiaryAccountSuffix, string PayingBankCode, string PayingAccountSuffix, DateOnly TransferDate, string TransferReference);
public sealed record RefundSettlementDebitDto(LongId BankImportId, int BankRowNumber, string TransactionId, DateOnly BookedDate, string Amount, string AccountDisplay, LongId ImportedBy);
public sealed record RefundSettlementProcedureDto(string BankDebitVerificationProcedureReference, string BeneficiaryVerificationProcedureReference);
public sealed record RefundSettlementReceiptPreviewDto(LongId ReceiptId, string VersionBefore, string VersionAfter, string PaidBefore, string PaidAfter,
    string Pending, string ReservedForRefund, string Available, string Received, string Allocated);
public sealed record RefundSettlementPreviewDto(string Binding, RefundSettlementViewDto View, RefundSettlementInputDto Input, RefundSettlementDebitDto Debit,
    RefundSettlementProcedureDto Policy, ImmutableArray<RefundSettlementReceiptPreviewDto> Receipts);
public sealed record RefundSettlementRecoveryDto(string Status, RefundSettlementResultDto? Result);

public static class RefundSettlementEndpoints {
    public static void MapRefundSettlementEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/billing/refund-payouts/{id}/settlement").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (string id, OrderReadDto request, HttpContext http, RefundPayoutService service, CancellationToken ct) => {
            if (request.Purpose != "refund-settlement") throw RefundSettlementPolicy.Invalid(); A.Private(http);
            return TypedResults.Ok(View(await service.ReadSettlementAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct)));
        }).WithName("ReadRefundSettlement");
        group.MapPost("/preview", async (string id, RefundSettlementInputDto request, HttpContext http, RefundPayoutService service, CancellationToken ct) => {
            A.Private(http); var value = await service.PreviewSettlementAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Domain(), ct);
            return TypedResults.Ok(Preview(value));
        }).WithName("PreviewRefundSettlement");
        group.MapPost("/verify", async (string id, RefundSettlementCommandDto request, HttpContext http, RefundPayoutService service,
            [FromHeader(Name = "Idempotency-Key")] string key, CancellationToken ct) => {
            if (request.Input is null) throw RefundSettlementPolicy.Invalid(); A.Private(http);
            return TypedResults.Ok(Result(await service.VerifySettlementAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Input.Domain(),
                request.Binding, request.Attested, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("VerifyRefundSettlement");
        group.MapPost("/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, RefundPayoutService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw RefundSettlementPolicy.Invalid(); A.Private(http);
            var row = await service.RecoverSettlementAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Key, ct);
            return TypedResults.Ok(new RefundSettlementRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverRefundSettlement");
        group.MapPost("/evidence/{reservationId}/original/query", async (string id, string reservationId, PaymentEvidenceReadDto request, HttpContext http, RefundPayoutEvidenceService service, CancellationToken ct) => {
            if (request.Purpose != "refund-settlement-evidence-view" || request.Reference is null || http.Request.Headers.ContainsKey("Range")) throw RefundSettlementPolicy.Invalid();
            var reference = new EvidenceRef(request.Reference.Id.Value, LongId.Parse(request.Reference.Version).Value, request.Reference.Sha256);
            var file = await service.ReadSettlementOriginalAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(reservationId).Value, reference, ct);
            A.Private(http); http.Response.Headers.XContentTypeOptions = "nosniff"; http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            http.Response.Headers["Referrer-Policy"] = "no-referrer"; http.Response.ContentLength = file.Length;
            return Results.Stream(file.Content, file.Mime, $"bukti-verifikasi-refund-{reference.Id}-{reference.Version}.{(file.Mime == "image/png" ? "png" : "jpg")}", enableRangeProcessing: false);
        }).Produces<byte[]>(200, "image/png", "image/jpeg").WithName("ReadRefundSettlementEvidenceOriginal");
    }
    internal static RefundSettlementResultDto Result(BsonDocument row) => new(new(row["settlementId"].AsInt64), new(row["payoutId"].AsInt64),
        new(row["financialApprovalId"].AsInt64), new(row["caseId"].AsInt64), new(row["returnId"].AsInt64), new(row["invoiceId"].AsInt64), A.MoneyText(row["amount"]),
        row["operation"].AsString, row["state"].AsString, new(row["recordedAt"].ToUniversalTime()),
        [.. row["receipts"].AsBsonArray.Select(value => new RefundSettlementReceiptDto(new(value["receiptId"].AsInt64), A.Number(value["version"].AsInt64),
            A.MoneyText(value["paid"]), A.MoneyText(value["pending"]), A.MoneyText(value["reservedForRefund"]), A.MoneyText(value["available"])))]);
    private static RefundSettlementViewDto View(BsonDocument row) => new(RefundPayoutEndpoints.Result(row["payout"].AsBsonDocument),
        new(row["caseId"].AsInt64), new(row["returnId"].AsInt64), new(row["invoiceId"].AsInt64), A.MoneyText(row["amount"]), A.Number(row["draftRevision"].AsInt64),
        new(row["draftId"].AsInt64), new(row["reservationId"].AsInt64), new(new(row["evidence"]["id"].AsInt64), A.Number(row["evidence"]["version"].AsInt64), row["evidence"]["sha256"].AsString),
        row["settlement"].IsBsonNull ? null : Result(row["settlement"].AsBsonDocument), row["candidateInstructions"].AsString,
        row["beneficiaryName"].AsString, row["beneficiaryBankCode"].AsString, row["beneficiaryAccountSuffix"].AsString,
        row["payingBankCode"].AsString, row["payingAccountSuffix"].AsString,
        DateOnly.ParseExact(row["transferDate"].AsString, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), row["transferReference"].AsString);
    private static RefundSettlementPreviewDto Preview(RefundSettlementPreview preview) {
        var row = preview.View; var input = row["input"]; var debit = row["debit"]; var policy = row["policy"];
        return new(preview.Binding, View(row), new(new(input["bankImportId"].AsInt64), input["bankRowNumber"].AsInt32,
            input["beneficiaryVerificationReference"].AsString, input["transferVerificationReference"].AsString),
            new(new(debit["bankImportId"].AsInt64), debit["bankRowNumber"].AsInt32, debit["transactionId"].AsString,
                DateOnly.ParseExact(debit["bookedDate"].AsString, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), A.MoneyText(debit["amount"]), debit["accountDisplay"].AsString, new(debit["importedBy"].AsInt64)),
            new(policy["bankDebitVerificationProcedureReference"].AsString, policy["beneficiaryVerificationProcedureReference"].AsString),
            [.. row["receipts"].AsBsonArray.Select(value => new RefundSettlementReceiptPreviewDto(new(value["receiptId"].AsInt64), A.Number(value["versionBefore"].AsInt64),
                A.Number(value["versionAfter"].AsInt64), A.MoneyText(value["paidBefore"]), A.MoneyText(value["paidAfter"]), A.MoneyText(value["pending"]),
                A.MoneyText(value["reservedForRefund"]), A.MoneyText(value["available"]), A.MoneyText(value["received"]), A.MoneyText(value["allocated"])))]);
    }
}
