using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using UnitToko.Api.Identity;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;

namespace UnitToko.Tests.Identity;

public sealed class Auth0ClaimsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 3, 0, 0, TimeSpan.Zero);
    internal static Auth0Options Settings() => new()
    {
        Enabled = true, Issuer = "https://unit-toko-fixture.us.auth0.com/", PublicOrigin = "https://unit-toko.co.id",
        ClientId = "fixture-client", ClientSecret = "fixture-secret-for-local-tests-only", ConnectionId = "con_fixture", ConnectionName = "fixture-database",
        ClaimsNamespace = "https://unit-toko.co.id/identity/", AssurancePolicyVersion = "unit-toko-otp-v1"
    };
    private static readonly string Challenge = new('A', 43);
    private static OidcTransaction Transaction() => new(new('b', 64), 1,
        new(OidcTransactionPurpose.Login, Settings().Issuer, Settings().ClientId, "/"), OidcTransactionStore.Hash(Challenge), Now.AddSeconds(-20), Now.AddMinutes(5));
    private static Dictionary<string, object> Payload() => new()
    {
        ["iss"] = Settings().Issuer, ["sub"] = "auth0|fixture-user", ["email"] = "Approved@Company.co.id", ["email_verified"] = true,
        ["auth_time"] = Now.AddSeconds(-15).ToUnixTimeSeconds(), ["iat"] = Now.ToUnixTimeSeconds(), ["amr"] = new[] { "pwd", "mfa" },
        [Settings().ClaimsNamespace + "assurance"] = new Dictionary<string, object>
        {
            ["policyVersion"] = Settings().AssurancePolicyVersion, ["connectionId"] = Settings().ConnectionId,
            ["method"] = "otp", ["factorCompletedAt"] = Now.AddSeconds(-5).ToUnixTimeSeconds(), ["challengeBinding"] = Challenge
        }
    };
    // Mapper tests intentionally isolate typed claim parsing. They do not claim that this unsigned
    // fixture authenticates: ProviderHandlerTests exercise the maintained handler and RSA signatures.
    private static JsonWebToken ParsingFixture(string json) => new($"eyJhbGciOiJub25lIn0.{Encode(json)}.");
    private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static JsonWebToken ParsingFixture(Dictionary<string, object> payload) => ParsingFixture(JsonSerializer.Serialize(payload));
    private static Dictionary<string, object> Assurance(Dictionary<string, object> payload) => (Dictionary<string, object>)payload[Settings().ClaimsNamespace + "assurance"];

    [Fact]
    public void Provider_authentication_and_actual_factor_times_are_distinct()
    {
        var proof = Auth0ClaimsMapper.MapValidated(ParsingFixture(Payload()), Transaction(), Settings(), Now);
        Assert.Equal(Now.AddSeconds(-15), proof.ProviderAuthenticatedAt);
        Assert.Equal(Now.AddSeconds(-5), proof.FactorCompletedAt);
        Assert.Equal("approved@company.co.id", proof.Email); Assert.Equal("otp", proof.FactorType);
        Assert.Equal(Challenge, proof.ChallengeBinding);
    }
    [Theory]
    [InlineData("email-string")]
    [InlineData("email-false")]
    [InlineData("missing-proof")]
    [InlineData("wrong-factor")]
    [InlineData("old-factor-new-token")]
    [InlineData("future-factor")]
    [InlineData("factor-time-string")]
    [InlineData("wrong-connection")]
    [InlineData("wrong-policy")]
    [InlineData("wrong-challenge")]
    [InlineData("unknown-proof-field")]
    [InlineData("missing-auth-time")]
    [InlineData("old-auth-new-factor")]
    public void Incomplete_or_unbound_provider_evidence_never_becomes_MFA(string variation)
    {
        var payload = Payload();
        switch (variation)
        {
            case "email-string": payload["email_verified"] = "true"; break;
            case "email-false": payload["email_verified"] = false; break;
            case "missing-proof": payload.Remove(Settings().ClaimsNamespace + "assurance"); break;
            case "wrong-factor": Assurance(payload)["method"] = "recovery-code"; break;
            case "old-factor-new-token": Assurance(payload)["factorCompletedAt"] = Now.AddMinutes(-6).ToUnixTimeSeconds(); break;
            case "future-factor": Assurance(payload)["factorCompletedAt"] = Now.AddMinutes(1).ToUnixTimeSeconds(); break;
            case "factor-time-string": Assurance(payload)["factorCompletedAt"] = Now.ToUnixTimeSeconds().ToString(); break;
            case "wrong-connection": Assurance(payload)["connectionId"] = "con_other"; break;
            case "wrong-policy": Assurance(payload)["policyVersion"] = "previous-policy"; break;
            case "wrong-challenge": Assurance(payload)["challengeBinding"] = new string('B', 43); break;
            case "unknown-proof-field": Assurance(payload)["mfaEnabled"] = true; break;
            case "missing-auth-time": payload.Remove("auth_time"); break;
            case "old-auth-new-factor": payload["auth_time"] = Now.AddMinutes(-2).ToUnixTimeSeconds(); break;
        }
        Assert.Equal("provider_identity_unverified", Assert.Throws<AuthorizationDeniedException>(() => Auth0ClaimsMapper.MapValidated(ParsingFixture(payload), Transaction(), Settings(), Now)).Code);
    }
    [Fact]
    public void Duplicate_claim_names_and_stepup_subject_substitution_are_rejected()
    {
        var json = JsonSerializer.Serialize(Payload()).Replace("\"email_verified\":true", "\"email_verified\":false,\"email_verified\":true", StringComparison.Ordinal);
        Assert.Throws<AuthorizationDeniedException>(() => Auth0ClaimsMapper.MapValidated(ParsingFixture(json), Transaction(), Settings(), Now));
        var transaction = Transaction() with { Request = new(OidcTransactionPurpose.StepUp, Settings().Issuer, Settings().ClientId, "/",
            1, new(new string('c', 64), 1, 1), 1, "auth0|another-user", "access.approve") };
        Assert.Throws<AuthorizationDeniedException>(() => Auth0ClaimsMapper.MapValidated(ParsingFixture(Payload()), transaction, Settings(), Now));
    }
    [Fact]
    public void Provider_is_disabled_by_default_and_enabled_configuration_rejects_dummy_or_unsafe_origins()
    {
        var validator = new Auth0OptionsValidator();
        Assert.False(new Auth0Options().Enabled); Assert.True(validator.Validate(null, new()).Succeeded);
        Assert.True(validator.Validate(null, Settings()).Succeeded);
        foreach (var issuer in new[] { "http://tenant.auth0.com/", "https://localhost/", "https://issuer.example/", "https://tenant.auth0.com/extra", "https://tenant.auth0.com/?x=1" })
        { var settings = Settings(); settings.Issuer = issuer; Assert.True(validator.Validate(null, settings).Failed); }
        var missingSecret = Settings(); missingSecret.ClientSecret = ""; Assert.True(validator.Validate(null, missingSecret).Failed);
    }
    [Theory]
    [InlineData("//outside.co.id")]
    [InlineData("https://outside.co.id")]
    [InlineData("/\\outside.co.id")]
    [InlineData("/%2f%2foutside.co.id")]
    [InlineData("/administration/../auth/logout")]
    [InlineData("/administration/access?next=https://outside.co.id")]
    [InlineData("/auth/logout")]
    [InlineData("/inventory/counts/")]
    [InlineData("/inventory/counts?warehouse=1")]
    [InlineData("/inventory/counts#other")]
    [InlineData("//inventory/counts")]
    [InlineData("/inventory/%63ounts")]
    [InlineData("/inventory/sources/aliases/")]
    [InlineData("/inventory/sources/aliases?next=https://outside.co.id")]
    [InlineData("/inventory/sources/aliases#other")]
    [InlineData("//inventory/sources/aliases")]
    [InlineData("/inventory/sources/%61liases")]
    [InlineData("/inventory/sources/suspensions/")]
    [InlineData("/inventory/sources/suspensions?next=https://outside.co.id")]
    [InlineData("/inventory/sources/suspensions#other")]
    [InlineData("//inventory/sources/suspensions")]
    [InlineData("/inventory/sources/%73uspensions")]
    [InlineData("/catalog/prices/")]
    [InlineData("/catalog/prices?next=https://outside.co.id")]
    [InlineData("/catalog/%70rices")]
    [InlineData("//catalog/prices")]
    [InlineData("/ordering/drafts/")]
    [InlineData("/ordering/drafts?next=https://outside.co.id")]
    [InlineData("/ordering/orders/")]
    [InlineData("/ordering/orders?next=https://outside.co.id")]
    [InlineData("/fulfillment/preparations/")]
    [InlineData("/fulfillment/preparations?next=https://outside.co.id")]
    [InlineData("//fulfillment/preparations")]
    public void Return_paths_are_exact_allowlisted_destinations(string path) =>
        Assert.Throws<DomainValidationException>(() => ReturnPathPolicy.Require(path, Settings()));
    [Fact]
    public void Stock_count_return_path_is_explicitly_allowlisted() =>
        Assert.Equal("/inventory/counts", ReturnPathPolicy.Require("/inventory/counts", Settings()));
    [Fact]
    public void Stock_alias_return_path_is_explicitly_allowlisted() =>
        Assert.Equal("/inventory/sources/aliases", ReturnPathPolicy.Require("/inventory/sources/aliases", Settings()));
    [Fact]
    public void Stock_suspension_return_path_is_explicitly_allowlisted() =>
        Assert.Equal("/inventory/sources/suspensions", ReturnPathPolicy.Require("/inventory/sources/suspensions", Settings()));
    [Fact]
    public void Price_return_path_is_explicitly_allowlisted() =>
        Assert.Equal("/catalog/prices", ReturnPathPolicy.Require("/catalog/prices", Settings()));
    [Fact]
    public void Buyer_catalog_return_path_is_explicitly_allowlisted() =>
        Assert.Equal("/catalog/browse", ReturnPathPolicy.Require("/catalog/browse", Settings()));
    [Theory]
    [InlineData("/shop")]
    [InlineData("/administration/organizations/addresses")]
    [InlineData("/ordering/spending-policies")]
    [InlineData("/accounting/books")]
    [InlineData("/procurement/requests")]
    [InlineData("/procurement/suppliers")]
    public void Operational_workspace_return_destinations_are_exact_and_within_the_configured_bound(string path) {
        var settings = Settings();
        Assert.Equal(path, ReturnPathPolicy.Require(path, settings));
        Assert.True(settings.AllowedReturnPaths.Length <= Auth0Options.MaximumReturnPaths);
        Assert.True(new Auth0OptionsValidator().Validate(null, settings).Succeeded);
        foreach (var changed in new[] { path + "/", path + "?next=https://outside.co.id", path.Replace("/", "//", StringComparison.Ordinal), path + "/00000000-0000-0000-0000-000000000001" })
            Assert.Throws<DomainValidationException>(() => ReturnPathPolicy.Require(changed, settings));
    }
    [Fact]
    public void Return_path_configuration_remains_bounded_unique_and_canonical() {
        var settings = Settings(); var validator = new Auth0OptionsValidator();
        settings.AllowedReturnPaths = Enumerable.Range(0, Auth0Options.MaximumReturnPaths).Select(index => "/reviewed/" + index).ToArray();
        Assert.True(validator.Validate(null, settings).Succeeded);
        settings.AllowedReturnPaths = [.. settings.AllowedReturnPaths, "/reviewed/overflow"];
        Assert.True(validator.Validate(null, settings).Failed);
        settings.AllowedReturnPaths = ["/ordering/spending-policies", "/ordering/spending-policies"];
        Assert.True(validator.Validate(null, settings).Failed);
        settings.AllowedReturnPaths = ["https://outside.co.id"];
        Assert.True(validator.Validate(null, settings).Failed);
    }
    [Fact]
    public void Order_draft_return_path_is_explicitly_allowlisted() =>
        Assert.Equal("/ordering/drafts", ReturnPathPolicy.Require("/ordering/drafts", Settings()));
    [Fact] public void Checkout_return_path_is_exactly_allowed() =>
        Assert.Equal("/ordering/checkout", ReturnPathPolicy.Require("/ordering/checkout", Settings()));
    [Fact] public void Own_orders_return_path_is_exactly_allowed() =>
        Assert.Equal("/ordering/orders", ReturnPathPolicy.Require("/ordering/orders", Settings()));
    [Fact] public void Shipment_preparation_return_path_is_exactly_allowed() =>
        Assert.Equal("/fulfillment/preparations", ReturnPathPolicy.Require("/fulfillment/preparations", Settings()));
    [Fact]
    public void Handler_configuration_requires_code_PKCE_validation_and_never_saves_tokens_to_a_browser_ticket()
    {
        var options = new OpenIdConnectOptions(); Auth0OidcAdapter.Configure(options, Settings());
        Assert.Equal("code", options.ResponseType); Assert.Equal("query", options.ResponseMode); Assert.True(options.UsePkce);
        Assert.False(options.SaveTokens); Assert.False(options.GetClaimsFromUserInfoEndpoint); Assert.False(options.MapInboundClaims);
        Assert.True(options.TokenValidationParameters.ValidateIssuer); Assert.True(options.TokenValidationParameters.ValidateAudience);
        Assert.True(options.TokenValidationParameters.ValidateLifetime); Assert.True(options.TokenValidationParameters.RequireSignedTokens);
        Assert.Equal("RS256", Assert.Single(options.TokenValidationParameters.ValidAlgorithms));
        Assert.DoesNotContain("offline_access", options.Scope); Assert.Equal("/auth/callback", options.CallbackPath.Value);
        var state = new OidcTransactionStart(new('C', 43), Challenge, Transaction());
        var properties = UnitTokoOidcChallenge.Properties(state);
        var restored = PropertiesSerializer.Default.Deserialize(PropertiesSerializer.Default.Serialize(properties))!;
        Assert.DoesNotContain(Challenge, restored.Items.Values);
        Assert.Empty(restored.Parameters);
    }
}
