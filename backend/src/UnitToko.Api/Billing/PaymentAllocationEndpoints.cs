using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Billing;

namespace UnitToko.Api.Billing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentAllocationQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId[] InvoiceIds);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentAllocationLineDto([property: JsonRequired] LongId InvoiceId, [property: JsonRequired] string ExpectedVersion, [property: JsonRequired] Money Amount);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentAllocationInputDto([property: JsonRequired] string ExpectedReceiptVersion, [property: JsonRequired] PaymentAllocationLineDto[] Lines) {
    internal static long Version(string text) {
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 0 or >= long.MaxValue - 1 || text != value.ToString(CultureInfo.InvariantCulture)) throw PaymentAllocationPolicy.Invalid();
        return value;
    }
    internal PaymentAllocationInput Domain() {
        if (Lines is null || Lines.Any(line => line is null)) throw PaymentAllocationPolicy.Invalid();
        return PaymentAllocationPolicy.Copy(new(Version(ExpectedReceiptVersion), [.. Lines.Select(line => new PaymentAllocationLine(line.InvoiceId, Version(line.ExpectedVersion), line.Amount))]));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentAllocationCommandDto([property: JsonRequired] string Binding, [property: JsonRequired] PaymentAllocationInputDto Input, [property: JsonRequired] string Reason);
public sealed record PaymentAllocationInvoiceDto(LongId InvoiceId, LongId NumberEntity, LongId NumberChannel, int NumberYear, string Number,
    string Version, string Total, string Paid, string Credited, string Outstanding, string PaymentState, string DueDate, string Aging, int DaysOverdue);
public sealed record PaymentAllocationViewDto(LongId VerificationId, LongId DeclarationId, LongId ReceiptId, LongId BuyingOrganizationId, string BuyerName,
    string TransactionId, string BookedDate, string ReceiptVersion, string Received, string Allocated, string ReservedForRefund, string Available, ImmutableArray<PaymentAllocationInvoiceDto> Invoices);
public sealed record PaymentAllocationPreviewDto(string Binding, PaymentAllocationViewDto View, string ProcedureReference, string Amount, string Unapplied, ImmutableArray<PaymentAllocationInvoiceResultDto> Invoices);
public sealed record PaymentAllocationInvoiceResultDto(LongId InvoiceId, string Version, string Amount, string Remaining);
public sealed record PaymentAllocationResultDto(LongId AllocationId, LongId VerificationId, LongId ReceiptId, string ReceiptVersion, string Amount, string Unapplied,
    DateTimeOffset RecordedAt, string Operation, ImmutableArray<PaymentAllocationInvoiceResultDto> Invoices);
public sealed record PaymentAllocationRecoveryDto(string Status, PaymentAllocationResultDto? Result);

public static class PaymentAllocationEndpoints
{
    public static void MapPaymentAllocationEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/billing/payment-verifications/{id}/allocations").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/query", async (string id, PaymentAllocationQueryDto request, HttpContext http, PaymentAllocationService service, IClock clock, CancellationToken ct) => {
            if (request.Purpose != "payment-allocation" || request.InvoiceIds is null) throw PaymentAllocationPolicy.Invalid(); Private(http);
            return TypedResults.Ok(View(await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, [.. request.InvoiceIds.Select(value => value.Value)], ct), clock));
        }).WithName("ReadPaymentAllocationBalances");
        group.MapPost("/preview", async (string id, PaymentAllocationInputDto request, HttpContext http, PaymentAllocationService service, IClock clock, CancellationToken ct) => {
            Private(http); var preview = await service.PreviewAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Domain(), ct);
            return TypedResults.Ok(new PaymentAllocationPreviewDto(preview.Binding, View(preview.View, clock), preview.Policy["procedureReference"].AsString,
                preview.Result.Total.ToString(), preview.Result.RemainingReceipt.ToString(), [.. preview.Result.Invoices.Select(line => new PaymentAllocationInvoiceResultDto(line.InvoiceId, Number(line.PreviousVersion + 1), line.Amount.ToString(), line.Remaining.ToString()))]));
        }).WithName("PreviewPaymentAllocation");
        group.MapPost("", async (string id, PaymentAllocationCommandDto request, HttpContext http, PaymentAllocationService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Input is null) throw PaymentAllocationPolicy.Invalid(); var input = request.Input.Domain();
            if (AccessRequestMetadata.ExpectedVersion(http.Request) != input.ExpectedReceiptVersion) throw PaymentAllocationPolicy.Invalid(); Private(http);
            return TypedResults.Ok(Result(await service.AllocateAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Binding, input,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("AllocatePaymentReceipt");
        group.MapPost("/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, PaymentAllocationService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw PaymentAllocationPolicy.Invalid(); var verificationId = LongId.Parse(id).Value; Private(http);
            var row = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Key, ct);
            if (row is not null && row["verificationId"].AsInt64 != verificationId) throw PaymentAllocationPolicy.Invalid();
            return TypedResults.Ok(new PaymentAllocationRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverPaymentAllocation");
    }
    internal static string Number(long value) => OrderDraftInputDto.Number(value);
    internal static string MoneyText(BsonValue value) => new Money(value.AsDecimal).ToString();
    internal static PaymentAllocationViewDto View(PaymentAllocationView view, IClock clock) {
        var balance = view.ReceiptBalance; var receipt = view.Receipt; var source = view.Verification["source"];
        var available = new Money(receipt["amount"].AsDecimal - balance["allocated"].AsDecimal - balance["reservedForRefund"].AsDecimal);
        var day = DateOnly.FromDateTime(clock.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime);
        return new(new(view.Verification["_id"].AsInt64), new(view.Verification["declarationId"].AsInt64), new(receipt["_id"].AsInt64), new(source["party"]["buyingOrganizationId"].AsInt64), source["invoices"][0]["buyerName"].AsString,
            receipt["transactionId"].AsString, receipt["bookedDate"].AsString, Number(balance["version"].AsInt64), MoneyText(receipt["amount"]), MoneyText(balance["allocated"]), MoneyText(balance["reservedForRefund"]), available.ToString(),
            [.. view.Invoices.Select(value => {
                var invoice = value["source"]; var funds = value["balance"];
                var due = ReceivablesPolicy.Calculate(new(invoice["total"].AsDecimal), new(funds["paid"].AsDecimal), new(funds["credited"].AsDecimal), DateOnly.ParseExact(invoice["dueDate"].AsString, "yyyy-MM-dd", CultureInfo.InvariantCulture), day);
                return new PaymentAllocationInvoiceDto(new(invoice["invoiceId"].AsInt64), new(invoice["numberEntity"].AsInt64), new(invoice["numberChannel"].AsInt64), checked((int)invoice["numberYear"].AsInt64), Number(invoice["number"].AsInt64),
                    Number(funds["version"].AsInt64), due.Total.ToString(), due.AllocatedPayment.ToString(), due.ApprovedCredit.ToString(), due.Outstanding.ToString(), due.PaymentState, invoice["dueDate"].AsString, due.Aging, due.DaysOverdue);
            })]);
    }
    private static PaymentAllocationResultDto Result(BsonDocument row) => new(new(row["allocationId"].AsInt64), new(row["verificationId"].AsInt64), new(row["receiptId"].AsInt64), Number(row["receiptVersion"].AsInt64), MoneyText(row["amount"]), MoneyText(row["unapplied"]),
        new(row["recordedAt"].ToUniversalTime()), row["operation"].AsString, [.. row["invoices"].AsBsonArray.Select(line => new PaymentAllocationInvoiceResultDto(new(line["invoiceId"].AsInt64), Number(line["version"].AsInt64), MoneyText(line["amount"]), MoneyText(line["remaining"])))]);
    internal static void Private(HttpContext http) { http.Response.Headers.CacheControl = "private, no-store, max-age=0"; http.Response.Headers.Pragma = "no-cache"; }
}
