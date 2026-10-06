using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Inventory;

public sealed record StockReleaseInput(StockLocation Location, long ReceiptId, long QualityAllocationId,
    long ValuationAllocationId, long Packages, string Policy)
{
    public void Validate() {
        if (Location is null || ReceiptId is <= 0 or >= long.MaxValue - 2 || QualityAllocationId is <= 0 or >= long.MaxValue - 2 ||
            ValuationAllocationId is <= 0 or >= long.MaxValue - 2 || Packages is < 1 or > DomainValues.MaximumLineQuantity ||
            Policy != LotValuePolicy.ProportionalFloorPolicy) throw Invalid();
        Location.Validate();
    }
    public static DomainValidationException Invalid() => new("invalid_stock_release", "Select the exact held receipt, quality and posted value with an explicit approved value policy");
}
public sealed record StockReleaseExpectation(long BalanceRevision, long PoolRevision, long PoolFence,
    long HeldValueRevision, long LotValueRevision)
{
    public void Validate() {
        if (BalanceRevision is <= 0 or >= long.MaxValue - 2 || PoolRevision is <= 0 or >= long.MaxValue - 2 ||
            PoolFence is < 0 or >= long.MaxValue - 2 || HeldValueRevision is < 0 or >= long.MaxValue - 2 || LotValueRevision is < 0 or >= long.MaxValue - 2)
            throw StockReleaseInput.Invalid();
    }
}
public enum StockReleaseDecision { Approve, Reject }
