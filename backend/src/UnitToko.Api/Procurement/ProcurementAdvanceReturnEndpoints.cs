using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Procurement;
using P = UnitToko.Api.Procurement.ProcurementAdvanceReturnDtos;
using R = UnitToko.Api.Procurement.ProcurementRequestEndpoints;

namespace UnitToko.Api.Procurement;

public static class ProcurementAdvanceReturnEndpoints
{
    public static void MapProcurementAdvanceReturnEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/procurement/advance-returns").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 415, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (ProcurementAdvanceReturnQueryDto request, HttpContext http,
            [FromServices] ProcurementAdvanceReturnService service, CancellationToken ct) => {
            Purpose(request?.Purpose); var row = await service.TargetsAsync(Actor(http), ct);
            return TypedResults.Ok(new ProcurementTargetsDto([.. row["items"].AsBsonArray.Select(R.Target)]));
        }).WithName("ProcurementAdvanceReturnTargets");
        group.MapPost("/policies/query", async (ProcurementAdvanceReturnPoliciesQueryDto request, HttpContext http,
            [FromServices] ProcurementAdvanceReturnService service, CancellationToken ct) => {
            Purpose(request?.Purpose);
            return TypedResults.Ok(P.Policies(await service.PoliciesAsync(Actor(http), Location(request!.Location), request.BeforePolicyId?.Value, ct)));
        }).WithName("ProcurementAdvanceReturnPolicies");
        group.MapPost("/receipts/query", async (ProcurementAdvanceReturnReceiptsQueryDto request, HttpContext http,
            [FromServices] ProcurementAdvanceReturnService service, CancellationToken ct) => {
            Purpose(request?.Purpose);
            return TypedResults.Ok(P.Receipts(await service.ReceiptsAsync(Actor(http), Location(request!.Location), request.BeforeReceiptId?.Value, ct)));
        }).WithName("ProcurementAdvanceReturnReceipts");
        group.MapPost("/verified-advances/query", async (ProcurementAdvanceReturnAdvancesQueryDto request, HttpContext http,
            [FromServices] ProcurementAdvanceReturnService service, CancellationToken ct) => {
            Purpose(request?.Purpose);
            return TypedResults.Ok(P.Advances(await service.AdvancesAsync(Actor(http), Location(request!.Location), request.ReceiptId.Value, request.BeforeSettlementId?.Value, ct)));
        }).WithName("ProcurementAdvanceReturnAdvances");
        group.MapPost("/preview/query", async (ProcurementAdvanceReturnPreviewInputDto request, HttpContext http,
            [FromServices] ProcurementAdvanceReturnService service, CancellationToken ct) => {
            Purpose(request?.Purpose); if (request!.Input is null) throw ProcurementAdvanceReturnRules.Invalid();
            return TypedResults.Ok(P.Preview(await service.PreviewAsync(Actor(http), request.Input.Domain(), ct)));
        }).WithName("PreviewProcurementAdvanceReturn");
        group.MapPost("/proposals", async (ProcurementAdvanceReturnProposeDto request, HttpContext http,
            [FromServices] ProcurementAdvanceReturnService service, [FromHeader(Name = "Idempotency-Key")] string key,
            CancellationToken ct) => {
            if (request?.Input is null) throw ProcurementAdvanceReturnRules.Invalid();
            var input = request.Input.Domain();
            return TypedResults.Ok(P.Result(await service.ProposeAsync(Actor(http), input, request.Binding,
                new AuthorizationCommandMetadata(AccessRequestMetadata.IdempotencyKey(http.Request), input.Reason, http.TraceIdentifier), ct)));
        }).WithName("ProposeProcurementAdvanceReturn");
        group.MapPost("/proposals/{id}/query", async (string id, ProcurementAdvanceReturnQueryDto request, HttpContext http,
            [FromServices] ProcurementAdvanceReturnService service, CancellationToken ct) => {
            Purpose(request?.Purpose);
            return TypedResults.Ok(P.Read(await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct)));
        }).WithName("ProcurementAdvanceReturnProposal");
        group.MapPost("/proposals/{id}/decision-preview/query", async (string id, ProcurementAdvanceReturnDecisionPreviewInputDto request,
            HttpContext http, [FromServices] ProcurementAdvanceReturnService service, CancellationToken ct) => {
            Purpose(request?.Purpose);
            if (AccessRequestMetadata.ExpectedVersion(http.Request) != 1) throw ProcurementAdvanceReturnRules.Invalid();
            return TypedResults.Ok(P.DecisionPreview(await service.PreviewDecisionAsync(Actor(http), LongId.Parse(id).Value,
                Decision(request!.Decision), ct)));
        }).WithName("PreviewProcurementAdvanceReturnDecision");
        group.MapPost("/proposals/{id}/decisions", async (string id, ProcurementAdvanceReturnDecideDto request, HttpContext http,
            [FromServices] ProcurementAdvanceReturnService service, [FromHeader(Name = "Idempotency-Key")] string key,
            CancellationToken ct) => {
            if (request is null) throw ProcurementAdvanceReturnRules.Invalid();
            return TypedResults.Ok(P.Result(await service.DecideAsync(Actor(http), LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), Decision(request.Decision), request.Binding,
                new AuthorizationCommandMetadata(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("DecideProcurementAdvanceReturn");
        group.MapPost("/history/query", async (ProcurementAdvanceReturnHistoryQueryDto request, HttpContext http,
            [FromServices] ProcurementAdvanceReturnService service, CancellationToken ct) => {
            Purpose(request?.Purpose);
            return TypedResults.Ok(P.History(await service.HistoryAsync(Actor(http), Location(request!.Location), request.BeforeProposalId?.Value, ct)));
        }).WithName("ProcurementAdvanceReturnHistory");
        group.MapPost("/operations/query", async (ProcurementAdvanceReturnRecoveryInputDto request, HttpContext http,
            [FromServices] ProcurementAdvanceReturnService service, CancellationToken ct) => {
            Purpose(request?.Purpose, "operation-recovery");
            return TypedResults.Ok(P.Recovery(await service.LookupAsync(Actor(http), request!.Operation, request.Key, ct)));
        }).WithName("RecoverProcurementAdvanceReturn");
    }
    private static ActorContext Actor(HttpContext http)
    {
        UnitToko.Api.Billing.PaymentAllocationEndpoints.Private(http);
        return http.Features.Get<ResolvedActor>()!.Actor;
    }
    private static ProcurementLocation Location(ProcurementLocationDto? value) => value?.Domain() ?? throw ProcurementAdvanceReturnRules.Invalid();
    private static void Purpose(string? value, string expected = "procurement-advance-return-review")
    { if (value != expected) throw ProcurementAdvanceReturnRules.Invalid(); }
    private static ProcurementAdvanceReturnDecision Decision(string? value) => value switch {
        "approve" => ProcurementAdvanceReturnDecision.Approve,
        "reject" => ProcurementAdvanceReturnDecision.Reject,
        _ => throw ProcurementAdvanceReturnRules.Invalid()
    };
}
