using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;

namespace UnitToko.Api.Inventory;

public static class PackConversionRuleEndpoints
{
    public static void MapPackConversionRuleEndpoints(this WebApplication app) {
        var group = app.MapGroup("/api/v1/inventory/pack-conversions").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/warehouses/query", async (PackConversionReadDto request, HttpContext http,
            [FromServices] PackConversionRuleService service, CancellationToken ct) => {
            Purpose(request.Purpose, "pack-conversion-rule-warehouses");
            return TypedResults.Ok(StockCountResponses.Warehouses(await service.WarehouseChoicesAsync(Actor(http), new(request.Purpose, http.TraceIdentifier), ct)));
        }).WithName("PackConversionRuleWarehouses");
        group.MapPost("/packages/query", async (PackConversionPackageQueryDto request, HttpContext http,
            [FromServices] PackConversionRuleService service, CancellationToken ct) => {
            var role = request.Role switch { "source" => PackConversionChoiceRole.Source, "destination" => PackConversionChoiceRole.Destination,
                _ => throw PackConversionRulePolicy.Invalid() };
            Purpose(request.Purpose, role == PackConversionChoiceRole.Source ? "pack-conversion-rule-source" : "pack-conversion-rule-destination");
            return TypedResults.Ok(StockCountResponses.Packages(await service.PackageChoicesAsync(Actor(http), StockCountInputs.Location(request.Location),
                role, request.Offset, request.Limit, request.Search, new(request.Purpose, http.TraceIdentifier), ct)));
        }).WithName("PackConversionRulePackages");
        group.MapPost("/rules/preview/query", async (PackConversionPreviewQueryDto request, HttpContext http,
            [FromServices] PackConversionRuleService service, CancellationToken ct) => {
            Purpose(request.Purpose, "pack-conversion-rule-preview");
            return TypedResults.Ok(PackConversionResponses.Preview(await service.PreviewRuleAsync(Actor(http),
                request.Input?.ToDomain() ?? throw PackConversionRulePolicy.Invalid(), ct)));
        }).WithName("PackConversionRulePreview");
        group.MapPost("/proposals", async (PackConversionProposeDto request, HttpContext http,
            [FromServices] PackConversionRuleService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) => {
            var result = await service.ProposeRuleAsync(Actor(http), request.Input?.ToDomain() ?? throw PackConversionRulePolicy.Invalid(), request.Binding,
                new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(PackConversionResponses.Command(result));
        }).WithName("ProposePackConversionRule");
        group.MapPost("/proposals/query", async (PackConversionPageQueryDto request, HttpContext http,
            [FromServices] PackConversionRuleService service, CancellationToken ct) => {
            Purpose(request.Purpose, "pack-conversion-rule-history");
            return TypedResults.Ok(PackConversionResponses.Proposals(await service.ListProposalsAsync(Actor(http),
                StockCountInputs.Location(request.Location), request.BeforeId?.Value, ct)));
        }).WithName("PackConversionRuleProposals");
        group.MapPost("/proposals/{id}/query", async (string id, PackConversionReadDto request, HttpContext http,
            [FromServices] PackConversionRuleService service, CancellationToken ct) => {
            Purpose(request.Purpose, "pack-conversion-rule-history");
            var row = await service.ReadProposalAsync(Actor(http), LongId.Parse(id).Value, ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row["proposal"]["revision"].AsInt64);
            return TypedResults.Ok(PackConversionResponses.Detail(row));
        }).WithName("PackConversionRuleProposalDetail");
        group.MapPost("/proposals/{id}/review/query", async (string id, PackConversionReviewQueryDto request, HttpContext http,
            [FromServices] PackConversionRuleService service, CancellationToken ct) => {
            Purpose(request.Purpose, "pack-conversion-rule-review");
            var row = await service.PreviewDecisionAsync(Actor(http), LongId.Parse(id).Value, request.Decision(), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row["proposal"]["revision"].AsInt64);
            return TypedResults.Ok(PackConversionResponses.Review(row));
        }).WithName("PackConversionRuleReview");
        foreach (var (suffix, decision, name) in new[] { ("approve", PackConversionRuleDecision.Approve, "ApprovePackConversionRule"),
            ("reject", PackConversionRuleDecision.Reject, "RejectPackConversionRule") })
            group.MapPost("/proposals/{id}/" + suffix, async (string id, PackConversionDecideDto request, HttpContext http,
                [FromServices] PackConversionRuleService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
                [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
                var result = await service.DecideRuleAsync(Actor(http), LongId.Parse(id).Value, AccessRequestMetadata.ExpectedVersion(http.Request), decision,
                    request.Binding, new(AccessRequestMetadata.IdempotencyKey(http.Request), StockSourceSetupInputs.Reason(request.Reason), http.TraceIdentifier), ct);
                http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
                return TypedResults.Ok(PackConversionResponses.Command(result));
            }).WithName(name);
        group.MapPost("/rules/query", async (PackConversionPageQueryDto request, HttpContext http,
            [FromServices] PackConversionRuleService service, CancellationToken ct) => {
            Purpose(request.Purpose, "pack-conversion-rule-history");
            return TypedResults.Ok(PackConversionResponses.Rules(await service.ListRulesAsync(Actor(http),
                StockCountInputs.Location(request.Location), request.BeforeId?.Value, ct)));
        }).WithName("PackConversionRules");
        group.MapPost("/operations/query", async (PackConversionReceiptQueryDto request, HttpContext http,
            [FromServices] PackConversionRuleService service, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery");
            var row = await service.LookupAsync(Actor(http), request.Operation, request.Key, ct);
            return TypedResults.Ok(new PackConversionReceiptDto(row is null ? "not_confirmed" : "committed", row is null ? null : PackConversionResponses.Command(row)));
        }).WithName("PackConversionRuleReceipt");
        group.MapPost("/admission/query", async (PackConversionAdmissionQueryDto request, HttpContext http,
            [FromServices] PackConversionRuleService service, CancellationToken ct) => {
            Purpose(request.Purpose, "pack-conversion-admission");
            return TypedResults.Ok(PackConversionResponses.Admission(await service.ReadAdmissionAsync(Actor(http), request.ToDomain(), ct)));
        }).WithName("PackConversionAdmission");
    }
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
    private static void Purpose(string actual, string expected) {
        if (actual != expected) throw new DomainValidationException("invalid_read_purpose", "Use the stated conversion-rule purpose");
    }
}
