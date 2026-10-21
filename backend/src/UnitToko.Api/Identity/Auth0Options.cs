using Microsoft.Extensions.Options;

namespace UnitToko.Api.Identity;

public sealed class Auth0Options
{
    public const string SectionName = "Identity:Auth0";
    public const string Scheme = "UnitTokoOidc";
    public const string CallbackPath = "/auth/callback";
    public const string SignedOutCallbackPath = "/auth/signed-out";
    public const int MaximumReturnPaths = 96;
    public bool Enabled { get; set; }
    public string Issuer { get; set; } = "";
    public string PublicOrigin { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string ConnectionName { get; set; } = "";
    public string ClaimsNamespace { get; set; } = "";
    public string AssurancePolicyVersion { get; set; } = "unit-toko-otp-v1";
    public string[] AllowedReturnPaths { get; set; } = ["/workspace", "/katalog", "/cart", "/operations/health", "/billing/invoice-releases", "/fulfillment/courier-assignments", "/inventory/pack-conversion-executions", "/inventory/stock-releases", "/inventory/sources/activations", "/", "/ordering/requests", "/ordering/requests/incoming", "/identity/account", "/administration/access", "/administration/delegations", "/administration/organizations", "/administration/organizations/addresses", "/administration/history", "/administration/person-reviews", "/administration/catalog", "/catalog/prices", "/catalog/public", "/shop", "/ordering/drafts", "/ordering/checkout", "/ordering/substitutions", "/ordering/reviews", "/ordering/orders", "/ordering/spending-policies", "/ordering/commercial-policies", "/accounting/books", "/reporting/accounts", "/operations/anomalies", "/procurement/requests", "/procurement/suppliers", "/procurement/purchase-orders", "/procurement/funding", "/procurement/supplier-banks", "/procurement/disbursements", "/procurement/settlements", "/procurement/receiving", "/procurement/inspections", "/procurement/supplier-documents", "/procurement/supplier-claims", "/procurement/stock-receipts", "/procurement/stock-valuations", "/procurement/supplier-payables", "/procurement/advance-returns", "/procurement/quality-acceptances", "/fulfillment/preparations", "/fulfillment/dispatches", "/fulfillment/shipments", "/fulfillment/receive", "/fulfillment/cases", "/billing/invoices", "/billing/payments", "/billing/payment-reviews", "/billing/receiving-accounts", "/billing/bank-statements", "/billing/bank-receipts", "/inventory/counts", "/inventory/pack-conversions", "/inventory/sources", "/inventory/sources/revisions", "/inventory/sources/aliases", "/inventory/sources/suspensions", "/inventory/sources/policies", "/inventory/source-reconciliations", "/inventory/replenishment", "/administration/accounts"];
}

public sealed class Auth0OptionsValidator : IValidateOptions<Auth0Options>
{
    public ValidateOptionsResult Validate(string? name, Auth0Options options)
    {
        if (!options.Enabled) return ValidateOptionsResult.Success;
        var valid = HttpsOrigin(options.Issuer, requireTrailingSlash: true) && HttpsOrigin(options.PublicOrigin, requireTrailingSlash: false) &&
            Bounded(options.ClientId, 200) && Bounded(options.ClientSecret, 4096) && Bounded(options.ConnectionId, 200) && Bounded(options.ConnectionName, 200) &&
            options.AssurancePolicyVersion.Length is > 0 and <= 40 && options.AssurancePolicyVersion.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') &&
            ValidNamespace(options.ClaimsNamespace) && options.AllowedReturnPaths is { Length: > 0 and <= Auth0Options.MaximumReturnPaths } &&
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
    /// <summary>A catalog product page, by its canonical public UUID only, so signing in from a public product returns to it.</summary>
    public static bool IsProductPath(string path) => System.Text.RegularExpressions.Regex.IsMatch(path,
        @"\A/shop/[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    /// <summary>
    /// The exact reviewed page (or canonical product page), optionally with the record it had open as a narrow query
    /// (<see cref="UnitToko.Platform.Identity.ReturnDestination"/>), within 200 characters in total.
    /// The frontend keeps the same page list (tests/return-paths-parity.test.ts fails when they differ).
    /// </summary>
    public static string Require(string? path, Auth0Options options)
    {
        var selected = path ?? "/";
        if (selected.Length > UnitToko.Platform.Identity.ReturnDestination.MaximumLength ||
            !UnitToko.Platform.Identity.ReturnDestination.TrySplit(selected, out var page, out _) ||
            !IsCanonicalPath(page) || !options.AllowedReturnPaths.Contains(page, StringComparer.Ordinal) && !IsProductPath(page))
            throw new UnitToko.Modules.Contracts.DomainValidationException("invalid_return_path", "Return destination is not allowed");
        return selected;
    }
}
