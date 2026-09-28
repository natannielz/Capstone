using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Inventory;

public enum StockAuthorityMode { Unconfirmed, WebLedger, ExternalLedger }
public enum StockPoolControl { Unconfirmed, ExclusiveWebAllocation, SharedWebLedger, ExternalHold }
public enum SourceAuthorityDisposition { Unavailable, WebLedger, ExternalHoldRequired }

/// <summary>Server-owned proposed or accepted source facts. These records are not request DTOs and
/// construction is not evidence of review, current authority, correct topology or an external capability.</summary>
public sealed record SourceStockPolicy(LongId Id, StockLocation Location, LongId PhysicalPoolId, long PoolRevision,
    long Revision, StockAuthorityMode Mode, TimeSpan MaximumAge, long SafetyBufferBaseUnits, string? ExternalNamespace);
public sealed record StockAuthorityReview(LongId ReviewId, LongId PolicyId, long PolicyRevision, LongId PhysicalPoolId,
    long PoolRevision, StockPoolControl Control, string? ReservationProtocolReference);
public sealed record SourceStockObservation(LongId PolicyId, long PolicyRevision, LongId PhysicalPoolId, long PoolRevision,
    long Revision, DateTimeOffset ObservedAt, DateTimeOffset? ReconciledAt, bool Reconciled, string SourceBatchReference);
public sealed record SourceAuthorityExpectation(long PolicyRevision, long PoolRevision, long ObservationRevision);
public sealed record SourceAuthorityDecision(SourceAuthorityDisposition Disposition, string ReasonCode,
    long? PolicyRevision, long? PoolRevision, long? ObservationRevision, DateTimeOffset? ObservedAt, DateTimeOffset? ReconciledAt);

/// <summary>Source precondition only, never a quantity, reservation, authorization or promise.
/// The transaction owner must load/fence the current reviewed policy, shared physical pool and observation.
/// ExternalHoldRequired still requires an actual acknowledged, bound and unexpired external reservation.</summary>
public static class StockAuthorityPolicy
{
    public static SourceAuthorityDecision Evaluate(SourceStockPolicy? policy, StockAuthorityReview? review,
        SourceStockObservation? observation, StockLocation location, SourceAuthorityExpectation expected, DateTimeOffset now)
    {
        RequireLocation(location);
        if (expected is null || expected.PolicyRevision <= 0 || expected.PoolRevision <= 0 || expected.ObservationRevision <= 0 || !Utc(now))
            throw Invalid();
        if (policy is null) return Decision("stock_authority_missing");
        Validate(policy);
        if (policy.Location != location) return Decision("stock_authority_scope_mismatch");
        if (policy.Revision != expected.PolicyRevision) return Decision("stock_policy_changed", policy);
        if (policy.PoolRevision != expected.PoolRevision) return Decision("stock_pool_changed", policy);
        if (policy.Mode == StockAuthorityMode.Unconfirmed) return Decision("stock_authority_unconfirmed", policy);
        if (review is null) return Decision("stock_authority_unreviewed", policy);
        Validate(review);
        if (review.PolicyId != policy.Id || review.PolicyRevision != policy.Revision ||
            review.PhysicalPoolId != policy.PhysicalPoolId || review.PoolRevision != policy.PoolRevision)
            return Decision("stock_authority_review_stale", policy);
        if (review.Control == StockPoolControl.Unconfirmed) return Decision("stock_pool_control_unconfirmed", policy);
        if (policy.Mode == StockAuthorityMode.WebLedger && review.Control is not (StockPoolControl.ExclusiveWebAllocation or StockPoolControl.SharedWebLedger) ||
            policy.Mode == StockAuthorityMode.ExternalLedger && review.Control != StockPoolControl.ExternalHold)
            return Decision("stock_authority_mode_mismatch", policy);
        if (review.Control == StockPoolControl.ExternalHold && review.ReservationProtocolReference is null)
            return Decision("external_hold_protocol_missing", policy);
        if (observation is null) return Decision("stock_observation_missing", policy);
        Validate(observation);
        if (observation.PolicyId != policy.Id || observation.PolicyRevision != policy.Revision ||
            observation.PhysicalPoolId != policy.PhysicalPoolId || observation.PoolRevision != policy.PoolRevision)
            return Decision("stock_observation_mismatch", policy);
        if (observation.Revision != expected.ObservationRevision) return Decision("stock_observation_changed", policy);
        if (observation.ObservedAt > now || observation.ReconciledAt > now || observation.ReconciledAt < observation.ObservedAt)
            return Decision("stock_source_time_invalid", policy, observation);
        if (!observation.Reconciled || observation.ReconciledAt is null)
            return Decision("stock_unreconciled", policy, observation);
        // Freshness is a half-open interval. Reconciliation does not rejuvenate the observed stock.
        if (now - observation.ObservedAt >= policy.MaximumAge)
            return Decision("stock_observation_stale", policy, observation);
        return policy.Mode == StockAuthorityMode.WebLedger
            ? Decision("web_ledger_authority", policy, observation, SourceAuthorityDisposition.WebLedger)
            : Decision("external_reservation_required", policy, observation, SourceAuthorityDisposition.ExternalHoldRequired);
    }

