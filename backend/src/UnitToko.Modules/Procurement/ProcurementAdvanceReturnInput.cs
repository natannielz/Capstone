using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Procurement;

public sealed record ProcurementAdvanceReturnLineInput(long SettlementId, Money Amount);
public sealed record ProcurementAdvanceReturnInput(ProcurementLocation Location, long ReceiptId,
    long PolicyId, long PolicyRevision, DateOnly PostingDate, string Reason,
    ImmutableArray<ProcurementAdvanceReturnLineInput> Lines);
public enum ProcurementAdvanceReturnDecision { Approve, Reject }

public static class ProcurementAdvanceReturnRules
{
    public const int MaximumLines = 100;

    public static ProcurementAdvanceReturnInput Copy(ProcurementAdvanceReturnInput input) => Validate(input).Input;
    public static Money Total(ProcurementAdvanceReturnInput input) => Validate(input).Total;

    private static (ProcurementAdvanceReturnInput Input, Money Total) Validate(ProcurementAdvanceReturnInput input)
    {
        if (input is null || input.ReceiptId <= 0 || input.PolicyId <= 0 ||
            input.PolicyRevision is <= 0 or >= long.MaxValue - 1 || input.PostingDate.Year < 2000 ||
            input.Lines.IsDefaultOrEmpty || input.Lines.Length > MaximumLines) throw Invalid();
        ProcurementRequestRules.Location(input.Location);
        ProcurementRequestRules.Text(input.Reason, 1000);
        AccountingCalendar.ValidateMonth($"{input.PostingDate.Year:D4}-{input.PostingDate.Month:D2}");

        var settlements = new HashSet<long>();
        var total = new Money(0);
        foreach (var line in input.Lines)
        {
            if (line is null || line.SettlementId <= 0 || line.Amount.Value <= 0 || !settlements.Add(line.SettlementId))
                throw Invalid();
            total = Money.Add(total, new Money(line.Amount.Value));
        }

        // Verified sources, not caller fields, must establish one supplier/PO and whole-receipt equality.
        var copy = input with { Location = input.Location with { },
            Lines = input.Lines.OrderBy(line => line.SettlementId).Select(line => line with { }).ToImmutableArray() };
        return (copy, total);
    }

    public static DomainValidationException Invalid(string code = "procurement_advance_return_invalid") =>
        new(code, "Review one verified bank receipt and bounded amounts from distinct supplier advance payouts");
}
