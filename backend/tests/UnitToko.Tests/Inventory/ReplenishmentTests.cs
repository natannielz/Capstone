using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;

namespace UnitToko.Tests.Inventory;

public sealed class ReplenishmentTests
{
    private static readonly DateTimeOffset Observed = new(2026, 10, 8, 8, 0, 0, TimeSpan.Zero);
    private static readonly ReplenishmentInputs Input = new(3.5m, 4, 3, 6, 1, 6);
    private static readonly ReplenishmentStock Stock = new(true, 10, 3, 2);

    [Fact]
    public void Conserves_commitments_and_rounds_purchase_without_subtracting_reservations_twice()
    {
        var advice = ReplenishmentPolicy.Evaluate(Input, Stock, true, [new("po:1", 2, Observed.AddDays(3), true)], Observed);
        Assert.Equal(ReplenishmentPolicy.Version, advice.RuleVersion);
        Assert.Equal(Observed, advice.ObservedAt);
        Assert.Equal(Observed.AddDays(4), advice.Horizon);
        Assert.Equal(5L, advice.AvailableAfterCommitments);
        Assert.True(advice.ThresholdAlert);
        Assert.Equal(2L, advice.EligibleSupplyPackages);
        Assert.Equal(7L, advice.InventoryPosition);
        Assert.Equal(17L, advice.RequiredStockPackages);
        Assert.Equal(12L, advice.SuggestedPurchasePackages);
        Assert.Empty(advice.Warnings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Missing_demand_lead_time_or_safety_keeps_threshold_but_never_fabricates_a_suggestion(int missing)
    {
        var input = missing switch { 0 => Input with { DailyDemandPerDay = null }, 1 => Input with { LeadTimeDays = null }, _ => Input with { SafetyStockPackages = null } };
        var advice = ReplenishmentPolicy.Evaluate(input, Stock, true, [], Observed);
        Assert.True(advice.ThresholdAlert);
        Assert.Null(advice.SuggestedPurchasePackages);
        Assert.Contains(new[] { "daily_demand_missing", "lead_time_missing", "safety_stock_missing" }[missing], advice.Warnings);
    }

    [Fact]
    public void Unknown_supply_coverage_is_distinct_from_verified_empty_supply()
    {
        var unknown = ReplenishmentPolicy.Evaluate(Input, Stock, false, [], Observed);
        Assert.True(unknown.ThresholdAlert);
        Assert.Null(unknown.EligibleSupplyPackages);
        Assert.Null(unknown.InventoryPosition);
        Assert.Null(unknown.SuggestedPurchasePackages);
        Assert.Contains("supply_coverage_unknown", unknown.Warnings);
        var empty = ReplenishmentPolicy.Evaluate(Input, Stock, true, [], Observed);
        Assert.Equal(0L, empty.EligibleSupplyPackages);
        Assert.Equal(12L, empty.SuggestedPurchasePackages);
    }

    [Theory]
    [InlineData("unverified")]
    [InlineData("expected_arrival_missing")]
    [InlineData("overdue")]
    public void Uncertain_arrivals_do_not_become_zero_supply_and_raise_an_unfounded_purchase(string reason)
    {
        var row = new ReplenishmentSupply("po:uncertain", 100, Observed.AddDays(2), true);
        row = reason switch { "unverified" => row with { Verified = false }, "expected_arrival_missing" => row with { ExpectedAt = null }, _ => row with { ExpectedAt = Observed.AddTicks(-1) } };
        var advice = ReplenishmentPolicy.Evaluate(Input, Stock, true, [row], Observed);
        Assert.Null(advice.SuggestedPurchasePackages);
        Assert.Null(advice.InventoryPosition);
        Assert.Null(advice.EligibleSupplyPackages);
        Assert.True(advice.ThresholdAlert);
        Assert.Equal(reason, Assert.Single(advice.SupplyDecisions).State);
    }

    [Fact]
    public void Supply_at_horizon_counts_but_later_supply_does_not_hide_current_shortage()
    {
        var advice = ReplenishmentPolicy.Evaluate(Input, Stock, true,
            [new("po:boundary", 2, Observed.AddDays(4), true), new("po:later", 100, Observed.AddDays(4).AddTicks(1), true)], Observed);
        Assert.Equal(2L, advice.EligibleSupplyPackages);
        Assert.Equal(12L, advice.SuggestedPurchasePackages);
        Assert.Equal(["eligible", "after_horizon"], advice.SupplyDecisions.Select(row => row.State));
        Assert.Contains("supply_after_horizon", advice.Warnings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Missing_stock_authority_or_commitment_coverage_never_looks_like_zero_stock(int missing)
    {
        var stock = missing switch { 0 => Stock with { CurrentAuthority = false }, 1 => Stock with { UsableBeforeReservations = null }, 2 => Stock with { ReservedPackages = null }, _ => Stock with { AdditionalUnreservedCommitments = null } };
        var advice = ReplenishmentPolicy.Evaluate(Input, stock, true, [], Observed);
        Assert.Null(advice.AvailableAfterCommitments);
        Assert.Null(advice.ThresholdAlert);
        Assert.Null(advice.InventoryPosition);
        Assert.Null(advice.SuggestedPurchasePackages);
    }

    [Fact]
    public void Explicit_zero_demand_is_not_missing_and_does_not_order_a_minimum_when_no_shortage()
    {
        var advice = ReplenishmentPolicy.Evaluate(Input with { DailyDemandPerDay = 0m, SafetyStockPackages = 0, PurchaseMinimumPackages = 100 }, Stock, true, [], Observed);
        Assert.Equal(0L, advice.SuggestedPurchasePackages);
        Assert.DoesNotContain("daily_demand_missing", advice.Warnings);
    }

    [Fact]
    public void Purchase_minimum_is_rounded_to_increment_and_not_taken_from_sales_rules()
    {
        var advice = ReplenishmentPolicy.Evaluate(Input with { PurchaseMinimumPackages = 13, PurchaseIncrementPackages = 6 }, Stock, true, [], Observed);
        Assert.Equal(18L, advice.SuggestedPurchasePackages);
    }

    [Fact]
    public void Explicit_unreserved_demand_can_make_inventory_position_negative()
    {
        var advice = ReplenishmentPolicy.Evaluate(Input, Stock with { AdditionalUnreservedCommitments = 10 }, true, [], Observed);
        Assert.Equal(-3L, advice.InventoryPosition);
        Assert.Equal(24L, advice.SuggestedPurchasePackages);
    }

    [Fact]
    public void Threshold_is_independent_of_missing_history_and_does_not_infer_an_unknown_threshold()
    {
        var advice = ReplenishmentPolicy.Evaluate(Input with { ReorderPointPackages = null }, Stock, true, [], Observed);
        Assert.Null(advice.ThresholdAlert);
        Assert.Equal(12L, advice.SuggestedPurchasePackages);
        Assert.Contains("reorder_point_missing", advice.Warnings);
    }

    [Fact]
    public void Duplicate_supply_reference_cannot_be_counted_twice()
    {
        var rows = new[] { new ReplenishmentSupply("po:1", 10, Observed, true), new ReplenishmentSupply("po:1", 10, Observed, true) };
        Assert.Throws<DomainValidationException>(() => ReplenishmentPolicy.Evaluate(Input, Stock, true, rows, Observed));
    }

    [Fact]
    public void Impossible_reservation_and_negative_policy_inputs_are_rejected()
    {
        Assert.Throws<DomainValidationException>(() => ReplenishmentPolicy.Evaluate(Input, Stock with { ReservedPackages = 11 }, true, [], Observed));
        Assert.Throws<DomainValidationException>(() => ReplenishmentPolicy.Evaluate(Input with { DailyDemandPerDay = -1m }, Stock, true, [], Observed));
        Assert.Throws<DomainValidationException>(() => ReplenishmentPolicy.Evaluate(Input with { PurchaseIncrementPackages = 0 }, Stock, true, [], Observed));
    }

    [Fact]
    public void Derived_quantity_and_horizon_overflow_fail_without_wrapping()
    {
        Assert.Throws<DomainValidationException>(() => ReplenishmentPolicy.Evaluate(Input with { DailyDemandPerDay = decimal.MaxValue }, Stock, true, [], Observed));
        Assert.Throws<DomainValidationException>(() => ReplenishmentPolicy.Evaluate(Input with { LeadTimeDays = int.MaxValue }, Stock, true, [], Observed));
        Assert.Throws<DomainValidationException>(() => ReplenishmentPolicy.Evaluate(Input, Stock, true, [new("po:1", long.MaxValue, Observed, true)], Observed));
    }
}
