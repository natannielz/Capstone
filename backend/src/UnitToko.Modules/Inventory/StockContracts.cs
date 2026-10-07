using UnitToko.Modules.Contracts;
using UnitToko.Modules.Organizations;

namespace UnitToko.Modules.Inventory;

/// <summary>Canonical scope identities; labels such as OMI never substitute for the channel ID.</summary>
public sealed record StockLocation(LongId OrganizationId, LongId ShopId, LongId WarehouseId, LongId ChannelId)
{
    public StockLocation Validate()
    {
        if (OrganizationId.Value <= 0 || ShopId.Value <= 0 || WarehouseId.Value <= 0 || ChannelId.Value <= 0)
            throw new DomainValidationException("invalid_stock_location", "Stock requires an exact warehouse and channel");
        return this;
    }
    public GrantScope ToScope()
    {
        Validate();
        return new(GrantScopeKind.FulfillmentWarehouse, OrganizationId.Value, ShopId: ShopId.Value,
            WarehouseId: WarehouseId.Value, ChannelId: ChannelId.Value);
    }
}

public sealed record StockBalanceKey(StockLocation Location, LongId SkuId, LongId DefinitionId, LongId LotId);

/// <summary>All buckets count packages of this exact SKU/definition/lot, never inferred base units.
/// Held and Expired are disjoint parts of OnHand. Reserved is a subset of the remaining eligible stock.</summary>
public sealed record StockBalance(StockBalanceKey Key, long OnHand, long Reserved, long Held, long Expired, long Revision);

/// <summary>Pure arithmetic only. The transaction owner must supply authoritative records and enforce
/// scope, source authority, lot eligibility, idempotency, immutable movements and accounting atomically.
/// Quantities are derived Int64 package totals; the owning command separately bounds user-entered quantities/steps.</summary>
public static class StockBalancePolicy
{
    public static long AvailablePackages(StockBalance value)
    {
        if (value is null || value.Key is null || value.Key.Location is null ||
            value.Key.SkuId.Value <= 0 || value.Key.DefinitionId.Value <= 0 || value.Key.LotId.Value <= 0 ||
            value.Revision <= 0 || value.OnHand < 0 || value.Reserved < 0 || value.Held < 0 || value.Expired < 0)
            throw Invalid("invalid_stock_balance");
        value.Key.Location.Validate();
        // Subtract in stages so corrupt near-Int64 buckets cannot overflow an intermediate sum.
        if (value.Held > value.OnHand) throw Invalid("invalid_stock_balance");
        var eligible = value.OnHand - value.Held;
        if (value.Expired > eligible) throw Invalid("invalid_stock_balance");
        eligible -= value.Expired;
        if (value.Reserved > eligible) throw Invalid("invalid_stock_balance");
        return eligible - value.Reserved;
    }

    public static StockBalance Reserve(StockBalance value, long revision, long quantity) => Change(value, revision, quantity, ChangeKind.Reserve);
    public static StockBalance Release(StockBalance value, long revision, long quantity) => Change(value, revision, quantity, ChangeKind.Release);
    public static StockBalance Dispatch(StockBalance value, long revision, long quantity) => Change(value, revision, quantity, ChangeKind.Dispatch);
    public static StockBalance Receive(StockBalance value, long revision, long quantity) => Change(value, revision, quantity, ChangeKind.Receive);
    public static StockBalance ReceiveHeld(StockBalance value, long revision, long quantity) => Change(value, revision, quantity, ChangeKind.ReceiveHeld);
    public static StockBalance Hold(StockBalance value, long revision, long quantity) => Change(value, revision, quantity, ChangeKind.Hold);
    public static StockBalance ReleaseHold(StockBalance value, long revision, long quantity) => Change(value, revision, quantity, ChangeKind.ReleaseHold);
    public static StockBalance Expire(StockBalance value, long revision, long quantity) => Change(value, revision, quantity, ChangeKind.Expire);
    public static StockBalance ExpireHeld(StockBalance value, long revision, long quantity) => Change(value, revision, quantity, ChangeKind.ExpireHeld);
    public static StockBalance ConvertOut(StockBalance value, long revision, long quantity) => Change(value, revision, quantity, ChangeKind.ConvertOut);
    public static StockBalance ConvertIn(StockBalance value, long revision, long quantity) => Change(value, revision, quantity, ChangeKind.ConvertIn);

    private enum ChangeKind { Reserve, Release, Dispatch, Receive, ReceiveHeld, Hold, ReleaseHold, Expire, ExpireHeld, ConvertOut, ConvertIn }
    private static StockBalance Change(StockBalance value, long expectedRevision, long quantity, ChangeKind kind)
    {
        var available = AvailablePackages(value);
        if (quantity <= 0) throw new DomainValidationException("invalid_quantity", "Derived stock quantity must be a positive Int64 count");
        if (expectedRevision <= 0 || expectedRevision != value.Revision) throw Invalid("stock_revision_conflict");
        if (value.Revision == long.MaxValue) throw Invalid("stock_revision_exhausted");
        if (kind is ChangeKind.Reserve or ChangeKind.Hold or ChangeKind.Expire or ChangeKind.ConvertOut && quantity > available)
            throw Invalid("insufficient_available_stock");
        if (kind is ChangeKind.Release or ChangeKind.Dispatch && quantity > value.Reserved)
            throw Invalid("insufficient_reserved_stock");
        if (kind is ChangeKind.ReleaseHold or ChangeKind.ExpireHeld && quantity > value.Held)
            throw Invalid("insufficient_held_stock");
        try
        {
            var changed = kind switch
            {
                ChangeKind.Reserve => value with { Reserved = checked(value.Reserved + quantity) },
                ChangeKind.Release => value with { Reserved = value.Reserved - quantity },
                ChangeKind.Dispatch => value with { OnHand = value.OnHand - quantity, Reserved = value.Reserved - quantity },
                ChangeKind.Receive => value with { OnHand = checked(value.OnHand + quantity) },
                ChangeKind.ReceiveHeld => value with { OnHand = checked(value.OnHand + quantity), Held = checked(value.Held + quantity) },
                ChangeKind.Hold => value with { Held = checked(value.Held + quantity) },
                ChangeKind.ReleaseHold => value with { Held = value.Held - quantity },
                ChangeKind.Expire => value with { Expired = checked(value.Expired + quantity) },
                ChangeKind.ExpireHeld => value with { Held = value.Held - quantity, Expired = checked(value.Expired + quantity) },
                ChangeKind.ConvertOut => value with { OnHand = checked(value.OnHand - quantity) },
                ChangeKind.ConvertIn => value with { OnHand = checked(value.OnHand + quantity) },
                _ => throw Invalid("invalid_stock_change")
            };
            changed = changed with { Revision = value.Revision + 1 };
            _ = AvailablePackages(changed);
            return changed;
        }
        catch (OverflowException) { throw Invalid("stock_quantity_overflow"); }
    }
    private static DomainValidationException Invalid(string code) => new(code, "Stock quantities or revisions are not valid for this change");
}
