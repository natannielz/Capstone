namespace UnitToko.Modules.Identity.Contracts;

public sealed record ActorContext(
    long UserId,
    string ProviderIssuer,
    string ProviderSubject,
    long GrantVersion,
    DateTimeOffset AuthenticatedAt,
    FactorAssurance Assurance)
{
    public ActorContext Validate()
    {
        if (UserId <= 0 || GrantVersion < 0 || string.IsNullOrWhiteSpace(ProviderIssuer) ||
            string.IsNullOrWhiteSpace(ProviderSubject) || AuthenticatedAt == default)
            throw new ArgumentException("An actor requires a valid identity and grant version.");
        return this;
    }
}

public enum FactorAssurance { Unknown, SingleFactor, MultiFactor, PhishingResistant }
