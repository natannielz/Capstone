namespace UnitToko.Platform.Identity;

/// <summary>Transport settings, not evidence that provider lifecycle coverage has been accepted.</summary>
public sealed class Auth0SecurityEventOptions
{
    public const string SectionName = "Identity:Auth0SecurityEvents";
    public bool EventsEnabled { get; set; }
    public bool LogsEnabled { get; set; }
    public string Issuer { get; set; } = "";
    public string EventSource { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string EventStreamId { get; set; } = "";
    public string EventsBearerToken { get; set; } = "";
    public string PreviousEventsBearerToken { get; set; } = "";
    public string LogsBearerToken { get; set; } = "";
    public string PreviousLogsBearerToken { get; set; } = "";

    public void Validate()
    {
        if (!EventsEnabled && !LogsEnabled) return;
        if (!ValidIssuer(Issuer)) throw Invalid();
        var credentials = new List<string>();
        if (EventsEnabled)
        {
            if (EventSource is not { Length: > 10 and <= 300 } || !EventSource.StartsWith("urn:auth0:", StringComparison.Ordinal) ||
                EventSource.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not (':' or '.' or '-' or '_' or '/')) ||
                TenantId is not { Length: >= 3 and <= 63 } || !char.IsAsciiLetterOrDigit(TenantId[0]) || !char.IsAsciiLetterOrDigit(TenantId[^1]) ||
                TenantId.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')) ||
                !ProviderId(EventStreamId, "est_")) throw Invalid();
            AddCredential(EventsBearerToken, PreviousEventsBearerToken, credentials);
        }
        if (LogsEnabled) AddCredential(LogsBearerToken, PreviousLogsBearerToken, credentials);
        if (credentials.Distinct(StringComparer.Ordinal).Count() != credentials.Count) throw Invalid();
    }

    private static void AddCredential(string current, string previous, List<string> credentials)
    {
        if (!ValidSecret(current) || !string.IsNullOrEmpty(previous) && !ValidSecret(previous)) throw Invalid();
        credentials.Add(current);
        if (!string.IsNullOrEmpty(previous)) credentials.Add(previous);
    }
    // Configuration cannot prove entropy. Require generated random secrets operationally; these
    // format/length/diversity checks reject empty, short and trivially repeated credentials.
    private static bool ValidSecret(string value) => BearerCharacters(value) && value.Length >= 43 &&
        value.Distinct().Count() >= 12;
    internal static bool BearerCharacters(string? value) => value is { Length: > 0 and <= 512 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~' or '+' or '/' or '=');
    internal static bool ProviderId(string? value, string prefix) => value is not null && value.Length == prefix.Length + 22 &&
        value.StartsWith(prefix, StringComparison.Ordinal) && value.AsSpan(prefix.Length).ContainsAnyExcept(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789".AsSpan()) == false;
    private static bool ValidIssuer(string value) => value is { Length: > 0 and <= 300 } &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.HostNameType == UriHostNameType.Dns && !uri.IsLoopback && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == "" &&
        uri.AbsolutePath == "/" && value == uri.GetLeftPart(UriPartial.Authority) + "/";
    private static InvalidOperationException Invalid() => new("Enabled provider event transports require reviewed HTTPS issuer, exact stream binding and separate strong bearer credentials.");
}
