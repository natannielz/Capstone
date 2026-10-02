using System.Collections.Immutable;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Procurement;

public sealed record SupplierOwner(long OrganizationId, long ShopId, long ChannelId);
public enum SupplierAccess { Propose, Approve }
public enum SupplierDecision { Approve, Reject }
public sealed record SupplierDetails(string SubjectReference, string LegalName, string DisplayName, string? RegistrationNamespace,
    string? RegistrationValue, string Address, string ContactName, string ContactMethod, string ContactValue);
public sealed record SupplierMappingInput(ProcurementLocation Location, string SourceSystem, string SourceAccountReference, string ExternalItemCode,
    long SkuId, long ExpectedSkuRevision, long DefinitionId, long DefinitionVersion, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil);
public sealed record SupplierChangeInput(SupplierOwner Owner, long? SupplierId, long BaseVersion, long PolicyId, long PolicyRevision,
    SupplierDetails Details, ImmutableArray<SupplierMappingInput> Mappings);

public static class SupplierRules
{
    public static SupplierOwner Owner(SupplierOwner value) {
        if (value is null || value.OrganizationId <= 0 || value.ShopId <= 0 || value.ChannelId <= 0) throw Invalid(); return value;
    }
    public static GrantScope Scope(SupplierOwner owner) { Owner(owner); return new(GrantScopeKind.FulfillmentShop,
        OrganizationId: owner.OrganizationId, ShopId: owner.ShopId, ChannelId: owner.ChannelId); }
    public static void Text(string? value, int maximum, bool optional = false) {
        if (optional && value is null) return;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > maximum || value.Any(char.IsControl)) throw Invalid();
    }
    public static SupplierDetails Details(SupplierDetails value) {
        if (value is null || value.ContactMethod is not ("email" or "phone" or "other") || (value.RegistrationNamespace is null) != (value.RegistrationValue is null)) throw Invalid();
        Text(value.SubjectReference, 128); Text(value.LegalName, 160); Text(value.DisplayName, 160);
        Text(value.RegistrationNamespace, 64, true); Text(value.RegistrationValue, 128, true); Text(value.Address, 500);
        Text(value.ContactName, 160); Text(value.ContactValue, 256); return value with { };
    }
    public static void Interval(DateTimeOffset from, DateTimeOffset until) {
        if (from.Offset != TimeSpan.Zero || until.Offset != TimeSpan.Zero || from == DateTimeOffset.MinValue ||
            from.Ticks % TimeSpan.TicksPerMillisecond != 0 || until.Ticks % TimeSpan.TicksPerMillisecond != 0 || until <= from) throw Invalid();
    }
    public static bool Overlaps(SupplierMappingInput left, SupplierMappingInput right) => left.ValidFrom < right.ValidUntil && right.ValidFrom < left.ValidUntil;
    public static bool SameNamespace(SupplierMappingInput left, SupplierMappingInput right) => left.SourceSystem == right.SourceSystem &&
        left.SourceAccountReference == right.SourceAccountReference && left.ExternalItemCode == right.ExternalItemCode;
    public static SupplierChangeInput Copy(SupplierChangeInput value) {
        if (value is null || value.SupplierId is <= 0 || value.BaseVersion is < 0 or >= long.MaxValue - 1 || value.SupplierId is null && value.BaseVersion != 0 ||
            value.PolicyId <= 0 || value.PolicyRevision <= 0 || value.Mappings.IsDefault || value.Mappings.Length is < 1 or > 100) throw Invalid();
        Owner(value.Owner); var details = Details(value.Details);
        var mappings = value.Mappings.Select(mapping => {
            if (mapping is null || mapping.SourceSystem is not ("omi" or "s_mart" or "supplier") || mapping.SkuId <= 0 || mapping.ExpectedSkuRevision <= 0 ||
                mapping.DefinitionId <= 0 || mapping.DefinitionVersion <= 0) throw Invalid();
            ProcurementRequestRules.Location(mapping.Location);
            if (mapping.Location.OrganizationId != value.Owner.OrganizationId || mapping.Location.ShopId != value.Owner.ShopId || mapping.Location.ChannelId != value.Owner.ChannelId) throw Invalid();
            Text(mapping.SourceAccountReference, 128); Text(mapping.ExternalItemCode, 128); Interval(mapping.ValidFrom, mapping.ValidUntil);
            return mapping with { Location = mapping.Location with { } };
        }).OrderBy(mapping => mapping.SourceSystem, StringComparer.Ordinal).ThenBy(mapping => mapping.SourceAccountReference, StringComparer.Ordinal)
            .ThenBy(mapping => mapping.ExternalItemCode, StringComparer.Ordinal).ThenBy(mapping => mapping.Location.WarehouseId)
            .ThenBy(mapping => mapping.SkuId).ThenBy(mapping => mapping.ValidFrom).ToImmutableArray();
        for (var index = 0; index < mappings.Length; index++) for (var other = index + 1; other < mappings.Length; other++)
            if (SameNamespace(mappings[index], mappings[other]) && Overlaps(mappings[index], mappings[other])) throw Invalid();
        return value with { Owner = value.Owner with { }, Details = details, Mappings = mappings };
    }
    public static DomainValidationException Invalid() => new("invalid_supplier_change", "Choose exact supplier identity, approved verification policy and dated source-to-package mappings");
}
