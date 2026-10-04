using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Procurement;
using P = UnitToko.Api.Procurement.ProcurementQualityAcceptanceDtos;
using R = UnitToko.Api.Procurement.ProcurementRequestEndpoints;

namespace UnitToko.Api.Procurement;

public static class ProcurementQualityAcceptanceEndpoints
{
    public static void MapProcurementQualityAcceptanceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/procurement/quality-acceptances").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 415, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/targets/query", async (ProcurementQualityAcceptanceQueryDto request, HttpContext http,
            [FromServices] ProcurementQualityAcceptanceService service, CancellationToken ct) => {
            Purpose(request?.Purpose); var row = await service.TargetsAsync(Actor(http), ct);
            return TypedResults.Ok(new ProcurementTargetsDto([.. row["items"].AsBsonArray.Select(R.Target)]));
        }).WithName("ProcurementQualityAcceptanceTargets");
        group.MapPost("/receipts/query", async (ProcurementQualityAcceptanceReceiptsQueryDto request, HttpContext http,
            [FromServices] ProcurementQualityAcceptanceService service, CancellationToken ct) => {
            Purpose(request?.Purpose);
            return TypedResults.Ok(P.Receipts(await service.ReceiptsAsync(Actor(http), Location(request!.Location), request.BeforeReceiptId?.Value, ct)));
        }).WithName("ProcurementQualityAcceptanceReceipts");
        group.MapPost("/policies/query", async (ProcurementQualityAcceptancePoliciesQueryDto request, HttpContext http,
            [FromServices] ProcurementQualityAcceptanceService service, CancellationToken ct) => {
            Purpose(request?.Purpose);
            return TypedResults.Ok(P.Policies(await service.PoliciesAsync(Actor(http), request!.ReceiptId.Value, request.BeforePolicyId?.Value, ct)));
        }).WithName("ProcurementQualityAcceptancePolicies");
        group.MapPost("/preview/query", async (ProcurementQualityAcceptancePreviewInputDto request, HttpContext http,
            [FromServices] ProcurementQualityAcceptanceService service, CancellationToken ct) => {
            Purpose(request?.Purpose); if (request!.Input is null) throw ProcurementQualityAcceptanceRules.Invalid();
            return TypedResults.Ok(P.Preview(await service.PreviewAsync(Actor(http), request.Input.Domain(), ct)));
        }).WithName("PreviewProcurementQualityAcceptance");
        group.MapPost("/proposals", async (ProcurementQualityAcceptanceProposeDto request, HttpContext http,
            [FromServices] ProcurementQualityAcceptanceService service, [FromHeader(Name = "Idempotency-Key")] string key,
            CancellationToken ct) => {
            if (request?.Input is null) throw ProcurementQualityAcceptanceRules.Invalid();
            var input = request.Input.Domain();
            return TypedResults.Ok(P.Result(await service.ProposeAsync(Actor(http), input, request.Binding,
                new AuthorizationCommandMetadata(AccessRequestMetadata.IdempotencyKey(http.Request), input.Reason, http.TraceIdentifier), ct)));
        }).WithName("ProposeProcurementQualityAcceptance");
        group.MapPost("/proposals/{id}/query", async (string id, ProcurementQualityAcceptanceQueryDto request, HttpContext http,
            [FromServices] ProcurementQualityAcceptanceService service, CancellationToken ct) => {
            Purpose(request?.Purpose);
            var row = await service.ReadAsync(Actor(http), LongId.Parse(id).Value, ct)
                ?? throw new AuthorizationDeniedException("resource_unavailable");
            return TypedResults.Ok(P.Read(row));
        }).WithName("ProcurementQualityAcceptanceProposal");
        group.MapPost("/proposals/{id}/decision-preview/query", async (string id, ProcurementQualityAcceptanceDecisionPreviewInputDto request,
            HttpContext http, [FromServices] ProcurementQualityAcceptanceService service, CancellationToken ct) => {
            Purpose(request?.Purpose);
            if (AccessRequestMetadata.ExpectedVersion(http.Request) != 1) throw ProcurementQualityAcceptanceRules.Invalid();
            return TypedResults.Ok(P.DecisionPreview(await service.PreviewDecisionAsync(Actor(http), LongId.Parse(id).Value,
                Decision(request!.Decision), request.Observation?.Domain(), ct)));
        }).WithName("PreviewProcurementQualityAcceptanceDecision");
        group.MapPost("/proposals/{id}/decisions", async (string id, ProcurementQualityAcceptanceDecideDto request, HttpContext http,
            [FromServices] ProcurementQualityAcceptanceService service, [FromHeader(Name = "Idempotency-Key")] string key,
            CancellationToken ct) => {
            if (request is null) throw ProcurementQualityAcceptanceRules.Invalid();
            return TypedResults.Ok(P.Result(await service.DecideAsync(Actor(http), LongId.Parse(id).Value,
                AccessRequestMetadata.ExpectedVersion(http.Request), Decision(request.Decision), request.Observation?.Domain(), request.Binding,
                new AuthorizationCommandMetadata(AccessRequestMetadata.IdempotencyKey(http.Request), request.Reason, http.TraceIdentifier), ct)));
        }).WithName("DecideProcurementQualityAcceptance");
        group.MapPost("/history/query", async (ProcurementQualityAcceptanceHistoryQueryDto request, HttpContext http,
            [FromServices] ProcurementQualityAcceptanceService service, CancellationToken ct) => {
            Purpose(request?.Purpose);
            return TypedResults.Ok(P.History(await service.HistoryAsync(Actor(http), Location(request!.Location), request.BeforeProposalId?.Value, ct)));
        }).WithName("ProcurementQualityAcceptanceHistory");
        group.MapPost("/operations/query", async (ProcurementQualityAcceptanceRecoveryInputDto request, HttpContext http,
            [FromServices] ProcurementQualityAcceptanceService service, CancellationToken ct) => {
            Purpose(request?.Purpose, "operation-recovery");
            var row = await service.LookupAsync(Actor(http), request!.Operation, request.Key, ct);
            return TypedResults.Ok(new ProcurementQualityAcceptanceRecoveryDto(row["state"].AsString,
                row["result"].IsBsonNull ? null : P.Result(row["result"])));
        }).WithName("RecoverProcurementQualityAcceptance");
    }

    private static ActorContext Actor(HttpContext http) {
        UnitToko.Api.Billing.PaymentAllocationEndpoints.Private(http); return http.Features.Get<ResolvedActor>()!.Actor;
    }
    private static void Purpose(string? value, string expected = "procurement-quality-acceptance-review") {
        if (value != expected) throw ProcurementQualityAcceptanceRules.Invalid();
    }
    private static ProcurementLocation Location(ProcurementLocationDto value) =>
        value is null ? throw ProcurementQualityAcceptanceRules.Invalid() : value.Domain();
    private static ProcurementQualityAcceptanceDecision Decision(string value) => value switch {
        "approve" => ProcurementQualityAcceptanceDecision.Approve,
        "reject" => ProcurementQualityAcceptanceDecision.Reject,
        _ => throw ProcurementQualityAcceptanceRules.Invalid()
    };
}
