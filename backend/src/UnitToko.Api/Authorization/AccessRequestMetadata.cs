using System.Globalization;
using UnitToko.Modules.Contracts;

namespace UnitToko.Api.Authorization;

public static class AccessRequestMetadata
{
    public static string IdempotencyKey(HttpRequest request)
    {
        var values = request.Headers["Idempotency-Key"];
        var key = values.Count == 1 ? values[0] : null;
        if (string.IsNullOrEmpty(key) || key.Length > 128 ||
            key.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':')))
            throw new DomainValidationException("invalid_idempotency_key", "A bounded Idempotency-Key is required");
        return key;
    }

    public static long ExpectedVersion(HttpRequest request)
    {
        var values = request.Headers.IfMatch;
        var value = values.Count == 1 ? values[0] : null;
        if (value is null || value.Length is < 3 or > 21 || value[0] != '"' || value[^1] != '"')
            throw InvalidVersion();
        var digits = value[1..^1];
        if (digits.Any(character => character is < '0' or > '9') ||
            (digits.Length > 1 && digits[0] == '0') ||
            !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            throw InvalidVersion();
        return version;
    }

    public static string VersionTag(long version)
    {
        if (version < 0) throw InvalidVersion();
        return '"' + version.ToString(CultureInfo.InvariantCulture) + '"';
    }

    private static DomainValidationException InvalidVersion() =>
        new("invalid_version", "If-Match must contain one quoted nonnegative Int64 version");
}
