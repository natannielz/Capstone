using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Platform.Identity;

public sealed record IdentitySession(SessionReference Reference, long UserId, long GrantVersion,
    ProviderIdentityProof Proof, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt,
    DateTimeOffset IdleExpiresAt, DateTimeOffset AbsoluteExpiresAt);

/// <summary>The raw reference is delivered only as the protected browser cookie, never in API JSON or logs.</summary>
public sealed class SessionIssue(string opaqueToken, IdentitySession session)
{
    public string OpaqueToken { get; } = opaqueToken;
    public IdentitySession Session { get; } = session;
    public override string ToString() => "SessionIssue [redacted]";
}
