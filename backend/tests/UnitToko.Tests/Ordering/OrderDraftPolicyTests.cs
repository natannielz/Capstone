using System.Collections.Immutable;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;

namespace UnitToko.Tests.Ordering;

public sealed class OrderDraftPolicyTests
{
    private static OrderDraftInput Input() => new(new(new(10), new(11)),
        [new(new(100), new(200), 1, new(300), 1, 2)],
        new("Penerima fixture", "+62 812-3456-7890", "Jalan Contoh 1\nLantai 2", "Jakarta", "12810", "Hubungi meja penerima"), "PO fixture");
    [Fact]
    public void Draft_preserves_explicit_choices_and_multiline_address_without_creating_commercial_consent() {
        var input = Input(); var draft = OrderDraftPolicy.Copy(input);
        Assert.Equal(input.Context, draft.Context); Assert.Equal(input.Selections.ToArray(), draft.Selections.ToArray());
        Assert.Equal(input.Delivery, draft.Delivery); Assert.Equal(input.PurchaseReference, draft.PurchaseReference);
        Assert.Equal(2, draft.Selections[0].Packages);
    }
    [Fact]
    public void Missing_delivery_and_purchase_reference_remain_unknown_until_checkout() {
        var value = OrderDraftPolicy.Copy(Input() with { Delivery = null, PurchaseReference = null });
        Assert.Null(value.Delivery); Assert.Null(value.PurchaseReference);
    }
    [Theory]
    [InlineData("empty-lines")] [InlineData("duplicate-lines")] [InlineData("quantity")]
    [InlineData("default-sku")] [InlineData("definition-revision")] [InlineData("price-revision")]
    [InlineData("recipient")] [InlineData("phone")] [InlineData("address-control")]
    [InlineData("long-address")] [InlineData("city")] [InlineData("postal")]
    [InlineData("reference-control")] [InlineData("instructions-control")]
    public void Invalid_or_ambiguous_draft_cannot_cross_the_owner_boundary(string fault) {
        var input = Input(); var line = input.Selections[0]; var delivery = input.Delivery!;
        input = fault switch {
            "empty-lines" => input with { Selections = [] },
            "duplicate-lines" => input with { Selections = [line, line] },
            "quantity" => input with { Selections = [line with { Packages = 1000001 }] },
            "default-sku" => input with { Selections = [line with { SkuId = default }] },
            "definition-revision" => input with { Selections = [line with { DefinitionVersion = 0 }] },
            "price-revision" => input with { Selections = [line with { PriceVersion = 0 }] },
            "recipient" => input with { Delivery = delivery with { RecipientName = " " } },
            "phone" => input with { Delivery = delivery with { Phone = "call anyone" } },
            "address-control" => input with { Delivery = delivery with { Address = "Address\0other" } },
            "long-address" => input with { Delivery = delivery with { Address = new string('a', 1001) } },
            "city" => input with { Delivery = delivery with { City = " Jakarta" } },
            "postal" => input with { Delivery = delivery with { PostalCode = "0000" } },
            "reference-control" => input with { PurchaseReference = "PO\nforged" },
            "instructions-control" => input with { Delivery = delivery with { Instructions = "Desk\u007f" } },
            _ => throw new InvalidOperationException()
        };
        Assert.Throws<DomainValidationException>(() => OrderDraftPolicy.Copy(input));
    }
    [Fact]
    public void Draft_copies_selection_array_storage_before_async_work() {
        var source = new[] { Input().Selections[0] };
        var wrapped = System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(source);
        var draft = OrderDraftPolicy.Copy(Input() with { Selections = wrapped });
        source[0] = source[0] with { Packages = 999 };
        Assert.Equal(2, draft.Selections[0].Packages);
    }
}
