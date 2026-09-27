using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using UnitToko.Api.Authorization;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Identity;

namespace UnitToko.Api.Identity;

internal sealed record VerifiedSessionFeature(IdentitySession Session);

/// <summary>Authenticates only a current, encrypted, approved persistent session. Submitted
/// identity headers, provider role claims and the legacy demo cookie have no authority.</summary>
public sealed class OpaqueSessionAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, MongoIdentitySessionStore sessions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (ProviderSecurityEventEndpoints.IsReceiver(Context)) return AuthenticateResult.NoResult();
        var token = Request.Cookies[OpaqueSessionCookie.Name];
        if (string.IsNullOrEmpty(token)) return AuthenticateResult.NoResult();
        var session = await sessions.ReadAsync(token, Context.RequestAborted);
        if (session is null) return AuthenticateResult.NoResult();
        Context.Features.Set(new VerifiedSessionFeature(session));
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString(CultureInfo.InvariantCulture)),
            new Claim("unit_toko_session", session.Reference.Id)
        ], Auth0OidcAdapter.SessionScheme);
        return AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Auth0OidcAdapter.SessionScheme));
    }
}

public sealed class VerifiedSessionActorResolver : IRequestActorResolver
{
    public ValueTask<ActorContext?> ResolveAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var session = context.Features.Get<VerifiedSessionFeature>()?.Session;
        return ValueTask.FromResult(session is null ? null : new ActorContext(session.UserId,
            session.Proof.Issuer, session.Proof.Subject, session.GrantVersion, session.Proof.FactorCompletedAt,
            FactorAssurance.MultiFactor, session.Reference));
    }
}
