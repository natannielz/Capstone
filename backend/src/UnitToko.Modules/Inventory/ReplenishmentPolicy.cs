using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Inventory;

/// <summary>All quantities refer to the exact selected SKU/definition in package units, never labels or inferred pack contents.</summary>
public sealed record ReplenishmentInputs(decimal? DailyDemandPerDay, int? LeadTimeDays, long? SafetyStockPackages,
    long? ReorderPointPackages, long PurchaseMinimumPackages, long PurchaseIncrementPackages);
public sealed record ReplenishmentStock(bool CurrentAuthority, long? UsableBeforeReservations,
    long? ReservedPackages, long? AdditionalUnreservedCommitments);
public sealed record ReplenishmentSupply(string Reference, long Packages, DateTimeOffset? ExpectedAt, bool Verified);
public sealed record ReplenishmentSupplyDecision(string Reference, string State);
public sealed record ReplenishmentAdvice(string RuleVersion, DateTimeOffset ObservedAt, DateTimeOffset? Horizon,
    long? AvailableAfterCommitments, bool? ThresholdAlert, long? EligibleSupplyPackages, long? InventoryPosition,
    long? RequiredStockPackages, long? SuggestedPurchasePackages, ImmutableArray<string> Warnings,
    ImmutableArray<ReplenishmentSupplyDecision> SupplyDecisions);

/// <summary>Read-only arithmetic. The owner must prove scope, current source/lot/history and complete commitments/supply coverage,
/// retain immutable input provenance, and require explicit human action through the existing procurement owner.</summary>
public static class ReplenishmentPolicy
{
    public const string Version = "replenishment-lead-time-v1";
    public static ReplenishmentAdvice Evaluate(ReplenishmentInputs input, ReplenishmentStock stock,
        bool supplyCoverageComplete, IReadOnlyList<ReplenishmentSupply> supplies, DateTimeOffset observedAt)
    {
        if (input is null || stock is null || supplies is null ||
            input.DailyDemandPerDay is < 0 || input.LeadTimeDays is < 0 ||
            input.SafetyStockPackages is < 0 || input.ReorderPointPackages is < 0 ||
            input.PurchaseMinimumPackages <= 0 || input.PurchaseIncrementPackages <= 0 ||
            stock.UsableBeforeReservations is < 0 || stock.ReservedPackages is < 0 ||
            stock.AdditionalUnreservedCommitments is < 0 || supplies.Count > 1000 ||
            stock.UsableBeforeReservations is { } usable && stock.ReservedPackages is { } reserved && reserved > usable)
            throw Invalid("replenishment_input_invalid");
        var warnings = new List<string>();
        var decisions = ImmutableArray.CreateBuilder<ReplenishmentSupplyDecision>();
        if (input.DailyDemandPerDay is null) warnings.Add("daily_demand_missing");
        if (input.LeadTimeDays is null) warnings.Add("lead_time_missing");
        if (input.SafetyStockPackages is null) warnings.Add("safety_stock_missing");
        if (input.ReorderPointPackages is null) warnings.Add("reorder_point_missing");
        if (!stock.CurrentAuthority) warnings.Add("stock_authority_unavailable");
        if (stock.UsableBeforeReservations is null) warnings.Add("usable_stock_unknown");
        if (stock.ReservedPackages is null || stock.AdditionalUnreservedCommitments is null) warnings.Add("commitment_coverage_unknown");
        if (!supplyCoverageComplete) warnings.Add("supply_coverage_unknown");
        try {
            DateTimeOffset? horizon = input.LeadTimeDays is { } days ? observedAt.AddDays(days) : null;
            long? available = stock.CurrentAuthority && stock.UsableBeforeReservations is { } onHand &&
                stock.ReservedPackages is { } heldForOrders && stock.AdditionalUnreservedCommitments is { } unreserved
                ? checked(onHand - heldForOrders - unreserved) : null;
            bool? low = available is { } current && input.ReorderPointPackages is { } threshold ? current <= threshold : null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var knownSupply = supplyCoverageComplete && horizon.HasValue;
            long due = 0;
            foreach (var supply in supplies) {
                if (supply is null || string.IsNullOrEmpty(supply.Reference) || supply.Reference.Length > 160 ||
                    supply.Reference != supply.Reference.Trim() || supply.Reference.Any(char.IsControl) ||
                    supply.Packages <= 0 || !seen.Add(supply.Reference))
                    throw Invalid("replenishment_supply_invalid");
                var state = !supply.Verified ? "unverified" :
                    supply.ExpectedAt is null ? "expected_arrival_missing" :
                    supply.ExpectedAt < observedAt ? "overdue" :
                    horizon is null ? "lead_time_missing" :
                    supply.ExpectedAt > horizon ? "after_horizon" : "eligible";
                decisions.Add(new(supply.Reference, state));
                if (state == "eligible") due = checked(due + supply.Packages);
                else warnings.Add("supply_" + state);
                // Excluding uncertain arrivals must not turn unknown supply into a verified zero and inflate a purchase.
                if (state is "unverified" or "expected_arrival_missing" or "overdue") knownSupply = false;
            }
            long? eligible = knownSupply ? due : null;
            long? position = available is { } stockPosition && eligible is { } incoming ? checked(stockPosition + incoming) : null;
            long? required = input.DailyDemandPerDay is { } demand && input.LeadTimeDays is { } lead && input.SafetyStockPackages is { } safety
                ? checked((long)decimal.Ceiling(demand * lead) + safety) : null;
            long? suggested = null;
            if (required is { } target && position is { } inventory) {
                var shortage = Math.Max(0m, target - (decimal)inventory);
                // A zero shortage never creates a minimum-sized order. Minimum and increment are explicit purchase rules.
                suggested = shortage == 0 ? 0 :
                    checked((long)(decimal.Ceiling(Math.Max(shortage, input.PurchaseMinimumPackages) / input.PurchaseIncrementPackages) * input.PurchaseIncrementPackages));
            }
            return new(Version, observedAt, horizon, available, low, eligible, position, required, suggested,
                warnings.Distinct(StringComparer.Ordinal).ToImmutableArray(), decisions.ToImmutable());
        }
        catch (OverflowException) { throw Invalid("replenishment_quantity_overflow"); }
        catch (ArgumentOutOfRangeException) { throw Invalid("replenishment_horizon_invalid"); }
    }
    private static DomainValidationException Invalid(string code) => new(code, "Replenishment requires valid explicit inputs and bounded exact quantities");
}
