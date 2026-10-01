using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using UnitToko.Api.Authorization;
using UnitToko.Api.Ordering;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Billing;
using A = UnitToko.Api.Billing.PaymentAllocationEndpoints;

namespace UnitToko.Api.Billing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentAllocationCorrectionQueryDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentAllocationCorrectionCandidatesQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId? Before);
public sealed record PaymentAllocationCorrectionCapacityDto(LongId InvoiceId, string OriginallyAllocated, string PreviouslyReversed);
public sealed record PaymentAllocationCorrectionViewDto(LongId OriginalAllocationId, DateTimeOffset OriginalRecordedAt,
    PaymentAllocationViewDto View, ImmutableArray<PaymentAllocationCorrectionCapacityDto> Capacities);
public sealed record PaymentAllocationCorrectionCandidatesDto(ImmutableArray<PaymentAllocationCorrectionViewDto> Items, bool HasMore);
public sealed record PaymentAllocationCorrectionPreviewDto(LongId OriginalAllocationId, string Binding, PaymentAllocationViewDto View,
    string ProcedureReference, string Amount, string Unapplied, ImmutableArray<PaymentAllocationInvoiceResultDto> Invoices,
    ImmutableArray<PaymentAllocationCorrectionCapacityDto> Capacities);
public sealed record PaymentAllocationCorrectionResultDto(LongId CorrectionId, LongId OriginalAllocationId, LongId VerificationId,
    LongId ReceiptId, string ReceiptVersion, string Amount, string Unapplied, DateTimeOffset RecordedAt, string Operation,
    ImmutableArray<PaymentAllocationInvoiceResultDto> Invoices);
public sealed record PaymentAllocationCorrectionRecoveryDto(string Status, PaymentAllocationCorrectionResultDto? Result);

public static class PaymentAllocationCorrectionEndpoints
{
    public static void MapPaymentAllocationCorrectionEndpoints(this WebApplication app) {
        var candidates = app.MapGroup("/api/v1/billing/payment-verifications/{id}/allocations/corrections").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        var group = app.MapGroup("/api/v1/billing/payment-allocations/{id}/corrections").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var route in new[] { candidates, group }) foreach (var status in new[] { 400, 401, 403, 409, 503 })
            route.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        candidates.MapPost("/query", async (string id, PaymentAllocationCorrectionCandidatesQueryDto request, HttpContext http, PaymentAllocationService service, IClock clock, CancellationToken ct) => {
            if (request.Purpose != "payment-allocation-correction") throw PaymentAllocationPolicy.Invalid(); A.Private(http);
            var result = await service.CorrectionCandidatesAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Before?.Value, ct);
            return TypedResults.Ok(new PaymentAllocationCorrectionCandidatesDto([.. result.Items.Select(item => View(item, clock))], result.HasMore));
        }).WithName("ReadPaymentAllocationCorrectionCandidates");
        group.MapPost("/query", async (string id, PaymentAllocationCorrectionQueryDto request, HttpContext http, PaymentAllocationService service, IClock clock, CancellationToken ct) => {
            Purpose(request); A.Private(http);
            return TypedResults.Ok(View(await service.ReadCorrectionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct), clock));
        }).WithName("ReadPaymentAllocationCorrectionBalances");
        group.MapPost("/preview", async (string id, PaymentAllocationInputDto request, HttpContext http, PaymentAllocationService service, IClock clock, CancellationToken ct) => {
            A.Private(http); var preview = await service.PreviewCorrectionAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Domain(), ct);
            return TypedResults.Ok(new PaymentAllocationCorrectionPreviewDto(preview.OriginalAllocationId, preview.Binding, A.View(preview.View, clock), preview.Policy["correctionProcedureReference"].AsString,
                preview.Result.Total.ToString(), preview.Result.RemainingReceipt.ToString(), [.. preview.Result.Invoices.Select(line => new PaymentAllocationInvoiceResultDto(line.InvoiceId, A.Number(line.PreviousVersion + 1), line.Amount.ToString(), line.Remaining.ToString()))], Capacities(preview.Capacities)));
        }).WithName("PreviewPaymentAllocationCorrection");
        group.MapPost("", async (string id, PaymentAllocationCommandDto request, HttpContext http, PaymentAllocationService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            if (request.Input is null) throw PaymentAllocationPolicy.Invalid(); var input = request.Input.Domain();
            if (AccessRequestMetadata.ExpectedVersion(http.Request) != input.ExpectedReceiptVersion) throw PaymentAllocationPolicy.Invalid(); A.Private(http);
            return TypedResults.Ok(Result(await service.CorrectAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, request.Binding, input,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("CorrectPaymentAllocation");
        group.MapPost("/operations/query", async (string id, OrderDraftRecoveryDto request, HttpContext http, PaymentAllocationService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw PaymentAllocationPolicy.Invalid(); var allocationId = LongId.Parse(id).Value; A.Private(http);
            var row = await service.RecoverCorrectionAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Key, ct);
            if (row is not null && row["originalAllocationId"].AsInt64 != allocationId) throw PaymentAllocationPolicy.Invalid();
            return TypedResults.Ok(new PaymentAllocationCorrectionRecoveryDto(row is null ? "not_confirmed" : "committed", row is null ? null : Result(row)));
        }).WithName("RecoverPaymentAllocationCorrection");
    }
    private static void Purpose(PaymentAllocationCorrectionQueryDto request) { if (request.Purpose != "payment-allocation-correction") throw PaymentAllocationPolicy.Invalid(); }
    private static ImmutableArray<PaymentAllocationCorrectionCapacityDto> Capacities(BsonArray rows) => [.. rows.Select(line => new PaymentAllocationCorrectionCapacityDto(new(line["invoiceId"].AsInt64), A.MoneyText(line["originallyAllocated"]), A.MoneyText(line["previouslyReversed"])))];
    private static PaymentAllocationCorrectionViewDto View(PaymentAllocationCorrectionView value, IClock clock) => new(value.OriginalAllocationId, value.OriginalRecordedAt, A.View(value.View, clock), Capacities(value.Capacities));
    private static PaymentAllocationCorrectionResultDto Result(BsonDocument row) => new(new(row["correctionId"].AsInt64), new(row["originalAllocationId"].AsInt64), new(row["verificationId"].AsInt64), new(row["receiptId"].AsInt64), A.Number(row["receiptVersion"].AsInt64), A.MoneyText(row["amount"]), A.MoneyText(row["unapplied"]),
        new(row["recordedAt"].ToUniversalTime()), row["operation"].AsString, [.. row["invoices"].AsBsonArray.Select(line => new PaymentAllocationInvoiceResultDto(new(line["invoiceId"].AsInt64), A.Number(line["version"].AsInt64), A.MoneyText(line["amount"]), A.MoneyText(line["remaining"])))]);
}
