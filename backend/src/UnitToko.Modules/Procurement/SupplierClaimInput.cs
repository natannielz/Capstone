using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Procurement;

public enum SupplierClaimBasis { PhysicalReceived, ShortageObserved }
public enum SupplierClaimResolution { Replacement, Credit, CashRefund, Investigation }
public enum SupplierClaimDecision { Approve, Reject }
public sealed record SupplierClaimLineInput(SupplierClaimBasis Basis, long InspectionId, int InspectionLineNumber, int Packages, string Issue,
    long? InvoiceDocumentId, int? InvoiceDocumentLineNumber, long? InvoiceMatchId, int? InvoiceMatchLineNumber);
public sealed record SupplierClaimInput(ProcurementLocation Location, long OrderId, long OrderVersion, long OrderApprovalId,
    string ClaimReference, DateOnly BusinessDate, DateOnly ResponseDueOn, string DeadlineBasisReference,
    SupplierClaimResolution RequestedResolution, string Reason, ImmutableArray<SupplierClaimLineInput> Lines);
public sealed record SupplierClaimDecisionInput(SupplierClaimDecision Decision, string Explanation);

public static class SupplierClaimRules
{
    public static void Text(string? value, int maximum) {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > maximum || value.Any(char.IsControl)) throw Invalid();
    }
    public static SupplierClaimInput Copy(SupplierClaimInput input) {
        if (input is null || input.OrderId <= 0 || input.OrderVersion != 1 || input.OrderApprovalId <= 0 || input.BusinessDate.Year < 2000 ||
            input.ResponseDueOn < input.BusinessDate || !Enum.IsDefined(input.RequestedResolution) || input.Lines.IsDefaultOrEmpty || input.Lines.Length > 100) throw Invalid();
        ProcurementRequestRules.Location(input.Location); Text(input.ClaimReference, 128); Text(input.DeadlineBasisReference, 256); Text(input.Reason, 1000);
        var lines = input.Lines.Select(line => {
            if (line is null || !Enum.IsDefined(line.Basis) || line.InspectionId <= 0 || line.InspectionLineNumber is < 1 or > 100 ||
                line.Packages is < 1 or > DomainValues.MaximumLineQuantity || line.Issue is not ("damaged" or "expired" or "opened" or "wrong_item" or "shortage" or "other") ||
                (line.Basis == SupplierClaimBasis.ShortageObserved) != (line.Issue == "shortage") ||
                line.InvoiceDocumentId is <= 0 || line.InvoiceDocumentLineNumber is < 1 or > 100 || line.InvoiceMatchId is <= 0 || line.InvoiceMatchLineNumber is < 1 or > 100 ||
                (line.InvoiceDocumentId is null) != (line.InvoiceDocumentLineNumber is null) || (line.InvoiceMatchId is null) != (line.InvoiceMatchLineNumber is null) ||
                line.InvoiceMatchId is not null && line.InvoiceDocumentId is null) throw Invalid();
            return line with { };
        }).OrderBy(line => line.InspectionId).ThenBy(line => line.InspectionLineNumber).ThenBy(line => line.Basis).ToImmutableArray();
        if (lines.GroupBy(line => (line.InspectionId, line.InspectionLineNumber, line.Basis)).Any(group => group.Count() != 1)) throw Invalid();
        return input with { Location = input.Location with { }, Lines = lines };
    }
    public static SupplierClaimDecisionInput Copy(SupplierClaimDecisionInput input) {
        if (input is null || !Enum.IsDefined(input.Decision)) throw Invalid(); Text(input.Explanation, 1000); return input with { };
    }
    public static DomainValidationException Invalid() => new("invalid_supplier_claim", "Select genuine inspected packages or observed shortages and record an explicit supplier-resolution request");
}
