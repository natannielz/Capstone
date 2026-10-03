using System.Globalization;
using System.Text.RegularExpressions;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Documents;

public sealed record SupplierDocumentFieldCandidate
{
    public string Field { get; }
    public string? Value { get; }
    public decimal? Confidence { get; }
    public IReadOnlyList<decimal>? BoundingBox { get; }
    public IReadOnlyList<string> Flags { get; }
    public SupplierDocumentFieldCandidate(string field, string? value, decimal? confidence, NormalizedBox? box, IEnumerable<string> flags) {
        if (!SupplierDocumentFieldExtractor.Fields.Contains(field, StringComparer.Ordinal)) throw ExtractionBounds.Invalid();
        if (value is not null) ExtractionBounds.Text(value, 256);
        if (confidence is { } number) ExtractionBounds.Confidence(number);
        Field = field; Value = value; Confidence = confidence; BoundingBox = box?.Values; Flags = ExtractionBounds.Flags(flags);
    }
}

public sealed record SupplierDocumentExtraction(IReadOnlyList<SupplierDocumentFieldCandidate> Fields, IReadOnlyList<string> QualityFlags);

// Explicit labels only. These suggestions never infer a purchase order line, quantity, tax, or approved total.
public static class SupplierDocumentFieldExtractor
{
    public const string Version = "supplier-document-labels-id-en-v1";
    public static IReadOnlyList<string> Fields { get; } = Array.AsReadOnly(new[] { "document_reference", "document_date", "supplier_name", "total_amount", "currency", "purchase_order_reference", "delivery_reference", "source_sku" });
    private static readonly IReadOnlyDictionary<string, string[]> Labels = new Dictionary<string, string[]> {
        ["document_reference"] = ["nomor invoice", "no. invoice", "invoice number", "nomor faktur", "no. faktur", "nomor sph", "no. sph", "statement number"],
        ["document_date"] = ["tanggal invoice", "invoice date", "tanggal faktur", "tanggal sph", "statement date", "tanggal dokumen", "document date"],
        ["supplier_name"] = ["nama pemasok", "supplier name", "nama penjual", "seller name"],
        ["total_amount"] = ["total tagihan", "total invoice", "invoice total", "total sph", "statement total"],
        ["purchase_order_reference"] = ["nomor po", "no. po", "purchase order number", "purchase order reference"],
        ["delivery_reference"] = ["nomor surat jalan", "no. surat jalan", "delivery note number", "delivery reference"],
        ["source_sku"] = ["kode barang pemasok", "supplier item code", "supplier sku", "source sku"]
    };
    public static SupplierDocumentExtraction Extract(IEnumerable<RecognizedLine> recognized) {
        var lines = ExtractionBounds.Lines(recognized); var fields = new Dictionary<string, SupplierDocumentFieldCandidate>();
        foreach (var name in Fields.Where(field => field != "currency")) {
            var matches = lines.Select(line => (Line: line, Value: Label(name, line.Text))).Where(pair => pair.Value is not null).ToArray();
            if (matches.Length != 1) {
                var flags = new[] { matches.Length == 0 ? "unknown" : "ambiguous" };
                fields[name] = new(name, null, null, null, flags);
                if (name == "total_amount") fields["currency"] = new("currency", null, null, null, flags);
                continue;
            }
            var (line, input) = matches[0]; var reasons = new List<string>();
            if (line.Confidence < .90m) reasons.Add("low_confidence");
            string? value;
            if (name == "total_amount") {
                var currency = Match(input!, @"^(Rp\.?|IDR)\s*(.+)$");
                value = currency.Success ? Amount(currency.Groups[2].Value) : null;
                fields["currency"] = new("currency", currency.Success ? "IDR" : null, line.Confidence, line.Box,
                    currency.Success ? reasons : reasons.Append("unknown"));
            } else if (name == "document_date") {
                value = DateOnly.TryParseExact(input, ["dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
            } else value = input!.Length is > 0 and <= 256 ? input : null;
            if (value is null) reasons.Add("unparsed");
            fields[name] = new(name, value, line.Confidence, line.Box, reasons);
        }
        var quality = new List<string> { "human_line_matching_required" };
        if (lines.Count == 0) quality.Add("no_text");
        if (fields["total_amount"].Value is null) quality.Add("amount_unknown");
        if (lines.Any(line => line.Confidence < .90m)) quality.Add("low_confidence");
        if (lines.Any(line => line.Box.X == 0 || line.Box.Y == 0 || line.Box.X + line.Box.Width == 1 || line.Box.Y + line.Box.Height == 1)) quality.Add("touches_image_edge");
        if (fields.Values.Any(field => field.Flags.Contains("ambiguous"))) quality.Add("ambiguous_fields");
        return new(Array.AsReadOnly(Fields.Select(field => fields[field]).ToArray()), Array.AsReadOnly(quality.ToArray()));
    }
    private static string? Label(string name, string text) {
        foreach (var label in Labels[name]) if (text.StartsWith(label, StringComparison.OrdinalIgnoreCase)) {
            var rest = text[label.Length..];
            if (rest.StartsWith(':')) return rest[1..].Trim();
            if (rest.StartsWith(" :", StringComparison.Ordinal)) return rest[2..].Trim();
        }
        return null;
    }
    private static string? Amount(string input) {
        string normalized;
        if (Match(input, @"^(?:0|[1-9][0-9]*)(?:\.[0-9]{1,2})?$").Success) normalized = input;
        else if (Match(input, @"^(?:0|[1-9][0-9]*),[0-9]{1,2}$").Success) normalized = input.Replace(',', '.');
        else if (Match(input, @"^[1-9][0-9]{0,2}(?:\.[0-9]{3})+(?:,[0-9]{1,2})?$").Success) normalized = input.Replace(".", "", StringComparison.Ordinal).Replace(',', '.');
        else return null;
        try { return DomainValues.FormatMoney(DomainValues.ParseMoney(normalized)); } catch (DomainValidationException) { return null; }
    }
    private static Match Match(string input, string pattern) => Regex.Match(input, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
}
