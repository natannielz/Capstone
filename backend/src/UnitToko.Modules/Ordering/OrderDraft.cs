using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Ordering;

/// <summary>Buyer-entered delivery request. Saving it neither verifies a recipient nor approves a branch address.</summary>
public sealed record OrderDeliveryRequest(string RecipientName, string Phone, string Address, string City, string? PostalCode, string? Instructions);
public sealed record OrderDraftInput(OrderBuyingContext Context, ImmutableArray<OrderPackageSelection> Selections,
    OrderDeliveryRequest? Delivery, string? PurchaseReference);

/// <summary>Drafts preserve explicit choices. Current authority/catalog and final commercial acceptance
/// are separate transaction checks. Draft contents have no stock or financial effect.</summary>
public static class OrderDraftPolicy
{
    public static OrderDraftInput Copy(OrderDraftInput value)
    {
        if (value is null || value.Context is null) throw Invalid();
        value.Context.Validate();
        if (value.Selections.IsDefaultOrEmpty || value.Selections.Length > 100 ||
            value.Selections.Any(line => line is null || line.SkuId.Value <= 0 || line.DefinitionId.Value <= 0 ||
                line.DefinitionVersion <= 0 || line.PriceId.Value <= 0 || line.PriceVersion <= 0 ||
                line.Packages is < 1 or > DomainValues.MaximumLineQuantity) ||
            value.Selections.Select(line => line.SkuId).Distinct().Count() != value.Selections.Length) throw Invalid();
        Optional(value.PurchaseReference, 160);
        if (value.Delivery is { } delivery) {
            Text(delivery.RecipientName, 160); Text(delivery.Phone, 32); Text(delivery.Address, 1000, multiline: true); Text(delivery.City, 120);
            if (!System.Text.RegularExpressions.Regex.IsMatch(delivery.Phone, @"\A\+?[0-9][0-9 ()-]*\z") ||
                delivery.Phone.Count(char.IsAsciiDigit) is < 7 or > 15) throw Invalid();
            if (delivery.PostalCode is not null && !System.Text.RegularExpressions.Regex.IsMatch(delivery.PostalCode, @"\A[0-9]{5}\z")) throw Invalid();
            Optional(delivery.Instructions, 1000, multiline: true);
        }
        return value with { Selections = value.Selections.ToArray().ToImmutableArray(), Delivery = value.Delivery is null ? null : value.Delivery with { } };
    }
    private static void Optional(string? value, int limit, bool multiline = false) { if (value is not null) Text(value, limit, multiline); }
    private static void Text(string value, int limit, bool multiline = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() != value || value.Length > limit ||
            value.Any(character => char.IsControl(character) && !(multiline && character == '\n'))) throw Invalid();
    }
    private static DomainValidationException Invalid() => new("invalid_order_draft", "Choose explicit packages and bounded delivery details");
}
