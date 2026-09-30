using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Documents;

internal sealed record ProcessingOriginal(PaymentOcrSourceSnapshot Source, ExpectedPublication Publication, StorageIoBudget Budget, BsonDocument Binding, DateTimeOffset Until);

internal sealed partial class EvidenceService
{
    // Internal service-only path. The owning worker transaction must already fence its live queue claim and approved policy.
    // Documents independently checks the owning source and exact immutable pin before issuing any private original grant.
    internal Task<ProcessingOriginal> ProcessingOriginalAsync(IClientSessionHandle tx, IPaymentOcrSource sourceOwner, BsonDocument claim, DateTimeOffset claimDeadline, CancellationToken ct) => GuardAsync<ProcessingOriginal>(tx, async () => {
        if (!ReferenceEquals(sourceOwner.Database.Client, database.Client) || !sourceOwner.Database.DatabaseNamespace.Equals(database.DatabaseNamespace)) throw Invalid();
        var source = await sourceOwner.RequireAsync(tx, claim, ct);
        var pin = await EvidencePinRecords.ReadAsync(database, tx, source.PinId, ct);
        var bundle = await LoadAsync(tx, source.ReservationId, ct);
        if (pin["associationId"] != source.SubmissionId || pin["associationHash"] != source.SubmissionHash || pin["declarationId"] != source.DeclarationId ||
            pin["declarationVersionId"] != source.DeclarationVersionId || pin["declarationRevision"] != source.DeclarationRevision || pin["reviewRevision"] != source.ReviewRevision ||
            pin["reservationId"] != source.ReservationId || pin["actorId"] != source.UploaderId || !pin["reference"].Equals(ReadReference(source.Evidence)) ||
            ReadTarget(bundle.Reservation) != new UnitToko.Modules.Documents.EvidenceTargetRef(UnitToko.Modules.Documents.EvidenceTargetKind.PaymentDeclaration, source.DeclarationId) ||
            bundle.Reservation["uploader"]["userId"] != source.UploaderId || !bundle.Reservation["owner"].Equals(CatalogRepository.Owner(source.Owner)) || Reference(bundle) != source.Evidence ||
            pin["versionRowId"] != bundle.Version["_id"] || pin["storeId"] != bundle.Version["storeId"] || pin["generation"] != bundle.Version["generation"])
            throw Conflict("evidence_processing_reference_changed");
        var eligible = await ReadEligibilityAsync(tx, bundle, ct);
        if (pin["validationHash"] != eligible.Validation["resultHash"] || pin["lifecycleRevision"] != eligible.Lifecycle["revision"]) throw Conflict("evidence_processing_reference_changed");
        var until = ReadUntil(eligible, claimDeadline); FinalCheck(until, ct);
        var binding = new BsonDocument {
            { "submissionId", source.SubmissionId }, { "declarationId", source.DeclarationId }, { "declarationVersionId", source.DeclarationVersionId },
            { "declarationRevision", source.DeclarationRevision }, { "reviewRevision", source.ReviewRevision }, { "submissionHash", source.SubmissionHash },
            { "reservationId", source.ReservationId }, { "pinId", source.PinId }, { "pinHash", pin["pinHash"] }, { "reference", ReadReference(source.Evidence) },
            { "versionRowId", bundle.Version["_id"] }, { "storeId", bundle.Version["storeId"] }, { "generation", bundle.Version["generation"] }, { "length", bundle.Version["length"] },
            { "validationHash", eligible.Validation["resultHash"] }, { "lifecycleRevision", eligible.Lifecycle["revision"] },
            { "readPolicyId", eligible.Policy["_id"] }, { "readPolicyRevision", eligible.Policy["revision"] },
            { "validationPolicyId", eligible.ValidationPolicy["_id"] }, { "validationPolicyRevision", eligible.ValidationPolicy["revision"] } };
        return new(source, Publication(bundle), new(eligible.Policy["maximumBytes"].AsInt64, TimeSpan.FromMilliseconds(eligible.Policy["verificationDurationMs"].AsInt64),
            (int)bundle.Reservation["admittedLimits"]["bufferBytes"].AsInt64), binding, until);
    });
}
