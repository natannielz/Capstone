using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using UnitToko.Modules.Identity;

namespace UnitToko.Platform.Identity;

public sealed class Auth0ProviderDeliveryOptions
{
    public bool Enabled { get; init; }
    public string Issuer { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string ConnectionId { get; init; } = "";
    public string ConnectionName { get; init; } = "";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

public enum Auth0ProviderDeliveryState { Blocked, ProviderRejected, ProviderAccepted, OutcomeUnknown }
public sealed record Auth0ProviderDeliveryResult(Auth0ProviderDeliveryState State, string Code, bool MessageRequestStarted)
{
    // Acceptance of an email request proves neither mailbox delivery nor completed account recovery.
    public bool DeliveryVerified => false;
    public bool EmailVerified => false;
    public bool SubjectBoundAtomically => false;
    public bool CanAutomaticallyRetry => false;
}

/// <summary>One outgoing password-email attempt for a purpose-authenticated recovery intent.
/// The dispatcher must bind its isCurrent callback to this exact intent ID/version, decrypted target
/// issuer/subject, current target security epoch and recorded actor grant/security epochs, then own the
/// authorization/claim fences and durable uncertain outcome. This transport does not reauthorize itself.
/// GET-by-subject followed by the email+connection reset endpoint is NOT an atomic subject-bound operation;
/// a concurrent provider email reassignment remains an integration limitation. No password ticket is created.
/// No activation, provider mutation, ticket generation or database write occurs here by default.</summary>
public sealed class Auth0ProviderDelivery : IDisposable
{
    private const int MaximumResponseBytes = 65536;
    private readonly Auth0ProviderDeliveryOptions options;
    private readonly SessionPayloadProtector protector;
    private readonly Func<CancellationToken, Task<string>> managementToken;
    private readonly HttpClient http;
    private readonly Uri? tenant;

    public Auth0ProviderDelivery(Auth0ProviderDeliveryOptions options, SessionPayloadProtector protector,
        Func<CancellationToken, Task<string>> managementToken) : this(options, protector, managementToken,
            new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5), MaxResponseHeadersLength = 16 }) { }

