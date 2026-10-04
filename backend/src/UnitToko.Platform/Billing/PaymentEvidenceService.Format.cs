using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;

namespace UnitToko.Platform.Billing;

public sealed partial class PaymentEvidenceService
{
    public async Task<EvidenceSubmissionFormatView> ReadSubmissionFormatAsync(ActorContext actor, long declarationId, long expectedRevision,
        EvidenceRef reference, AuthorizationReadMetadata metadata, bool review, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await unit.ExecuteAsync((tx, token) => (review ? reviewEvidence : evidence).ReadSubmissionFormatAsync(tx, actor,
            Target(declarationId), expectedRevision, reference, metadata, review ? "payment-review-evidence-format-view" : "payment-evidence-format-view", token), ct);
    }
}
