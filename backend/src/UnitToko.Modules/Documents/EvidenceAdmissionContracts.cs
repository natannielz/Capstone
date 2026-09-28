using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Documents;

public enum EvidencePurpose { CatalogImage = 1 }

/// <summary>No scope, hash, address, byte count or storage ticket is accepted from the caller.</summary>
public sealed record ReserveCatalogImage
{
    public EvidenceTargetRef Target { get; }
    public EvidencePurpose Purpose { get; }
    public long? ExpectedTargetRevision { get; }
    public string? Filename { get; }
    public ReserveCatalogImage(EvidenceTargetRef target, EvidencePurpose purpose = EvidencePurpose.CatalogImage,
        long? expectedTargetRevision = null, string? filename = null)
    {
        if (target is null || purpose != EvidencePurpose.CatalogImage || expectedTargetRevision is <= 0)
            throw new DomainValidationException("evidence_invalid_admission", "Supported target, purpose and positive revision are required");
        Target = target; Purpose = purpose; ExpectedTargetRevision = expectedTargetRevision; Filename = filename;
    }
}

/// <summary>Admission metadata only; neither this result nor a reference grants access to bytes.</summary>
public sealed record ReservationResult(long ReservationId, long LineageId, long VersionRowId, long AttemptId,
    long ContentVersion, long Revision, DateTimeOffset ExpiresAt);
public sealed record EvidenceResult(long ReservationId, EvidenceRef Reference, long LifecycleRevision);
public sealed record SafeStatus(long ReservationId, string State, long Revision, DateTimeOffset ExpiresAt,
    EvidenceRef? Reference, string ScanStatus, string DecoderStatus, string? SafeFailure);
public sealed record UploadOutcome(string State, EvidenceResult? Evidence = null);
