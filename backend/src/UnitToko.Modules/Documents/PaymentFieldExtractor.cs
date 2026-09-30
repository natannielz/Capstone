using System.Globalization;
using System.Text.RegularExpressions;
using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Documents;

/// <summary>Conservative, versioned label parser. No fuzzy digit repair, inferred timezone, balance, fee or bank confirmation.</summary>
public static class PaymentFieldExtractor
{
    public const string Version = "payment-labels-id-en-v1";
    public static IReadOnlyList<string> Fields { get; } = Array.AsReadOnly(new[] { "amount", "currency", "transfer_date", "transfer_time", "recipient", "account", "bank", "reference" });
    private static readonly IReadOnlyDictionary<string, string[]> Labels = new Dictionary<string, string[]> {
        ["amount"] = ["nominal", "jumlah transfer", "jumlah", "transfer amount", "amount"],
        ["transfer_date"] = ["tanggal transfer", "tanggal", "transfer date", "date"],
        ["transfer_time"] = ["waktu transfer", "waktu", "transfer time", "time"],
        ["recipient"] = ["nama penerima", "penerima", "recipient", "beneficiary"],
        ["account"] = ["rekening tujuan", "no. rekening tujuan", "nomor rekening tujuan", "recipient account", "beneficiary account"],
        ["bank"] = ["bank tujuan", "recipient bank", "bank"],
        ["reference"] = ["no. referensi", "nomor referensi", "referensi", "reference", "reference number"]
    };
    public static PaymentExtraction Extract(IEnumerable<RecognizedLine> recognized) {
        var lines = ExtractionBounds.Lines(recognized);
        var parsed = new Dictionary<string, FieldCandidate>();
        foreach (var name in Fields.Where(n => n != "currency")) {
            var matches = lines.Select(line => (Line: line, Value: LabelValue(name, line.Text))).Where(v => v.Value is not null).ToArray();
            if (matches.Length != 1) {
                var flags = new[] { matches.Length == 0 ? "unknown" : "ambiguous" };
                parsed[name] = new(name, null, null, null, flags);
                if (name == "amount") parsed["currency"] = new("currency", null, null, null, flags);
                continue;
            }
            var (line, input) = matches[0]; var reasons = new List<string>();
            if (line.Confidence < .90m) reasons.Add("low_confidence");
            string? value;
            switch (name) {
                case "amount":
                    var currency = Match(input!, @"^(Rp\.?|IDR)\s*(.+)$");
                    value = currency.Success ? Amount(currency.Groups[2].Value) : null;
                    parsed["currency"] = new("currency", currency.Success ? "IDR" : null, line.Confidence, line.Box,
                        currency.Success ? reasons : reasons.Append("unknown"));
                    break;
                case "transfer_date":
                    value = DateOnly.TryParseExact(input, ["dd/MM/yyyy", "dd-MM-yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                        ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
                    break;
                case "transfer_time":
                    value = TimeOnly.TryParseExact(input, ["HH:mm:ss", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                        ? time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : null;
                    if (value is not null) reasons.Add("timezone_unverified");
                    break;
                case "account":
                    value = Match(input!, @"^[0-9*Xx]{4,34}$").Success ? input : null;
                    if (value?.Any(c => c is '*' or 'X' or 'x') == true) reasons.Add("masked");
                    break;
                default: value = input!.Length is > 0 and <= 256 ? input : null; break;
            }
            if (value is null) reasons.Add("unparsed");
            parsed[name] = new(name, value, line.Confidence, line.Box, reasons);
        }
        var quality = new List<string>();
        if (lines.Count == 0) quality.Add("no_text");
        if (parsed["amount"].Value is null) quality.Add("amount_unknown");
        if (lines.Any(l => l.Confidence < .90m)) quality.Add("low_confidence");
        if (lines.Any(l => l.Box.X == 0 || l.Box.Y == 0 || l.Box.X + l.Box.Width == 1 || l.Box.Y + l.Box.Height == 1)) quality.Add("touches_image_edge");
        if (parsed.Values.Any(f => f.Flags.Contains("ambiguous"))) quality.Add("ambiguous_fields");
        return new(Array.AsReadOnly(Fields.Select(f => parsed[f]).ToArray()), Array.AsReadOnly(quality.ToArray()));
    }
    private static string? LabelValue(string field, string text) {
        foreach (var label in Labels[field]) {
            if (!text.StartsWith(label, StringComparison.OrdinalIgnoreCase)) continue;
            var remainder = text[label.Length..];
            // A colon is mandatory; e.g. "jumlah biaya" is not the label "jumlah".
            if (remainder.StartsWith(':')) return remainder[1..].Trim();
            if (remainder.StartsWith(" :", StringComparison.Ordinal)) return remainder[2..].Trim();
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
    private static Match Match(string value, string pattern) => Regex.Match(value, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
}
