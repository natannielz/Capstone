using System.Collections.Immutable;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Procurement;

namespace UnitToko.Tests.Procurement;

public sealed class ProcurementAdvanceReturnDomainTests
{
    [Fact]
    public void Multiple_distinct_payouts_are_canonicalized_without_losing_exact_ids_or_cents()
    {
        var input = Valid() with { Lines = [new(9007199254740993L, new(0.10m)), new(8, new(0.20m)), new(7, new(0.01m))] };

        var copy = ProcurementAdvanceReturnRules.Copy(input);

        Assert.Collection(copy.Lines,
            line => { Assert.Equal(7, line.SettlementId); Assert.Equal(new Money(0.01m), line.Amount); },
            line => { Assert.Equal(8, line.SettlementId); Assert.Equal(new Money(0.20m), line.Amount); },
            line => { Assert.Equal(9007199254740993L, line.SettlementId); Assert.Equal(new Money(0.10m), line.Amount); });
        Assert.Equal(new Money(0.31m), ProcurementAdvanceReturnRules.Total(input));
        Assert.Equal(9007199254740993L, input.Lines[0].SettlementId);
    }

    [Fact]
    public void A_payout_cannot_be_counted_twice_even_when_its_return_amounts_differ()
    {
        var input = Valid() with { Lines = [new(7, new(10)), new(7, new(20))] };

        Assert.Throws<DomainValidationException>(() => ProcurementAdvanceReturnRules.Copy(input));
        Assert.Throws<DomainValidationException>(() => ProcurementAdvanceReturnRules.Total(input));
    }

    [Fact]
    public void Missing_or_nonpositive_payout_lines_are_rejected()
    {
        ImmutableArray<ProcurementAdvanceReturnLineInput>[] invalid = [default, [], [null!],
            [new(0, new(1))], [new(-1, new(1))], [new(7, default)]];

        foreach (var lines in invalid)
            Assert.Throws<DomainValidationException>(() => ProcurementAdvanceReturnRules.Copy(Valid() with { Lines = lines }));
    }

    [Fact]
    public void One_hundred_payout_lines_are_accepted_but_the_next_line_is_rejected()
    {
        var lines = Enumerable.Range(1, 100).Select(id => new ProcurementAdvanceReturnLineInput(id, new(0.01m))).ToImmutableArray();
        var input = Valid() with { Lines = lines };

        Assert.Equal(new Money(1), ProcurementAdvanceReturnRules.Total(input));
        Assert.Throws<DomainValidationException>(() => ProcurementAdvanceReturnRules.Copy(input with { Lines = lines.Add(new(101, new(0.01m))) }));
    }

    [Fact]
    public void Individually_valid_lines_cannot_overflow_the_supported_document_total()
    {
        var input = Valid() with { Lines = [new(7, new(999_999_999_999_999.98m)), new(8, new(0.01m))] };

        Assert.Equal(new Money(999_999_999_999_999.99m), ProcurementAdvanceReturnRules.Total(input));
        var excessive = input with { Lines = input.Lines.SetItem(1, new(8, new(0.02m))) };
        Assert.Throws<DomainValidationException>(() => ProcurementAdvanceReturnRules.Copy(excessive));
        Assert.Throws<DomainValidationException>(() => ProcurementAdvanceReturnRules.Total(excessive));
    }

    [Fact]
    public void Missing_input_receipt_policy_or_invalid_revision_is_rejected()
    {
        var input = Valid();
        ProcurementAdvanceReturnInput[] invalid = [null!, input with { ReceiptId = 0 }, input with { ReceiptId = -1 },
            input with { PolicyId = 0 }, input with { PolicyId = -1 }, input with { PolicyRevision = 0 },
            input with { PolicyRevision = -1 }, input with { PolicyRevision = long.MaxValue - 1 }, input with { PolicyRevision = long.MaxValue }];

        foreach (var value in invalid)
            Assert.Throws<DomainValidationException>(() => ProcurementAdvanceReturnRules.Copy(value));
        Assert.Throws<DomainValidationException>(() => ProcurementAdvanceReturnRules.Total(null!));
    }

    [Fact]
    public void Location_must_name_every_positive_scope_dimension()
    {
        ProcurementLocation[] invalid = [null!, new(0, 2, 3, 4), new(1, 0, 3, 4), new(1, 2, 0, 4), new(1, 2, 3, 0),
            new(-1, 2, 3, 4), new(1, -2, 3, 4), new(1, 2, -3, 4), new(1, 2, 3, -4)];

        foreach (var location in invalid)
            Assert.Throws<DomainValidationException>(() => ProcurementAdvanceReturnRules.Copy(Valid() with { Location = location }));
    }

    [Fact]
    public void Posting_date_and_a_bounded_explicit_reason_are_required()
    {
        var input = Valid();
        string[] invalidReasons = [null!, "", " ", " leading", "trailing ", "line\nbreak", new('a', 1001)];
        foreach (var reason in invalidReasons)
            Assert.Throws<DomainValidationException>(() => ProcurementAdvanceReturnRules.Copy(input with { Reason = reason }));
        Assert.Throws<DomainValidationException>(() => ProcurementAdvanceReturnRules.Copy(input with { PostingDate = default }));
        Assert.Throws<DomainValidationException>(() => ProcurementAdvanceReturnRules.Copy(input with { PostingDate = new(1999, 12, 31) }));

        var boundary = ProcurementAdvanceReturnRules.Copy(input with { PostingDate = new(2000, 1, 1), Reason = new('a', 1000) });
        Assert.Equal(new DateOnly(2000, 1, 1), boundary.PostingDate);
        Assert.Equal(1000, boundary.Reason.Length);
    }

    private static ProcurementAdvanceReturnInput Valid() => new(new(1, 2, 3, 4), 5, 6, 1,
        new(2026, 10, 4), "Review unused supplier advance returned to the bank", [new(7, new(100))]);
}
