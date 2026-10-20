using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Billing;

namespace UnitToko.Api.Billing;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvoiceReleaseAllocationDto([property: JsonRequired] LongId ReceiptId, [property: JsonRequired] LongId SkuId,
    [property: JsonRequired] string LineRevision, [property: JsonRequired] Money Tax, [property: JsonRequired] Money Charges);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvoiceReleaseMappingsDto([property: JsonRequired] string Receivable, [property: JsonRequired] string Revenue,
    [property: JsonRequired] string Tax, [property: JsonRequired] string Charges);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvoiceReleaseInputDto([property: JsonRequired] InvoiceReceiptDto[] Receipts, [property: JsonRequired] string ExpectedReleaseRevision,
    [property: JsonRequired] DateOnly PeriodFrom, [property: JsonRequired] DateOnly PeriodTo, [property: JsonRequired] DateTimeOffset ValidFrom,
    [property: JsonRequired] DateTimeOffset ValidUntil, [property: JsonRequired] string TemplateApprovalReference, [property: JsonRequired] string NumberingApprovalReference,
    [property: JsonRequired] string EligibilityReviewReference, [property: JsonRequired] InvoiceReleaseMappingsDto Mappings,
    [property: JsonRequired] InvoiceReleaseAllocationDto[] Allocations)
{
    internal InvoiceReleaseInput Domain() {
        if (Mappings is null || Allocations is null || Allocations.Length > InvoicePolicy.MaximumLines || Allocations.Any(value => value is null)) throw InvoiceReleaseRules.Invalid();
        return InvoiceReleaseRules.Copy(new(InvoiceReleaseEndpoints.Selected(Receipts), InvoiceReleaseEndpoints.Revision(ExpectedReleaseRevision), PeriodFrom, PeriodTo, ValidFrom, ValidUntil,
            TemplateApprovalReference, NumberingApprovalReference, EligibilityReviewReference, new(Mappings.Receivable, Mappings.Revenue, Mappings.Tax, Mappings.Charges),
            [.. Allocations.Select(value => new InvoiceReleaseAllocation(value.ReceiptId, value.SkuId, InvoiceReleaseEndpoints.Revision(value.LineRevision), value.Tax, value.Charges))]));
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvoiceReleaseQueryDto([property: JsonRequired] string Purpose, [property: JsonRequired] LongId? BeforeId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvoiceReleaseSelectionDto([property: JsonRequired] string Purpose, [property: JsonRequired] InvoiceReceiptDto[] Receipts);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvoiceReleasePreviewRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] InvoiceReleaseInputDto Input);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvoiceReleaseProposeDto([property: JsonRequired] InvoiceReleaseInputDto Input, [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvoiceReleaseDecisionPreviewDto([property: JsonRequired] string Purpose, [property: JsonRequired] string ExpectedRevision, [property: JsonRequired] string Decision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvoiceReleaseDecideDto([property: JsonRequired] string ExpectedRevision, [property: JsonRequired] string Decision,
    [property: JsonRequired] string Binding, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvoiceReleaseReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvoiceReleaseRecoveryRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Action, [property: JsonRequired] string Key);
public sealed record InvoiceReleasePreviewDto(string SelectionKey, string Binding, string Revision, InvoiceReleaseInputDto Input, InvoiceReleaseFacts Facts);
public sealed record InvoiceReleaseViewDto(InvoiceReleaseCommandResult Result, LongId ActorId, DateTimeOffset RecordedAt, string Reason,
    InvoiceReleaseInputDto Input, InvoiceReleaseFacts Facts, string RecordHash);
public sealed record InvoiceReleasePageDto(ImmutableArray<InvoiceReleaseViewDto> Items, LongId? NextId);
public sealed record InvoiceReleaseRecoveryDto(string Status, InvoiceReleaseCommandResult? Result);

public static class InvoiceReleaseEndpoints
{
    public static void MapInvoiceReleaseEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/billing/invoice-releases").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/candidates/query", async (InvoiceReleaseQueryDto request, HttpContext http, InvoiceReleaseService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(await service.CandidatesAsync(http.Features.Get<ResolvedActor>()!.Actor, request.BeforeId?.Value, ct));
        }).WithName("ListInvoiceReleaseCandidates");
        group.MapPost("/eligibility/query", async (InvoiceReleaseSelectionDto request, HttpContext http, InvoiceReleaseService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(await service.EligibilityAsync(http.Features.Get<ResolvedActor>()!.Actor, Selected(request.Receipts), ct));
        }).WithName("ReadInvoiceReleaseEligibility");
        group.MapPost("/preview/query", async (InvoiceReleasePreviewRequestDto request, HttpContext http, InvoiceReleaseService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Preview(await service.PreviewAsync(http.Features.Get<ResolvedActor>()!.Actor,
                request.Input?.Domain() ?? throw InvoiceReleaseRules.Invalid(), ct)));
        }).WithName("PreviewInvoiceRelease");
        group.MapPost("", async (InvoiceReleaseProposeDto request, HttpContext http, InvoiceReleaseService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            var input = request.Input?.Domain() ?? throw InvoiceReleaseRules.Invalid();
            if (AccessRequestMetadata.ExpectedVersion(http.Request) != input.ExpectedReleaseRevision) throw InvoiceReleaseRules.Invalid();
            return TypedResults.Ok(await service.ProposeAsync(http.Features.Get<ResolvedActor>()!.Actor, input, request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct));
        }).WithName("ProposeInvoiceRelease");
        group.MapPost("/proposals/{id}/decision-preview/query", async (string id, InvoiceReleaseDecisionPreviewDto request, HttpContext http, InvoiceReleaseService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(Preview(await service.PreviewDecisionAsync(http.Features.Get<ResolvedActor>()!.Actor,
                LongId.Parse(id).Value, Revision(request.ExpectedRevision), Decision(request.Decision), ct)));
        }).WithName("PreviewInvoiceReleaseDecision");
        group.MapPost("/proposals/{id}/decisions", async (string id, InvoiceReleaseDecideDto request, HttpContext http, InvoiceReleaseService service,
            [FromHeader(Name = "Idempotency-Key")] string key, [FromHeader(Name = "If-Match")] string expected, CancellationToken ct) => {
            var revision = Revision(request.ExpectedRevision); if (AccessRequestMetadata.ExpectedVersion(http.Request) != revision) throw InvoiceReleaseRules.Invalid();
            return TypedResults.Ok(await service.DecideAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, revision, Decision(request.Decision), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct));
        }).WithName("DecideInvoiceRelease");
        group.MapPost("/query", async (InvoiceReleaseQueryDto request, HttpContext http, InvoiceReleaseService service, CancellationToken ct) => {
            Purpose(request.Purpose); var page = await service.ListAsync(http.Features.Get<ResolvedActor>()!.Actor, request.BeforeId?.Value, ct);
            return TypedResults.Ok(new InvoiceReleasePageDto([.. page.Items.Select(View)], page.NextId));
        }).WithName("ListInvoiceReleases");
        group.MapPost("/records/{id}/query", async (string id, InvoiceReleaseReadDto request, HttpContext http, InvoiceReleaseService service, CancellationToken ct) => {
            Purpose(request.Purpose); return TypedResults.Ok(View(await service.ReadAsync(http.Features.Get<ResolvedActor>()!.Actor, LongId.Parse(id).Value, ct)));
        }).WithName("ReadInvoiceRelease");
        group.MapPost("/operations/query", async (InvoiceReleaseRecoveryRequestDto request, HttpContext http, InvoiceReleaseService service, CancellationToken ct) => {
            if (request.Purpose != "operation-recovery") throw InvoiceReleaseRules.Invalid();
            var result = await service.RecoverAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Action, request.Key, ct);
            return TypedResults.Ok(new InvoiceReleaseRecoveryDto(result is null ? "not_confirmed" : "committed", result));
        }).WithName("RecoverInvoiceRelease");
    }
    internal static long Revision(string text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0 &&
        value < long.MaxValue - 1 && value.ToString(CultureInfo.InvariantCulture) == text ? value : throw InvoiceReleaseRules.Invalid();
    internal static ImmutableArray<InvoiceReceiptSelection> Selected(InvoiceReceiptDto[] values) {
        if (values is null || values.Length > InvoicePolicy.MaximumReceipts || values.Any(value => value is null)) throw InvoiceReleaseRules.Invalid();
        return InvoicePolicy.Copy(values.Select(value => new InvoiceReceiptSelection(value.ShipmentId, value.ReceiptId)));
    }
    private static InvoiceReleaseDecision Decision(string value) => value switch { "approve" => InvoiceReleaseDecision.Approve,
        "reject" => InvoiceReleaseDecision.Reject, _ => throw InvoiceReleaseRules.Invalid() };
    private static void Purpose(string value) { if (value != "invoice-releases") throw InvoiceReleaseRules.Invalid(); }
    private static InvoiceReleaseInputDto Input(InvoiceReleaseInput input) => new([.. input.Receipts.Select(value => new InvoiceReceiptDto(value.ShipmentId, value.ReceiptId))],
        input.ExpectedReleaseRevision.ToString(CultureInfo.InvariantCulture), input.PeriodFrom, input.PeriodTo, input.ValidFrom, input.ValidUntil,
        input.TemplateApprovalReference, input.NumberingApprovalReference, input.EligibilityReviewReference,
        new(input.Mappings.Receivable, input.Mappings.Revenue, input.Mappings.Tax, input.Mappings.Charges),
        [.. input.Allocations.Select(value => new InvoiceReleaseAllocationDto(value.ReceiptId, value.SkuId, value.LineRevision.ToString(CultureInfo.InvariantCulture), value.Tax, value.Charges))]);
    private static InvoiceReleasePreviewDto Preview(InvoiceReleasePreview value) => new(value.SelectionKey, value.Binding, value.Revision, Input(value.Input), value.Facts);
    private static InvoiceReleaseViewDto View(InvoiceReleaseView value) => new(value.Result, value.ActorId, value.RecordedAt, value.Reason, Input(value.Input), value.Facts, value.RecordHash);
}