    private static void Validate(SourceStockPolicy policy)
    {
        RequireLocation(policy.Location);
        if (policy.Id.Value <= 0 || policy.PhysicalPoolId.Value <= 0 || policy.PoolRevision <= 0 || policy.Revision <= 0 ||
            !Enum.IsDefined(policy.Mode) || policy.MaximumAge <= TimeSpan.Zero ||
            policy.MaximumAge.Ticks % TimeSpan.TicksPerMillisecond != 0 || policy.SafetyBufferBaseUnits < 0 ||
            policy.ExternalNamespace is { } source && !StockCountPolicy.CanonicalText(source, 128) ||
            policy.Mode == StockAuthorityMode.WebLedger && policy.ExternalNamespace is not null ||
            policy.Mode == StockAuthorityMode.ExternalLedger && policy.ExternalNamespace is null)
            throw Invalid();
    }

    private static void Validate(StockAuthorityReview review)
    {
        if (review.ReviewId.Value <= 0 || review.PolicyId.Value <= 0 || review.PolicyRevision <= 0 ||
            review.PhysicalPoolId.Value <= 0 || review.PoolRevision <= 0 || !Enum.IsDefined(review.Control) ||
            review.ReservationProtocolReference is { } protocol && !StockCountPolicy.CanonicalText(protocol, 160) ||
            review.Control != StockPoolControl.ExternalHold && review.ReservationProtocolReference is not null)
            throw Invalid();
    }

    private static void Validate(SourceStockObservation observation)
    {
        if (observation.PolicyId.Value <= 0 || observation.PolicyRevision <= 0 || observation.PhysicalPoolId.Value <= 0 ||
            observation.PoolRevision <= 0 || observation.Revision <= 0 || !Utc(observation.ObservedAt) ||
            observation.ReconciledAt is { } at && !Utc(at) || !StockCountPolicy.CanonicalText(observation.SourceBatchReference, 160))
            throw Invalid();
    }

    private static void RequireLocation(StockLocation location)
    {
        if (location is null) throw Invalid();
        try { location.Validate(); } catch (DomainValidationException) { throw Invalid(); }
    }
    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
    private static SourceAuthorityDecision Decision(string code, SourceStockPolicy? policy = null,
        SourceStockObservation? observation = null, SourceAuthorityDisposition disposition = SourceAuthorityDisposition.Unavailable) =>
        new(disposition, code, policy?.Revision, policy?.PoolRevision, observation?.Revision, observation?.ObservedAt, observation?.ReconciledAt);
    private static DomainValidationException Invalid() => new("invalid_stock_authority", "Stock authority requires canonical scoped versioned source facts");
}
