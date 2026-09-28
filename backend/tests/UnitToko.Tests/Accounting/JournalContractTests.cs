using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Tests.Accounting;
public sealed class JournalContractTests
{
    private static readonly OperationalBookKey Book = new(100, 101, 103, "IDR");
    private static readonly JournalSource Source = new("procurement", 42, "receipt", 1);
    private static SourceSnapshot Snapshot(IEnumerable<PostingLine>? lines = null) => new(Source, 1, Book, new(100, 101, 103, 102, 10, 11), new DateOnly(2026, 9, 28), lines ?? [new("inventory", new(100m), new(0m)), new("payable", new(0m), new(100m))]);
    private static ChartSnapshot Chart() => new(20, 1, 1, [new("inventory", 501, "INV", true), new("payable", 502, "AP", true)]);
    [Fact]
    public void Balanced_source_resolves_server_chart_and_fingerprint_ignores_actor()
    {
        var entry = JournalDraft.Create(Snapshot(), Chart());
        Assert.Equal(100m, entry.Total.Value);
        Assert.Equal(501, entry.Lines[0].AccountId);
        Assert.Equal("procurement:42:receipt:1", entry.Source.EventKey);
        Assert.Equal(64, entry.Fingerprint.Length);
        Assert.Equal(entry.Fingerprint, JournalDraft.Create(Snapshot(), Chart()).Fingerprint);
        Assert.NotEqual(entry.Fingerprint, JournalDraft.Create(Snapshot([new("inventory", new(50m), new(0m)), new("payable", new(0m), new(50m))]), Chart()).Fingerprint);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1, 1, 0, 2)]
    [InlineData(1, 0, 0, 2)]
    public void Zero_double_sided_and_unbalanced_lines_deny(decimal d1, decimal c1, decimal d2, decimal c2) => Assert.Throws<DomainValidationException>(() => JournalDraft.Create(Snapshot([new("inventory", new(d1), new(c1)), new("payable", new(d2), new(c2))]), Chart()));
    [Fact]
    public void Aggregate_overflow_denies_even_when_each_line_is_valid() => Assert.Throws<DomainValidationException>(() => JournalDraft.Create(Snapshot([new("inventory", new(999999999999999.99m), new(0m)), new("inventory", new(0.01m), new(0m)), new("payable", new(0m), new(999999999999999.99m)), new("payable", new(0m), new(0.01m))]), Chart()));
    [Fact]
    public void Snapshot_copies_caller_lines_before_validation()
    {
        PostingLine[] lines = [new("inventory", new(100m), new(0m)), new("payable", new(0m), new(100m))];
        var source = Snapshot(lines);
        lines[0] = new("unknown", new(7m), new(0m));
        Assert.Equal(100m, JournalDraft.Create(source, Chart()).Total.Value);
    }

    [Fact]
    public void Unknown_inactive_duplicate_chart_mapping_and_default_source_deny()
    {
        Assert.Throws<DomainValidationException>(() => JournalDraft.Create(Snapshot(), new(20, 1, 1, [new("inventory", 501, "INV", true)])));
        Assert.Throws<DomainValidationException>(() => JournalDraft.Create(Snapshot(), new(20, 1, 1, [new("inventory", 501, "INV", false), new("payable", 502, "AP", true)])));
        Assert.Throws<DomainValidationException>(() => new ChartSnapshot(20, 1, 1, [new("inventory", 501, "INV", true), new("inventory", 502, "AP", true)]));
        Assert.Throws<DomainValidationException>(() => new JournalSource("procurement", 0, "receipt", 1).Validate());
        Assert.Throws<DomainValidationException>(() => new OperationalBookKey(100, 101, 103, "idr").Validate());
        Assert.Throws<DomainValidationException>(() => new OperationalBookKey(100, 0, 103, "IDR").Validate());
        Assert.Throws<DomainValidationException>(() => JournalDraft.Create(new(Source, 1, Book, new(100, 999, 103, null, null, null), new(2026, 9, 28), Snapshot().Lines), Chart()));
    }

    [Fact]
    public void Malformed_inputs_line_bounds_and_precision_deny()
    {
        Assert.Throws<DomainValidationException>(() => new JournalSource("bad:namespace", 1, "receipt", 1).Validate());
        Assert.Throws<DomainValidationException>(() => new SourceSnapshot(Source, 0, Book, new(100, 101, 103, null, null, null), new(2026, 9, 28), Snapshot().Lines));
        Assert.Throws<DomainValidationException>(() => new SourceSnapshot(Source, 1, Book, new(100, 101, 103, null, null, null), default, Snapshot().Lines));
        Assert.Throws<DomainValidationException>(() => Snapshot([]));
        Assert.Throws<DomainValidationException>(() => Snapshot(Enumerable.Repeat(new PostingLine("inventory", new(1m), new(0m)), 101)));
        Assert.Throws<DomainValidationException>(() => new Money(1.001m));
    }

    [Theory]
    [InlineData("2026-09-30T16:59:59Z", "2026-09-30", "2026-09")]
    [InlineData("2026-09-30T17:00:00Z", "2026-10-01", "2026-10")]
    public void Jakarta_business_date_crosses_month_at_local_midnight(string instant, string date, string month)
    {
        var value = AccountingCalendar.BusinessDate(DateTimeOffset.Parse(instant));
        Assert.Equal(date, value.ToString("yyyy-MM-dd"));
        Assert.Equal(month, AccountingCalendar.Month(value));
    }
}
