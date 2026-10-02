using System.Collections.Immutable;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Procurement;

public sealed record ProcurementLocation(long OrganizationId, long ShopId, long WarehouseId, long ChannelId);
public enum ProcurementRoute { OmiRegular, Ddo, Smart, PasarKering }
public enum ProcurementRequestKind { StockNeed, IncomingNotice, PurchaseRequest }
public enum ProcurementRequestDecision { Approve, Reject }
public sealed record ProcurementRequestLineInput(long SkuId, long ExpectedSkuRevision, long DefinitionId, long DefinitionVersion, int Packages, string? Specification);
public sealed record ProcurementRequestInput(ProcurementLocation Location, ProcurementRoute Route, ProcurementRequestKind Kind,
    DateOnly BusinessDate, string? ExternalReference, string Reason, ImmutableArray<ProcurementRequestLineInput> Lines);

public static class ProcurementRequestRules
{
    public static ProcurementLocation Location(ProcurementLocation value) {
        if (value is null || value.OrganizationId <= 0 || value.ShopId <= 0 || value.WarehouseId <= 0 || value.ChannelId <= 0) throw Invalid();
        return value;
    }
    public static GrantScope Scope(ProcurementLocation value) { Location(value); return new(GrantScopeKind.FulfillmentWarehouse,
        OrganizationId: value.OrganizationId, ShopId: value.ShopId, WarehouseId: value.WarehouseId, ChannelId: value.ChannelId); }
    public static void Text(string? value, int maximum, bool optional = false) {
        if (optional && value is null) return;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > maximum || value.Any(char.IsControl)) throw Invalid();
    }
    public static ProcurementRequestInput Copy(ProcurementRequestInput value) {
        if (value is null || !Enum.IsDefined(value.Route) || !Enum.IsDefined(value.Kind) || value.BusinessDate == DateOnly.MinValue ||
            value.Lines.IsDefault || value.Lines.Length is < 1 or > 100) throw Invalid();
        Location(value.Location); Text(value.Reason, 1000); Text(value.ExternalReference, 128, true);
        if (value.Route == ProcurementRoute.OmiRegular ? value.Kind is not (ProcurementRequestKind.StockNeed or ProcurementRequestKind.IncomingNotice) : value.Kind != ProcurementRequestKind.PurchaseRequest) throw Invalid();
        if (value.Kind == ProcurementRequestKind.IncomingNotice && value.ExternalReference is null) throw Invalid();
        var lines = value.Lines.Select(line => {
            if (line is null || line.SkuId <= 0 || line.ExpectedSkuRevision <= 0 || line.DefinitionId <= 0 || line.DefinitionVersion <= 0 ||
                line.Packages is < 1 or > DomainValues.MaximumLineQuantity) throw Invalid();
            Text(line.Specification, 1000, true); return line with { };
        }).OrderBy(line => line.SkuId).ToImmutableArray();
        if (lines.Select(line => line.SkuId).Distinct().Count() != lines.Length) throw Invalid();
        return value with { Location = value.Location with { }, Lines = lines };
    }
    public static DomainValidationException Invalid() => new("invalid_procurement_request", "Choose an explicit route, location, date, reason and catalog packages");
}
