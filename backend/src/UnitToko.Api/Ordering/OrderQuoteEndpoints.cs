using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using UnitToko.Api.Authorization;
using UnitToko.Api.Catalog;
using UnitToko.Api.Organizations;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Ordering;

namespace UnitToko.Api.Ordering;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderContextDto([property: JsonRequired] LongId OrganizationId, [property: JsonRequired] LongId BranchId,
    [property: JsonRequired] LongId? DivisionId)
{
    internal OrderBuyingContext Domain() { var context = new OrderBuyingContext(OrganizationId, BranchId, DivisionId); context.Validate(); return context; }
    internal static OrderContextDto From(OrderBuyingContext context) => new(context.OrganizationId, context.BranchId, context.DivisionId);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderReadDto([property: JsonRequired] string Purpose);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderAddressesRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] OrderContextDto Context);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record OrderPackageSelectionDto([property: JsonRequired] LongId SkuId, [property: JsonRequired] LongId DefinitionId,
    [property: JsonRequired] string DefinitionVersion, [property: JsonRequired] LongId PriceId, [property: JsonRequired] string PriceVersion,
    [property: JsonRequired] int Packages);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrderQuoteRequestDto([property: JsonRequired] string Purpose, [property: JsonRequired] OrderContextDto Context,
    [property: JsonRequired] OrderPackageSelectionDto[] Selections);
public sealed record OrderContextOptionDto(OrderContextDto Context, string? OrganizationName, string? BranchName, string? DivisionName);
public sealed record OrderContextsDto(OrderContextOptionDto[] Items);
public sealed record OrderPostalAddressDto(OrganizationAddressTargetDto Target, string Purpose,
    LongId AddressId, LongId VersionId, string Version, string SourceScopeVersion, LongId ProfileId, string ProfileVersion,
    string Code, string Name, string SourceHash, string SnapshotHash, DateTimeOffset EffectiveAt, PostalAddressDto Address);
public sealed record OrderAddressesDto(OrderContextDto Context, DateTimeOffset ObservedAt,
    OrderPostalAddressDto LegalBilling, OrderPostalAddressDto Delivery, string Binding);
public sealed record OrderQuoteLineDto(LongId FamilyId, string FamilyName, string PublicId, string SkuCode,
    CatalogDefinitionDto Definition, CatalogPriceDto Price, int Packages, string BaseUnits, Money CatalogSubtotal);
public sealed record OrderQuoteDto(OrderContextDto Context, DateTimeOffset CalculatedAt, string Currency,
    Money CatalogSubtotal, OrderQuoteLineDto[] Lines);

public static class OrderQuoteEndpoints
{
    public static void MapOrderQuoteEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/ordering").RequireAuthorization().AddEndpointFilter<ActorRequiredFilter>();
        foreach (var status in new[] { 400, 401, 403, 409, 413, 503 }) group.WithMetadata(new ProducesResponseTypeAttribute(typeof(ApiProblem), status, "application/problem+json"));
        group.MapPost("/contexts/query", async (OrderReadDto request, HttpContext http, [FromServices] OrderQuoteService service, CancellationToken ct) => {
            Purpose(request.Purpose);
            var rows = await service.ContextsAsync(http.Features.Get<ResolvedActor>()!.Actor, ct);
            return TypedResults.Ok(new OrderContextsDto([.. rows.Select(row => new OrderContextOptionDto(
                OrderContextDto.From(row.Context), row.OrganizationName, row.BranchName, row.DivisionName))]));
        }).WithName("OrderBuyingContexts");
        group.MapPost("/addresses/query", async (OrderAddressesRequestDto request, HttpContext http, [FromServices] OrderQuoteService service, CancellationToken ct) => {
            Purpose(request.Purpose);
            if (request.Context is null) throw Invalid();
            var value = await service.AddressesAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Context.Domain(), ct);
            return TypedResults.Ok(new OrderAddressesDto(OrderContextDto.From(value.Context), value.ObservedAt,
                Postal(value.LegalBilling), Postal(value.Delivery), value.Binding));
        }).WithName("OrderAddresses").WithDescription("Current approved legal billing and exact branch or division delivery addresses for order preparation. No order admission, recipient, contact, tax or financial authority is granted.");
        group.MapPost("/quote/query", async (OrderQuoteRequestDto request, HttpContext http, [FromServices] OrderQuoteService service, CancellationToken ct) => {
            Purpose(request.Purpose);
            if (request.Context is null || request.Selections is null || request.Selections.Length is < 1 or > 100 || request.Selections.Any(line => line is null)) throw Invalid();
            OrderQuotePreview value;
            try {
                value = await service.PreviewAsync(http.Features.Get<ResolvedActor>()!.Actor, request.Context.Domain(),
                    [.. request.Selections.Select(line => new OrderPackageSelection(line.SkuId, line.DefinitionId,
                        CatalogInputs.PositiveVersion(line.DefinitionVersion), line.PriceId, CatalogInputs.PositiveVersion(line.PriceVersion), line.Packages))], ct);
            } catch (DomainValidationException error) when (error.Code == "order_selection_changed") {
                throw new PersistenceConflictException(error.Code, "Package or price changed; reload and review the selection");
            }
            return TypedResults.Ok(new OrderQuoteDto(OrderContextDto.From(value.Context), value.CalculatedAt, value.Catalog.Currency,
                value.Catalog.CatalogSubtotal, [.. value.Catalog.Lines.Select(line => new OrderQuoteLineDto(line.FamilyId, line.FamilyName,
                    line.PublicId.ToString("D"), line.SkuCode, CatalogResponses.From(line.Definition), CatalogPriceResponses.Price(line.Price)!,
                    line.Packages, line.BaseUnits.ToString(CultureInfo.InvariantCulture), line.CatalogSubtotal))]));
        }).WithName("OrderQuotePreview").WithDescription("Current branch-scoped catalog subtotal only. No stock hold, order submission, tax/credit approval or payable commitment. Submission must recalculate in its own transaction.");
    }
    private static OrderPostalAddressDto Postal(OrderPostalAddress value) => new(new(value.Target.Kind, new(value.Target.Id)),
        value.Purpose == OrganizationAddressPurpose.LegalBilling ? "legal_billing" : "delivery", value.AddressId, value.VersionId,
        value.Version.ToString(CultureInfo.InvariantCulture), value.SourceScopeVersion.ToString(CultureInfo.InvariantCulture), value.ProfileId,
        value.ProfileVersion.ToString(CultureInfo.InvariantCulture), value.Code, value.Name, value.SourceHash, value.SnapshotHash, value.EffectiveAt,
        new(value.Address.Line1, value.Address.Line2, value.Address.City, value.Address.Region, value.Address.PostalCode, value.Address.CountryCode));
    private static void Purpose(string value) { if (value != "order-preparation") throw Invalid(); }
    private static DomainValidationException Invalid() => new("invalid_order_quote", "Choose a branch and explicit current packages");
}
