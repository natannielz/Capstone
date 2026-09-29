using Microsoft.Extensions.Options;

namespace UnitToko.Api.Identity;

public sealed class Auth0Options
{
    public const string SectionName = "Identity:Auth0";
    public const string Scheme = "UnitTokoOidc";
    public const string CallbackPath = "/auth/callback";
    public const string SignedOutCallbackPath = "/auth/signed-out";
    public bool Enabled { get; set; }
    public string Issuer { get; set; } = "";
    public string PublicOrigin { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string ConnectionName { get; set; } = "";
    public string ClaimsNamespace { get; set; } = "";
    public string AssurancePolicyVersion { get; set; } = "unit-toko-otp-v1";
    public string[] AllowedReturnPaths { get; set; } = ["/", "/identity/account", "/administration/access", "/administration/organizations", "/administration/history", "/administration/person-reviews", "/administration/catalog", "/catalog/prices", "/catalog/browse", "/ordering/drafts", "/ordering/checkout", "/inventory/counts", "/inventory/sources", "/inventory/sources/revisions", "/inventory/sources/aliases", "/inventory/sources/suspensions", "/inventory/sources/policies"];
}

public sealed class Auth0OptionsValidator : IValidateOptions<Auth0Options>
{
    public ValidateOptionsResult Validate(string? name, Auth0Options options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;
        var valid = HttpsOrigin(options.Issuer, requireTrailingSlash: true) && HttpsOrigin(options.PublicOrigin, requireTrailingSlash: false) &&
            Bounded(options.ClientId, 200) && Bounded(options.ClientSecret, 4096) && Bounded(options.ConnectionId, 200) && Bounded(options.ConnectionName, 200) &&
            options.AssurancePolicyVersion.Length is > 0 and <= 40 && options.AssurancePolicyVersion.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') &&
            ValidNamespace(options.ClaimsNamespace) && options.AllowedReturnPaths is { Length: > 0 and <= 32 } &&
            options.AllowedReturnPaths.Distinct(StringComparer.Ordinal).Count() == options.AllowedReturnPaths.Length && options.AllowedReturnPaths.All(ReturnPathPolicy.IsCanonicalPath);
        return valid ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail("Auth0 requires reviewed HTTPS origins, confidential-client configuration, a controlled claims namespace and exact local return paths.");
    }
    internal static bool Bounded(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && value.All(character => !char.IsControl(character)) && !string.IsNullOrWhiteSpace(value);
    private static bool HttpsOrigin(string value, bool requireTrailingSlash) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.AbsolutePath == "/" && uri.Query == "" && uri.Fragment == "" && uri.UserInfo == "" &&
        !uri.IsLoopback && uri.HostNameType == UriHostNameType.Dns && !PlaceholderHost(uri.Host) &&
        value == uri.GetLeftPart(UriPartial.Authority) + (requireTrailingSlash ? "/" : "");
    private static bool ValidNamespace(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.Query == "" && uri.Fragment == "" && uri.UserInfo == "" &&
        !uri.IsLoopback && uri.HostNameType == UriHostNameType.Dns && !PlaceholderHost(uri.Host) && value.EndsWith('/') && value.Length <= 300 &&
        !new[] { "auth0.com", "webtask.io", "webtask.run" }.Any(host => uri.Host == host || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    private static bool PlaceholderHost(string host) => new[] { "example.com", "example.org", "example.net", "localhost", "invalid", "test", "example" }
        .Any(suffix => host == suffix || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
}

public static class ReturnPathPolicy
{
    public static bool IsCanonicalPath(string? path) => path is { Length: > 0 and <= 200 } && path.StartsWith('/') &&
        !path.Contains("//", StringComparison.Ordinal) && !path.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '\\' or '%' or '?' or '#' or ':') &&
        !path.Split('/').Any(segment => segment is "." or "..");
    public static string Require(string? path, Auth0Options options)
    {
        var selected = path ?? "/";
        if (!IsCanonicalPath(selected) || !options.AllowedReturnPaths.Contains(selected, StringComparer.Ordinal))
            throw new UnitToko.Modules.Contracts.DomainValidationException("invalid_return_path", "Return destination is not allowed");
        return selected;
    }
}
