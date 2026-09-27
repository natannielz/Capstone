namespace UnitToko.Modules.Identity.Contracts;

public sealed record ActorContext(
    long UserId,
    string ProviderIssuer,
    string ProviderSubject,
    long GrantVersion,
    DateTimeOffset AuthenticatedAt,
    FactorAssurance Assurance,
    SessionReference? Session = null)
{
    // This is the actual provider-verified second-factor completion time, never cookie issue or primary-login time.
    public DateTimeOffset FactorCompletedAt => AuthenticatedAt;

    public ActorContext Validate()
    {
        if (UserId <= 0 || GrantVersion < 0 || string.IsNullOrWhiteSpace(ProviderIssuer) ||
            string.IsNullOrWhiteSpace(ProviderSubject) || AuthenticatedAt == default)
            throw new ArgumentException("An actor requires a valid identity and grant version.");
        if (Session is { } session && (session.Id is not { Length: 64 } ||
            session.Id.Any(character => !char.IsAsciiHexDigitLower(character)) || session.Version < 1 || session.SecurityVersion < 1))
            throw new ArgumentException("A session actor requires a valid persistent session reference.");
        return this;
    }
}

public enum FactorAssurance { Unknown, SingleFactor, MultiFactor, PhishingResistant }
