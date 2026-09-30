using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Billing;

// A bank-controlled transaction ID is an identity candidate, not a confirmed receipt.
// Neither a reference, a filename nor an image/file hash establishes transaction identity.
public sealed record BankStatementFormat(string Delimiter, string DateFormat, string DecimalSeparator,
    string CreditMarker, string DebitMarker, ImmutableArray<string> Columns, string? IdentityNamespace);
public sealed record BankStatementRow(int Number, string? TransactionId, DateOnly BookedDate, string Direction,
    Money Amount, string? PayerName, string? Reference, string? IdentityCandidate);
public sealed record BankStatementIssue(int Row, string Field, string Code);
public sealed record BankStatementPreview(string SourceSha256, ImmutableArray<BankStatementRow> Rows,
    ImmutableArray<BankStatementIssue> Issues, Money Credits, Money Debits) {
    public bool CanStage => Rows.Length > 0 && Issues.All(issue => issue.Code is "identity_missing" or "identity_repeated" or "reference_repeated" or "payer_unknown");
}

public static class BankStatementParser
{
    public const int MaximumBytes = 262144, MaximumRows = 500, MaximumCellLength = 512;
    public static readonly ImmutableArray<string> Fields = ["transactionId", "bookedDate", "direction", "amount", "payerName", "reference", "accountNumber", "currency"];
    public static void Validate(BankStatementFormat format) {
        if (format is null || format.Delimiter is not ("," or ";" or "\t") || format.DateFormat is not ("yyyy-MM-dd" or "dd/MM/yyyy") ||
            format.DecimalSeparator is not ("." or ",") || !Text(format.CreditMarker, 16) || !Text(format.DebitMarker, 16) || format.CreditMarker == format.DebitMarker ||
            format.Columns.IsDefault || format.Columns.Length != Fields.Length || format.Columns.Any(value => !Text(value, 80)) || format.Columns.Distinct(StringComparer.Ordinal).Count() != Fields.Length ||
            format.IdentityNamespace is not null && (!Text(format.IdentityNamespace, 80) || format.IdentityNamespace.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))) throw Invalid();
    }
    public static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= maximum &&
        !value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format);
    public static DomainValidationException Invalid(string code = "invalid_bank_statement") => new(code, "Use the approved bank statement format and an explicit receiving account version");
    public static string Digest(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));
    public static string Candidate(string physicalAccountIdentity, string identityNamespace, string transactionId) =>
        Digest(Encoding.UTF8.GetBytes($"{physicalAccountIdentity.Length}:{physicalAccountIdentity}{identityNamespace.Length}:{identityNamespace}{transactionId.Length}:{transactionId}"));

    public static BankStatementPreview Parse(ReadOnlySpan<byte> bytes, BankStatementFormat format, ReceivingAccountDetails account,
        string physicalAccountIdentity, DateOnly today) {
        Validate(format); _ = ReceivingAccountPolicy.Copy(account); AccountingCalendar.Validate(today);
        if (bytes.Length is < 1 or > MaximumBytes || physicalAccountIdentity is null || physicalAccountIdentity.Length != 64 || physicalAccountIdentity.Any(c => !char.IsAsciiHexDigitLower(c))) throw Invalid();
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw Invalid("bank_statement_utf8_required"); }
        if (text.StartsWith('\uFEFF')) text = text[1..];
        var cells = Csv(text, format.Delimiter[0]);
        if (cells.Count < 2 || !cells[0].SequenceEqual(format.Columns)) throw Invalid("bank_statement_header_mismatch");
        var issues = ImmutableArray.CreateBuilder<BankStatementIssue>(); var rows = ImmutableArray.CreateBuilder<BankStatementRow>();
        var identities = new HashSet<string>(StringComparer.Ordinal); var references = new HashSet<string>(StringComparer.Ordinal);
        var credits = new Money(0); var debits = new Money(0);
        for (var index = 1; index < cells.Count; index++) {
            var row = cells[index]; var number = index + 1;
            if (row.Length != Fields.Length) { issues.Add(new(number, "row", "column_count")); continue; }
            var before = issues.Count;
            for (var column = 0; column < row.Length; column++) if (row[column].Length > 0 && !Text(row[column], column is 4 or 5 ? 160 : 80)) issues.Add(new(number, Fields[column], "invalid_text"));
            if (!DateOnly.TryParseExact(row[1], format.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date == default || date > today)
                issues.Add(new(number, "bookedDate", "invalid_date"));
            var direction = row[2] == format.CreditMarker ? "credit" : row[2] == format.DebitMarker ? "debit" : "";
            if (direction.Length == 0) issues.Add(new(number, "direction", "invalid_direction"));
            var amount = new Money(0);
            try {
                // Grouping separators and implicit number repair are deliberately unsupported.
                var value = row[3];
                if (format.DecimalSeparator == ",") { if (value.Contains('.')) throw Invalid(); value = value.Replace(',', '.'); }
                amount = Money.Parse(value); if (amount.Value <= 0) throw Invalid();
            } catch (DomainValidationException) { issues.Add(new(number, "amount", "invalid_amount")); }
            if (row[6] != account.AccountNumber) issues.Add(new(number, "accountNumber", "account_mismatch"));
            if (row[7] != account.Currency) issues.Add(new(number, "currency", "currency_mismatch"));
            if (issues.Count != before) continue;
            string? identity = null;
            if (format.IdentityNamespace is null || row[0].Length == 0) issues.Add(new(number, "transactionId", "identity_missing"));
            else { identity = Candidate(physicalAccountIdentity, format.IdentityNamespace, row[0]); if (!identities.Add(identity)) issues.Add(new(number, "transactionId", "identity_repeated")); }
            if (row[5].Length > 0 && !references.Add(row[5])) issues.Add(new(number, "reference", "reference_repeated"));
            if (row[4].Length == 0) issues.Add(new(number, "payerName", "payer_unknown"));
            rows.Add(new(number, row[0].Length == 0 ? null : row[0], date, direction, amount, row[4].Length == 0 ? null : row[4], row[5].Length == 0 ? null : row[5], identity));
            try { if (direction == "credit") credits = Money.Add(credits, amount); else debits = Money.Add(debits, amount); }
            catch (DomainValidationException) { issues.Add(new(number, "amount", "total_out_of_range")); }
        }
        return new(Digest(bytes), rows.ToImmutable(), issues.ToImmutable(), credits, debits);
    }

    private static List<string[]> Csv(string text, char delimiter) {
        var rows = new List<string[]>(); var row = new List<string>(); var cell = new StringBuilder();
        var quoted = false; var closed = false; var ended = false;
        void Field() { row.Add(cell.ToString()); cell.Clear(); closed = false; if (row.Count > Fields.Length) throw Invalid("bank_statement_column_limit"); }
        void Row() { Field(); rows.Add(row.ToArray()); row.Clear(); if (rows.Count > MaximumRows + 1) throw Invalid("bank_statement_row_limit"); }
        for (var i = 0; i < text.Length; i++) {
            var c = text[i]; ended = false;
            if (quoted) {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else { quoted = false; closed = true; } }
                else cell.Append(c);
            } else if (c == delimiter) Field();
            else if (c is '\r' or '\n') {
                if (c == '\r') { if (i + 1 >= text.Length || text[i + 1] != '\n') throw Invalid("bank_statement_invalid_csv"); i++; }
                Row(); ended = true;
            } else if (c == '"' && cell.Length == 0 && !closed) quoted = true;
            else { if (closed || c == '"') throw Invalid("bank_statement_invalid_csv"); cell.Append(c); }
            if (cell.Length > MaximumCellLength) throw Invalid("bank_statement_cell_limit");
        }
        if (quoted) throw Invalid("bank_statement_invalid_csv");
        if (!ended && (cell.Length > 0 || row.Count > 0 || closed)) Row();
        return rows;
    }
}
