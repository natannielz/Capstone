using System.Text;
using System.Text.RegularExpressions;

namespace UnitToko.Modules.Catalog;

public sealed record ProductMatch(string Kind, string Field, int? EditDistance, int Rank);

/// <summary>Search hints only. Neither a match nor its distance establishes product identity or package contents.</summary>
public static class ProductMatchRule
{
    public const string Version = "catalog-code-name-v1";
    public const int CandidateLimit = 256;
    public static string Normalize(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ").ToUpperInvariant();

    public static ProductMatch? Match(string query, string code, string name, string package)
    {
        query = Normalize(query); code = Normalize(code); name = Normalize(name); package = Normalize(package);
        if (query.Length == 0) return new("browse", "name", null, 5);
        if (query == code) return new("exact_code", "sku", 0, 0);
        if (query == name) return new("exact_name", "name", 0, 1);
        var needles = Tokens(query);
        var fields = new[] { (name, "name"), (package, "package"), (code, "sku") };
        foreach (var (value, field) in fields)
            if (value.Contains(query, StringComparison.Ordinal)) return new("contains", field, null, 2);
        foreach (var (value, field) in fields) {
            var words = Tokens(value);
            if (needles.Length is > 0 and <= 4 && needles.All(token => token.Length >= 2 && words.Any(word => word.StartsWith(token, StringComparison.Ordinal))))
                return new("token_prefix", field, null, 3);
        }
        // ponytail: at most four 3–32-letter query tokens and 256 scoped fuzzy candidates; add an indexed search owner if larger catalogues need exhaustive typo search.
        if (needles.Length is 0 or > 4 || needles.Any(token => token.Length is < 3 or > 32 || !token.All(char.IsLetter))) return null;
        var names = Tokens(name).Where(token => token.Length is >= 3 and <= 32 && token.All(char.IsLetter)).ToArray();
        if (names.Length == 0) return null;
        var total = 0;
        foreach (var token in needles) {
            var limit = token.Length < 6 ? 1 : 2;
            var distance = names.Select(word => Distance(token, word, limit)).Min();
            if (distance > limit) return null;
            total += distance;
        }
        return new("similar_name", "name", total, 4);
    }

    private static string[] Tokens(string value) => Regex.Split(value, @"[^\p{L}\p{N}]+").Where(token => token.Length > 0).ToArray();
    private static int Distance(string left, string right, int limit)
    {
        if (Math.Abs(left.Length - right.Length) > limit) return limit + 1;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray(); var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++) {
            current[0] = i; var best = i;
            for (var j = 1; j <= right.Length; j++) {
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
                best = Math.Min(best, current[j]);
            }
            if (best > limit) return limit + 1;
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }
}
