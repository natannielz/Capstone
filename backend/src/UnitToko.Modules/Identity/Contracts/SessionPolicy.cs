namespace UnitToko.Modules.Identity.Contracts;

public sealed record SessionPolicy(string TrustedIssuer, string TrustedConnectionId, string AssurancePolicyVersion)
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan FreshFactorLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StepUpLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ProviderClockSkew = TimeSpan.FromSeconds(30);
    public const int MaximumProviderPayloadBytes = 65_536;

    public SessionPolicy Validate()
    {
        if (!Uri.TryCreate(TrustedIssuer, UriKind.Absolute, out var issuer) || issuer.Scheme != "https" ||
            issuer.UserInfo.Length != 0 || issuer.Query.Length != 0 || issuer.Fragment.Length != 0 ||
            !Text(TrustedIssuer, 2048) || !Text(TrustedConnectionId, 256) || !Text(AssurancePolicyVersion, 64))
            throw new ArgumentException("A trusted HTTPS issuer, reviewed connection and assurance policy are required.");
        return this;
    }

    public bool Accepts(ProviderIdentityProof proof, DateTimeOffset now) => AcceptsEvidence(proof, now, true);

    /// <summary>Stored evidence retains its original factor time. Sensitive authorization
    /// separately enforces freshness; ordinary session retrieval must not renew assurance.</summary>
    public bool AcceptsStoredEvidence(ProviderIdentityProof proof, DateTimeOffset now) => AcceptsEvidence(proof, now, false);

    private bool AcceptsEvidence(ProviderIdentityProof proof, DateTimeOffset now, bool requireFreshFactor) =>
        proof.Issuer == TrustedIssuer && proof.ConnectionId == TrustedConnectionId && proof.EmailVerified &&
        Text(proof.Subject, 256) && Text(proof.Email, 320) && proof.FactorType == "otp" &&
        proof.AssurancePolicyVersion == AssurancePolicyVersion && proof.ProviderAuthenticatedAt != default &&
        proof.ProviderAuthenticatedAt <= now + ProviderClockSkew && proof.FactorCompletedAt != default &&
        proof.FactorCompletedAt >= proof.ProviderAuthenticatedAt - ProviderClockSkew &&
        proof.FactorCompletedAt <= now + ProviderClockSkew &&
        (!requireFreshFactor || proof.FactorCompletedAt >= now - FreshFactorLifetime) &&
        (proof.ChallengeBinding is null || Text(proof.ChallengeBinding, 128)) &&
        (proof.ProviderSessionId is null || Text(proof.ProviderSessionId, 256)) &&
        (proof.DisplayName is null || Text(proof.DisplayName, 160));

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);
}