    // Test-only handler injection; production construction never follows redirects or supplies ambient cookies.
    internal Auth0ProviderDelivery(Auth0ProviderDeliveryOptions options, SessionPayloadProtector protector,
        Func<CancellationToken, Task<string>> managementToken, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(managementToken); ArgumentNullException.ThrowIfNull(handler);
        this.options = options; this.protector = protector; this.managementToken = managementToken;
        if (options.Enabled) {
            if (!Uri.TryCreate(options.Issuer, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps ||
                origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.AbsolutePath != "/" ||
                !origin.IsDefaultPort || origin.IsLoopback || origin.HostNameType != UriHostNameType.Dns ||
                !Regex.IsMatch(origin.IdnHost, "^[a-z0-9][a-z0-9-]*(?:\\.[a-z0-9][a-z0-9-]*)?\\.auth0\\.com\\z", RegexOptions.CultureInvariant) ||
                origin.AbsoluteUri != options.Issuer || !Bounded(options.ClientId, 128) || !Bounded(options.ConnectionName, 128) ||
                !Regex.IsMatch(options.ConnectionId, "^con_[A-Za-z0-9_-]{1,128}\\z", RegexOptions.CultureInvariant) ||
                options.RequestTimeout < TimeSpan.FromSeconds(1) || options.RequestTimeout > TimeSpan.FromSeconds(30))
                throw new ArgumentException("Auth0 delivery requires reviewed native HTTPS tenant and database connection settings");
            tenant = origin;
        }
        http = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<Auth0ProviderDeliveryResult> DeliverAsync(BsonDocument intent,
        Func<CancellationToken, Task<bool>> isCurrent, CancellationToken cancellationToken = default) =>
        DeliverCoreAsync(intent, isCurrent, _ => Task.FromResult(true), cancellationToken);

    // The owning dispatcher commits its exact claim's irreversible send marker here, after lookups.
    // A failed/uncertain marker commit prevents POST; the dispatcher still refuses to reclaim that claim.
    internal Task<Auth0ProviderDeliveryResult> DeliverClaimedAsync(BsonDocument intent,
        Func<CancellationToken, Task<bool>> isCurrent, Func<CancellationToken, Task<bool>> beforeMessage,
        CancellationToken cancellationToken) => DeliverCoreAsync(intent, isCurrent, beforeMessage, cancellationToken);

    private async Task<Auth0ProviderDeliveryResult> DeliverCoreAsync(BsonDocument intent,
        Func<CancellationToken, Task<bool>> isCurrent, Func<CancellationToken, Task<bool>> beforeMessage,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled) return Blocked("delivery_disabled");
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(beforeMessage);
        string subject;
        try {
            // Only the existing account-recovery producer's exact protected shape is accepted.
            // Invitation has no provider subject; factor recovery needs a separately approved destructive contract.
            if (intent is null || intent.GetValue("kind", "") != "passwordRecovery") return Blocked("unsupported_intent");
            var owned = intent.DeepClone().AsBsonDocument;
            if (owned.GetValue("status", "") != "blocked" || !Positive(owned, "_id") || !Positive(owned, "userId") ||
                !Positive(owned, "requestedBy") || !Positive(owned, "securityVersion") || owned.GetValue("version", 0) != 1L ||
                !owned.TryGetValue("protectedPayload", out var encrypted) || !encrypted.IsString || encrypted.AsString.Length > 262144)
                return Blocked("invalid_intent");
            var payload = BsonDocument.Parse(protector.Unprotect("account-recovery", encrypted.AsString));
            string[] expected = ["issuer", "subject", "kind", "actorGrantVersion", "actorSecurityVersion"];
            if (payload.ElementCount != expected.Length || payload.Names.Distinct(StringComparer.Ordinal).Count() != expected.Length ||
                payload.Names.Any(name => !expected.Contains(name, StringComparer.Ordinal)) ||
                payload.GetValue("issuer", "") != options.Issuer || payload.GetValue("kind", "") != "password" ||
                !PositivePayloadInteger(payload, "actorGrantVersion") || !PositivePayloadInteger(payload, "actorSecurityVersion") ||
                !payload.TryGetValue("subject", out var id) || !id.IsString ||
                !Regex.IsMatch(id.AsString, "^auth0\\|[A-Za-z0-9_.-]{1,128}\\z", RegexOptions.CultureInvariant))
                return Blocked("invalid_intent_binding");
            subject = id.AsString;
        } catch (Exception error) when (error is CryptographicException or FormatException or InvalidOperationException or ArgumentException) {
            return Blocked("invalid_protected_intent");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        var ct = deadline.Token;
        string email;
        try {
            ct.ThrowIfCancellationRequested();
            if (!await isCurrent(ct)) return Blocked("authority_changed");
            var token = await managementToken(ct);
            if (token is null || token.Length is < 1 or > 8192 || token.Any(character => character > 126 || character < 33))
                return Blocked("management_credential_unavailable");
            // Do not search/link by caller-supplied email. The authenticated exact subject owns this lookup.
            using var user = await ReadAsync("api/v2/users/" + Uri.EscapeDataString(subject) +
                "?fields=user_id,email,email_verified,identities,blocked&include_fields=true", token, ct);
            var value = user.RootElement;
            // Auth0 documents absent blocked as not blocked; true and non-boolean values remain refused.
            if (Text(value, "user_id") != subject || !AbsentOrFalse(value, "blocked") ||
                !value.TryGetProperty("email_verified", out var verified) || verified.ValueKind != JsonValueKind.True ||
                !value.TryGetProperty("identities", out var identities) || identities.ValueKind != JsonValueKind.Array || identities.GetArrayLength() != 1)
                return Blocked("provider_user_binding_changed");
            var identity = identities[0];
            if (Text(identity, "provider") != "auth0" || Text(identity, "user_id") != subject[6..] ||
                Text(identity, "connection") != options.ConnectionName || !identity.TryGetProperty("isSocial", out var social) || social.ValueKind != JsonValueKind.False)
                return Blocked("unsupported_provider_identity");
            email = OnboardingPolicy.NormalizeEmail(Text(value, "email"));
            using var connection = await ReadAsync("api/v2/connections/" + Uri.EscapeDataString(options.ConnectionId) +
                "?fields=id,name,strategy,options&include_fields=true", token, ct);
            var configured = connection.RootElement;
            if (Text(configured, "id") != options.ConnectionId || Text(configured, "name") != options.ConnectionName || Text(configured, "strategy") != "auth0" ||
                // GET connection documents native options:{}; absent options may mean a missing read:connections_options scope.
                !configured.TryGetProperty("options", out var settings) || settings.ValueKind != JsonValueKind.Object ||
                !AbsentOrFalse(settings, "enabledDatabaseCustomization"))
                return Blocked("unsupported_database_connection");
            if (!await ConnectionHasClientAsync(token, ct)) return Blocked("connection_client_unavailable");
            if (!await isCurrent(ct)) return Blocked("authority_changed");
            ct.ThrowIfCancellationRequested();
            if (!await beforeMessage(ct)) return Blocked("authority_changed");
            ct.ThrowIfCancellationRequested();
        } catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException or
            InvalidOperationException or ArgumentException or UnitToko.Modules.Contracts.DomainValidationException) {
            return Blocked("provider_lookup_unavailable");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(tenant!, "dbconnections/change_password")) {
            Content = JsonContent.Create(new { client_id = options.ClientId, email, connection = options.ConnectionName })
        };
        // No bearer credential, password, ticket, fake Idempotency-Key or automatic retry on this email endpoint.
        try {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.OK) return new(Auth0ProviderDeliveryState.ProviderAccepted, "provider_request_accepted", true);
            var status = (int)response.StatusCode;
            if (status is >= 400 and < 500 && status != 408)
                return new(Auth0ProviderDeliveryState.ProviderRejected, "provider_request_rejected", true);
            // A redirect, timeout, 5xx or undocumented success is not evidence that no email was scheduled.
            return Unknown();
        } catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException) {
            return Unknown();
        }
    }

    private async Task<bool> ConnectionHasClientAsync(string token, CancellationToken ct)
    {
        // The connection object's enabled_clients field is deprecated. Follow only bounded opaque cursors.
        string? from = null; var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < 4; page++) {
            using var result = await ReadAsync("api/v2/connections/" + Uri.EscapeDataString(options.ConnectionId) + "/clients?take=100" +
                (from is null ? "" : "&from=" + Uri.EscapeDataString(from)), token, ct);
            if (!result.RootElement.TryGetProperty("clients", out var clients) || clients.ValueKind != JsonValueKind.Array || clients.GetArrayLength() > 100)
                throw new JsonException("Auth0 connection client response invalid");
            if (clients.EnumerateArray().Any(client => Text(client, "client_id") == options.ClientId)) return true;
            if (!result.RootElement.TryGetProperty("next", out var next)) return false;
            if (next.ValueKind != JsonValueKind.String || next.GetString() is not { } cursor || !Bounded(cursor, 256) || !seen.Add(cursor))
                throw new JsonException("Auth0 connection client cursor invalid");
            from = cursor;
        }
        return false;
    }

    private async Task<JsonDocument> ReadAsync(string path, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(tenant!, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new("application/json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new HttpRequestException("Auth0 source lookup unavailable");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream(); var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) != 0) {
            if (bytes.Length + read > MaximumResponseBytes) throw new JsonException("Auth0 source response exceeds its bound");
            bytes.Write(buffer, 0, read);
        }
        var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        if (!UniqueObjects(document.RootElement)) { document.Dispose(); throw new JsonException("Auth0 source response is ambiguous"); }
        return document;
    }
    private static bool UniqueObjects(JsonElement value) => value.ValueKind switch {
        JsonValueKind.Object => value.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count() &&
            value.EnumerateObject().All(property => UniqueObjects(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(UniqueObjects), _ => true
    };
    private static bool AbsentOrFalse(JsonElement value, string field) => value.ValueKind == JsonValueKind.Object &&
        (!value.TryGetProperty(field, out var flag) || flag.ValueKind == JsonValueKind.False);
    private static string Text(JsonElement value, string field) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var text) &&
        text.ValueKind == JsonValueKind.String && text.GetString() is { } result && Bounded(result, 256) ? result : throw new JsonException("Auth0 source binding invalid");
    private static bool Bounded(string value, int maximum) => value is not null && value.Length is > 0 && value.Length <= maximum && value == value.Trim() && !value.Any(char.IsControl);
    private static bool Positive(BsonDocument value, string field) => value.TryGetValue(field, out var item) && item.IsInt64 && item.AsInt64 > 0;
    // AccountRecoveryService protects payload.ToJson(). Parsing that JSON narrows small Int64 values to
    // Int32 while retaining large values as Int64. Accept those exact integer representations only;
    // persisted database IDs/epochs still use the separate strict Int64 guard above.
    private static bool PositivePayloadInteger(BsonDocument value, string field) => value.TryGetValue(field, out var item) &&
        (item.IsInt32 && item.AsInt32 > 0 || item.IsInt64 && item.AsInt64 > 0);
    private static Auth0ProviderDeliveryResult Blocked(string code) => new(Auth0ProviderDeliveryState.Blocked, code, false);
    private static Auth0ProviderDeliveryResult Unknown() => new(Auth0ProviderDeliveryState.OutcomeUnknown, "provider_outcome_unknown", true);
    public void Dispose() => http.Dispose();
}
