using System.Collections.Immutable;
using System.Text.Json.Serialization;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Procurement;

public enum SupplierDocumentKind { Invoice, Sph }
public sealed record SupplierDocumentLineInput(string SourceLineReference, string Description, string? SourceSystem,
    string? SourceAccount, string? SourceCode, int? Packages, Money? NetPricePerPackage, Money? TaxAmount, Money? ChargeAmount,
    string? RelatedInvoiceReference, Money? StatementAmount);
public sealed record SupplierDocumentInput(ProcurementLocation Location, long AnchorOrderId, long AnchorOrderVersion, long AnchorOrderApprovalId,
    SupplierDocumentKind Kind, string DocumentReference, DateOnly? DocumentDate, string Currency, Money? DocumentTotal,
    ImmutableArray<SupplierDocumentLineInput> Lines, string? Discrepancy);
public sealed record SupplierDocumentMatchLineInput(int DocumentLineNumber, int? Packages, long? InspectionId, int? InspectionLineNumber,
    long? InvoiceMatchId, int? InvoiceMatchLineNumber, Money? NetAmount, Money? TaxAmount, Money? ChargeAmount, Money? StatementAmount, string? Discrepancy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? QualityAllocationId = null);
public sealed record SupplierDocumentMatchInput(long DocumentId, long DocumentRevision, SupplierDocumentInput ReviewedInput, long? OcrResultId, ImmutableArray<SupplierDocumentMatchLineInput> Lines);

