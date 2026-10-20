using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Catalog;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;

namespace UnitToko.Api.Catalog;

public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this WebApplication app)
    {
        app.MapCatalogBulkEndpoints();
        var group = app.MapGroup("/api/v1/catalog/administration")
            .RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        ProblemsMetadata(group);
        group.MapPost("/owners/query", async (CatalogReadDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, CancellationToken ct) =>
            TypedResults.Ok(CatalogResponses.From(await service.OwnerOptionsAsync(Actor(http), Read(http, request.Purpose), ct))))
            .WithName("CatalogOwnerOptions");
        group.MapPost("/categories/query", async (CatalogOwnerPageQueryDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, CancellationToken ct) =>
        {
            Page(request.Offset, request.Limit);
            return TypedResults.Ok(CatalogResponses.From(await service.CategoriesAsync(Actor(http),
                CatalogInputs.Owner(request.Owner), request.Offset, request.Limit, Read(http, request.Purpose), ct)));
        }).WithName("CatalogCategoryQuery");
        group.MapPost("/families/query", async (CatalogFamilyQueryDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, CancellationToken ct) =>
        {
            Page(request.Offset, request.Limit); Search(request.Search);
            return TypedResults.Ok(CatalogResponses.From(await service.FamiliesAsync(Actor(http),
                CatalogInputs.Owner(request.Owner), request.Search, request.Offset, request.Limit, Read(http, request.Purpose), ct)));
        }).WithName("CatalogFamilyQuery");
        group.MapPost("/families/{id}/query", async (string id, CatalogReadDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, CancellationToken ct) =>
        {
            var result = await service.FamilyAsync(Actor(http), LongId.Parse(id).Value, Read(http, request.Purpose), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Revision);
            return TypedResults.Ok(CatalogResponses.From(result));
        }).WithName("CatalogFamilyRead");
        group.MapPost("/families/{id}/skus/query", async (string id, CatalogSkuQueryDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, CancellationToken ct) =>
        {
            Page(request.Offset, request.Limit);
            return TypedResults.Ok(CatalogResponses.From(await service.SkusAsync(Actor(http), LongId.Parse(id).Value,
                request.Offset, request.Limit, Read(http, request.Purpose), ct)));
        }).WithName("CatalogSkuQuery");
        group.MapPost("/skus/{id}/query", async (string id, CatalogReadDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, CancellationToken ct) =>
        {
            var result = await service.SkuAsync(Actor(http), LongId.Parse(id).Value, Read(http, request.Purpose), ct);
            http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Sku.Revision);
            return TypedResults.Ok(CatalogResponses.From(result));
        }).WithName("CatalogSkuRead");
        group.MapPost("/assortments/query", async (CatalogOwnerPageQueryDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, CancellationToken ct) =>
        {
            Page(request.Offset, request.Limit);
            return TypedResults.Ok(CatalogResponses.From(await service.AssortmentsAsync(Actor(http), CatalogInputs.Owner(request.Owner),
                request.Offset, request.Limit, Read(http, request.Purpose), ct)));
        }).WithName("CatalogAssortmentQuery");
        group.MapPost("/operations/query", async (CatalogReceiptQueryDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, CancellationToken ct) =>
        {
            Operation(request.Operation); Key(request.Key);
            var result = await service.ReadOperationReceiptAsync(Actor(http), request.Operation, request.Key,
                Read(http, request.Purpose, "operation-recovery"), ct);
            if (result is not null) http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Version);
            return TypedResults.Ok(CatalogResponses.Receipt(result));
        }).WithName("CatalogReceiptQuery");

        group.MapPost("/categories", async (CatalogCategorySaveDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
        {
            var version = AccessRequestMetadata.ExpectedVersion(http.Request);
            if (request.CategoryId is null ? version != 0 : version <= 0)
                throw new DomainValidationException("invalid_version", "Choose the current category version");
            var input = new CatalogSaveCategory(CatalogInputs.Owner(request.Owner), request.CategoryId, version,
                CatalogInputs.Text(request.Code), CatalogInputs.Text(request.Name));
            return Saved(http, await service.SaveCategoryAsync(Actor(http), input, Metadata(http, request.Reason), ct));
        }).WithName("CatalogCategorySave");
        group.MapPost("/families", async (CatalogFamilyCreateDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CancellationToken ct) =>
        {
            var input = new CatalogCreateFamily(CatalogInputs.Owner(request.Owner), request.CategoryId,
                CatalogInputs.Text(request.Name), CatalogInputs.Text(request.Description), CatalogInputs.Images(request.ImageAssetIds));
            return Saved(http, await service.CreateFamilyAsync(Actor(http), input, Metadata(http, request.Reason), ct));
        }).WithName("CatalogFamilyCreate");
        group.MapPost("/families/{id}/edit", async (string id, CatalogFamilyEditDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
        {
            var input = new CatalogEditFamily(LongId.Parse(id), Version(http), request.CategoryId,
                CatalogInputs.Text(request.Name), CatalogInputs.Text(request.Description), CatalogInputs.Images(request.ImageAssetIds));
            return Saved(http, await service.EditFamilyAsync(Actor(http), input, Metadata(http, request.Reason), ct));
        }).WithName("CatalogFamilyEdit");
        group.MapPost("/families/{id}/skus", async (string id, CatalogSkuCreateDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
        {
            var input = new CatalogCreateSku(LongId.Parse(id), Version(http), CatalogInputs.Text(request.Code),
                CatalogInputs.Text(request.PhysicalItemKey), CatalogInputs.Definition(request.Definition));
            return Saved(http, await service.CreateSkuAsync(Actor(http), input, Metadata(http, request.Reason), ct));
        }).WithName("CatalogSkuCreate");
        group.MapPost("/skus/{id}/definition", async (string id, CatalogDefinitionSaveDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
            Saved(http, await service.ReviseDefinitionAsync(Actor(http),
                new(LongId.Parse(id), Version(http), CatalogInputs.Definition(request.Definition)), Metadata(http, request.Reason), ct)))
            .WithName("CatalogDefinitionRevise");
        group.MapPost("/skus/{id}/replacement", async (string id, CatalogSkuCreateDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
            Saved(http, await service.CreateReplacementSkuAsync(Actor(http),
                new(LongId.Parse(id), Version(http), CatalogInputs.Text(request.Code), CatalogInputs.Text(request.PhysicalItemKey),
                    CatalogInputs.Definition(request.Definition)), Metadata(http, request.Reason), ct)))
            .WithName("CatalogSkuReplace");
        group.MapPost("/skus/{id}/retire", async (string id, CatalogReasonDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
            Saved(http, await service.RetireSkuAsync(Actor(http), new(LongId.Parse(id), Version(http)), Metadata(http, request.Reason), ct)))
            .WithName("CatalogSkuRetire");
        group.MapPost("/skus/{id}/publications", async (string id, CatalogPublicationSaveDto request, HttpContext http,
            [FromServices] CatalogAuthoringService service, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
            [FromHeader(Name = "If-Match")] string expectedVersion, CancellationToken ct) =>
            Saved(http, await service.SetPublicationsAsync(Actor(http), request.ToDomain(LongId.Parse(id), Version(http)), Metadata(http, request.Reason), ct)))
            .WithName("CatalogSkuPublish");

        var buyer = app.MapGroup("/api/v1/catalog/variants")
            .RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        ProblemsMetadata(buyer);
        buyer.MapPost("/query", async (CatalogVariantQueryDto request, HttpContext http,
            [FromServices] CatalogDefinitionQueryService service, CancellationToken ct) =>
        {
            Page(request.Offset, request.Limit); Search(request.Search);
            var sort = request.Sort switch {
                null or "added" => CatalogBuyerSort.Added, "name" => CatalogBuyerSort.Name,
                _ => throw new DomainValidationException("catalog_query_invalid", "Choose a supported catalog order") };
            return TypedResults.Ok(CatalogBuyerIdentifierResponses.From(await service.QueryBuyerAsync(Actor(http), request.Offset, request.Limit,
                request.Search, request.Category, sort, ct)));
        }).WithName("CatalogPublishedVariantQuery");
        buyer.MapPost("/{publicId}/query", async Task<CatalogBuyerVariantDto?> (string publicId, CatalogVariantReadDto request, HttpContext http,
            [FromServices] CatalogDefinitionQueryService service, CancellationToken ct) =>
        {
            var result = await service.ReadBuyerPublicAsync(Actor(http), publicId, ct);
            return result is null ? null : CatalogBuyerIdentifierResponses.From(result);
        }).WithName("CatalogPublishedVariantRead");
    }

    private static Microsoft.AspNetCore.Http.HttpResults.Ok<CatalogMutationResultDto> Saved(HttpContext http, CatalogMutationResult result)
    {
        http.Response.Headers.ETag = AccessRequestMetadata.VersionTag(result.Version);
        return TypedResults.Ok(CatalogResponses.From(result));
    }
    private static void ProblemsMetadata(RouteGroupBuilder group)
    {
        foreach (var status in new[] { 400, 401, 403, 409, 503 })
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(ApiProblem), ["application/problem+json"]));
    }
    private static ActorContext Actor(HttpContext http) => http.Features.Get<ResolvedActor>()?.Actor ??
        throw new AuthorizationDeniedException("unauthenticated");
    private static AuthorizationReadMetadata Read(HttpContext http, string purpose, string expected = "catalog-authoring")
    {
        if (purpose != expected) throw new DomainValidationException("invalid_read_purpose", "Choose the required catalog purpose");
        return new(purpose, http.TraceIdentifier);
    }
    private static AuthorizationCommandMetadata Metadata(HttpContext http, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000 || reason.Any(char.IsControl))
            throw new DomainValidationException("invalid_reason", "A bounded reason is required");
        return new(AccessRequestMetadata.IdempotencyKey(http.Request), reason.Trim(), http.TraceIdentifier);
    }
    private static void Page(int offset, int limit)
    {
        if (offset is < 0 or > 10000 || limit is < 1 or > 100)
            throw new DomainValidationException("invalid_page", "Choose a bounded result page");
    }
    private static void Search(string? search)
    {
        if (search is not null && (search.Length > 128 || search.Any(char.IsControl)))
            throw new DomainValidationException("invalid_catalog_search", "Enter a bounded catalog search");
    }
    private static long Version(HttpContext http)
    {
        var version = AccessRequestMetadata.ExpectedVersion(http.Request);
        if (version <= 0) throw new DomainValidationException("invalid_version", "Choose a positive reviewed version");
        return version;
    }
    private static void Operation(string operation)
    {
        if (operation is not ("catalog.category.save" or "catalog.family.create" or "catalog.family.edit" or
            "catalog.sku.create" or "catalog.sku.revise" or "catalog.sku.replace" or "catalog.sku.retire" or "catalog.sku.publish"))
            throw new DomainValidationException("invalid_operation", "Choose a catalog operation");
    }
    private static void Key(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 128 ||
            key.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':')))
            throw new DomainValidationException("invalid_idempotency_key", "A bounded request key is required");
    }
}
