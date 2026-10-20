using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;

namespace UnitToko.Api.Catalog;

public sealed record PublicCatalogAttributeDto(string Key, string Value);
/// <summary>What anyone may see before signing in. There is deliberately no price, stock, owner or internal id.</summary>
public sealed record PublicCatalogItemDto(string PublicId, string Code, string Name, string Description, string CategoryCode, string CategoryName,
    string PackageLabel, string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage, PublicCatalogAttributeDto[] Attributes,
    int MinimumOrderPackages, int OrderIncrementPackages);
public sealed record PublicCatalogCategoryDto(string Code, string Name, string Count);
public sealed record PublicCatalogPageDto(string Total, PublicCatalogItemDto[] Items, PublicCatalogCategoryDto[] Categories);

public sealed record CatalogPublicShowcaseQueryDto([property: System.Text.Json.Serialization.JsonRequired] string Purpose);
public sealed record CatalogPublicShowcaseProposeDto(bool MakePublic, string Reason);
public sealed record CatalogPublicShowcaseDecideDto(string Reason);
public sealed record CatalogPublicShowcaseOwnerDto(string Kind, LongId OrganizationId, LongId? ShopId, LongId? WarehouseId, LongId? ChannelId);
public sealed record CatalogPublicShowcasePendingDto(LongId ProposalId, bool MakePublic, LongId ProposedBy, DateTimeOffset ProposedAt, string Reason);
public sealed record CatalogPublicShowcaseDto(LongId AssortmentId, CatalogPublicShowcaseOwnerDto Owner, string? OwnerName, string ListedProducts, bool IsPublic, string Revision,
    CatalogPublicShowcasePendingDto? Pending);
public sealed record CatalogPublicShowcaseListDto(CatalogPublicShowcaseDto[] Items);
public sealed record CatalogPublicShowcaseCommandDto(LongId ProposalId, LongId? DecisionId, LongId AssortmentId, string? Status, string? HeadRevision);

