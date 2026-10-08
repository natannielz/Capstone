using UnitToko.Modules.Contracts;
using UnitToko.Modules.Operations;

namespace UnitToko.Tests.Operations;

public sealed class OperationalAnomalyRuleTests
{
    [Fact]
    public void Same_quantity_has_explainable_results_under_two_explicit_synthetic_policies()
    {
        AnomalyOrderLine[] request = [new("SKU/definition-1", 12, 5000m, 60000m, "IDR")];
        var first = OperationalAnomalyRules.Order(request, new("synthetic-review-1", "TEST-APPROVAL-1", 10, null, null, null));
        var second = OperationalAnomalyRules.Order(request, new("synthetic-review-2", "TEST-APPROVAL-2", 20, null, null, null));
        Assert.Equal("flagged", first.Single(x => x.Category == "quantity").State);
        Assert.Equal("within_policy", second.Single(x => x.Category == "quantity").State);
        Assert.Contains("10 paket", first.Single(x => x.Category == "quantity").Explanation);
        Assert.Contains("synthetic-review-2", second.Single(x => x.Category == "quantity").Explanation);
        Assert.Equal(new AnomalyOrderLine("SKU/definition-1", 12, 5000m, 60000m, "IDR"), request[0]);
    }

    [Fact]
    public void No_policy_or_wrong_currency_never_means_normal()
    {
        var absent = OperationalAnomalyRules.Order([new("SKU", 10, 5, 50, "IDR")], null);
        Assert.Equal(2, absent.Count(x => x.State == "policy_needed"));
        Assert.Equal("consistent", absent.Single(x => x.Code == "price_extension").State);
        var other = OperationalAnomalyRules.Refund(new(20, 20, "IDR"), new("synthetic", "TEST", null, null, "USD", 10));
        Assert.Equal("policy_needed", other.Single(x => x.Code == "refund_limit").State);
        Assert.Equal("unavailable", OperationalAnomalyRules.AdjustmentUnavailable().State);
    }

    [Fact]
    public void Invariants_flag_exact_inconsistency_and_threshold_is_strictly_greater()
    {
        var signals = OperationalAnomalyRules.Order([new("SKU", 10, 5, 49, "IDR")], new("synthetic", "TEST", 10, 5, "IDR", null));
        Assert.Equal("flagged", signals.Single(x => x.Code == "price_extension").State);
        Assert.All(signals.Where(x => x.Code != "price_extension"), x => Assert.Equal("within_policy", x.State));
        Assert.Equal("flagged", OperationalAnomalyRules.Refund(new(21, 20, "IDR"), null)[0].State);
        Assert.Throws<DomainValidationException>(() => OperationalAnomalyRules.Validate(new("synthetic", "", 1, null, null, null)));
        Assert.Throws<DomainValidationException>(() => OperationalAnomalyRules.Validate(new("synthetic", "TEST", 0, null, null, null)));
    }
}
