using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Procurement;

public sealed record PurchaseArrivalInput(long OrderId, long OrderVersion, long ApprovalId, long SkuId,
    long DefinitionId, long ExpectedRevision, DateTimeOffset? ExpectedAt, string ConfirmationReference);

public static class PurchaseArrivalRules
{
    public static PurchaseArrivalInput Copy(PurchaseArrivalInput value)
    {
        if (value is null || value.OrderId <= 0 || value.OrderVersion <= 0 || value.ApprovalId <= 0 ||
            value.SkuId <= 0 || value.DefinitionId <= 0 || value.ExpectedRevision is <= 0 or >= long.MaxValue ||
            value.ExpectedAt is { } date && (date.Offset != TimeSpan.Zero || date.Ticks % TimeSpan.TicksPerMillisecond != 0))
            throw Invalid();
        PurchaseOrderRules.Text(value.ConfirmationReference, 256);
        return value with { };
    }
    public static DomainValidationException Invalid() => new("purchase_arrival_invalid", "Review the exact purchase order, package, arrival time and supplier confirmation reference");
}
