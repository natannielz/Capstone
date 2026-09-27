using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;

namespace UnitToko.Api.Identity;

public static class Auth0ClaimsMapper
{
    /// <summary>Called only from the maintained handler's TokenValidated event. This mapper
    /// does not authenticate a token itself: signature/issuer/audience/nonce/state are handler duties.</summary>
    public static ProviderIdentityProof MapValidated(SecurityToken token, OidcTransaction transaction, Auth0Options options, DateTimeOffset now)
    {
        var encoded = token switch { JsonWebToken json => json.EncodedPayload, JwtSecurityToken jwt => jwt.RawPayload, _ => throw Denied() };
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 90_000) throw Denied();
        try
        {
            using var document = JsonDocument.Parse(Base64UrlEncoder.Decode(encoded), new JsonDocumentOptions { MaxDepth = 8 });
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object) throw Denied();
            RequireUniqueProperties(payload);
            var issuer = Text(payload, "iss", 500); var subject = Text(payload, "sub", 200);
            if (issuer != options.Issuer || issuer != transaction.Request.Issuer || transaction.Request.ClientId != options.ClientId ||
                transaction.CreatedAt > now || transaction.ExpiresAt <= now ||
                !payload.TryGetProperty("email_verified", out var verified) || verified.ValueKind != JsonValueKind.True)
                throw Denied();
            var email = OnboardingPolicy.NormalizeEmail(Text(payload, "email", 254));
            var providerTime = Timestamp(payload, "auth_time");
            if (!payload.TryGetProperty(options.ClaimsNamespace + "assurance", out var assurance) || assurance.ValueKind != JsonValueKind.Object)
                throw Denied();
            var fields = assurance.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
            if (!fields.SequenceEqual(new[] { "challengeBinding", "connectionId", "factorCompletedAt", "method", "policyVersion" })) throw Denied();
            var connection = Text(assurance, "connectionId", 200); var method = Text(assurance, "method", 40);
            var policy = Text(assurance, "policyVersion", 40); var challenge = Text(assurance, "challengeBinding", 43);
            var factorTime = Timestamp(assurance, "factorCompletedAt");
            if (connection != options.ConnectionId || method != "otp" || policy != options.AssurancePolicyVersion ||
                !OidcTransactionStore.MatchesChallenge(transaction, challenge) ||
                factorTime < transaction.CreatedAt - SessionPolicy.ProviderClockSkew ||
                factorTime > now + SessionPolicy.ProviderClockSkew || factorTime < now - SessionPolicy.FreshFactorLifetime ||
                providerTime < transaction.CreatedAt - SessionPolicy.ProviderClockSkew || providerTime > now + SessionPolicy.ProviderClockSkew || factorTime < providerTime - SessionPolicy.ProviderClockSkew ||
                transaction.Request.Purpose == OidcTransactionPurpose.StepUp && subject != transaction.Request.ExpectedSubject)
                throw Denied();
            return new(issuer, subject, email, true, connection, providerTime, factorTime, method, policy, challenge,
                OptionalText(payload, "sid", 256), OptionalText(payload, "name", 160));
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or DomainValidationException)
        { throw Denied(); }
    }
    private static void RequireUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw Denied();
                RequireUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) RequireUniqueProperties(child);
    }
    private static string Text(JsonElement value, string field, int maximum)
    {
        if (!value.TryGetProperty(field, out var raw) || raw.ValueKind != JsonValueKind.String ||
            raw.GetString() is not { } text || !Auth0OptionsValidator.Bounded(text, maximum)) throw Denied();
        return text;
    }
    private static string? OptionalText(JsonElement value, string field, int maximum) => value.TryGetProperty(field, out _) ? Text(value, field, maximum) : null;
    private static DateTimeOffset Timestamp(JsonElement value, string field)
    {
        if (!value.TryGetProperty(field, out var raw) || raw.ValueKind != JsonValueKind.Number || !raw.TryGetInt64(out var seconds) || seconds <= 0) throw Denied();
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { throw Denied(); }
    }
    private static AuthorizationDeniedException Denied() => new("provider_identity_unverified");
}
