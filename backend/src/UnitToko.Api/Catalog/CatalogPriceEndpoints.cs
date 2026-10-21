using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;

namespace UnitToko.Api.Catalog;

public static class CatalogPriceEndpoints
{
    public static void MapCatalogPriceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/catalog/prices").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        group.MapPost("/skus/query", async (CatalogPriceSelectionDto request, HttpContext http,
            [FromServices] CatalogPriceService service, CancellationToken ct) => {
            Purpose(request.Purpose);
            var rows = await service.SelectSkusAsync(Actor(http), Access(request.Access), request.Search, request.AfterId?.Value, request.Limit, ct);
            return TypedResults.Ok(new CatalogPriceChoicesDto([.. rows.Select(CatalogPriceResponses.Choice)]));
        }).WithName("SelectCatalogPriceSkus");
        group.MapPost("/skus/{id}/proposals/query", async (string id, CatalogPriceProposalListDto request, HttpContext http,
            [FromServices] CatalogPriceService service, CancellationToken ct) => {
            Purpose(request.Purpose);
            var rows = await service.ListProposalsAsync(Actor(http), LongId.Parse(id).Value, Access(request.Access), request.BeforeId?.Value, request.Limit, ct);
            var viewer = Actor(http).UserId;
            return TypedResults.Ok(new CatalogPriceProposalsDto([.. rows.Select(row => CatalogPriceResponses.Proposal(row, viewer))]));
        }).WithName("ListCatalogPriceProposals");
        group.MapPost("/skus/{id}/preview/query", async (string id, CatalogPriceReadDto request, HttpContext http,
            [FromServices] CatalogPriceService service, CancellationToken ct) => {
            Purpose(request.Purpose); var value = await service.PreviewAsync(Actor(http), LongId.Parse(id).Value, Access(request.Access), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(value.Expected.SkuRevision);
            return TypedResults.Ok(CatalogPriceResponses.Preview(value));
        }).WithName("CatalogPricePreview");
        group.MapPost("/skus/{id}/current/query", async (string id, CatalogPriceReadDto request, HttpContext http,
            [FromServices] CatalogPriceService service, CancellationToken ct) => {
            Purpose(request.Purpose);
            return TypedResults.Ok(new CatalogPriceCurrentDto(CatalogPriceResponses.Price(
                await service.ReadCurrentAsync(Actor(http), LongId.Parse(id).Value, Access(request.Access), ct))));
        }).WithName("CatalogPriceCurrent");
        group.MapPost("/skus/{id}/history/query", async (string id, CatalogPriceHistoryDto request, HttpContext http,
            [FromServices] CatalogPriceService service, CancellationToken ct) => {
            Purpose(request.Purpose);
            if (request.Limit is < 1 or > 100) throw Invalid();
            var rows = await service.ReadHistoryAsync(Actor(http), LongId.Parse(id).Value, Access(request.Access),
                request.BeforeVersion is null ? null : CatalogInputs.PositiveVersion(request.BeforeVersion), request.Limit, ct);
            return TypedResults.Ok(new CatalogPriceHistoryResultDto([.. rows.Select(CatalogPriceResponses.Price)]));
        }).WithName("CatalogPriceHistory");
        group.MapPost("/skus/{id}/proposals/{proposalId}/query", async (string id, string proposalId, CatalogPriceReadDto request,
            HttpContext http, [FromServices] CatalogPriceService service, CancellationToken ct) => {
            Purpose(request.Purpose);
            var row = await service.ReadProposalAsync(Actor(http), LongId.Parse(id).Value, LongId.Parse(proposalId).Value, Access(request.Access), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(row["proposal"]["revision"].AsInt64);
            return TypedResults.Ok(CatalogPriceResponses.Proposal(row, Actor(http).UserId));
        }).WithName("CatalogPriceProposal");
        group.MapPost("/skus/{id}/proposals", async (string id, CatalogPriceProposeDto request, HttpContext http,
            [FromServices] CatalogPriceService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
            if (request.Input is null) throw Invalid();
            var expected = new CatalogPriceExpectation(PositiveVersion(http), request.ExpectedDefinitionId,
                CatalogInputs.PositiveVersion(request.ExpectedDefinitionVersion), Version(request.ExpectedPriceVersion));
            var row = await Change(() => service.ProposeAsync(Actor(http), LongId.Parse(id).Value, expected,
                new(request.Input.Currency, request.Input.UnitPrice), Metadata(http, request.Reason), ct));
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
            return TypedResults.Ok(CatalogPriceResponses.Command(row));
        }).WithName("ProposeCatalogPrice");
        foreach (var (action, decision, name) in new[] { ("approve", CatalogPriceDecision.Approve, "ApproveCatalogPrice"),
            ("reject", CatalogPriceDecision.Reject, "RejectCatalogPrice"), ("withdraw", CatalogPriceDecision.Withdraw, "WithdrawCatalogPrice") })
            group.MapPost("/proposals/{id}/" + action, async (string id, CatalogPriceDecideDto request, HttpContext http,
                [FromServices] CatalogPriceService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
                [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) => {
                var row = await Change(() => service.DecideAsync(Actor(http), LongId.Parse(id).Value, PositiveVersion(http),
                    decision, CatalogAuthoringPolicy.Text(request.ReviewReference, 160), Metadata(http, request.Reason), ct));
                http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(1);
                return TypedResults.Ok(CatalogPriceResponses.Command(row));
            }).WithName(name);
        group.MapPost("/skus/{id}/operations/query", async (string id, CatalogPriceRecoveryDto request, HttpContext http,
            [FromServices] CatalogPriceService service, CancellationToken ct) => {
            Purpose(request.Purpose, "operation-recovery");
            if (request.Action is not ("propose" or "approve" or "reject" or "withdraw") || string.IsNullOrEmpty(request.Key) ||
                request.Key.Length > 128 || request.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or ':'))) throw Invalid();
            var row = await service.RecoverAsync(Actor(http), LongId.Parse(id).Value, request.Action, request.Key, ct);
            return TypedResults.Ok(new CatalogPriceReceiptDto(row is null ? "not_confirmed" : "committed", row is null ? null : CatalogPriceResponses.Command(row)));
        }).WithName("RecoverCatalogPriceOperation");
    }
    private static DomainValidationException Invalid() => new("invalid_catalog_price_request", "Complete the bounded price review fields");
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
    private static CatalogPriceAccess Access(string access) => access switch { "prepare" => CatalogPriceAccess.Prepare, "review" => CatalogPriceAccess.Review, _ => throw Invalid() };
    private static void Purpose(string purpose, string expected = "catalog-pricing") { if (purpose != expected) throw Invalid(); }
    private static long Version(string value) => value == "0" ? 0 : CatalogInputs.PositiveVersion(value);
    private static long PositiveVersion(HttpContext http)
    {
        var version = AccessRequestMetadata.ExpectedVersion(http.Request); if (version <= 0) throw Invalid(); return version;
    }
    private static AuthorizationCommandMetadata Metadata(HttpContext http, string reason) => new(
        AccessRequestMetadata.IdempotencyKey(http.Request), CatalogAuthoringPolicy.Text(reason, 1000), http.TraceIdentifier);
    private static async Task<T> Change<T>(Func<Task<T>> command)
    {
        try { return await command(); }
        catch (DomainValidationException error) when (error.Code == "catalog_price_changed")
        { throw new PersistenceConflictException(error.Code, "Price or package changed; reload and review"); }
        catch (AuthorizationDeniedException error) when (error.Code == "approval_revision_invalid")
        { throw new PersistenceConflictException("catalog_price_changed", "Price proposal changed; reload and review"); }
    }
}
