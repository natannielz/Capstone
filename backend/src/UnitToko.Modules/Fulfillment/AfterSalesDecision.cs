using System.Collections.Immutable;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Fulfillment;
public sealed record AfterSalesDecisionInput(string Action, string PublicExplanation, ImmutableArray<AfterSalesCaseLine> Lines);
public static class AfterSalesDecisionPolicy
{
    public static AfterSalesDecisionInput Copy(AfterSalesDecisionInput input) {
        if (input is null || input.Action is not ("authorize_return" or "reject") || !ReceivingAccountPolicy.Text(input.PublicExplanation, 1000) || input.Lines.IsDefault) throw Invalid();
        if (input.Action == "reject") { if (!input.Lines.IsEmpty) throw Invalid(); return input with { Lines = [] }; }
        return input with { Lines = AfterSalesCasePolicy.CopyLines(input.Lines) };
    }
    public static void RequireRequested(AfterSalesDecisionInput input, ImmutableArray<AfterSalesCaseLine> requested) {
        input = Copy(input); requested = AfterSalesCasePolicy.CopyLines(requested);
        foreach (var line in input.Lines) {
            var original = requested.SingleOrDefault(item => item.ReceiptId == line.ReceiptId && item.SkuId == line.SkuId);
            if (original is null || line.ReportedBaseUnits > original.ReportedBaseUnits) throw Invalid();
        }
    }
    public static string State(AfterSalesDecisionInput input) => Copy(input).Action == "authorize_return" ? "return_authorized" : "rejected";
    public static DomainValidationException Invalid() => new("invalid_after_sales_decision", "Review the original case quantities and explain the manual decision");
}
