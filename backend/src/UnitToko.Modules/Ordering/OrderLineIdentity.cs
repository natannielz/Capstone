using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Ordering;

/// <summary>The initial immutable order line uses its original SKU and revision zero.
/// Every accepted amendment introduces a new line at that order decision revision.
/// A repeated SKU, definition or price never merges distinct buyer agreements.</summary>
public readonly record struct OrderLineIdentity(LongId SkuId, long Revision)
{
    public static OrderLineIdentity Of(OrderQuotedLine line) => new OrderLineIdentity(line.Definition.SkuId, line.LineRevision).Validate();
    public OrderLineIdentity Validate() {
        if (SkuId.Value <= 0 || Revision != 0 && (Revision is < 2 or > 103))
            throw new DomainValidationException("invalid_order_line_identity", "Choose the exact accepted order line");
        return this;
    }
}
