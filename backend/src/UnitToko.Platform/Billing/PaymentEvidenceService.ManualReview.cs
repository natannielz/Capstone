using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Documents;

namespace UnitToko.Platform.Billing;

public sealed partial class PaymentEvidenceService
{
    // The consumer must also use WithVerificationOriginalAsync to hold and verify the actual bytes.
    internal Task<ManualFailureSource> ManualFailureAsync(IClientSessionHandle tx, PaymentExtractionView view, long revision, CancellationToken ct) =>
        reviewEvidence.ManualFailureAsync(tx, view, revision, ct);
    internal Task ValidateAcceptedManualFailureAsync(IClientSessionHandle tx, long id, long revision, long submissionId, EvidenceRef reference, BsonDocument snapshot, CancellationToken ct) =>
        reviewEvidence.ValidateAcceptedManualFailureAsync(tx, id, revision, submissionId, reference, snapshot, ct);
}
