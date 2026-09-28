using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Accounting;
public sealed record OperationalBookKey(long OrganizationId, long ShopId, long ChannelId, string Currency)
{
    public OperationalBookKey Validate()
    {
        AccountingValidation.Positive(OrganizationId, ShopId, ChannelId);
        if (Currency is null || !Regex.IsMatch(Currency, @"\A[A-Z]{3}\z", RegexOptions.CultureInvariant))
            throw AccountingValidation.Invalid("invalid_currency");
        return this;
    }

    public string CanonicalKey => string.Create(CultureInfo.InvariantCulture, $"{OrganizationId}:{ShopId}:{ChannelId}:{Currency}");
}

public sealed record JournalSource(string Namespace, long DocumentId, string EventKind, long Occurrence)
{
    public JournalSource Validate()
    {
        AccountingValidation.Code(Namespace);
        AccountingValidation.Code(EventKind);
        AccountingValidation.Positive(DocumentId, Occurrence);
        return this;
    }

    public string EventKey => string.Create(CultureInfo.InvariantCulture, $"{Namespace}:{DocumentId}:{EventKind}:{Occurrence}");
}

public sealed record JournalDimensions(long OrganizationId, long ShopId, long ChannelId, long? WarehouseId, long? BuyingOrganizationId, long? BranchId)
{
    public void Validate(OperationalBookKey book)
    {
        AccountingValidation.Positive(OrganizationId, ShopId, ChannelId);
        foreach (var id in new[]
        {
            WarehouseId,
            BuyingOrganizationId,
            BranchId
        }

        )
            if (id.HasValue)
                AccountingValidation.Positive(id.Value);
        if (OrganizationId != book.OrganizationId || ShopId != book.ShopId || ChannelId != book.ChannelId || BranchId.HasValue && !BuyingOrganizationId.HasValue)
            throw AccountingValidation.Invalid("source_scope_mismatch");
    }
}

public sealed record PostingLine(string MappingKey, Money Debit, Money Credit);
public sealed record AccountMapping(string MappingKey, long AccountId, string AccountCode, bool Active);
public sealed class ChartSnapshot
{
    public long Id { get; }
    public long BookId { get; }
    public long Version { get; }
    public ImmutableArray<AccountMapping> Mappings { get; }

    public ChartSnapshot(long id, long bookId, long version, IEnumerable<AccountMapping> mappings)
    {
        AccountingValidation.Positive(id, bookId, version);
        Id = id;
        BookId = bookId;
        Version = version;
        Mappings = AccountingValidation.Copy(mappings, 1, 1000);
        foreach (var mapping in Mappings)
        {
            if (mapping is null)
                throw AccountingValidation.Invalid("invalid_chart");
            AccountingValidation.Code(mapping.MappingKey);
            AccountingValidation.Code(mapping.AccountCode);
            AccountingValidation.Positive(mapping.AccountId);
        }

        if (Mappings.Select(item => item.MappingKey).Distinct(StringComparer.Ordinal).Count() != Mappings.Length || Mappings.GroupBy(item => item.AccountId).Any(group => group.Select(item => item.AccountCode).Distinct().Count() != 1) || Mappings.GroupBy(item => item.AccountCode).Any(group => group.Select(item => item.AccountId).Distinct().Count() != 1))
            throw AccountingValidation.Invalid("invalid_chart");
    }
}

public sealed class SourceSnapshot
{
    public JournalSource Source { get; }
    public long Revision { get; }
    public OperationalBookKey Book { get; }
    public JournalDimensions Dimensions { get; }
    public DateOnly Date { get; }
    public ImmutableArray<PostingLine> Lines { get; }

    public SourceSnapshot(JournalSource source, long revision, OperationalBookKey book, JournalDimensions dimensions, DateOnly date, IEnumerable<PostingLine> lines)
    {
        if (source is null || book is null || dimensions is null)
            throw AccountingValidation.Invalid("invalid_source");
        Source = source.Validate();
        Book = book.Validate();
        dimensions.Validate(book);
        AccountingValidation.Positive(revision);
        AccountingCalendar.Validate(date);
        Revision = revision;
        Dimensions = dimensions;
        Date = date;
        Lines = AccountingValidation.Copy(lines, 2, 100);
        foreach (var line in Lines)
        {
            if (line is null)
                throw AccountingValidation.Invalid("invalid_journal_line");
            AccountingValidation.Code(line.MappingKey);
            AccountingValidation.Line(line.Debit, line.Credit);
        }
    }
}

public sealed record ResolvedJournalLine(string MappingKey, long AccountId, string AccountCode, Money Debit, Money Credit);
public sealed class JournalDraft
{
    public JournalSource Source { get; }
    public long SourceRevision { get; }
    public OperationalBookKey Book { get; }
    public JournalDimensions Dimensions { get; }
    public DateOnly Date { get; }
    public long ChartId { get; }
    public long ChartVersion { get; }
    public long? ReversalOf { get; }
    public Money Total { get; }
    public string Fingerprint { get; }
    public ImmutableArray<ResolvedJournalLine> Lines { get; }

