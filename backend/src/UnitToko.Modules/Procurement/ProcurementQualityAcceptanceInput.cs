using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Procurement;

/// <summary>An explicit, segregated physical subset of one immutable held receipt.</summary>
public sealed record ProcurementQualityAcceptanceInput(ProcurementLocation Location, long ReceiptId, long ReceiptRevision,
    int AcceptedPackages, long PolicyId, long PolicyRevision, DateTimeOffset ObservedAt, string QualityReference,
    string PutawayReference, string SegregationReference, string AcceptedCondition, string LotReference,
    DateOnly? ExpiresOn, string? NonExpiringReason, string Reason);

/// <summary>The checker supplies a new observation; the maker's observation cannot stand in for it.</summary>
public sealed record ProcurementQualityAcceptanceObservation(DateTimeOffset ObservedAt, int AcceptedPackages,
    string QualityReference, string PutawayReference, string SegregationReference, string AcceptedCondition,
    string LotReference, DateOnly? ExpiresOn, string? NonExpiringReason);

public enum ProcurementQualityAcceptanceDecision { Approve, Reject }

public static class ProcurementQualityAcceptanceRules
{
    private static void Text(string? value, int maximum, bool optional = false) {
        if (optional && value is null) return;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value != value.Trim() || value.Any(char.IsControl)) throw Invalid();
    }
    private static void Observation(DateTimeOffset observedAt, int packages, string quality, string putaway, string segregation,
        string condition, string lot, DateOnly? expiry, string? nonExpiring) {
        if (observedAt == default || observedAt.Offset != TimeSpan.Zero || observedAt.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            packages is < 1 or > DomainValues.MaximumLineQuantity ||
            condition is not ("sealed_undamaged" or "intact_as_defined") || expiry is { Year: < 2000 } ||
            (expiry is null) != (nonExpiring is not null)) throw Invalid();
        Text(quality, 256); Text(putaway, 256); Text(segregation, 256); Text(lot, 128); Text(nonExpiring, 1000, true);
    }
    public static ProcurementQualityAcceptanceInput Copy(ProcurementQualityAcceptanceInput input) {
        if (input is null || input.ReceiptId <= 0 || input.ReceiptRevision != 1 || input.PolicyId <= 0 ||
            input.PolicyRevision is <= 0 or >= long.MaxValue - 1) throw Invalid();
        ProcurementRequestRules.Location(input.Location); Observation(input.ObservedAt, input.AcceptedPackages, input.QualityReference,
            input.PutawayReference, input.SegregationReference, input.AcceptedCondition, input.LotReference, input.ExpiresOn, input.NonExpiringReason);
        Text(input.Reason, 1000); return input with { Location = input.Location with { } };
    }
    public static ProcurementQualityAcceptanceObservation Copy(ProcurementQualityAcceptanceObservation input) {
        if (input is null) throw Invalid();
        Observation(input.ObservedAt, input.AcceptedPackages, input.QualityReference, input.PutawayReference,
            input.SegregationReference, input.AcceptedCondition, input.LotReference, input.ExpiresOn, input.NonExpiringReason);
        return input with { };
    }
    public static DomainValidationException Invalid(string code = "invalid_procurement_quality_acceptance") =>
        new(code, "Review the exact held receipt, independently observed healthy subset, lot, expiry and putaway evidence");
}
