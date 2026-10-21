using System.Text.RegularExpressions;

namespace UnitToko.Platform.Identity;

/// <summary>
/// A local sign-in return destination: a canonical path, optionally followed by the record the page had open
/// (<c>?draft=7</c>, <c>?order=41</c>), so signing in again continues the same work (Review Focus 2).
/// The query grammar is deliberately narrow and matches the frontend's <c>return-query.ts</c>: at most four
/// <c>name=value</c> pairs of ASCII letters, digits, '-' and '_' only. It cannot carry another address, an encoded
/// character, a fragment or a second '?'. Which paths are allowed stays the API's reviewed allowlist.
/// </summary>
public static partial class ReturnDestination
{
    public const int MaximumLength = 200;

    [GeneratedRegex(@"\A[a-z][A-Za-z0-9]{0,31}=[A-Za-z0-9_-]{1,64}(?:&[a-z][A-Za-z0-9]{0,31}=[A-Za-z0-9_-]{1,64}){0,3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeQuery();

    public static bool IsSafeQuery(string? query) => query is { Length: > 0 } && SafeQuery().IsMatch(query);

    /// <summary>Splits a destination into its path and optional query; false when the query part is present but unsafe.</summary>
    public static bool TrySplit(string destination, out string path, out string? query)
    {
        var mark = destination.IndexOf('?', StringComparison.Ordinal);
        path = mark < 0 ? destination : destination[..mark];
        query = mark < 0 ? null : destination[(mark + 1)..];
        return query is null || IsSafeQuery(query);
    }
}
