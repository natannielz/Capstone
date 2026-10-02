using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Ordering;

public sealed record OrderSpendingPolicyTarget(ScopeNodeKind Kind, long Id);
public enum OrderSpendingPolicyDecision { Approve, Reject }
public sealed record OrderSpendingPolicyInput(OrderSpendingPolicyTarget Target, long ExpectedScopeVersion,
    long ExpectedProfileVersion, long ExpectedPolicyVersion, OrderSpendingRule Rule, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil);

public static class OrderSpendingPolicyRules
{
    public static void RequireTarget(OrderSpendingPolicyTarget target) {
        if (target is null || target.Id <= 0 || target.Kind is not (ScopeNodeKind.Branch or ScopeNodeKind.Division)) throw Invalid();
    }
    public static OrderSpendingPolicyInput Copy(OrderSpendingPolicyInput input) {
        if (input is null || input.ExpectedScopeVersion is <= 0 or >= long.MaxValue || input.ExpectedProfileVersion is <= 0 or >= long.MaxValue ||
            input.ExpectedPolicyVersion < 0 || input.ExpectedPolicyVersion >= long.MaxValue - 1) throw Invalid();
        RequireTarget(input.Target); RequireTerms(input.Rule, input.ValidFrom, input.ValidUntil);
        return input with { Target = input.Target with { }, Rule = input.Rule with { }, ValidFrom = input.ValidFrom.ToUniversalTime(), ValidUntil = input.ValidUntil.ToUniversalTime() };
    }
    public static void RequireTerms(OrderSpendingRule rule, DateTimeOffset validFrom, DateTimeOffset validUntil) {
        if (rule is null || validFrom <= DateTimeOffset.MinValue || validFrom >= validUntil ||
            validFrom.UtcTicks % TimeSpan.TicksPerMillisecond != 0 || validUntil.UtcTicks % TimeSpan.TicksPerMillisecond != 0) throw Invalid();
        _ = OrderSpendingPolicy.Initial(rule, new Money(0));
        if (rule.Threshold is { } threshold) _ = new Money(threshold.Value);
    }
    public static DomainValidationException Invalid() => new("order_spending_policy_invalid", "An exact branch or division, explicit spending rule and bounded validity period are required");
}
