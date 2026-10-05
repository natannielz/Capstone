using UnitToko.Modules.Contracts;

namespace UnitToko.Modules.Documents;

public enum EvidencePurpose { CatalogImage = 1, PaymentImage = 2, RefundPayoutImage = 3, ProcurementDisbursementImage = 4, ProcurementUnboxingVideo = 5, SupplierDocumentImage = 6, PaymentPdf = 7, SupplierDocumentPdf = 8, PaymentSupplementPdf = 9 }

/// <summary>PDF admission binds an explicit persisted purpose; an image request never becomes a PDF through MIME detection.</summary>
public sealed record ReserveEvidencePdf
{
    public EvidenceTargetRef Target { get; }
    public EvidencePurpose Purpose { get; }
    public long ExpectedTargetRevision { get; }
    public ReserveEvidencePdf(EvidenceTargetRef target, EvidencePurpose purpose, long expectedTargetRevision) {
        if (target is null || expectedTargetRevision <= 0 || !((purpose == EvidencePurpose.PaymentPdf && target.Kind == EvidenceTargetKind.PaymentDeclaration) ||
            (purpose == EvidencePurpose.SupplierDocumentPdf && target.Kind == EvidenceTargetKind.SupplierDocument && expectedTargetRevision == 1) ||
            (purpose == EvidencePurpose.PaymentSupplementPdf && target.Kind == EvidenceTargetKind.PaymentSupplement && expectedTargetRevision == 1)))
            throw new DomainValidationException("evidence_invalid_pdf_admission", "Use the payment declaration or supplier-document draft and its current PDF admission revision");
        Target = target; Purpose = purpose; ExpectedTargetRevision = expectedTargetRevision;
    }
}

/// <summary>Video admission is separate from image limits and requires its actual inspection owner.</summary>
public sealed record ReserveEvidenceVideo
{
    public EvidenceTargetRef Target { get; }
    public EvidencePurpose Purpose => EvidencePurpose.ProcurementUnboxingVideo;
    public long ExpectedTargetRevision { get; }
    public ReserveEvidenceVideo(EvidenceTargetRef target, long expectedTargetRevision) {
        if (target is null || target.Kind != EvidenceTargetKind.ProcurementInspection || expectedTargetRevision <= 0)
            throw new DomainValidationException("evidence_invalid_admission", "Use the actual inspection draft and its current revision");
        Target = target; ExpectedTargetRevision = expectedTargetRevision;
    }
}

public sealed record ReserveEvidenceImage
{
    public EvidenceTargetRef Target { get; }
    public EvidencePurpose Purpose { get; }
    public long? ExpectedTargetRevision { get; }
    public ReserveEvidenceImage(EvidenceTargetRef target, EvidencePurpose purpose, long? expectedTargetRevision) {
        if (target is null || expectedTargetRevision is <= 0 || !((purpose == EvidencePurpose.CatalogImage && target.Kind is EvidenceTargetKind.CatalogFamily or EvidenceTargetKind.CatalogSku) ||
            (purpose == EvidencePurpose.PaymentImage && target.Kind == EvidenceTargetKind.PaymentDeclaration) ||
            (purpose == EvidencePurpose.RefundPayoutImage && target.Kind == EvidenceTargetKind.RefundPayout) ||
            (purpose == EvidencePurpose.ProcurementDisbursementImage && target.Kind == EvidenceTargetKind.ProcurementDisbursement) ||
            (purpose == EvidencePurpose.SupplierDocumentImage && target.Kind == EvidenceTargetKind.SupplierDocument)))
            throw new DomainValidationException("evidence_invalid_admission", "Use the owning record's supported evidence purpose");
        Target = target; Purpose = purpose; ExpectedTargetRevision = expectedTargetRevision;
    }
}

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
        if (target is null || target.Kind is not (EvidenceTargetKind.CatalogFamily or EvidenceTargetKind.CatalogSku) ||
            purpose != EvidencePurpose.CatalogImage || expectedTargetRevision is <= 0)
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
