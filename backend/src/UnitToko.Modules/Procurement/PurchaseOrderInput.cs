using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Procurement;

public sealed record PurchaseOrderCommercialReference(string Kind, string Reference, DateOnly IssuedDate, DateTimeOffset ValidUntil);
public sealed record PurchaseOrderPaymentTerms(string Kind, string Description, Money RequiredAdvance, string BalanceTrigger, DateOnly? DueDate);
public sealed record PurchaseOrderLineInput(long RequestSkuId, int Packages, string SourceSystem, string SourceAccountReference,
    string ExternalItemCode, long DefinitionId, long DefinitionVersion, Money NetPricePerPackage, Money TaxAmount, Money SupplierChargeAmount);
public sealed record PurchaseOrderInput(ProcurementLocation Location, long RequestId, long RequestVersion, long RequestDecisionId,
    long SupplierId, long SupplierVersionId, long SupplierVersion, DateOnly BusinessDate, string Currency,
    PurchaseOrderCommercialReference CommercialReference, string? ExternalOrderReference, PurchaseOrderPaymentTerms PaymentTerms,
    ImmutableArray<PurchaseOrderLineInput> Lines);
public enum PurchaseOrderDecision { Approve, Reject }
public sealed record PurchaseOrderTotals(Money Net, Money Tax, Money SupplierCharges, Money Total);

public static class PurchaseOrderRules
{
    public static void Text(string? value, int maximum, bool optional = false) {
        if (optional && value is null) return;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > maximum || value.Any(char.IsControl)) throw Invalid();
    }
    public static PurchaseOrderInput Copy(PurchaseOrderInput value) {
        if (value is null || value.RequestId <= 0 || value.RequestVersion != 1 || value.RequestDecisionId <= 0 || value.SupplierId <= 0 ||
            value.SupplierVersionId <= 0 || value.SupplierVersion <= 0 || value.BusinessDate == DateOnly.MinValue || value.Currency != "IDR" ||
            value.Lines.IsDefault || value.Lines.Length is < 1 or > 100 || value.CommercialReference is null || value.PaymentTerms is null) throw Invalid();
        ProcurementRequestRules.Location(value.Location); Text(value.ExternalOrderReference, 128, true);
        var reference = value.CommercialReference;
        if (reference.Kind is not ("quote" or "order" or "invoice") || reference.IssuedDate == DateOnly.MinValue || reference.IssuedDate > value.BusinessDate ||
            reference.ValidUntil.Offset != TimeSpan.Zero || reference.ValidUntil.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            AccountingCalendar.BusinessDate(reference.ValidUntil) < reference.IssuedDate) throw Invalid();
        Text(reference.Reference, 128);
        var lines = value.Lines.Select(line => {
            if (line is null || line.RequestSkuId <= 0 || line.DefinitionId <= 0 || line.DefinitionVersion <= 0 || line.Packages is < 1 or > DomainValues.MaximumLineQuantity ||
                line.SourceSystem is not ("omi" or "s_mart" or "supplier")) throw Invalid();
            Text(line.SourceAccountReference, 128); Text(line.ExternalItemCode, 128);
            _ = new Money(line.NetPricePerPackage.Value); _ = new Money(line.TaxAmount.Value); _ = new Money(line.SupplierChargeAmount.Value); return line with { };
        }).OrderBy(line => line.RequestSkuId).ToImmutableArray();
        if (lines.Select(line => line.RequestSkuId).Distinct().Count() != lines.Length) throw Invalid();
        var copy = value with { Location = value.Location with { }, CommercialReference = reference with { }, PaymentTerms = value.PaymentTerms with { }, Lines = lines };
        var totals = Totals(copy); var terms = copy.PaymentTerms; Text(terms.Description, 1000); _ = new Money(terms.RequiredAdvance.Value);
        if (terms.Kind is not ("full_before_dispatch" or "deposit" or "on_receipt" or "credit") || terms.BalanceTrigger is not ("before_dispatch" or "on_receipt" or "due_date") ||
            (terms.BalanceTrigger == "due_date" ? terms.DueDate is null || terms.DueDate < value.BusinessDate : terms.DueDate is not null)) throw Invalid();
        if (terms.Kind == "full_before_dispatch" && (terms.RequiredAdvance != totals.Total || terms.BalanceTrigger != "before_dispatch") ||
            terms.Kind == "deposit" && (terms.RequiredAdvance.Value <= 0 || terms.RequiredAdvance.Value >= totals.Total.Value) ||
            terms.Kind == "on_receipt" && (terms.RequiredAdvance.Value != 0 || terms.BalanceTrigger != "on_receipt") ||
            terms.Kind == "credit" && (terms.RequiredAdvance.Value != 0 || terms.BalanceTrigger != "due_date")) throw Invalid();
        return copy;
    }
    public static PurchaseOrderTotals Totals(PurchaseOrderInput input) {
        var net = new Money(0); var tax = new Money(0); var charges = new Money(0);
        foreach (var line in input.Lines) { net = Money.Add(net, Money.Multiply(line.NetPricePerPackage, line.Packages)); tax = Money.Add(tax, line.TaxAmount); charges = Money.Add(charges, line.SupplierChargeAmount); }
        return new(net, tax, charges, Money.Add(Money.Add(net, tax), charges));
    }
    public static DomainValidationException Invalid() => new("invalid_purchase_order", "Review the actual request, supplier mapping, commercial amounts and payment terms");
}
