using Microsoft.AspNetCore.Authentication;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Identity;

namespace UnitToko.Api.Identity;

public sealed class ProviderTokenEnvelope(string idToken)
{
    public string IdToken { get; } = idToken;
    public override string ToString() => "ProviderTokenEnvelope [redacted]";
}

/// <summary>Root orchestration re-reads/consumes state and admits the exact provider identity,
/// atomically issuing or rotating the opaque session. It writes the cookie only after commit.</summary>
public interface IOidcSignInCompletion
{
    Task CompleteAsync(HttpContext http, string stateToken, OidcTransaction transaction, ProviderIdentityProof proof,
        ProviderTokenEnvelope tokens, CancellationToken cancellationToken);
}

public static class UnitTokoOidcChallenge
{
    internal const string StateItem = "unit-toko:transaction";
    internal const string ChallengeParameter = "unit-toko:challenge";
    public static AuthenticationProperties Properties(OidcTransactionStart start)
    {
        var properties = new AuthenticationProperties { RedirectUri = "/" };
        properties.Items[StateItem] = start.StateToken;
        // Parameters are transient handler inputs; unlike Items they are not persisted in state.
        properties.Parameters[ChallengeParameter] = start.ChallengeBinding;
        return properties;
    }
}
