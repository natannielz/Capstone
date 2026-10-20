using System.Text.Json.Serialization;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Api.Catalog;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPriceReadDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Access);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record CatalogPriceHistoryDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Access,
    string? BeforeVersion = null, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPriceInputDto([property: JsonRequired] string Currency, [property: JsonRequired] Money UnitPrice);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPriceProposeDto([property: JsonRequired] LongId ExpectedDefinitionId,
    [property: JsonRequired] string ExpectedDefinitionVersion, [property: JsonRequired] string ExpectedPriceVersion,
    [property: JsonRequired] CatalogPriceInputDto Input, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPriceDecideDto([property: JsonRequired] string ReviewReference, [property: JsonRequired] string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogPriceRecoveryDto([property: JsonRequired] string Purpose,
    [property: JsonRequired] string Action, [property: JsonRequired] string Key);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record CatalogPriceSelectionDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Access,
    string? Search = null, LongId? AfterId = null, int Limit = 25);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record CatalogPriceProposalListDto([property: JsonRequired] string Purpose, [property: JsonRequired] string Access,
    LongId? BeforeId = null, int Limit = 25);

public sealed record CatalogPriceSkuDto(LongId Id, LongId FamilyId, string Code, string PublicId, string Version, bool Active);
public sealed record CatalogPriceExpectationDto(string SkuVersion, LongId DefinitionId, string DefinitionVersion, string PriceVersion);
public sealed record CatalogPriceDto(LongId Id, LongId SkuId, string Version, string Currency, Money UnitPrice, DateTimeOffset EffectiveAt);
public sealed record CatalogPricePreviewDto(CatalogPriceSkuDto Sku, CatalogDefinitionDto Definition,
    CatalogPriceExpectationDto Expected, CatalogPriceDto? CurrentPrice);
public sealed record CatalogPriceCurrentDto(CatalogPriceDto? Price);
public sealed record CatalogPriceHistoryResultDto(CatalogPriceDto[] Items);
public sealed record CatalogPriceDecisionDto(LongId Id, string Action, DateTimeOffset DecidedAt, string ReviewReference, string Reason);
public sealed record CatalogPriceProposalDto(LongId ProposalId, LongId SkuId, string Revision, string Status,
    CatalogPriceSkuDto Sku, CatalogDefinitionDto Definition, CatalogPriceExpectationDto Expected,
    CatalogPriceDto? BasePrice, CatalogPriceInputDto Input, DateTimeOffset ProposedAt, string Reason,
    CatalogPriceDecisionDto? Decision, CatalogPriceDto? Price, LongId ProposedBy);
public sealed record CatalogPriceCommandDto(LongId ProposalId, LongId SkuId, string Revision, string Status,
    LongId? DecisionId, LongId? PriceId, string? PriceVersion);
public sealed record CatalogPriceReceiptDto(string State, CatalogPriceCommandDto? Result);
public sealed record CatalogPriceChoiceDto(CatalogPriceSkuDto Sku, string FamilyName, CatalogDefinitionDto Definition);
public sealed record CatalogPriceChoicesDto(CatalogPriceChoiceDto[] Items);
public sealed record CatalogPriceProposalsDto(CatalogPriceProposalDto[] Items);
