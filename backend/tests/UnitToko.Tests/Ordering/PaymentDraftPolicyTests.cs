using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Ordering;
public sealed class PaymentDraftPolicyTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);
    private static PaymentDraftInput Input() => new([new(9007199254740993L)], new Money(40000m), Today, "Cabang Tebet", "TRANSFER-01", "Pembayaran sebagian");
    [Fact]
    public void Draft_keeps_lossless_invoice_references_and_declared_amount_without_claiming_bank_receipt() {
        var input = Input(); var copied = PaymentDraftPolicy.Copy(input, Today);
        Assert.Equal(9007199254740993L, copied.InvoiceIds[0].Value); Assert.Equal(40000m, copied.DeclaredAmount.Value);
        Assert.Equal(input.InvoiceIds.Select(id => id.Value).ToArray(), copied.InvoiceIds.Select(id => id.Value).ToArray());
        Assert.Equal(input.BankReference, copied.BankReference); Assert.Equal(input.Note, copied.Note);
        Assert.Equal(100000m, PaymentDraftPolicy.Copy(input with { DeclaredAmount = new(100000m) }, Today).DeclaredAmount.Value);
    }
    [Theory]
    [InlineData("duplicate")] [InlineData("empty")] [InlineData("too-many")] [InlineData("zero")] [InlineData("future")]
    [InlineData("blank-payer")] [InlineData("unsafe-reference")]
    public void Invalid_declarations_are_rejected(string fault) {
        var input = Input(); input = fault switch {
            "duplicate" => input with { InvoiceIds = [new(1), new(1)] }, "empty" => input with { InvoiceIds = [] },
            "too-many" => input with { InvoiceIds = Enumerable.Range(1, 21).Select(id => new LongId(id)).ToImmutableArray() },
            "zero" => input with { DeclaredAmount = new(0m) }, "future" => input with { TransferDate = Today.AddDays(1) },
            "blank-payer" => input with { PayerName = " " }, _ => input with { BankReference = "private\nvalue" }
        };
        Assert.Throws<DomainValidationException>(() => PaymentDraftPolicy.Copy(input, Today));
    }
}
