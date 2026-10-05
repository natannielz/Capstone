using UnitToko.Platform.Documents;
using System.Diagnostics;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Billing;

// Only a private-evidence adapter can issue this capability after exact persisted-purpose
// reconstruction and verified opening. No HTTP DTO accepts any publication or storage token.
internal sealed class VerifiedPaymentSupplementOriginal
{
    private readonly EvidenceReadClaim claim;
    private readonly VerifiedOriginalHandle handle;
    internal ExpectedPublication Original => claim.Original;
    internal long UploaderId { get; }
    internal long ActorGrantVersion { get; }
    private VerifiedPaymentSupplementOriginal(ActorContext actor, EvidenceReadClaim claim, VerifiedOriginalHandle handle)
    { UploaderId = actor.UserId; ActorGrantVersion = actor.GrantVersion; this.claim = claim; this.handle = handle; }
    internal static VerifiedPaymentSupplementOriginal FromVerifiedClaim(ActorContext actor, EvidencePurpose actualPersistedPurpose,
        EvidenceReadClaim claim, VerifiedOriginalHandle handle) {
        if (actor is null || claim is null || handle is null || actualPersistedPurpose.ToString() != "PaymentSupplementPdf" || claim.Target.Kind.ToString() != "PaymentSupplement" ||
            claim.TargetRevision != 1 || claim.Mime != "application/pdf" || claim.Reference.Sha256 != claim.Original.Content.Sha256 ||
            claim.ReservationId != claim.Original.Attempt.ReservationId || claim.Reference.Version != 1 || !handle.Stream.CanRead ||
            handle.Attempt != claim.Original.Attempt || handle.Content != claim.Original.Content) throw Changed();
        return new(actor, claim, handle);
    }
    internal void Require(ActorContext actor, PaymentSupplementAttachInput input, ExpectedPublication actual, IClock clock) {
        input.Validate();
        if (actor.UserId != UploaderId || actor.GrantVersion != ActorGrantVersion || input.SupplementId != claim.Target.Id ||
            input.ExpectedRevision != claim.TargetRevision || input.ReservationId != claim.ReservationId || input.Evidence != claim.Reference ||
            actual != Original || clock.UtcNow >= claim.Deadline || !handle.Stream.CanRead || handle.Attempt != actual.Attempt ||
            handle.Content != actual.Content || Stopwatch.GetElapsedTime(claim.LeaseStarted) >= claim.InitialLeaseDuration) throw Changed();
    }
    private static PersistenceConflictException Changed() => new("payment_supplement_original_changed", "PDF lampiran atau hak akses berubah; muat ulang tanpa mengganti gambar utama.");
}
