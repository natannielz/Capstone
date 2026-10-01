using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;

namespace UnitToko.Api.Billing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnFinancialApprovalQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Action, LongId? BeforeAllocationId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnFinancialReleaseInputDto([property: JsonRequired] LongId OriginalAllocationId, [property: JsonRequired] string ExpectedReceiptVersion, [property: JsonRequired] Money Amount);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnFinancialApprovalInputDto([property: JsonRequired] string ExpectedInvoiceVersion, [property: JsonRequired] ReturnFinancialReleaseInputDto[] Releases) {
    internal ReturnFinancialApprovalInput Domain() {
        if (Releases is null || Releases.Any(line => line is null)) throw ReturnFinancialApprovalPolicy.Invalid();
        return ReturnFinancialApprovalPolicy.Copy(new(PaymentAllocationInputDto.Version(ExpectedInvoiceVersion),
            [.. Releases.Select(line => new ReturnFinancialReleaseInput(line.OriginalAllocationId, PaymentAllocationInputDto.Version(line.ExpectedReceiptVersion), line.Amount))]));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnFinancialApprovalPreviewInputDto([property: JsonRequired] string Action, [property: JsonRequired] ReturnFinancialApprovalInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnFinancialApprovalCommandDto([property: JsonRequired] string Action, [property: JsonRequired] string Binding,
    [property: JsonRequired] ReturnFinancialApprovalInputDto Input, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReturnFinancialApprovalRecoveryInputDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Action, [property: JsonRequired] string RequestKey);
public sealed record ReturnFinancialInvoiceDto(LongId InvoiceId, string Version, string Issued, string Paid, string Credited, string Outstanding, string PaymentState, string BuyerName);
public sealed record ReturnFinancialReceiptBalanceDto(LongId ReceiptId, string Version, string Received, string Allocated, string ReservedForRefund, string Available);
public sealed record ReturnFinancialAllocationDto(LongId OriginalAllocationId, LongId ReceiptId, string ReceiptVersion, string OriginallyAllocated, string PreviouslyReleased, string Available);
public sealed record ReturnFinancialReceiptResultDto(LongId ReceiptId, string Version, string Released, string ReservedForRefund, string Available);
public sealed record ReturnFinancialReleaseResultDto(LongId OriginalAllocationId, LongId InvoiceId, string Amount);
public sealed record ReturnFinancialApprovalResultDto(LongId FinancialApprovalId, LongId CaseId, LongId ReturnId, LongId InvoiceId, string Action, string InvoiceVersion,
    string Credit, string ReservedRefund, string RemainingInvoice, DateTimeOffset RecordedAt, string Operation, string State,
    ImmutableArray<ReturnFinancialReceiptResultDto> Receipts, ImmutableArray<ReturnFinancialReleaseResultDto> Releases);
public sealed record ReturnFinancialApprovalViewDto(ReturnEntitlementResultDto Request, ReturnFinancialInvoiceDto Invoice,
    ImmutableArray<ReturnFinancialReceiptBalanceDto> Receipts, ImmutableArray<ReturnFinancialAllocationDto> Allocations, bool HasMore, LongId? BeforeAllocationId, ReturnFinancialApprovalResultDto? Approval);
public sealed record ReturnFinancialApprovalPreviewDto(ReturnFinancialApprovalViewDto View, string Binding, string CreditNoteProcedureReference, string TaxTreatmentReference,
    string ChargeTreatmentReference, string RefundReservationProcedureReference, string InvoiceVersion, string Credit, string ReservedRefund, string RemainingInvoice,
    ImmutableArray<ReturnFinancialReceiptResultDto> Receipts, ImmutableArray<ReturnFinancialReleaseResultDto> Releases);
public sealed record ReturnFinancialApprovalRecoveryDto(string Status, ReturnFinancialApprovalResultDto? Result);

public static class ReturnFinancialApprovalEndpoints
{
    public static void MapReturnFinancialApprovalEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/billing/cases/{id}/returns/{returnId}/financial-approval").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (string id, string returnId, ReturnFinancialApprovalQueryDto request, HttpContext http, ReturnFinancialApprovalService service, CancellationToken ct) => {
            if (request.Purpose != "return-financial-approval") throw ReturnFinancialApprovalPolicy.Invalid(); A.Private(http);
            return TypedResults.Ok(View(await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, request.Action, request.BeforeAllocationId?.Value, ct)));
        }).WithName("ReadReturnFinancialApproval");
        group.MapPost("/preview", async (string id, string returnId, ReturnFinancialApprovalPreviewInputDto request, HttpContext http, ReturnFinancialApprovalService service, CancellationToken ct) => {
            if (request.Input is null) throw ReturnFinancialApprovalPolicy.Invalid(); A.Private(http);
            var preview = await service.PreviewAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, request.Action, request.Input.Domain(), ct);
            var result = preview.Result;
            return TypedResults.Ok(new ReturnFinancialApprovalPreviewDto(View(preview.View), preview.Binding, preview.Policy["creditNoteProcedureReference"].AsString,
                preview.Policy["taxTreatmentReference"].AsString, preview.Policy["chargeTreatmentReference"].AsString, preview.Policy["refundReservationProcedureReference"].AsString,
                A.Number(result.InvoiceAfter.Version), result.Credit.ToString(), result.ReservedRefund.ToString(), result.RemainingInvoice.ToString(),
                [.. result.Receipts.Select(receipt => new ReturnFinancialReceiptResultDto(receipt.After.ReceiptId, A.Number(receipt.After.Version), receipt.Released.ToString(), receipt.After.ReservedForRefund.ToString(), receipt.Available.ToString()))],
                [.. result.Releases.Select(line => new ReturnFinancialReleaseResultDto(line.OriginalAllocationId, result.InvoiceAfter.InvoiceId, line.Amount.ToString()))]));
        }).WithName("PreviewReturnFinancialApproval");
        group.MapPost("", async (string id, string returnId, ReturnFinancialApprovalCommandDto request, HttpContext http, ReturnFinancialApprovalService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Input is null) throw ReturnFinancialApprovalPolicy.Invalid(); var input = request.Input.Domain();
            if (AccessRequestMetadata.ExpectedVersion(http.Request) != input.ExpectedInvoiceVersion) throw ReturnFinancialApprovalPolicy.Invalid(); A.Private(http);
            return TypedResults.Ok(Result(await service.ApproveAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value,
                request.Action, request.Binding, input, new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("ApproveReturnFinancialResolution");
        group.MapPost("/operations/query", async (string id, string returnId, ReturnFinancialApprovalRecoveryInputDto request, HttpContext http, ReturnFinancialApprovalService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw ReturnFinancialApprovalPolicy.Invalid(); A.Private(http);
            var row = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, LongId.Parse(returnId).Value, request.Action, request.RequestKey, ct);
            return TypedResults.Ok(new ReturnFinancialApprovalRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverReturnFinancialApproval");
    }
    internal static ReturnFinancialApprovalResultDto Result(BsonDocument row) => new(new(row["financialApprovalId"].AsInt64), new(row["caseId"].AsInt64), new(row["returnId"].AsInt64), new(row["invoiceId"].AsInt64),
        row["action"].AsString, A.Number(row["invoiceVersion"].AsInt64), A.MoneyText(row["credit"]), A.MoneyText(row["reservedRefund"]), A.MoneyText(row["remainingInvoice"]), new(row["recordedAt"].ToUniversalTime()),
        row["operation"].AsString, row["state"].AsString,
        [.. row["receipts"].AsBsonArray.Select(receipt => new ReturnFinancialReceiptResultDto(new(receipt["receiptId"].AsInt64), A.Number(receipt["version"].AsInt64), A.MoneyText(receipt["released"]), A.MoneyText(receipt["reservedForRefund"]), A.MoneyText(receipt["available"])))],
        [.. row["releases"].AsBsonArray.Select(line => new ReturnFinancialReleaseResultDto(new(line["originalAllocationId"].AsInt64), new(line["invoiceId"].AsInt64), A.MoneyText(line["amount"]))) ]);
    private static ReturnFinancialApprovalViewDto View(ReturnFinancialApprovalView view) {
        var request = view.Request;
        var publicRequest = new BsonDocument { { "returnId", request["_id"] }, { "caseId", request["caseId"] }, { "invoiceId", request["invoiceId"] }, { "action", request["action"] },
            { "version", request["version"] }, { "requestedAt", request["requestedAt"] }, { "net", request["net"] }, { "tax", request["tax"] }, { "charges", request["charges"] }, { "credit", request["credit"] },
            { "lines", request["lines"].DeepClone() }, { "state", "financial_review_requested" }, { "operation", "billing.return.entitlement.request" } };
        var invoice = request["source"]["invoice"]; var balance = view.InvoiceBalance;
        var issued = new Money(invoice["total"].AsDecimal); var paid = new Money(balance["paid"].AsDecimal); var credited = new Money(balance["credited"].AsDecimal);
        var outstanding = new Money(issued.Value - paid.Value - credited.Value);
        var paymentState = outstanding.Value == 0 ? credited == issued ? "credited" : "paid" : paid.Value != 0 || credited.Value != 0 ? "partial" : "unpaid";
        var receipts = view.Receipts.Select(value => {
            var state = value["before"]; var total = new Money(value["receiptTotal"].AsDecimal);
            return new ReturnFinancialReceiptBalanceDto(new(value["receiptId"].AsInt64), A.Number(state["version"].AsInt64), total.ToString(), A.MoneyText(state["allocated"]), A.MoneyText(state["reservedForRefund"]),
                new Money(total.Value - state["allocated"].AsDecimal - state["reservedForRefund"].AsDecimal).ToString());
        }).ToImmutableArray();
        return new(ReturnEntitlementEndpoints.Result(publicRequest), new(new(request["invoiceId"].AsInt64), A.Number(balance["version"].AsInt64), issued.ToString(), paid.ToString(), credited.ToString(), outstanding.ToString(), paymentState, invoice["buyerName"].AsString),
            receipts, [.. view.Links.OrderByDescending(line => line["originalAllocationId"].AsInt64).Select(line => new ReturnFinancialAllocationDto(new(line["originalAllocationId"].AsInt64), new(line["receiptId"].AsInt64),
                receipts.Single(receipt => receipt.ReceiptId.Value == line["receiptId"].AsInt64).Version, A.MoneyText(line["originallyAllocated"]), A.MoneyText(line["previouslyReleased"]),
                new Money(line["originallyAllocated"].AsDecimal - line["previouslyReleased"].AsDecimal).ToString()))],
            view.HasMore, view.Links.Count == 0 ? null : new LongId(view.Links.Min(line => line["originalAllocationId"].AsInt64)), view.Approval is null ? null : Result(view.Approval));
    }
}
