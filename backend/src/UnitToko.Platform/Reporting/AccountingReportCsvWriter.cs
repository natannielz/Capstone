using System.Globalization;
using System.Text;
using System.Buffers;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Reporting;

namespace UnitToko.Platform.Reporting;

internal static class AccountingReportCsvWriter
{
    internal static byte[] Create(AccountingReportSnapshot snapshot)
    {
        var writer = new Writer();
        writer.TextRow("Unit Toko — neraca saldo dan jurnal tersimpan", "Bukan penutupan buku yang disetujui");
        writer.TextRow("Organisasi", snapshot.Book.Organization.Name, "Toko", snapshot.Book.Shop.Name, "Kanal", snapshot.Book.Channel.Name,
            "Mata uang", snapshot.Book.Key.Currency);
        writer.TextRow("Tanggal akhir inklusif", snapshot.EndDate, "Zona waktu", snapshot.TimeZone, "Cakupan", "Kumulatif sejak awal buku");
        writer.TextRow("Snapshot", snapshot.SnapshotHash, "Dicatat pada", snapshot.CapturedAt.ToString("O", CultureInfo.InvariantCulture));
        writer.TextRow("ID akun", "Kode akun pada bagan terkait", "Debit", "Kredit", "Saldo debit dikurangi kredit");
        foreach (var row in snapshot.Accounts)
            writer.Row(Id(row.AccountId), Text(string.Join(" / ", row.AccountCodes)), Number(row.Debit.Value), Number(row.Credit.Value), Number(row.Balance.Value));
        writer.Row(Text("TOTAL"), Text(""), Number(snapshot.Debit.Value), Number(snapshot.Credit.Value), Number(snapshot.Debit.Value - snapshot.Credit.Value));
        writer.TextRow("");
        writer.TextRow("ID jurnal", "Tanggal bisnis", "Sumber", "ID dokumen sumber", "Peristiwa", "Kejadian", "ID bagan", "Versi bagan",
            "Pembalikan jurnal", "Keterangan tersimpan", "ID akun", "Kode akun", "Pemetaan", "Debit", "Kredit");
        foreach (var journal in snapshot.Journals)
            foreach (var line in journal.Lines)
                writer.Row(Id(journal.Id), Text(journal.BusinessDate), Text(journal.Source.Namespace), Id(journal.Source.DocumentId),
                    Text(journal.Source.EventKind), Id(journal.Source.Occurrence), Id(journal.ChartId), Id(journal.ChartVersion),
                    journal.ReversalOf is { } original ? Id(original) : Text(""), Text(journal.Description), Id(line.AccountId),
                    Text(line.AccountCode), Text(line.MappingKey), Number(line.Debit.Value), Number(line.Credit.Value));
        return writer.Finish();
    }
    // Text and trusted numeric cells have separate entry points. Negative balances remain numeric.
    internal static string Text(string value)
    {
        var significant = value.AsSpan();
        while (!significant.IsEmpty && Rune.DecodeFromUtf16(significant, out var rune, out var consumed) == OperationStatus.Done &&
            (Rune.IsWhiteSpace(rune) || Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)) significant = significant[consumed..];
        if ((!significant.IsEmpty && significant[0] is '=' or '+' or '-' or '@') ||
            (value.Length > 0 && value[0] is '\t' or '\r' or '\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
    private static string Id(LongId value) => Text("'" + value.ToString()); // Preserve every Int64 digit in spreadsheet imports.
    private static string Number(decimal value) => DomainValues.FormatMoney(value);
    private sealed class Writer
    {
        private readonly StringBuilder content = new();
        private int bytes = 3; // UTF-8 BOM is part of the size limit.
        internal void TextRow(params string[] values) => Row(values.Select(Text).ToArray());
        internal void Row(params string[] cells)
        {
            var row = string.Join(',', cells) + "\r\n";
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(row));
            if (bytes > AccountingReportRules.MaximumCsvBytes) throw AccountingReportRules.TooLarge();
            content.Append(row);
        }
        internal byte[] Finish()
        {
            var result = new byte[bytes]; result[0] = 0xef; result[1] = 0xbb; result[2] = 0xbf;
            Encoding.UTF8.GetBytes(content.ToString(), result.AsSpan(3)); return result;
        }
    }
}