    private JournalDraft(JournalSource source, long sourceRevision, OperationalBookKey book, JournalDimensions dimensions, DateOnly date, long chartId, long chartVersion, IEnumerable<ResolvedJournalLine> lines, long? reversalOf)
    {
        if (source is null || book is null || dimensions is null)
            throw AccountingValidation.Invalid("invalid_journal");
        Source = source.Validate();
        AccountingValidation.Positive(sourceRevision, chartId, chartVersion);
        Book = book.Validate();
        dimensions.Validate(book);
        AccountingCalendar.Validate(date);
        SourceRevision = sourceRevision;
        Dimensions = dimensions;
        Date = date;
        ChartId = chartId;
        ChartVersion = chartVersion;
        ReversalOf = reversalOf;
        if (reversalOf.HasValue)
            AccountingValidation.Positive(reversalOf.Value);
        Lines = AccountingValidation.Copy(lines, 2, 100);
        Money debit = new(0m), credit = new(0m);
        foreach (var line in Lines)
        {
            if (line is null)
                throw AccountingValidation.Invalid("invalid_journal_line");
            AccountingValidation.Code(line.MappingKey);
            AccountingValidation.Code(line.AccountCode);
            AccountingValidation.Positive(line.AccountId);
            AccountingValidation.Line(line.Debit, line.Credit);
            debit = Money.Add(debit, line.Debit);
            credit = Money.Add(credit, line.Credit);
        }

        if (debit.Value <= 0 || debit != credit)
            throw AccountingValidation.Invalid("unbalanced_journal");
        Total = debit;
        Fingerprint = AccountingValidation.Hash(new { Source, SourceRevision, Book, Dimensions, Date = Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ChartId, ChartVersion, ReversalOf, Lines });
    }

    public static JournalDraft Create(SourceSnapshot source, ChartSnapshot chart)
    {
        if (source is null || chart is null)
            throw AccountingValidation.Invalid("invalid_journal");
        var mappings = chart.Mappings.ToDictionary(item => item.MappingKey, StringComparer.Ordinal);
        var lines = source.Lines.Select(line =>
        {
            if (!mappings.TryGetValue(line.MappingKey, out var account) || !account.Active)
                throw AccountingValidation.Invalid("account_unavailable");
            return new ResolvedJournalLine(line.MappingKey, account.AccountId, account.AccountCode, line.Debit, line.Credit);
        });
        return new(source.Source, source.Revision, source.Book, source.Dimensions, source.Date, chart.Id, chart.Version, lines, null);
    }

    // Only a trusted owning reversal source may call the persistence writer. Current chart mappings never rewrite history.
    public static JournalDraft Reverse(JournalSource source, long revision, OperationalBookKey book, JournalDimensions dimensions, DateOnly date, long originalId, long originalChartId, long originalChartVersion, IEnumerable<ResolvedJournalLine> originalLines)
    {
        var copy = AccountingValidation.Copy(originalLines, 2, 100);
        if (copy.Any(line => line is null))
            throw AccountingValidation.Invalid("invalid_journal_line");
        return new(source, revision, book, dimensions, date, originalChartId, originalChartVersion, copy.Select(line => line with { Debit = line.Credit, Credit = line.Debit }), originalId);
    }

    // Revalidates stored immutable accounting content and computes its canonical fingerprint; grants no write authority.
    public static JournalDraft FromResolved(JournalSource source, long revision, OperationalBookKey book, JournalDimensions dimensions, DateOnly date, long chartId, long chartVersion, IEnumerable<ResolvedJournalLine> lines, long? reversalOf) => new(source, revision, book, dimensions, date, chartId, chartVersion, lines, reversalOf);
}

public static class AccountingCalendar
{
    private static readonly TimeZoneInfo Jakarta = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jakarta");
    public static DateOnly BusinessDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Jakarta).DateTime);
    public static string Month(DateOnly date)
    {
        Validate(date);
        return date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    }

    public static void Validate(DateOnly date)
    {
        if (date == default)
            throw AccountingValidation.Invalid("invalid_business_date");
    }

    public static void ValidateMonth(string month)
    {
        if (month is null || !DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || month != date.ToString("yyyy-MM", CultureInfo.InvariantCulture))
            throw AccountingValidation.Invalid("invalid_period");
    }
}

public static class AccountingValidation
{
    public static DomainValidationException Invalid(string code) => new(code, "Accounting data is invalid or unavailable");
    public static void Positive(params long[] ids)
    {
        if (ids.Any(id => id <= 0))
            throw Invalid("invalid_accounting_id");
    }

    public static void Code(string code)
    {
        if (code is null || !Regex.IsMatch(code, @"\A[A-Za-z][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant))
            throw Invalid("invalid_accounting_code");
    }

    public static void Line(Money debit, Money credit)
    {
        _ = new Money(debit.Value);
        _ = new Money(credit.Value);
        if (debit.Value <= 0 == credit.Value <= 0)
            throw Invalid("invalid_journal_line");
    }

    public static ImmutableArray<T> Copy<T>(IEnumerable<T> values, int minimum, int maximum)
    {
        if (values is null)
            throw Invalid("invalid_accounting_array");
        var copy = values.Take(maximum + 1).ToImmutableArray();
        if (copy.Length < minimum || copy.Length > maximum)
            throw Invalid("invalid_accounting_array");
        return copy;
    }

    public static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value))).ToLowerInvariant();
}
