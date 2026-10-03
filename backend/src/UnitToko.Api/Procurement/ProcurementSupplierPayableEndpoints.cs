using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Procurement;
using P = UnitToko.Api.Procurement.ProcurementSupplierPayableDtos;
using R = UnitToko.Api.Procurement.ProcurementRequestEndpoints;

namespace UnitToko.Api.Procurement;

public static class ProcurementSupplierPayableEndpoints
{
    public static void MapProcurementSupplierPayableEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/procurement/supplier-payables").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 415, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (ProcurementSupplierPayableQueryDto request, HttpContext http,
            [FromServices] ProcurementSupplierPayableService service, CancellationToken ct) => {
            Purpose(request?.Purpose); var row = await service.TargetsAsync(Actor(http), ct);
            return TypedResults.Ok(new ProcurementTargetsDto([.. row["items"].AsBsonArray.Select(R.Target)]));
        }).WithName("ProcurementSupplierPayableTargets");
        group.MapPost("/policies/query", async (ProcurementSupplierPayablePoliciesQueryDto request, HttpContext http,
            [FromServices] ProcurementSupplierPayableService service, CancellationToken ct) => {
            Purpose(request?.Purpose); var row = await service.PoliciesAsync(Actor(http), Location(request!.Location), request.BeforePolicyId?.Value, ct);
            return TypedResults.Ok(P.Policies(row));
        }).WithName("ProcurementSupplierPayablePolicies");
        group.MapPost("/valuation-allocations/query", async (ProcurementSupplierPayableValuationsQueryDto request, HttpContext http,
            [FromServices] ProcurementSupplierPayableService service, CancellationToken ct) => {
            Purpose(request?.Purpose); var row = await service.ValuationsAsync(Actor(http), Location(request!.Location), request.BeforeValuationAllocationId?.Value, ct);
            return TypedResults.Ok(P.Valuations(row));
        }).WithName("ProcurementSupplierPayableValuations");
        group.MapPost("/payables/query", async (ProcurementSupplierPayableListQueryDto request, HttpContext http,
            [FromServices] ProcurementSupplierPayableService service, CancellationToken ct) => {
            Purpose(request?.Purpose); var row = await service.PayablesAsync(Actor(http), Location(request!.Location), request.BeforePayableId?.Value, ct);
            return TypedResults.Ok(P.Payables(row));
        }).WithName("ProcurementSupplierPayables");
        group.MapPost("/payables/{id}/query", async (string id, ProcurementSupplierPayableQueryDto request, HttpContext http,
            [FromServices] ProcurementSupplierPayableService service, CancellationToken ct) => {
            Purpose(request?.Purpose); return TypedResults.Ok(P.PayableChoice(await service.PayableAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("ProcurementSupplierPayableCurrent");
        group.MapPost("/verified-advances/query", async (ProcurementSupplierPayableAdvancesQueryDto request, HttpContext http,
            [FromServices] ProcurementSupplierPayableService service, CancellationToken ct) => {
            Purpose(request?.Purpose); var row = await service.AdvancesAsync(Actor(http), Location(request!.Location), request.PayableId.Value,
                request.BeforeSettlementId?.Value, ct);
            return TypedResults.Ok(P.Advances(row));
        }).WithName("ProcurementSupplierPayableVerifiedAdvances");
        group.MapPost("/verified-advances/{id}/query", async (string id, ProcurementSupplierPayableQueryDto request, HttpContext http,
            [FromServices] ProcurementSupplierPayableService service, CancellationToken ct) => {
            Purpose(request?.Purpose); return TypedResults.Ok(P.AdvanceChoice(await service.AdvanceAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("ProcurementSupplierPayableVerifiedAdvance");
        group.MapPost("/preview/query", async (ProcurementSupplierPayablePreviewInputDto request, HttpContext http,
            [FromServices] ProcurementSupplierPayableService service, CancellationToken ct) => {
            Purpose(request?.Purpose); if (request!.Input is null) throw ProcurementSupplierPayableRules.Invalid();
            return TypedResults.Ok(P.Preview(await service.PreviewAsync(Actor(http), request.Input.Domain(), ct)));
        }).WithName("PreviewProcurementSupplierPayable");
        group.MapPost("/proposals", async (ProcurementSupplierPayableProposeDto request, HttpContext http,
            [FromServices] ProcurementSupplierPayableService service, [FromHeader(Name = "Idempotency-Key")] string key,
            CancellationToken ct) => {
            if (request?.Input is null) throw ProcurementSupplierPayableRules.Invalid();
            var input = request.Input.Domain();
            var row = await service.ProposeAsync(Actor(http), input, request.Binding,
                new AuthorizationCommandMetadata(AccessRequestMetadata.IdempotencyKey(http.Request), input.Reason, http.TraceIdentifier), ct);
            return TypedResults.Ok(P.Result(row));
        }).WithName("ProposeProcurementSupplierPayable");
        group.MapPost("/proposals/{id}/query", async (string id, ProcurementSupplierPayableQueryDto request, HttpContext http,
            [FromServices] ProcurementSupplierPayableService service, CancellationToken ct) => {
            Purpose(request?.Purpose); return TypedResults.Ok(P.Read(await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("ProcurementSupplierPayableProposal");
        group.MapPost("/proposals/{id}/decision-preview/query", async (string id, ProcurementSupplierPayableDecisionPreviewInputDto request,
            HttpContext http, [FromServices] ProcurementSupplierPayableService service, CancellationToken ct) => {
            Purpose(request?.Purpose); if (AccessRequestMetadata.ExpectedVersion(http.Request) != 1) throw ProcurementSupplierPayableRules.Invalid();
            return TypedResults.Ok(P.DecisionPreview(await service.PreviewDecisionAsync(Actor(http), LongId.Parse(id).Value,
                Decision(request!.Decision), ct)));
        }).WithName("PreviewProcurementSupplierPayableDecision");
        group.MapPost("/proposals/{id}/decisions", async (string id, ProcurementSupplierPayableDecideDto request, HttpContext http,
            [FromServices] ProcurementSupplierPayableService service, [FromHeader(Name = "Idempotency-Key")] string key,
            CancellationToken ct) => {
            if (request is null) throw ProcurementSupplierPayableRules.Invalid();
            return TypedResults.Ok(P.Result(await service.DecideAsync(Actor(http), LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), Decision(request.Decision), request.Binding,
                new AuthorizationCommandMetadata(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("DecideProcurementSupplierPayable");
        group.MapPost("/history/query", async (ProcurementSupplierPayableHistoryQueryDto request, HttpContext http,
            [FromServices] ProcurementSupplierPayableService service, CancellationToken ct) => {
            Purpose(request?.Purpose); return TypedResults.Ok(P.History(await service.HistoryAsync(Actor(http), Location(request!.Location),
                request.BeforeProposalId?.Value, ct)));
        }).WithName("ProcurementSupplierPayableHistory");
        group.MapPost("/operations/query", async (ProcurementSupplierPayableRecoveryInputDto request, HttpContext http,
            [FromServices] ProcurementSupplierPayableService service, CancellationToken ct) => {
            Purpose(request?.Purpose, "operation-recovery"); return TypedResults.Ok(P.Recovery(await service.LookupAsync(Actor(http),
                request!.Operation, request.Key, ct)));
        }).WithName("RecoverProcurementSupplierPayable");
    }
    private static ActorContext Actor(HttpContext http) { UnitToko.Api.Billing.PaymentAllocationEndpoints.Private(http); return http.Features.Get<ResolvedActor>()!.Actor; }
    private static ProcurementLocation Location(ProcurementLocationDto? value) => value?.Domain() ?? throw ProcurementSupplierPayableRules.Invalid();
    private static void Purpose(string? value, string expected = "supplier-payable-review")
    { if (value != expected) throw ProcurementSupplierPayableRules.Invalid(); }
    private static ProcurementSupplierPayableDecision Decision(string? value) => value switch {
        "approve" => ProcurementSupplierPayableDecision.Approve, "reject" => ProcurementSupplierPayableDecision.Reject,
        _ => throw ProcurementSupplierPayableRules.Invalid() };
}
