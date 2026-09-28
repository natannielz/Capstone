using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Accounting;
public sealed class AccountingFingerprintTests
{
    private static readonly OperationalBookKey Book = new(1, 2, 3, "IDR");
    private static readonly JournalSource Source = new("fixture", 1, "post", 1);
    private static readonly JournalDimensions Dimensions = new(1, 2, 3, 4, 5, 6);
    private static readonly DateOnly Date = new(2026, 9, 28);
    private static ResolvedJournalLine[] Lines(decimal amount = 100m) => [new("cash", 10, "CASH", new(amount), new(0m)), new("ar", 20, "AR", new(0m), new(amount))];
    [Fact]
    public void Fingerprint_binds_every_accounting_identity_and_canonical_money_scale()
    {
        JournalDraft Make(JournalSource? source = null, long revision = 1, JournalDimensions? dimensions = null, DateOnly? date = null, long chartId = 1, long chartVersion = 1, IEnumerable<ResolvedJournalLine>? lines = null, long? reversal = null) => JournalDraft.FromResolved(source ?? Source, revision, Book, dimensions ?? Dimensions, date ?? Date, chartId, chartVersion, lines ?? Lines(), reversal);
        var original = Make();
        Assert.Equal(original.Fingerprint, Make(lines: Lines(100.00m)).Fingerprint);
        foreach (var changed in new[]
        {
            Make(source: Source with { Occurrence = 2 }),
            Make(revision: 2),
            Make(dimensions: Dimensions with { WarehouseId = 7 }),
            Make(date: Date.AddDays(1)),
            Make(chartId: 2),
            Make(chartVersion: 2),
            Make(lines: [Lines()[0] with { AccountId = 11 }, Lines()[1]]),
            Make(reversal: 99)
        }

        )
            Assert.NotEqual(original.Fingerprint, changed.Fingerprint);
    }

    [Fact]
    public void Chart_and_reversal_copy_inputs_and_preserve_exact_inverse()
    {
        AccountMapping[] mappings = [new("cash", 10, "CASH", true), new("ar", 20, "AR", true)];
        var chart = new ChartSnapshot(1, 1, 1, mappings);
        mappings[0] = new("other", 99, "OTHER", false);
        Assert.Equal("cash", chart.Mappings[0].MappingKey);
        var lines = Lines();
        var reverse = JournalDraft.Reverse(Source with { EventKind = "reverse" }, 1, Book, Dimensions, Date.AddDays(1), 99, 1, 1, lines);
        lines[0] = lines[0] with
        {
            AccountId = 999
        };
        Assert.Equal(10, reverse.Lines[0].AccountId);
        Assert.Equal(100m, reverse.Lines[0].Credit.Value);
        Assert.Equal(100m, reverse.Lines[1].Debit.Value);
        Assert.Equal(99, reverse.ReversalOf);
    }

    [Fact]
    public void Inconsistent_identity_and_null_mappings_deny_but_documented_upper_counts_work()
    {
        Assert.Throws<DomainValidationException>(() => new ChartSnapshot(1, 1, 1, [new("a", 1, "ONE", true), new("b", 1, "TWO", true)]));
        Assert.Throws<DomainValidationException>(() => new ChartSnapshot(1, 1, 1, [new("a", 1, "ONE", true), new("b", 2, "ONE", true)]));
        Assert.Throws<DomainValidationException>(() => new ChartSnapshot(1, 1, 1, [null !]));
        var chart = new ChartSnapshot(1, 1, 1, Enumerable.Range(1, 1000).Select(index => new AccountMapping("key" + index, index, "CODE" + index, true)));
        Assert.Equal(1000, chart.Mappings.Length);
        var lines = Enumerable.Range(0, 50).SelectMany(_ => new[] { new PostingLine("key1", new(1m), new(0m)), new PostingLine("key2", new(0m), new(1m)) });
        var draft = JournalDraft.Create(new(Source, 1, Book, Dimensions, Date, lines), chart);
        Assert.Equal(100, draft.Lines.Length);
        Assert.Equal(50m, draft.Total.Value);
    }
}
