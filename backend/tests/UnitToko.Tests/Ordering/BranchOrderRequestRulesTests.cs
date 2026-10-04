using System.Collections.Immutable;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Ordering;
using Xunit;

namespace UnitToko.Tests.Ordering;

public sealed class BranchOrderRequestRulesTests {
    private static BranchOrderRequestInput Input(int packages = 2) => new(
        new OrderBuyingContext(new LongId(10), new LongId(11)),
        ImmutableArray.Create(new BranchOrderRequestLine(new LongId(100), new LongId(200), 1, packages)), null);

    [Fact]
    public void Request_only_input_needs_exact_positive_package_count_and_distinct_skus() {
        Assert.Equal(2, BranchOrderRequestRules.Copy(Input()).Lines[0].Packages);
        Assert.Throws<DomainValidationException>(() => BranchOrderRequestRules.Copy(Input(0)));
        Assert.Throws<DomainValidationException>(() => BranchOrderRequestRules.Copy(Input(int.MaxValue)));
        Assert.Throws<DomainValidationException>(() => BranchOrderRequestRules.Copy(Input() with {
            Lines = ImmutableArray.Create(Input().Lines[0], Input().Lines[0]) }));
    }

    [Fact]
    public void Request_note_and_decision_reason_have_bounded_clean_text() {
        Assert.Null(BranchOrderRequestRules.Copy(Input()).Note);
        Assert.Throws<DomainValidationException>(() => BranchOrderRequestRules.Copy(Input() with { Note = " " }));
        Assert.Throws<DomainValidationException>(() => BranchOrderRequestRules.Copy(Input() with { Note = "\nopaque" }));
        BranchOrderRequestRules.Reason("Accepted for staff handling");
        Assert.Throws<DomainValidationException>(() => BranchOrderRequestRules.Reason(" "));
    }
}
