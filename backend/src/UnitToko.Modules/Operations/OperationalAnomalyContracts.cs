using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Operations;

public sealed record AnomalySourceChoice(string Kind, LongId Id, string Label);
public sealed record AnomalySourceChoices(IReadOnlyList<AnomalySourceChoice> Items, LongId? NextCursor, AnomalySignal Adjustment);
public sealed record AnomalyDisposition(LongId Id, LongId ReviewerId, DateTimeOffset RecordedAt, string Outcome, string Reason);
public sealed record AnomalyPolicyRecovery(string Status, string? PolicyVersion);
public sealed record AnomalyOperationRecovery(string Status, AnomalyEvaluation? Evaluation);
public sealed record AnomalyEvaluation(LongId Id, string SourceKind, LongId SourceId, string SourceHash,
    string RuleVersion, string? PolicyVersion, LongId PolicyRevision, DateTimeOffset RecordedAt,
    IReadOnlyList<AnomalySignal> Signals, bool Current, AnomalyDisposition? Disposition);
