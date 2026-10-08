using System.Collections.Immutable;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Catalog;

public sealed record ScanRequest(string Purpose, string CodeKind, string RawCode, string? SourceNamespace,
    string? SourceAccount, GrantScope? Location, long? DocumentId, long? DocumentRevision, long? AuxiliaryId,
    long? AuxiliaryRevision, string? SourceReview);
public sealed record ScanCandidate(string TargetReference, LongId SkuId, LongId DefinitionId, long DefinitionVersion,
    string SkuCode, string PackageLabel, string PackageKind, string BaseUnitCode, int BaseUnitsPerPackage,
    OperationalScanSelection? Selection = null, string? CurrentPrimaryCode = null);
public sealed record ScanSourceCandidate(ScanCandidate Candidate, string? SourceNamespace = null,
    string? SourceAccount = null, string? SourceCode = null);
public sealed record ScanResolution(string Kind, string Reason, ImmutableArray<ScanCandidate> Candidates,
    string Purpose, long? DocumentId, long? DocumentRevision, long? AuxiliaryId, long? AuxiliaryRevision, string? SourceReview);

/// <summary>Exact resolution inside an already authorized owner context. A result never changes quantity,
/// reserves goods, proves a lot or authorizes a command. Missing identifier registries remain unknown.</summary>
public static class ScanRules
{
    public static void Validate(ScanRequest request)
    {
        if (request is null || request.Purpose is not ("receiving" or "picking" or "dispatch" or "stocktake") ||
            request.CodeKind is not ("sku" or "supplier" or "gtin") || request.RawCode is null || request.RawCode.Length > 256 ||
            request.DocumentId is <= 0 || request.DocumentRevision is <= 0 || request.AuxiliaryId is <= 0 || request.AuxiliaryRevision is <= 0 ||
            request.SourceReview is { } review && (review.Length != 64 || review.Any(c => !char.IsAsciiHexDigit(c))) ||
            request.SourceNamespace is { } source && !Text(source, 32) || request.SourceAccount is { } account && !Text(account, 128)) throw Invalid();
        if (request.Purpose is "receiving" or "stocktake") {
            var location = request.Location;
            if (location is null || location.Kind != GrantScopeKind.FulfillmentWarehouse || location.OrganizationId is not > 0 ||
                location.ShopId is not > 0 || location.WarehouseId is not > 0 || location.ChannelId is not > 0 ||
                location.BranchId is not null || location.DivisionId is not null) throw Invalid();
        } else if (request.Location is not null) throw Invalid(); // Document owners derive fulfillment scope.
        if (request.Purpose == "stocktake") {
            if (request.DocumentId is not null || request.DocumentRevision is not null || request.AuxiliaryId is not null ||
                request.AuxiliaryRevision is not null || request.SourceReview is not null) throw Invalid();
        } else if (request.DocumentId is null || request.DocumentRevision is null) throw Invalid();
        if (request.Purpose is "receiving" or "dispatch") {
            if (request.AuxiliaryId is null || request.AuxiliaryRevision is null) throw Invalid();
        } else if (request.AuxiliaryId is not null || request.AuxiliaryRevision is not null) throw Invalid();
        if (request.Purpose is "picking" or "dispatch") { if (request.SourceReview is null) throw Invalid(); }
        else if (request.SourceReview is not null) throw Invalid();
        if (request.CodeKind == "supplier") {
            if (request.SourceNamespace is not ("supplier" or "omi" or "s_mart") || request.SourceAccount is null) throw Invalid();
        } else if (request.SourceNamespace is not null || request.SourceAccount is not null) throw Invalid();
    }
    public static string? ExactSku(string raw) {
        try {return CatalogPolicy.NormalizeSkuCode(raw);} catch (DomainValidationException) {return null;}
    }
    public static ScanResolution Resolve(ScanRequest request, IReadOnlyList<ScanSourceCandidate> source)
    {
        Validate(request);
        if (source is null || source.Count > 100 || source.Any(item => item is null || item.Candidate is null)) throw Invalid();
        ScanResolution Result(string kind, string reason, ImmutableArray<ScanCandidate> candidates = default) =>
            new(kind, reason, candidates.IsDefault ? [] : candidates, request.Purpose, request.DocumentId, request.DocumentRevision,
                request.AuxiliaryId, request.AuxiliaryRevision, request.SourceReview);
        if (!Text(request.RawCode, 256)) return Result("invalid", "invalid_code");
        string? gtin = null;
        if (request.CodeKind == "gtin") {
            try { gtin = Gtin.Parse(request.RawCode).Canonical14; } catch (DomainValidationException) {return Result("invalid", "invalid_gtin");}
        }
        if (request.CodeKind == "supplier" && request.Purpose != "receiving") return Result("unknown", "source_mapping_not_in_document");
        var code = request.CodeKind == "sku" ? ExactSku(request.RawCode) : request.RawCode;
        if (code is null) return Result("invalid", "invalid_sku_code");
        var matches = source.Where(item => request.CodeKind is "sku" or "gtin" ?
            item.Candidate.Selection?.Identifier is { } identifier && identifier.Kind == (request.CodeKind == "sku" ? "skuCode" : "gtin") &&
                identifier.CanonicalValue == (request.CodeKind == "gtin" ? gtin : code) :
            item.SourceNamespace == request.SourceNamespace && item.SourceAccount == request.SourceAccount && item.SourceCode == code)
            .Select(item => item.Candidate).Distinct().ToImmutableArray();
        foreach (var item in matches)
            if (!Text(item.TargetReference, 128) || item.SkuId.Value <= 0 || item.DefinitionId.Value <= 0 || item.DefinitionVersion <= 0 ||
                ExactSku(item.SkuCode) != item.SkuCode || !Text(item.PackageLabel, 160) || item.PackageKind is not ("loose" or "sealed" or "other") ||
                !Text(item.BaseUnitCode, 64) || item.BaseUnitsPerPackage <= 0 || item.Selection is null ||
                (item.Selection.Identifier is null) == (item.Selection.Source is null) ||
                item.Selection.Identifier is { } id && (id.IdentifierId.Value <= 0 || id.IdentifierRevision <= 0 ||
                    id.SkuId != item.SkuId || id.DefinitionId != item.DefinitionId || id.DefinitionVersion != item.DefinitionVersion) ||
                item.Selection.Source is { } approved && (request.Purpose != "receiving" || request.CodeKind != "supplier" ||
                    approved.OrderId.Value != request.DocumentId || approved.OrderVersion != request.DocumentRevision ||
                    approved.InspectionId.Value != request.AuxiliaryId || approved.InspectionRevision != request.AuxiliaryRevision ||
                    approved.InspectionLineNumber <= 0 || approved.SkuId != item.SkuId || approved.DefinitionId != item.DefinitionId ||
                    approved.DefinitionVersion != item.DefinitionVersion || approved.SourceSystem != request.SourceNamespace ||
                    approved.SourceAccount != request.SourceAccount || approved.SourceCode != request.RawCode)) throw Invalid();
        return matches.Length switch {0 => Result("unknown", "code_not_in_context"), 1 => Result("match", "exact_code", matches),
            _ => Result("ambiguous", "multiple_document_targets", matches)};
    }
    private static bool Text(string value, int maximum) => value.Length is > 0 && value.Length <= maximum && value.Trim() == value && !value.Any(char.IsControl);
    public static DomainValidationException Invalid() => new("invalid_operational_scan", "Choose an explicit code type and the exact current operational context");
}
