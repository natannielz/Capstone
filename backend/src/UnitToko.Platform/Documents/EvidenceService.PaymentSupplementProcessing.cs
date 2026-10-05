using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Documents;

// Only the separate supplement association can grant this private original.
internal sealed record PaymentSupplementPdfProcessingOriginal(PaymentSupplementOcrSourceSnapshot Source,
    ExpectedPublication Publication, StorageIoBudget Budget, BsonDocument Binding, DateTimeOffset Until);

internal sealed partial class EvidenceService
{
    internal Task<PaymentSupplementPdfProcessingOriginal> PaymentSupplementPdfDocumentProcessingOriginalAsync(
        IClientSessionHandle tx, IPaymentSupplementOcrSource sourceOwner, BsonDocument claim,
        DateTimeOffset claimDeadline, CancellationToken ct) => PaymentSupplementPdfOriginalAsync(tx, sourceOwner, claim, claimDeadline, true, ct);

    // The scoped read adapter already owns a captured final D06 fence. Only its
    // callback may defer this duplicate fence; worker reads always fence here.
    private Task<PaymentSupplementPdfProcessingOriginal> PaymentSupplementPdfOriginalAsync(
        IClientSessionHandle tx, IPaymentSupplementOcrSource sourceOwner, BsonDocument claim,
        DateTimeOffset claimDeadline, bool fenceDecision, CancellationToken ct) => GuardAsync<PaymentSupplementPdfProcessingOriginal>(tx, async () => {
        if (claim.GetValue("kind", "") != PaymentSupplementBson.JobKind ||
            !ReferenceEquals(sourceOwner.Database.Client, database.Client) ||
            !sourceOwner.Database.DatabaseNamespace.Equals(database.DatabaseNamespace)) throw Invalid();
        var source = await sourceOwner.RequireAsync(tx, claim, ct);
        var decisions = new PaymentSupplementPolicy(database, clock);
        var decision = await decisions.RequireAsync(tx, source.Scope, ct);
        var pin = await EvidencePinRecords.ReadAsync(database, tx, source.PinId, ct);
        var bundle = await LoadAsync(tx, source.ReservationId, ct);
        if (!IsPdf(bundle) || PersistedPurpose(bundle.Reservation) != EvidencePurpose.PaymentSupplementPdf)
            throw Conflict("evidence_processing_format_changed");
        var owner = new GrantScope(GrantScopeKind.FulfillmentShop, source.Scope["organizationId"].AsInt64,
            ShopId: source.Scope["shopId"].AsInt64, ChannelId: source.Scope["channelId"].AsInt64);
        if (source.SupplementRevision != 2 || source.PdfEvidence == source.Primary.PrimaryEvidence ||
            pin["associationKind"] != "PaymentSupplement" || pin["associationId"] != source.SupplementId ||
            pin["associationHash"] != source.AttachmentHash || pin["supplementId"] != source.SupplementId ||
            pin["supplementRevision"] != source.SupplementRevision || !pin["primary"].Equals(PaymentSupplementBson.Primary(source.Primary)) ||
            pin["primarySubmissionHash"] != source.PrimarySubmissionHash || pin["reservationId"] != source.ReservationId ||
            pin["actorId"] != source.UploaderId || !pin["reference"].Equals(ReadReference(source.PdfEvidence)) ||
            ReadTarget(bundle.Reservation) != new EvidenceTargetRef(EvidenceTargetKind.PaymentSupplement, source.SupplementId) ||
            bundle.Reservation["admittedTargetRevision"] != 1L || bundle.Reservation["uploader"]["userId"] != source.UploaderId ||
            !bundle.Reservation["owner"].Equals(CatalogRepository.Owner(owner)) || Reference(bundle) != source.PdfEvidence ||
            pin["versionRowId"] != bundle.Version["_id"] || pin["storeId"] != bundle.Version["storeId"] ||
            pin["generation"] != bundle.Version["generation"] || Publication(bundle) != source.Original)
            throw Conflict("evidence_processing_reference_changed");
        var eligible = await ReadEligibilityAsync(tx, bundle, ct);
        if (pin["validationHash"] != eligible.Validation["resultHash"] || pin["lifecycleRevision"] != eligible.Lifecycle["revision"])
            throw Conflict("evidence_processing_reference_changed");
        if (fenceDecision) await decisions.FenceAsync(tx, decision, ct);
        var until = PdfProcessingReadUntil(eligible, claimDeadline);
        if (Date(decision["expiresAt"]) < until) until = Date(decision["expiresAt"]);
        FinalCheck(until, ct);
        var binding = new BsonDocument {
            { "submissionId", source.SupplementId }, { "supplementId", source.SupplementId }, { "supplementRevision", source.SupplementRevision },
            { "primary", PaymentSupplementBson.Primary(source.Primary) }, { "primarySubmissionHash", source.PrimarySubmissionHash },
            { "submissionHash", source.AttachmentHash }, { "reservationId", source.ReservationId }, { "pinId", source.PinId },
            { "uploaderId", source.UploaderId }, { "pinHash", pin["pinHash"] }, { "reference", ReadReference(source.PdfEvidence) },
            { "versionRowId", bundle.Version["_id"] }, { "storeId", bundle.Version["storeId"] }, { "generation", bundle.Version["generation"] },
            { "length", bundle.Version["length"] }, { "validationHash", eligible.Validation["resultHash"] },
            { "lifecycleRevision", eligible.Lifecycle["revision"] }, { "readPolicyId", eligible.Policy["_id"] },
            { "readPolicyRevision", eligible.Policy["revision"] }, { "validationPolicyId", eligible.ValidationPolicy["_id"] },
            { "validationPolicyRevision", eligible.ValidationPolicy["revision"] }
        };
        return new(source, Publication(bundle), new(eligible.Policy["maximumBytes"].AsInt64,
            TimeSpan.FromMilliseconds(eligible.Policy["verificationDurationMs"].AsInt64),
            (int)bundle.Reservation["admittedLimits"]["bufferBytes"].AsInt64), binding, until);
    });
}
