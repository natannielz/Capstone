using System.Collections.Immutable;

namespace UnitToko.Platform.Identity;

public enum ProviderEventTransport { Auth0Events, Auth0Logs }
public enum ProviderSecurityChange { AccountDisabled, AccountDeleted, PasswordChanged, FactorChanged, FactorRemoved }
public sealed record ProviderEventBatchResult(int Applied, int Duplicates, int UnknownSubjects, int Ignored);
internal sealed record VerifiedProviderSecurityEvent(string Issuer, string EventId, string Subject,
    ProviderSecurityChange Change, DateTimeOffset OccurredAt, string Fingerprint);
internal sealed record VerifiedProviderSecurityBatch(ImmutableArray<VerifiedProviderSecurityEvent> Events, int IgnoredCount);
