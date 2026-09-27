using Microsoft.Extensions.Options;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using UnitToko.Platform.Mongo;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Api.Identity;

/// <summary>The maintained OIDC handler calls this only after all protocol checks finish.
/// No cookie is issued until admission, state consumption and session issuance commit together.</summary>
public sealed class VerifiedOidcSignInCompletion(MongoUnitOfWork unit, OidcTransactionStore transactions,
    IdentityOnboardingService onboarding, MongoIdentitySessionStore sessions, IOptions<Auth0Options> configured) : IOidcSignInCompletion
{
    public async Task CompleteAsync(HttpContext http, string stateToken, OidcTransaction validated,
        ProviderIdentityProof proof, ProviderTokenEnvelope tokens, CancellationToken cancellationToken)
    {
        var options = configured.Value;
        if (!options.Enabled || proof.Issuer != options.Issuer) throw Denied();
        var browserToken = http.Request.Cookies[OpaqueSessionCookie.Name];
        if (validated.Request.Purpose == OidcTransactionPurpose.StepUp)
        {
            if (!OidcTransactionStore.IsToken(browserToken) ||
                OidcTransactionStore.Hash(browserToken!) != validated.Request.ExpectedSession?.Id)
                throw Denied();
        }
        else if (browserToken is not null && await sessions.ReadAsync(browserToken, cancellationToken) is not null)
            throw new AuthorizationDeniedException("already_signed_in");

        var issue = await unit.ExecuteAsync(async (tx, ct) =>
        {
            var current = await transactions.ReadPendingAsync(stateToken, tx, ct);
            if (current.Id != validated.Id || current.Version != validated.Version || current.Request != validated.Request ||
                current.Request.Issuer != options.Issuer || current.Request.ClientId != options.ClientId ||
                !OidcTransactionStore.MatchesChallenge(current, proof.ChallengeBinding))
                throw Denied();
            ReturnPathPolicy.Require(current.Request.ReturnPath, options);
            SessionIssue result;
            if (current.Request.Purpose == OidcTransactionPurpose.Login)
            {
                var invitation = current.Request.InvitationId is { } invitationId
                    ? new InvitationRedemptionBinding(invitationId, current.Request.InvitationSecretHash!) : null;
                var admission = await onboarding.AdmitAsync(tx, proof, invitation, ct);
                result = await sessions.CreateAsync(tx, admission, proof, tokens.IdToken, ct);
            }
            else
            {
                if (current.Request.ExpectedSession is not { } reference || current.Request.ExpectedSubject != proof.Subject)
                    throw Denied();
                result = await sessions.RotateForStepUpAsync(tx, reference, proof, tokens.IdToken, proof.ChallengeBinding!, current.CreatedAt, ct);
                if (result.Session.UserId != current.Request.ExpectedUserId ||
                    result.Session.GrantVersion != current.Request.ExpectedGrantVersion)
                    throw Denied();
            }
            await transactions.ConsumeAsync(current, tx, ct);
            return result;
        }, cancellationToken);
        OpaqueSessionCookie.Issue(http.Response, issue);
    }
    private static AuthorizationDeniedException Denied() => new("sign_in_state_changed");
}

public static class OpaqueSessionCookie
{
    public const string Name = "__Host-ut_session";
    public static void Issue(HttpResponse response, SessionIssue issue) => response.Cookies.Append(Name, issue.OpaqueToken,
        Options(issue.Session.AbsoluteExpiresAt));
    public static void Delete(HttpResponse response) => response.Cookies.Delete(Name, Options(null));
    private static CookieOptions Options(DateTimeOffset? expires) => new()
    {
        HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", Domain = null,
        IsEssential = true, Expires = expires
    };
}
