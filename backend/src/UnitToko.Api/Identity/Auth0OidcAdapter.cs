using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;

namespace UnitToko.Api.Identity;

public static class Auth0OidcAdapter
{
    public const string SessionScheme = "UnitTokoSession";
    public static IServiceCollection AddUnitTokoAuth0(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidateOptions<Auth0Options>, Auth0OptionsValidator>();
        services.AddOptions<Auth0Options>().Bind(configuration.GetSection(Auth0Options.SectionName)).ValidateOnStart();
        var settings = configuration.GetSection(Auth0Options.SectionName).Get<Auth0Options>() ?? new();
        var validation = new Auth0OptionsValidator().Validate(null, settings);
        if (validation.Failed) throw new OptionsValidationException(Auth0Options.SectionName, typeof(Auth0Options), validation.Failures);
        if (!settings.Enabled) return services;
        services.AddScoped<Auth0OidcEvents>();
        services.AddAuthentication().AddOpenIdConnect(Auth0Options.Scheme, options => Configure(options, settings));
        return services;
    }
    public static void Configure(OpenIdConnectOptions options, Auth0Options settings)
    {
        options.Authority = settings.Issuer;
        options.ClientId = settings.ClientId;
        options.ClientSecret = settings.ClientSecret;
        options.SignInScheme = SessionScheme; // TicketReceived always handles completion explicitly.
        options.SignOutScheme = SessionScheme;
        options.CallbackPath = Auth0Options.CallbackPath;
        options.SignedOutCallbackPath = Auth0Options.SignedOutCallbackPath;
        options.SignedOutRedirectUri = settings.PublicOrigin + "/";
        options.RemoteSignOutPath = PathString.Empty;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.UsePkce = true;
        options.RequireHttpsMetadata = true;
        options.MapInboundClaims = false;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.UseTokenLifetime = false;
        options.Scope.Clear(); options.Scope.Add("openid"); options.Scope.Add("email"); options.Scope.Add("profile");
        options.RemoteAuthenticationTimeout = OidcTransactionStore.Lifetime;
        options.BackchannelTimeout = TimeSpan.FromSeconds(15);
        options.ProtocolValidator.RequireNonce = true;
        options.ProtocolValidator.NonceLifetime = OidcTransactionStore.Lifetime;
        options.TokenValidationParameters = new()
        {
            ValidateIssuerSigningKey = true, RequireSignedTokens = true, ValidateIssuer = true, ValidIssuer = settings.Issuer,
            ValidateAudience = true, ValidAudience = settings.ClientId, ValidateLifetime = true, RequireExpirationTime = true,
            ClockSkew = SessionPolicy.ProviderClockSkew, ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            NameClaimType = "sub", RoleClaimType = "unit_toko_provider_roles_ignored"
        };
        options.CorrelationCookie.Name = "__Host-ut_oidc_correlation.";
        options.CorrelationCookie.Path = "/"; options.CorrelationCookie.Domain = null;
        options.CorrelationCookie.HttpOnly = true; options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.Name = "__Host-ut_oidc_nonce.";
        options.NonceCookie.Path = "/"; options.NonceCookie.Domain = null;
        options.NonceCookie.HttpOnly = true; options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        options.EventsType = typeof(Auth0OidcEvents);
    }
}

public sealed class Auth0OidcEvents(OidcTransactionStore transactions, IOidcSignInCompletion completion,
    IOptions<Auth0Options> configured, IClock clock) : OpenIdConnectEvents
{
    private const string ValidatedItem = "unit-toko:validated-provider";
    private sealed record Validated(string StateToken, OidcTransaction Transaction, ProviderIdentityProof Proof, ProviderTokenEnvelope Tokens);
    public override async Task RedirectToIdentityProvider(RedirectContext context)
    {
        var options = configured.Value;
        if (!options.Enabled || !context.Properties.Items.TryGetValue(UnitTokoOidcChallenge.StateItem, out var state) || state is null ||
            !context.Properties.Parameters.TryGetValue(UnitTokoOidcChallenge.ChallengeParameter, out var raw) || raw is not string challenge)
            throw new AuthorizationDeniedException("oidc_state_unavailable");
        var transaction = await transactions.ReadPendingAsync(state, context.HttpContext.RequestAborted);
        if (transaction.Request.Issuer != options.Issuer || transaction.Request.ClientId != options.ClientId ||
            !OidcTransactionStore.MatchesChallenge(transaction, challenge)) throw new AuthorizationDeniedException("oidc_state_unavailable");
        ReturnPathPolicy.Require(transaction.Request.ReturnPath, options);
        context.ProtocolMessage.RedirectUri = options.PublicOrigin + Auth0Options.CallbackPath;
        context.ProtocolMessage.Prompt = "login";
        context.ProtocolMessage.MaxAge = "0";
        context.ProtocolMessage.AcrValues = "http://schemas.openid.net/pape/policies/2007/06/multi-factor";
        context.ProtocolMessage.SetParameter("connection", options.ConnectionName);
        context.ProtocolMessage.SetParameter("unit_toko_challenge", challenge);
        context.Properties.Parameters.Remove(UnitTokoOidcChallenge.ChallengeParameter);
    }
    public override async Task TokenValidated(TokenValidatedContext context)
    {
        if (!configured.Value.Enabled || context.Properties is null ||
            !context.Properties.Items.TryGetValue(UnitTokoOidcChallenge.StateItem, out var state) || state is null)
        { context.Fail("provider_authentication_failed"); return; }
        var transaction = await transactions.ReadPendingAsync(state, context.HttpContext.RequestAborted);
        var proof = Auth0ClaimsMapper.MapValidated(context.SecurityToken, transaction, configured.Value, clock.UtcNow);
        var idToken = context.TokenEndpointResponse?.IdToken;
        if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > SessionPolicy.MaximumProviderPayloadBytes)
        { context.Fail("provider_authentication_failed"); return; }
        context.HttpContext.Items[ValidatedItem] = new Validated(state, transaction, proof, new(idToken));
        // Do not consume state or issue sessions here: the handler still has protocol checks.
    }
    public override async Task TicketReceived(TicketReceivedContext context)
    {
        if (context.HttpContext.Items[ValidatedItem] is not Validated validated)
        { context.Fail("provider_authentication_failed"); return; }
        var destination = ReturnPathPolicy.Require(validated.Transaction.Request.ReturnPath, configured.Value);
        await completion.CompleteAsync(context.HttpContext, validated.StateToken, validated.Transaction, validated.Proof, validated.Tokens, context.HttpContext.RequestAborted);
        context.HandleResponse();
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Redirect(destination);
    }
    public override Task RedirectToIdentityProviderForSignOut(RedirectContext context)
    {
        context.Properties.RedirectUri = "/";
        context.ProtocolMessage.PostLogoutRedirectUri = configured.Value.PublicOrigin + Auth0Options.SignedOutCallbackPath;
        context.ProtocolMessage.SetParameter("client_id", configured.Value.ClientId);
        return Task.CompletedTask;
    }
    public override async Task RemoteFailure(RemoteFailureContext context)
    {
        context.HandleResponse();
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        await context.Response.WriteAsJsonAsync(new { code = "provider_authentication_failed", title = "Sign-in could not be verified", traceId = context.HttpContext.TraceIdentifier });
    }
}
