using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Ordering;

public enum OrderCommercialApplicability { Required = 1, NotApplicable = 2 }
public enum OrderCommercialPolicyDecision { Approve, Reject }
public sealed record OrderCommercialApplicabilityRule(OrderCommercialApplicability Budget, OrderCommercialApplicability CostCenter,
    OrderCommercialApplicability Spending, OrderCommercialApplicability CreditHold);
public sealed record OrderCommercialPolicyTarget(ScopeNodeKind Kind, long Id, OperationalBookKey Seller);
public sealed record OrderCommercialSellerVersions(long OrganizationScopeVersion, long OrganizationProfileVersion,
    long ShopScopeVersion, long ShopProfileVersion, long ChannelScopeVersion, long ChannelProfileVersion);
public sealed record OrderCommercialInvoiceTerms(string Grouping, string TermOrigin, int TermDays, string DayRule,
    ImmutableArray<DayOfWeek> WorkingDays, ImmutableArray<DateOnly> Holidays, string NumberingProcedure,
    string ReissueProcedure, string TaxDocumentProcess);
public sealed record OrderCommercialRule(OrderCommercialApplicabilityRule Applicability, Money Charges, string TaxTreatment,
    decimal TaxRate, string? ExemptionReference, string TaxBase, string TaxRounding, string Rounding, OrderCommercialInvoiceTerms Terms);
public sealed record OrderCommercialPolicyInput(OrderCommercialPolicyTarget Target, long ExpectedScopeVersion,
    long ExpectedProfileVersion, OrderCommercialSellerVersions SellerVersions, long ExpectedPolicyVersion,
    OrderCommercialRule Rule, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil);
public sealed record OrderCommercialTotals(Money Subtotal, Money Tax, Money Charges, Money Total);