public static class SupplierDocumentRules
{
    public static void Text(string? value, int maximum, bool optional = false) {
        if (optional && value is null) return;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > maximum || value.Any(char.IsControl)) throw Invalid();
    }
    private static void Amount(Money? value) { if (value is { } amount) _ = new Money(amount.Value); }
    public static SupplierDocumentInput Copy(SupplierDocumentInput input) {
        if (input is null || input.AnchorOrderId <= 0 || input.AnchorOrderVersion != 1 || input.AnchorOrderApprovalId <= 0 ||
            !Enum.IsDefined(input.Kind) || input.DocumentDate is { Year: < 2000 } || input.Currency != "IDR" || input.Lines.IsDefaultOrEmpty || input.Lines.Length > 100) throw Invalid();
        ProcurementRequestRules.Location(input.Location); Text(input.DocumentReference, 128); Text(input.Discrepancy, 1000, true); Amount(input.DocumentTotal);
        var lines = input.Lines.Select(line => {
            if (line is null || line.Packages is < 1 or > DomainValues.MaximumLineQuantity) throw Invalid();
            Text(line.SourceLineReference, 128); Text(line.Description, 512); Text(line.SourceSystem, 64, true); Text(line.SourceAccount, 128, true); Text(line.SourceCode, 128, true); Text(line.RelatedInvoiceReference, 128, true);
            Amount(line.NetPricePerPackage); Amount(line.TaxAmount); Amount(line.ChargeAmount); Amount(line.StatementAmount);
            if (input.Kind == SupplierDocumentKind.Invoice ? line.RelatedInvoiceReference is not null || line.StatementAmount is not null :
                line.Packages is not null || line.NetPricePerPackage is not null || line.TaxAmount is not null || line.ChargeAmount is not null) throw Invalid();
            return line with { };
        }).OrderBy(line => line.SourceLineReference, StringComparer.Ordinal).ToImmutableArray();
        if (lines.Select(line => line.SourceLineReference).Distinct(StringComparer.Ordinal).Count() != lines.Length) throw Invalid();
        var result = input with { Location = input.Location with { }, Lines = lines };
        if (!ExactTotal(result) && result.Discrepancy is null) throw Invalid();
        return result;
    }
    public static bool ExactTotal(SupplierDocumentInput input) {
        if (input.DocumentDate is null || input.DocumentTotal is null) return false;
        try {
            decimal total = 0;
            foreach (var line in input.Lines) {
                if (input.Kind == SupplierDocumentKind.Sph) { if (line.StatementAmount is not { } amount) return false; total = checked(total + amount.Value); }
                else {
                    if (line.Packages is not { } packages || line.NetPricePerPackage is not { } net || line.TaxAmount is not { } tax || line.ChargeAmount is not { } charge) return false;
                    total = checked(total + net.Value * packages + tax.Value + charge.Value);
                }
            }
            return total <= DomainValues.MaximumMoney && total == input.DocumentTotal.Value.Value;
        } catch (OverflowException) { return false; }
    }
    public static SupplierDocumentMatchInput Copy(SupplierDocumentMatchInput input) {
        if (input is null || input.DocumentId <= 0 || input.DocumentRevision != 1 || input.OcrResultId is <= 0 || input.Lines.IsDefaultOrEmpty || input.Lines.Length > 100) throw Invalid();
        var reviewed = Copy(input.ReviewedInput);
        var lines = input.Lines.Select(line => {
            if (line is null || line.DocumentLineNumber < 1 || line.DocumentLineNumber > reviewed.Lines.Length || line.Packages is < 1 or > DomainValues.MaximumLineQuantity ||
                line.InspectionId is <= 0 || line.InvoiceMatchId is <= 0 || line.QualityAllocationId is <= 0 ||
                line.InspectionLineNumber is < 1 or > 100 || line.InvoiceMatchLineNumber is < 1 or > 100) throw Invalid();
            Text(line.Discrepancy, 1000, true); Amount(line.NetAmount); Amount(line.TaxAmount); Amount(line.ChargeAmount); Amount(line.StatementAmount);
            var invoice = reviewed.Kind == SupplierDocumentKind.Invoice && line.Packages is not null && line.InspectionId is not null && line.InspectionLineNumber is not null && line.InvoiceMatchId is null && line.InvoiceMatchLineNumber is null &&
                line.NetAmount is not null && line.TaxAmount is not null && line.ChargeAmount is not null && line.StatementAmount is null;
            var statement = reviewed.Kind == SupplierDocumentKind.Sph && line.Packages is null && line.InspectionId is null && line.InspectionLineNumber is null && line.InvoiceMatchId is not null && line.InvoiceMatchLineNumber is not null &&
                line.NetAmount is null && line.TaxAmount is null && line.ChargeAmount is null && line.StatementAmount is { Value: > 0 };
            var unresolved = line.Discrepancy is not null && line.InspectionId is null && line.InspectionLineNumber is null && line.InvoiceMatchId is null && line.InvoiceMatchLineNumber is null;
            if (!(unresolved || line.Discrepancy is null && (invoice || statement)) ||
                line.QualityAllocationId is not null && (!invoice || line.Discrepancy is not null)) throw Invalid();
            return line with { };
        }).OrderBy(line => line.DocumentLineNumber).ThenBy(line => line.InspectionId).ThenBy(line => line.InspectionLineNumber).ThenBy(line => line.InvoiceMatchId).ThenBy(line => line.InvoiceMatchLineNumber).ThenBy(line => line.QualityAllocationId).ToImmutableArray();
        if (lines.GroupBy(line => (line.DocumentLineNumber, line.InspectionId, line.InspectionLineNumber, line.InvoiceMatchId,
                line.InvoiceMatchLineNumber, line.QualityAllocationId)).Any(group => group.Count() != 1)) throw Invalid();
        return input with { ReviewedInput = reviewed, Lines = lines };
    }
    public static void SameIdentity(SupplierDocumentInput original, SupplierDocumentInput reviewed) {
        if (original.Location != reviewed.Location || original.AnchorOrderId != reviewed.AnchorOrderId || original.AnchorOrderVersion != reviewed.AnchorOrderVersion || original.AnchorOrderApprovalId != reviewed.AnchorOrderApprovalId ||
            original.Kind != reviewed.Kind || original.DocumentReference != reviewed.DocumentReference || original.Currency != reviewed.Currency ||
            !original.Lines.Select(line => line.SourceLineReference).SequenceEqual(reviewed.Lines.Select(line => line.SourceLineReference))) throw Invalid();
        // A human can transcribe unknown facts; changing a known original requires later governed correction.
        if (original.DocumentDate is not null && original.DocumentDate != reviewed.DocumentDate || original.DocumentTotal is not null && original.DocumentTotal != reviewed.DocumentTotal) throw Invalid();
        for (var i = 0; i < original.Lines.Length; i++) {
            var a = original.Lines[i]; var b = reviewed.Lines[i];
            if (a.Description != b.Description || a.SourceSystem is not null && a.SourceSystem != b.SourceSystem || a.SourceAccount is not null && a.SourceAccount != b.SourceAccount || a.SourceCode is not null && a.SourceCode != b.SourceCode ||
                a.Packages is not null && a.Packages != b.Packages || a.NetPricePerPackage is not null && a.NetPricePerPackage != b.NetPricePerPackage || a.TaxAmount is not null && a.TaxAmount != b.TaxAmount ||
                a.ChargeAmount is not null && a.ChargeAmount != b.ChargeAmount || a.StatementAmount is not null && a.StatementAmount != b.StatementAmount || a.RelatedInvoiceReference is not null && a.RelatedInvoiceReference != b.RelatedInvoiceReference) throw Invalid();
        }
    }
    public static DomainValidationException Invalid() => new("invalid_supplier_document", "Record exact supplier document facts and unresolved differences; no stock or financial clearance is inferred");
}
