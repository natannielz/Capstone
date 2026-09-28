using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Identity;

namespace UnitToko.Api.Identity;

public static class IdentityRuntime
{
    public static bool AddUnitTokoIdentityRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddUnitTokoAuth0(configuration);
        var settings = configuration.GetSection(Auth0Options.SectionName).Get<Auth0Options>() ?? new();
        var events = configuration.GetSection(Auth0SecurityEventOptions.SectionName).Get<Auth0SecurityEventOptions>() ?? new();
        events.Validate();
        if ((events.EventsEnabled || events.LogsEnabled) && (!settings.Enabled || events.Issuer != settings.Issuer))
            throw new InvalidOperationException("Provider event issuer must match the enabled sign-in provider.");
        services.AddSingleton(events);
        if (!settings.Enabled)
        {
            services.AddSingleton<IRequestActorResolver, UnavailableRequestActorResolver>();
            services.AddAuthentication("Unavailable")
                .AddScheme<AuthenticationSchemeOptions, UnavailableAuthenticationHandler>("Unavailable", _ => { });
            return false;
        }
        // Framework automatic forwarding can trust arbitrary sources. Use only the explicit list below.
        if (string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED"), "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuration["FORWARDEDHEADERS_ENABLED"], "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Automatic forwarded headers are incompatible with the explicit identity proxy policy.");
        var proxies = configuration.GetSection("Identity:TrustedProxies").Get<string[]>() ?? [];
        if (proxies.Length > 16 || proxies.Any(value => !IPAddress.TryParse(value, out var ip) ||
            ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) || proxies.Distinct(StringComparer.Ordinal).Count() != proxies.Length)
            throw new InvalidOperationException("Identity trusted proxies must be an explicit bounded list of IP addresses.");
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = proxies.Length == 0 ? ForwardedHeaders.None :
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = 1;
            options.RequireHeaderSymmetry = true;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var value in proxies) options.KnownProxies.Add(IPAddress.Parse(value));
            options.AllowedHosts.Clear();
            options.AllowedHosts.Add(new Uri(settings.PublicOrigin).Host);
        });
        services.AddSingleton<IdentityKeyMaterial>();
        services.AddSingleton(sp => sp.GetRequiredService<IdentityKeyMaterial>().Protector);
        // AddUnitTokoAuth0 registered the framework defaults earlier. This final registration ensures
        // OIDC, anti-forgery and the payload protector all resolve the same protected persistent provider.
        services.AddSingleton<IDataProtectionProvider>(sp => sp.GetRequiredService<SessionPayloadProtector>().Provider);
        services.AddSingleton(new SessionPolicy(settings.Issuer, settings.ConnectionId, settings.AssurancePolicyVersion));
        services.AddSingleton<MongoIdentitySessionStore>();
        services.AddSingleton<OidcTransactionStore>();
        services.AddSingleton<IdentityOnboardingService>();
        services.AddSingleton<AccountRecoveryService>();
        services.AddSingleton<PersonWithdrawalService>();
        services.AddSingleton<ProviderSecurityEventVerifier>();
        services.AddSingleton<ProviderSecurityEventService>();
        services.AddScoped<IOidcSignInCompletion, VerifiedOidcSignInCompletion>();
        services.AddSingleton<IRequestActorResolver, VerifiedSessionActorResolver>();
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = Auth0OidcAdapter.SessionScheme;
            options.DefaultChallengeScheme = Auth0OidcAdapter.SessionScheme;
        }).AddScheme<AuthenticationSchemeOptions, OpaqueSessionAuthenticationHandler>(Auth0OidcAdapter.SessionScheme, _ => { });
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-Token";
            options.FormFieldName = "__RequestVerificationToken";
            options.Cookie.Name = "__Host-ut_csrf";
            options.Cookie.Path = "/";
            options.Cookie.Domain = null;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await Problems.WriteAsync(context.HttpContext, 429, "rate_limited", "Too many requests; try again later");
            };
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!IdentityBrowserSecurity.IsProtectedPath(context.Request.Path))
                    return RateLimitPartition.GetNoLimiter("public-health");
                var challenge = context.Request.Path.Equals("/auth/login", StringComparison.OrdinalIgnoreCase) ||
                    context.Request.Path.Equals("/auth/step-up", StringComparison.OrdinalIgnoreCase);
                var key = (challenge ? "challenge:" : "api:") + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new()
                {
                    PermitLimit = challenge ? 20 : 240, Window = challenge ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(1),
                    QueueLimit = 0, AutoReplenishment = true
                });
            });
        });
        return true;
    }

    public static void MapIdentitySecurityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/identity/csrf", (HttpContext context, [Microsoft.AspNetCore.Mvc.FromServices] IAntiforgery antiforgery) =>
            TypedResults.Ok(new CsrfTokenResponse(antiforgery.GetAndStoreTokens(context).RequestToken!)))
            .AllowAnonymous().WithName("IdentityCsrf")
            .Produces<ApiProblem>(403, "application/problem+json")
            .Produces<ApiProblem>(503, "application/problem+json");
    }
}
public sealed record CsrfTokenResponse(string RequestToken);
