using System.Collections.Immutable;
using UnitToko.Modules.Contracts;

namespace UnitToko.Platform.Mongo;

/// <summary>Explicit server-side consumer registration; never supplied by a queued payload.</summary>
public sealed class OutboxClaimPolicy
{
    public ImmutableArray<string> Kinds { get; }
    public int MaximumAttempts { get; }
    public TimeSpan Lease { get; }
    public TimeSpan MaximumProcessingTime { get; }
    public OutboxClaimPolicy(IEnumerable<string> kinds, int maximumAttempts, TimeSpan lease, TimeSpan maximumProcessingTime)
    {
        var copy = kinds?.Take(33).ToImmutableArray() ?? [];
        if (copy.Length is < 1 or > 32 || copy.Any(kind => string.IsNullOrWhiteSpace(kind) || kind.Length > 100 || kind != kind.Trim()) ||
            copy.Distinct(StringComparer.Ordinal).Count() != copy.Length || maximumAttempts is < 1 or > 1000 ||
            lease < TimeSpan.FromSeconds(1) || lease > TimeSpan.FromHours(1) || maximumProcessingTime < lease || maximumProcessingTime > TimeSpan.FromHours(1))
            throw new DomainValidationException("invalid_outbox_policy", "Consumer kinds and processing budgets must be explicit and bounded");
        Kinds = copy; MaximumAttempts = maximumAttempts; Lease = lease; MaximumProcessingTime = maximumProcessingTime;
    }
}