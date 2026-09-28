using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Accounting;
public sealed class AccountingDomainReviewTests
{
    [Theory]
    [InlineData("IDR\n")]
    [InlineData("IDR\r\n")]
    [InlineData("IDR ")]
    [InlineData(" IDR")]
    public void Currency_cannot_create_a_second_book_by_invisible_suffix(string currency) => Assert.Throws<DomainValidationException>(() => new OperationalBookKey(1, 2, 3, currency).Validate());
    [Fact]
    public void Reversal_malformed_inputs_have_typed_rejection()
    {
        var source = new JournalSource("fixture", 1, "reverse", 1);
        var book = new OperationalBookKey(1, 2, 3, "IDR");
        var dims = new JournalDimensions(1, 2, 3, null, null, null);
        ResolvedJournalLine[] lines = [new("cash", 1, "CASH", new(1m), new(0m)), new("ar", 2, "AR", new(0m), new(1m))];
        Assert.Throws<DomainValidationException>(() => JournalDraft.Reverse(null !, 1, book, dims, new(2026, 9, 28), 1, 1, 1, lines));
        Assert.Throws<DomainValidationException>(() => JournalDraft.Reverse(source, 1, null !, dims, new(2026, 9, 28), 1, 1, 1, lines));
        Assert.Throws<DomainValidationException>(() => JournalDraft.Reverse(source, 1, book, null !, new(2026, 9, 28), 1, 1, 1, lines));
        Assert.Throws<DomainValidationException>(() => JournalDraft.Reverse(source, 1, book, dims, new(2026, 9, 28), 1, 1, 1, [null !, lines[1]]));
        Assert.Throws<DomainValidationException>(() => JournalDraft.Reverse(source, 1, book, dims, new(2026, 9, 28), 1, 1, 1, null !));
    }
}