public static class CatalogPublicEndpoints
{
    public static void MapCatalogPublicEndpoints(this WebApplication app)
    {
        // Anonymous, read-only and cacheable for a minute: no session, lease or audit is involved, so traffic causes no writes.
        var open = app.MapGroup("/api/v1/public/catalog").AllowAnonymous();
        foreach (var status in new[] { 400, 404, 503 })
            open.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        open.MapGet("", async (HttpContext http, [FromServices] CatalogDefinitionQueryService service, [FromQuery] string? q, [FromQuery] string? kategori,
            [FromQuery] string? urut, [FromQuery] int? offset, [FromQuery] int? limit, CancellationToken ct) =>
        {
            var sort = urut switch { null or "" or "terbaru" => PublicCatalogSort.Added, "nama" => PublicCatalogSort.Name,
                _ => throw new DomainValidationException("public_catalog_query_invalid", "Choose a supported order") };
            var page = await service.QueryPublicAsync(offset ?? 0, limit ?? 24, q, kategori, sort, ct);
            Cacheable(http);
            return TypedResults.Ok(new PublicCatalogPageDto(page.Total.ToString(CultureInfo.InvariantCulture), [.. page.Items.Select(Item)],
                [.. page.Categories.Select(item => new PublicCatalogCategoryDto(item.Code, item.Name, item.Count.ToString(CultureInfo.InvariantCulture)))]));
        }).WithName("PublicCatalogQuery");
        open.MapGet("/{publicId}", async Task<Microsoft.AspNetCore.Http.HttpResults.Results<Microsoft.AspNetCore.Http.HttpResults.Ok<PublicCatalogItemDto>,
            Microsoft.AspNetCore.Http.HttpResults.NotFound>> (string publicId, HttpContext http, [FromServices] CatalogDefinitionQueryService service, CancellationToken ct) =>
        {
            var item = await service.ReadPublicItemAsync(publicId, ct);
            Cacheable(http);
            return item is null ? TypedResults.NotFound() : TypedResults.Ok(Item(item));
        }).WithName("PublicCatalogRead");

        var staff = app.MapGroup("/api/v1/catalog/public-showcase").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            staff.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
        staff.MapPost("/query", async (CatalogPublicShowcaseQueryDto request, HttpContext http, [FromServices] CatalogPublicShowcaseService service, CancellationToken ct) =>
        {
            if (request.Purpose != "catalog-public-showcase") throw Invalid();
            var items = await service.ListAsync(Actor(http), ct);
            return TypedResults.Ok(new CatalogPublicShowcaseListDto([.. items.Select(item => new CatalogPublicShowcaseDto(new(item.AssortmentId),
                new(item.Owner.Kind == UnitToko.Modules.Organizations.GrantScopeKind.FulfillmentShop ? "fulfillmentShop" : "fulfillmentWarehouse",
                    new(item.Owner.OrganizationId ?? 0), Id(item.Owner.ShopId), Id(item.Owner.WarehouseId), Id(item.Owner.ChannelId)), item.OwnerName,
                item.ListedProducts.ToString(CultureInfo.InvariantCulture), item.IsPublic, item.Revision.ToString(CultureInfo.InvariantCulture),
                item.Pending is null ? null : new(new(item.Pending.ProposalId), item.Pending.MakePublic, new(item.Pending.ProposedBy), item.Pending.ProposedAt, item.Pending.Reason)))]));
        }).WithName("CatalogPublicShowcaseList");
        staff.MapPost("/{assortmentId}/proposals", async (string assortmentId, CatalogPublicShowcaseProposeDto request, HttpContext http,
            [FromServices] CatalogPublicShowcaseService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) =>
        {
            var version = AccessRequestMetadata.ExpectedVersion(http.Request); if (version < 0) throw Invalid();
            var row = await service.ProposeAsync(Actor(http), LongId.Parse(assortmentId).Value, version, request.MakePublic, Metadata(http, request.Reason), ct);
            return TypedResults.Ok(Command(row));
        }).WithName("ProposeCatalogPublicShowcase");
        foreach (var (action, decision, name) in new[] { ("approve", CatalogPublicShowcaseDecision.Approve, "ApproveCatalogPublicShowcase"),
            ("reject", CatalogPublicShowcaseDecision.Reject, "RejectCatalogPublicShowcase") })
            staff.MapPost("/proposals/{proposalId}/" + action, async (string proposalId, CatalogPublicShowcaseDecideDto request, HttpContext http,
                [FromServices] CatalogPublicShowcaseService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
                [FromHeader(Name = "If-Match")] string ifMatch, CancellationToken ct) =>
            {
                var version = AccessRequestMetadata.ExpectedVersion(http.Request); if (version <= 0) throw Invalid();
                var row = await service.DecideAsync(Actor(http), LongId.Parse(proposalId).Value, version, decision, Metadata(http, request.Reason), ct);
                return TypedResults.Ok(Command(row));
            }).WithName(name);
    }

    private static PublicCatalogItemDto Item(PublicCatalogItem item) => new(item.PublicId, item.Code, item.Name, item.Description, item.CategoryCode, item.CategoryName,
        item.PackageLabel, item.PackageKind, item.BaseUnitCode, item.BaseUnitsPerPackage, [.. item.Attributes.Select(pair => new PublicCatalogAttributeDto(pair.Key, pair.Value))],
        item.MinimumOrderPackages, item.OrderIncrementPackages);
    /// <summary>Replaces the identity layer's no-store default: the same answer is safe for everyone for a minute.</summary>
    private static void Cacheable(HttpContext http) => http.Response.Headers.CacheControl = "public, max-age=60";
    private static LongId? Id(long? value) => value is { } id ? new LongId(id) : null;
    private static CatalogPublicShowcaseCommandDto Command(MongoDB.Bson.BsonDocument row) => new(new(row["proposalId"].AsInt64),
        row.Contains("decisionId") ? new LongId(row["decisionId"].AsInt64) : null, new(row["assortmentId"].AsInt64),
        row.GetValue("status", MongoDB.Bson.BsonNull.Value).IsString ? row["status"].AsString : null,
        row.GetValue("headRevision", MongoDB.Bson.BsonNull.Value).IsInt64 ? row["headRevision"].AsInt64.ToString(CultureInfo.InvariantCulture) : null);
    private static DomainValidationException Invalid() => new("invalid_catalog_public_request", "Complete the public catalog review fields");
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ?? throw new AuthorizationDeniedException("unauthenticated");
    private static AuthorizationCommandMetadata Metadata(HttpContext http, string reason) => new(
        AccessRequestMetadata.IdempotencyKey(http.Request), CatalogAuthoringPolicy.Text(reason, 1000), http.TraceIdentifier);
}