/// <summary>Explicit approval data and exact calculation only. Applicability supplies
/// no budget balance, cost-center assignment, spending approval or credit capacity.</summary>
public static class OrderCommercialPolicyRules
{
    public static void RequireTarget(OrderCommercialPolicyTarget target)
    {
        if (target is null || target.Id <= 0 || target.Kind is not (ScopeNodeKind.Branch or ScopeNodeKind.Division)) throw Invalid();
        AccountingBookSetupRules.Key(target.Seller);
    }
    public static OrderCommercialPolicyInput Copy(OrderCommercialPolicyInput input)
    {
        if (input is null || input.SellerVersions is null || input.ExpectedScopeVersion is <= 0 or >= long.MaxValue ||
            input.ExpectedProfileVersion is <= 0 or >= long.MaxValue || input.ExpectedPolicyVersion < 0 || input.ExpectedPolicyVersion >= long.MaxValue - 1) throw Invalid();
        RequireTarget(input.Target);
        foreach (var version in new[] { input.SellerVersions.OrganizationScopeVersion, input.SellerVersions.OrganizationProfileVersion,
            input.SellerVersions.ShopScopeVersion, input.SellerVersions.ShopProfileVersion, input.SellerVersions.ChannelScopeVersion, input.SellerVersions.ChannelProfileVersion })
            if (version is <= 0 or >= long.MaxValue) throw Invalid();
        RequireTerms(input.Rule, input.ValidFrom, input.ValidUntil);
        return input with { Target = input.Target with { Seller = input.Target.Seller with { } }, SellerVersions = input.SellerVersions with { },
            Rule = CopyRule(input.Rule), ValidFrom = input.ValidFrom.ToUniversalTime(), ValidUntil = input.ValidUntil.ToUniversalTime() };
    }
    public static void RequireTerms(OrderCommercialRule rule, DateTimeOffset validFrom, DateTimeOffset validUntil)
    {
        if (validFrom <= DateTimeOffset.MinValue || validFrom >= validUntil || validFrom.UtcTicks % TimeSpan.TicksPerMillisecond != 0 ||
            validUntil.UtcTicks % TimeSpan.TicksPerMillisecond != 0) throw Invalid();
        _ = CopyRule(rule);
    }
    public static OrderCommercialRule CopyRule(OrderCommercialRule rule)
    {
        if (rule?.Applicability is not { } applicability || rule.Terms is not { } terms) throw Invalid();
        foreach (var value in new[] { applicability.Budget, applicability.CostCenter, applicability.Spending, applicability.CreditHold })
            if (!Enum.IsDefined(value)) throw Invalid();
        _ = new Money(rule.Charges.Value);
        var scale = (decimal.GetBits(rule.TaxRate)[3] >> 16) & 0xff;
        if (scale > 4 || rule.TaxRate is < 0 or > 100 || rule.TaxTreatment is not ("taxable_exclusive" or "exempt") ||
            rule.TaxTreatment == "taxable_exclusive" && (rule.TaxRate <= 0 || rule.ExemptionReference is not null) ||
            rule.TaxTreatment == "exempt" && (rule.TaxRate != 0 || rule.ExemptionReference is null) ||
            rule.TaxBase is not ("subtotal" or "subtotal_and_charges") || rule.TaxRounding is not ("line" or "document") ||
            rule.Rounding is not ("half_even" or "half_up")) throw Invalid();
        if (rule.ExemptionReference is { } exemption) Text(exemption);
        // These are the existing InvoiceAdmission choices and bounds. Calendar,
        // numbering, document templates and issuance remain their owning workflows.
        if (terms.Grouping is not ("receipt" or "buying_branch_period_channel" or "buying_org_period_channel") ||
            terms.TermOrigin is not ("issue_date" or "earliest_receipt" or "latest_receipt") || terms.TermDays is < 0 or > 365 ||
            terms.DayRule is not ("calendar_days" or "working_days") || terms.WorkingDays.IsDefault || terms.WorkingDays.Any(day => !Enum.IsDefined(day)) ||
            terms.WorkingDays.Distinct().Count() != terms.WorkingDays.Length || terms.DayRule == "working_days" && terms.WorkingDays.IsEmpty ||
            terms.Holidays.IsDefault || terms.Holidays.Length > 730 || terms.Holidays.Any(date => date == default) || terms.Holidays.Distinct().Count() != terms.Holidays.Length) throw Invalid();
        Text(terms.NumberingProcedure); Text(terms.ReissueProcedure); Text(terms.TaxDocumentProcess);
        return rule with { Applicability = applicability with { }, Terms = terms with {
            WorkingDays = [.. terms.WorkingDays.Order()], Holidays = [.. terms.Holidays.Order()] } };
    }
    public static OrderCommercialTotals Calculate(OrderCommercialRule rule, IEnumerable<Money> lineSubtotals)
    {
        rule = CopyRule(rule);
        if (lineSubtotals is null) throw Invalid(); var lines = lineSubtotals.Take(101).ToArray();
        if (lines.Length is < 1 or > 100) throw Invalid();
        var subtotal = new Money(0m); foreach (var line in lines) subtotal = Money.Add(subtotal, new Money(line.Value));
        var tax = new Money(0m);
        if (rule.TaxTreatment == "taxable_exclusive") {
            // A rate of 1% is 10000 of the 1000000 exact basis units. Reuse the
            // existing approved two-decimal rounding arithmetic, without floats.
            Money Tax(Money basis) => InvoicePolicy.AcceptedNet(basis, 1000000, checked((long)(rule.TaxRate * 10000m)), rule.Rounding);
            if (rule.TaxRounding == "document") tax = Tax(rule.TaxBase == "subtotal_and_charges" ? Money.Add(subtotal, rule.Charges) : subtotal);
            else {
                foreach (var line in lines) tax = Money.Add(tax, Tax(line));
                if (rule.TaxBase == "subtotal_and_charges") tax = Money.Add(tax, Tax(rule.Charges));
            }
        }
        return new(subtotal, tax, rule.Charges, Money.Add(Money.Add(subtotal, tax), rule.Charges));
    }
    private static void Text(string value) {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() != value || value.Length > 1000 || value.Any(char.IsControl)) throw Invalid();
    }
    public static DomainValidationException Invalid() => new("order_commercial_policy_invalid", "Explicit scoped commercial, tax, applicability and invoice terms with finite validity are required");
}
