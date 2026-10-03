using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Procurement;

/// <summary>Overlapping condition observations; these counts must never be summed to infer healthy or releasable stock.</summary>
public sealed record ProcurementInspectionLineInput(long RequestSkuId, long DefinitionId, long DefinitionVersion,
    int DeclaredPackages, int ReceivedPackages, int DamagedPackages, int ExpiredPackages, int OpenedPackages,
    string? LotCode, DateOnly? ExpiryDate, string? PutawayReference, string? Discrepancy);
public sealed record ProcurementInspectionInput(ProcurementLocation Location, long NoticeId, long IntakeId, DateOnly InspectionDate,
    string InspectionReference, int InspectedContainers, ImmutableArray<ProcurementInspectionLineInput> Lines, string? Discrepancy);

public static class ProcurementInspectionRules
{
    public static void Text(string? value, int maximum, bool optional = false) {
        if (optional && value is null) return;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > maximum || value.Any(char.IsControl)) throw Invalid();
    }
    public static ProcurementInspectionInput Copy(ProcurementInspectionInput input) {
        if (input is null || input.NoticeId <= 0 || input.IntakeId <= 0 || input.InspectionDate.Year < 2000 ||
            input.InspectedContainers is < 1 or > DomainValues.MaximumLineQuantity || input.Lines.IsDefaultOrEmpty || input.Lines.Length > 100) throw Invalid();
        ProcurementRequestRules.Location(input.Location); Text(input.InspectionReference, 128); Text(input.Discrepancy, 1000, true);
        var lines = input.Lines.Select(line => {
            if (line is null || line.RequestSkuId <= 0 || line.DefinitionId <= 0 || line.DefinitionVersion is <= 0 or >= long.MaxValue - 1 ||
                new[] { line.DeclaredPackages, line.ReceivedPackages, line.DamagedPackages, line.ExpiredPackages, line.OpenedPackages }.Any(value => value is < 0 or > DomainValues.MaximumLineQuantity) ||
                (long)line.DeclaredPackages + line.ReceivedPackages == 0 || line.DamagedPackages > line.ReceivedPackages ||
                line.ExpiredPackages > line.ReceivedPackages || line.OpenedPackages > line.ReceivedPackages || line.ExpiryDate is { Year: < 2000 }) throw Invalid();
            Text(line.LotCode, 128, true); Text(line.PutawayReference, 256, true); Text(line.Discrepancy, 1000, true);
            if ((line.DeclaredPackages != line.ReceivedPackages || line.DamagedPackages > 0 || line.ExpiredPackages > 0 || line.OpenedPackages > 0) && line.Discrepancy is null) throw Invalid();
            if (line.ExpiredPackages > 0 && line.ExpiryDate is { } expiry && expiry >= input.InspectionDate) throw Invalid();
            return line with { };
        }).OrderBy(line => line.RequestSkuId).ThenBy(line => line.LotCode, StringComparer.Ordinal).ThenBy(line => line.ExpiryDate).ThenBy(line => line.PutawayReference, StringComparer.Ordinal).ToImmutableArray();
        if (lines.GroupBy(line => (line.RequestSkuId, line.LotCode, line.ExpiryDate, line.PutawayReference)).Any(group => group.Count() != 1)) throw Invalid();
        return input with { Location = input.Location with { }, Lines = lines };
    }
    public static DomainValidationException Invalid() => new("invalid_procurement_inspection", "Record actual container and package observations; goods remain quarantined pending matching and putaway");
}
