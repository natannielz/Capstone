using System.Collections.Immutable;

namespace UnitToko.Modules.Procurement;

/// <summary>Explicit intended receiving quantities, not stock reservation, receipt or release.</summary>
public sealed record PurchaseOrderInboundAllocation(long RequestSkuId, int Packages, long PoolId, long PoolRevision);
public sealed record PurchaseOrderInboundPoolSelection(ImmutableArray<PurchaseOrderInboundAllocation> Allocations);

public static class PurchaseOrderInboundRules
{
    public const int MaximumAllocations = 100, MaximumPools = 8;
    public static PurchaseOrderInboundPoolSelection Copy(PurchaseOrderInput order, PurchaseOrderInboundPoolSelection selection) {
        order = PurchaseOrderRules.Copy(order);
        if (selection is null || selection.Allocations.IsDefaultOrEmpty || selection.Allocations.Length > MaximumAllocations ||
            selection.Allocations.Any(row => row is null || row.RequestSkuId is <= 0 or >= long.MaxValue - 1 ||
                row.PoolId is <= 0 or >= long.MaxValue - 1 || row.PoolRevision is <= 0 or >= long.MaxValue - 1 ||
                row.Packages is < 1 or > 1000000)) throw PurchaseOrderRules.Invalid();
        var rows = selection.Allocations.OrderBy(row => row.RequestSkuId).ThenBy(row => row.PoolId).Select(row => row with { }).ToImmutableArray();
        if (rows.Select(row => (row.RequestSkuId, row.PoolId)).Distinct().Count() != rows.Length ||
            rows.Select(row => row.PoolId).Distinct().Count() > MaximumPools ||
            rows.GroupBy(row => row.PoolId).Any(group => group.Select(row => row.PoolRevision).Distinct().Count() != 1) ||
            !rows.Select(row => row.RequestSkuId).Distinct().SequenceEqual(order.Lines.Select(row => row.RequestSkuId)) ||
            order.Lines.Any(line => rows.Where(row => row.RequestSkuId == line.RequestSkuId).Sum(row => (long)row.Packages) != line.Packages))
            throw PurchaseOrderRules.Invalid();
        return new(rows);
    }
}
