using System.Buffers;
using System.Globalization;
using System.Text;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Reporting;

public sealed record AccountingReportReadMetadata(string Purpose, string CorrelationId)
{
    public const string ReportingPurpose = "accounting-reporting";
    public void Validate()
    {
        if (Purpose != ReportingPurpose || CorrelationId is not { Length: >= 1 and <= 128 } ||
            CorrelationId != CorrelationId.Trim() || !ValidText(CorrelationId))
            throw new DomainValidationException("invalid_accounting_report_metadata", "Select the accounting reporting purpose and provide a bounded request reference");
    }
    private static bool ValidText(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty) {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done ||
                Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format) return false;
            remaining = remaining[consumed..];
        }
        return value.IsNormalized(NormalizationForm.FormC);
    }
}

public static class AccountingReportRules
{
    public const int MaximumBooks = 128, MaximumJournals = 500, MaximumLines = 10_000, MaximumAccounts = 2_000, MaximumCharts = 64;
    public const int MaximumCsvBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(15);
    public static DateOnly EndDate(string value)
    {
        if (value is not { Length: 10 } || !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) || date == default || value != date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            throw new DomainValidationException("invalid_accounting_report_date", "Use a valid calendar date in yyyy-MM-dd format");
        return date;
    }
    public static void Binding(string value)
    {
        if (value is not { Length: 64 } || value.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new DomainValidationException("invalid_accounting_report_binding", "Use the snapshot reference returned by the report");
    }
    public static DomainValidationException TooLarge() => new("accounting_report_too_large",
        "This cumulative selection exceeds the supported report limit; select an earlier end date or request an approved bulk reporting workflow");
}

public sealed record AccountingReportBookKey(LongId OrganizationId, LongId ShopId, LongId ChannelId, string Currency);
public sealed record AccountingReportLabel(LongId Id, string Code, string Name);
public sealed record AccountingReportBook(LongId Id, AccountingReportBookKey Key, AccountingReportLabel Organization,
    AccountingReportLabel Shop, AccountingReportLabel Channel);
public sealed record AccountingReportBooks(LongId ReadId, IReadOnlyList<AccountingReportBook> Items);
public sealed record AccountingReportAccount(LongId AccountId, IReadOnlyList<string> AccountCodes, Money Debit, Money Credit, SignedMoney Balance);
public sealed record AccountingReportLine(string MappingKey, LongId AccountId, string AccountCode, Money Debit, Money Credit);
public sealed record AccountingReportSource(string Namespace, LongId DocumentId, string EventKind, LongId Occurrence);
public sealed record AccountingReportJournal(LongId Id, string BusinessDate, AccountingReportSource Source,
    LongId ChartId, LongId ChartVersion, LongId? ReversalOf, string Description, IReadOnlyList<AccountingReportLine> Lines);
/// <summary>A cumulative persisted-journal view. It grants no posting, valuation, stock or approved-close authority.</summary>
public sealed record AccountingReportSnapshot(LongId ReadId, string SnapshotHash, AccountingReportBook Book, string EndDate,
    DateTimeOffset CapturedAt, DateTimeOffset ExpiresAt, Money Debit, Money Credit,
    IReadOnlyList<AccountingReportAccount> Accounts, IReadOnlyList<AccountingReportJournal> Journals)
{
    public string TimeZone => "Asia/Jakarta";
    public bool Cumulative => true;
    public bool IsApprovedClose => false;
}
public sealed record AccountingReportCsv(LongId ReadId, LongId SnapshotReadId, string SnapshotHash, string FileName, byte[] Bytes);
