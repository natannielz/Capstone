namespace UnitToko.Modules.Identity.Contracts;

/// <summary>Created only after the OIDC handler has validated the provider token and the
/// controlled assurance claims. Email is an attribute, never an identity key.</summary>
public sealed record ProviderIdentityProof(
    string Issuer, string Subject, string Email, bool EmailVerified, string ConnectionId,
    DateTimeOffset ProviderAuthenticatedAt, DateTimeOffset FactorCompletedAt, string FactorType,
    string AssurancePolicyVersion, string? ChallengeBinding = null, string? ProviderSessionId = null,
    string? DisplayName = null);

/// <summary>Current local approval result. The session writer rechecks this identity in the
/// same transaction as invitation redemption and OIDC transaction consumption.</summary>
public sealed record IdentityAdmission(long UserId, long SecurityVersion, long GrantVersion,
    string Issuer, string Subject);

/// <summary>Trusted backend reference. Id is the session token hash, never the browser secret.</summary>
public sealed record SessionReference(string Id, long Version, long SecurityVersion);
