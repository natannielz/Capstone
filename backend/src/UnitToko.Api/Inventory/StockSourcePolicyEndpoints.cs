using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
namespace UnitToko.Api.Inventory;

public static class StockSourcePolicyEndpoints
{
    public static void MapStockSourcePolicyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/inventory/sources").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/pools/{poolId}/policy-preview/query", async (string poolId, StockSourcePolicyPreviewRequestDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-policy-preview");
            var row = await service.PreviewOperatingPolicyAsync(Actor(http), LongId.Parse(poolId).Value, ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row["poolRevision"].AsInt64);
            return TypedResults.Ok(StockSourcePolicyResponses.Preview(row));
        }).WithName("StockSourcePolicyPreview");
        group.MapPost("/pools/{poolId}/policy-proposals", async (string poolId, StockSourcePolicyProposeDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) =>
        {
            var expected = new StockSourcePolicyExpectation(AccessRequestMetadata.ExpectedVersion(http.Request),
                StockSourceSetupInputs.Number(request.ExpectedSuspensionVersion, true), StockSourceSetupInputs.Number(request.ExpectedPolicyVersion, true),
                request.ConfigurationHash);
            expected.Validate();
            var input = request.Input?.ToDomain() ?? throw StockSourceSetupInputs.Invalid();
            var row = await service.ProposeOperatingPolicyAsync(Actor(http), LongId.Parse(poolId).Value, expected, input,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(StockSourcePolicyResponses.Command(row));
        }).WithName("ProposeStockSourcePolicy");
        group.MapPost("/pools/{poolId}/policy-proposals/{id}/review/query", async (string poolId, string id,
            StockSourcePolicyReviewRequestDto request, HttpContext http, [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-policy-review");
            var row = await service.PreviewOperatingPolicyDecisionAsync(Actor(http), LongId.Parse(poolId).Value, LongId.Parse(id).Value, request.Decision(), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row["proposal"]["revision"].AsInt64);
            return TypedResults.Ok(StockSourcePolicyResponses.Review(row));
        }).WithName("StockSourcePolicyReview");
        group.MapPost("/pools/{poolId}/policy/query", async (string poolId, StockSourcePolicyReadDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-policy-history");
            return TypedResults.Ok(StockSourcePolicyResponses.Latest(await service.ReadOperatingPolicyAsync(Actor(http), LongId.Parse(poolId).Value, ct)));
        }).WithName("StockSourcePolicyLatest");
        group.MapPost("/pools/{poolId}/policy-proposals/query", async (string poolId, StockSourcePolicyHistoryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-policy-history");
            var page = await service.ListOperatingPolicyProposalsAsync(Actor(http), LongId.Parse(poolId).Value, request.Offset, request.Limit, ct);
            return TypedResults.Ok(new StockSourcePolicyPageDto(page.Total.ToString(CultureInfo.InvariantCulture), [.. page.Items.Select(StockSourcePolicyResponses.Detail)]));
        }).WithName("StockSourcePolicyHistory");
        group.MapPost("/pools/{poolId}/policy-proposals/{id}/query", async Task<StockSourcePolicyDetailDto?> (string poolId, string id,
            StockSourcePolicyReadDto request, HttpContext http, [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "inventory-source-policy-history");
            var row = await service.ReadOperatingPolicyProposalAsync(Actor(http), LongId.Parse(poolId).Value, LongId.Parse(id).Value, ct);
            if (row is null) return null;
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row["proposal"]["revision"].AsInt64);
            return StockSourcePolicyResponses.Detail(row);
        }).WithName("StockSourcePolicyDetail");
        foreach (var (suffix, action, name) in new[] {
            ("approve", StockSourceSetupDecision.Approve, "ApproveStockSourcePolicy"),
            ("reject", StockSourceSetupDecision.Reject, "RejectStockSourcePolicy"),
            ("withdraw", StockSourceSetupDecision.Withdraw, "WithdrawStockSourcePolicy") })
            group.MapPost("/policy-proposals/{id}/" + suffix, async (string id, StockSourceSetupDecideDto request, HttpContext http,
                [FromServices] StockSourceSetupService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
                [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) =>
            {
                try
                {
                    var row = await service.DecideOperatingPolicyAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request),
                        action, request.ReviewReference, new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
                    http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
                    return TypedResults.Ok(StockSourcePolicyResponses.Command(row));
                }
                catch (AuthorizationDeniedException ex) when (ex.Code == "approval_revision_invalid")
                { throw new PersistenceConflictException("stock_source_policy_changed", "The policy proposal changed; review its current version"); }
            }).WithName(name);
        group.MapPost("/policy-operations/query", async (StockSourcePolicyRecoveryDto request, HttpContext http,
            [FromServices] StockSourceSetupService service, CancellationToken ct) =>
        {
            StockSourceSetupInputs.Purpose(request.Purpose, "operation-recovery");
            if (request.Operation is not ("stock.source.policy.propose" or "stock.source.policy.approve" or "stock.source.policy.reject" or "stock.source.policy.withdraw") ||
                string.IsNullOrEmpty(request.Key) || request.Key.Length > 128 || request.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':')))
                throw new DomainValidationException("invalid_stock_source_policy_recovery", "Use the original operating-policy command reference");
            var row = await service.RecoverOperatingPolicyAsync(Actor(http), request.PhysicalPoolId.Value, request.Operation, request.Key, http.TraceIdentifier, ct);
            return TypedResults.Ok(new StockSourcePolicyReceiptDto(row is null ? "not_confirmed" : "committed", row is null ? null : StockSourcePolicyResponses.Command(row)));
        }).WithName("StockSourcePolicyReceipt");
    }
    private static ActorContext Actor(HttpContext http) =>
        http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
}