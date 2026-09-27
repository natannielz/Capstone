using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Identity;

/// <summary>Verifies the two configured native Auth0 transports. Log streams are supplemental;
/// successful verification does not establish immediate provider-change coverage or recovery ownership.</summary>
public sealed class ProviderSecurityEventVerifier
{
    private const int MaximumBodyBytes = 65_536;
    private const int MaximumBatch = 100;
    private readonly string issuer, eventSource, tenantId, eventStreamId;
    private readonly Credentials events, logs;
    private readonly IClock clock;
    private static readonly Regex Timestamp = new("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public ProviderSecurityEventVerifier(Auth0SecurityEventOptions options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(clock);
        options.Validate();
        this.clock = clock;
        // Snapshot trusted configuration. A caller mutating a bound options object cannot switch
        // tenant or credentials while an already-created verifier is processing a message.
        issuer = options.Issuer; eventSource = options.EventSource; tenantId = options.TenantId; eventStreamId = options.EventStreamId;
        events = new(options.EventsEnabled, options.EventsBearerToken, options.PreviousEventsBearerToken);
        logs = new(options.LogsEnabled, options.LogsBearerToken, options.PreviousLogsBearerToken);
    }

    internal VerifiedProviderSecurityBatch Verify(ProviderEventTransport transport, ReadOnlyMemory<byte> body, string authorization)
    {
        var credentials = transport switch
        {
            ProviderEventTransport.Auth0Events => events,
            ProviderEventTransport.Auth0Logs => logs,
            _ => throw new AuthorizationDeniedException("invalid_provider_credential")
        };
        if (!credentials.Enabled) throw new AuthorizationDeniedException("provider_events_disabled");
        Authenticate(credentials, authorization);
        if (body.Length is 0 or > MaximumBodyBytes) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            ValidateTree(document.RootElement);
            var result = ImmutableArray.CreateBuilder<VerifiedProviderSecurityEvent>();
            var ignored = 0;
            if (transport == ProviderEventTransport.Auth0Events)
            {
                var change = CloudEvent(document.RootElement);
                if (change is null) ignored++; else result.Add(change);
            }
            else
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is < 1 or > MaximumBatch) throw Invalid();
                foreach (var entry in root.EnumerateArray())
                {
                    var change = Log(entry);
                    if (change is null) ignored++; else result.Add(change);
                }
            }
            return new(result.ToImmutable(), ignored);
        }
        catch (JsonException) { throw Invalid(); }
    }

    private VerifiedProviderSecurityEvent? CloudEvent(JsonElement root)
    {
        RequireObject(root);
        if (Text(root, "specversion", 10) != "1.0" || Text(root, "source", 300) != eventSource ||
            Text(root, "a0tenant", 63) != tenantId || Text(root, "a0stream", 100) != eventStreamId) throw Invalid();
        var id = Text(root, "id", 128);
        if (!Auth0SecurityEventOptions.ProviderId(id, "evt_")) throw Invalid();
        var type = EventType(root, "type");
        var at = Time(root, "time");
        var data = Object(root, "data");
        if (type is not ("user.updated" or "user.deleted")) return null;
        var user = Object(data, "object");
        var subject = Subject(user);
        if (type == "user.deleted") return Verified("event:" + id, subject, ProviderSecurityChange.AccountDeleted, at);
        // Other profile changes need not carry blocked. Absence is not evidence of a block.
        if (!user.TryGetProperty("blocked", out var blocked)) return null;
        if (blocked.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
        return blocked.GetBoolean() ? Verified("event:" + id, subject, ProviderSecurityChange.AccountDisabled, at) : null;
    }

    private VerifiedProviderSecurityEvent? Log(JsonElement envelope)
    {
        RequireObject(envelope);
        var id = Text(envelope, "log_id", 128);
        if (id.Any(character => character is < '0' or > '9')) throw Invalid();
        var data = Object(envelope, "data");
        if (data.TryGetProperty("log_id", out _) && Text(data, "log_id", 128) != id) throw Invalid();
        var type = EventType(data, "type");
        var at = Time(data, "date");
        ProviderSecurityChange? change = type switch
        {
            "scp" => ProviderSecurityChange.PasswordChanged,
            "gd_unenroll" => ProviderSecurityChange.FactorRemoved,
            "gd_enrollment_complete" or "gd_update_device_account" => ProviderSecurityChange.FactorChanged,
            _ => null
        };
        // Some unrelated logs have no affected user. Only the allowlisted successful lifecycle
        // records need one, and only data.user_id is authoritative; details.auth.subject is the caller.
        return change.HasValue ? Verified("log:" + id, Subject(data), change.Value, at) : null;
    }

    private VerifiedProviderSecurityEvent Verified(string id, string subject, ProviderSecurityChange change, DateTimeOffset at)
    {
        var normalized = at.ToUniversalTime();
        // Fixed-order semantic fields exclude profile/email/description and transport formatting.
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new[] { "auth0-security-v1", issuer, id, subject,
            change.ToString(), normalized.ToString("O", CultureInfo.InvariantCulture) });
        var fingerprint = Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
        return new(issuer, id, subject, change, normalized, fingerprint);
    }

    private DateTimeOffset Time(JsonElement parent, string name)
    {
        var value = Text(parent, name, 40);
        if (!Timestamp.IsMatch(value) || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ||
            time < DateTimeOffset.UnixEpoch || time > clock.UtcNow.AddSeconds(30)) throw Invalid();
        return time;
    }
    private static string Subject(JsonElement parent)
    {
        var value = Text(parent, "user_id", 256);
        if (value.Any(char.IsWhiteSpace)) throw Invalid();
        return value;
    }
    private static string EventType(JsonElement parent, string name)
    {
        var value = Text(parent, name, 100);
        if (value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))) throw Invalid();
        return value;
    }
    private static string Text(JsonElement parent, string name, int maximum)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) throw Invalid();
        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl) || value.Trim() != value) throw Invalid();
        return value;
    }
    private static JsonElement Object(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var result)) throw Invalid();
        RequireObject(result); return result;
    }
    private static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid();
    }
    private static void ValidateTree(JsonElement element)
    {
        // The raw-byte and depth limits bound traversal, including ignored profile metadata.
        // Only named security fields impose value limits; unrelated metadata must not suppress
        // a valid revocation. Duplicate decoded names remain ambiguous at every depth.
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                ValidateTree(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) ValidateTree(item);
        }
    }
    private static void Authenticate(Credentials credentials, string authorization)
    {
        if (authorization is null || authorization.Length is < 8 or > 519 || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new AuthorizationDeniedException("invalid_provider_credential");
        var candidate = authorization[7..];
        if (!Auth0SecurityEventOptions.BearerCharacters(candidate)) throw new AuthorizationDeniedException("invalid_provider_credential");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        // Evaluate both fixed-size comparisons regardless of which rotation slot matches.
        var matches = CryptographicOperations.FixedTimeEquals(digest, credentials.Current) |
            CryptographicOperations.FixedTimeEquals(digest, credentials.Previous);
        if (!matches) throw new AuthorizationDeniedException("invalid_provider_credential");
    }
    private sealed class Credentials(bool enabled, string current, string previous)
    {
        internal bool Enabled { get; } = enabled;
        internal byte[] Current { get; } = SHA256.HashData(Encoding.UTF8.GetBytes(enabled ? current : ""));
        internal byte[] Previous { get; } = SHA256.HashData(Encoding.UTF8.GetBytes(enabled && !string.IsNullOrEmpty(previous) ? previous : ""));
    }
    private static DomainValidationException Invalid() => new("invalid_provider_event", "Provider event was invalid");
}
