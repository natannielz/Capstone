using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Ordering;
using B = UnitToko.Platform.Ordering.OrderCommercialPolicyBson;

namespace UnitToko.Tests.Ordering;

public sealed class OrderCommercialPolicyRuleTests
{
    internal static OrderCommercialRule Rule => new(new(OrderCommercialApplicability.Required, OrderCommercialApplicability.NotApplicable,
        OrderCommercialApplicability.Required, OrderCommercialApplicability.Required), Money.Parse("0.00"), "taxable_exclusive", 10m, null,
        "subtotal", "document", "half_even", new("receipt", "issue_date", 14, "calendar_days", [], [],
            "Synthetic reviewed numbering procedure", "Synthetic linked correction and reissue procedure", "Synthetic tax-document procedure"));
    private static OrderCommercialPolicyInput Input => new(new(ScopeNodeKind.Branch, 11, new(100, 101, 103, "IDR")), 2, 1,
        new(2, 1, 2, 1, 2, 1), 0, Rule, DateTimeOffset.Parse("2026-10-01T00:00:00Z"), DateTimeOffset.Parse("2026-11-01T00:00:00Z"));

    [Fact]
    public void Signed_line_and_document_rounding_use_exact_existing_minor_unit_arithmetic()
    {
        var lines = new[] { Money.Parse("0.05"), Money.Parse("0.05") };
        var document = OrderCommercialPolicyRules.Calculate(Rule, lines);
        var evenLines = OrderCommercialPolicyRules.Calculate(Rule with { TaxRounding = "line" }, lines);
        var upLines = OrderCommercialPolicyRules.Calculate(Rule with { TaxRounding = "line", Rounding = "half_up" }, lines);
        Assert.Equal(Money.Parse("0.10"), document.Subtotal); Assert.Equal(Money.Parse("0.01"), document.Tax);
        Assert.Equal(Money.Parse("0.00"), evenLines.Tax); Assert.Equal(Money.Parse("0.02"), upLines.Tax);
        Assert.Equal(Money.Parse("0.11"), document.Total); Assert.Equal(Money.Parse("0.12"), upLines.Total);
    }
    [Fact]
    public void Approved_charge_taxability_is_explicit_and_fixed_charge_is_counted_once()
    {
        var rule = Rule with { Charges = Money.Parse("0.05") }; var lines = new[] { Money.Parse("0.05") };
        var excluded = OrderCommercialPolicyRules.Calculate(rule, lines);
        var included = OrderCommercialPolicyRules.Calculate(rule with { TaxBase = "subtotal_and_charges" }, lines);
        Assert.Equal(Money.Parse("0.00"), excluded.Tax); Assert.Equal(Money.Parse("0.01"), included.Tax);
        Assert.Equal(Money.Parse("0.05"), included.Charges); Assert.Equal(Money.Parse("0.11"), included.Total);
        Assert.Equal(Money.Parse("0.02"), OrderCommercialPolicyRules.Calculate(rule with { TaxRounding = "line", TaxBase = "subtotal_and_charges", Rounding = "half_up" }, lines).Tax);
    }
    [Fact]
    public void Zero_tax_requires_deliberate_exemption_and_cannot_be_inferred_from_missing_policy()
    {
        var exemption = Rule with { TaxTreatment = "exempt", TaxRate = 0m, ExemptionReference = "Synthetic Finance exemption decision", Charges = Money.Parse("1.25") };
        var result = OrderCommercialPolicyRules.Calculate(exemption, [Money.Parse("123.45")]);
        Assert.Equal(Money.Parse("0.00"), result.Tax); Assert.Equal(Money.Parse("124.70"), result.Total);
        Assert.Throws<DomainValidationException>(() => OrderCommercialPolicyRules.CopyRule(exemption with { ExemptionReference = null }));
        Assert.Throws<DomainValidationException>(() => OrderCommercialPolicyRules.CopyRule(Rule with { TaxRate = 0m }));
        Assert.Throws<DomainValidationException>(() => OrderCommercialPolicyRules.CopyRule(exemption with { TaxRate = 1m }));
    }
    [Fact]
    public void Numeric_limits_and_explicit_applicability_are_enforced_before_any_record_can_exist()
    {
        foreach (var invalid in new[] { Rule with { TaxRate = -1m }, Rule with { TaxRate = 100.0001m }, Rule with { TaxRate = .00001m },
            Rule with { Applicability = Rule.Applicability with { CreditHold = 0 } }, Rule with { TaxTreatment = "" }, Rule with { TaxBase = "unknown" },
            Rule with { TaxRounding = "" }, Rule with { Rounding = "none" }, Rule with { Terms = Rule.Terms with { TermDays = 366 } },
            Rule with { Terms = Rule.Terms with { DayRule = "working_days", WorkingDays = [] } }, Rule with { Terms = Rule.Terms with { NumberingProcedure = "" } } })
            Assert.Throws<DomainValidationException>(() => OrderCommercialPolicyRules.CopyRule(invalid));
        Assert.Throws<DomainValidationException>(() => OrderCommercialPolicyRules.Calculate(Rule, []));
        Assert.Throws<DomainValidationException>(() => OrderCommercialPolicyRules.Calculate(Rule, Enumerable.Repeat(Money.Parse("1.00"), 101)));
        Assert.Throws<DomainValidationException>(() => OrderCommercialPolicyRules.Calculate(Rule, [new Money(DomainValues.MaximumMoney), Money.Parse("0.01")]));
        Assert.Throws<DomainValidationException>(() => OrderCommercialPolicyRules.Calculate(Rule with { TaxRate = 100m }, [new Money(DomainValues.MaximumMoney)]));
    }
    [Fact]
    public void Bundle_input_and_Bson_round_trip_preserve_exact_scope_rate_and_reviewed_terms_without_fake_policy_ids()
    {
        var input = OrderCommercialPolicyRules.Copy(Input with { Rule = Rule with { TaxRate = 12.3456m, Terms = Rule.Terms with {
            DayRule = "working_days", WorkingDays = [DayOfWeek.Friday, DayOfWeek.Monday], Holidays = [new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 1)] } } });
        var serialized = B.Input(input); var restored = B.Input(serialized);
        Assert.Equal(serialized, B.Input(restored)); Assert.Equal(12.3456m, restored.Rule.TaxRate);
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Friday }, restored.Rule.Terms.WorkingDays.ToArray());
        Assert.Equal("IDR", restored.Target.Seller.Currency); Assert.Equal(103L, restored.Target.Seller.ChannelId);
        foreach (var invalid in new[] { Input with { Target = Input.Target with { Seller = Input.Target.Seller with { Currency = "USD" } } },
            Input with { Target = Input.Target with { Kind = ScopeNodeKind.Warehouse } }, Input with { ExpectedScopeVersion = 0 },
            Input with { SellerVersions = Input.SellerVersions with { ChannelProfileVersion = 0 } }, Input with { ValidUntil = Input.ValidFrom } })
            Assert.Throws<DomainValidationException>(() => OrderCommercialPolicyRules.Copy(invalid));
    }
}
