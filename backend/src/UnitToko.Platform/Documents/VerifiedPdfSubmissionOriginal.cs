using System.Diagnostics;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Documents;

/// <summary>Internal capability projected only after persisted format reconstruction and verified original opening. Never a request DTO.</summary>
internal sealed class VerifiedPdfSubmissionOriginal
{
    private readonly EvidenceReadClaim claim;
    private readonly VerifiedOriginalHandle handle;
    internal EvidencePurpose Purpose { get; }
    internal ExpectedPublication Original => claim.Original;
    internal long UploaderId { get; }
    internal long ActorGrantVersion { get; }
    private VerifiedPdfSubmissionOriginal(ActorContext actor, EvidencePurpose purpose, EvidenceReadClaim claim, VerifiedOriginalHandle handle)
    { UploaderId = actor.UserId; ActorGrantVersion = actor.GrantVersion; Purpose = purpose; this.claim = claim; this.handle = handle; }
    internal static VerifiedPdfSubmissionOriginal FromVerifiedClaim(ActorContext actor, EvidencePurpose actualPurpose, EvidenceReadClaim claim, VerifiedOriginalHandle handle)
    {
        if (actor is null || claim is null || handle is null || actualPurpose is not (EvidencePurpose.PaymentPdf or EvidencePurpose.SupplierDocumentPdf) ||
            claim.Mime != PdfDocumentExtractionContract.InputMime || claim.Target.Kind != (actualPurpose == EvidencePurpose.PaymentPdf ? EvidenceTargetKind.PaymentDeclaration : EvidenceTargetKind.SupplierDocument) ||
            claim.Reference.Sha256 != claim.Original.Content.Sha256 || claim.ReservationId != claim.Original.Attempt.ReservationId ||
            !handle.Stream.CanRead || handle.Attempt != claim.Original.Attempt || handle.Content != claim.Original.Content) throw Invalid();
        return new(actor, actualPurpose, claim, handle);
    }
    internal void Require(ActorContext actor, EvidenceTargetRef target, long targetRevision, long reservationId, EvidenceRef reference,
        ExpectedPublication persistedOriginal, IClock clock)
    {
        if (actor.UserId != UploaderId || actor.GrantVersion != ActorGrantVersion || target != claim.Target || targetRevision != claim.TargetRevision ||
            reservationId != claim.ReservationId || reference != claim.Reference || persistedOriginal != Original || clock.UtcNow >= claim.Deadline ||
            !handle.Stream.CanRead || handle.Attempt != Original.Attempt || handle.Content != Original.Content ||
            Stopwatch.GetElapsedTime(claim.LeaseStarted) >= claim.InitialLeaseDuration) throw Invalid();
    }
    private static PersistenceConflictException Invalid() => new("pdf_submission_original_changed", "PDF asli atau hak pengajuan berubah; muat ulang sebelum mengajukan.");
}
