using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Documents;

internal sealed partial class EvidenceService
{
    // Immutable reconstruction only: expiry or renewal of today's approval cannot rewrite
    // yesterday's submitted evidence. The command/read/worker separately fences live authority.
    internal Task<ExpectedPublication?> SubmissionOriginalAsync(IClientSessionHandle tx, EvidenceTargetRef target,
        long targetRevision, long reservationId, EvidenceRef reference, long uploaderId, CancellationToken ct) =>
        GuardAsync<ExpectedPublication?>(tx, async () => {
            if (targetRevision <= 0 || reservationId <= 0 || uploaderId <= 0 || reference is null) throw EvidenceValidationRecords.Broken();
            var bundle = await LoadAsync(tx, reservationId, ct);
            var purpose = bundle.Reservation["purpose"].AsString;
            var format = (target.Kind, purpose) switch {
                (EvidenceTargetKind.PaymentDeclaration, "PaymentImage") => false,
                (EvidenceTargetKind.SupplierDocument, "SupplierDocumentImage") => false,
                (EvidenceTargetKind.SupplierDocument, "SupplierDocumentPdf") => true,
                _ => throw EvidenceValidationRecords.Broken()
            };
            if (ReadTarget(bundle.Reservation) != target || Reference(bundle) != reference ||
                bundle.Reservation["uploader"]["userId"] != uploaderId ||
                bundle.Reservation["admittedTargetRevision"].AsInt64 > targetRevision ||
                target.Kind == EvidenceTargetKind.SupplierDocument && bundle.Reservation["admittedTargetRevision"] != targetRevision)
                throw EvidenceValidationRecords.Broken();
            var validation = await HistoricalValidationAsync(tx, bundle, ct) ?? throw EvidenceValidationRecords.Broken();
            var lifecycle = await RequireHistoricalLifecycleAsync(tx, bundle, validation, ct) ?? throw EvidenceValidationRecords.Broken();
            if (validation["status"] != "passed" || lifecycle["state"] != "validated" ||
                format && validation["mime"] != PdfDocumentExtractionContract.InputMime ||
                !format && validation["mime"] == PdfDocumentExtractionContract.InputMime) throw EvidenceValidationRecords.Broken();
            return format ? Publication(bundle) : null;
        });
}

// No private bytes, grant, lease, approval renewal or historical actor promotion is issued here.
internal sealed class EvidenceSubmissionOriginalRecords(IMongoDatabase database)
{
    internal Task<ExpectedPublication?> ReadAsync(IClientSessionHandle tx, EvidenceTargetRef target, long targetRevision,
        long reservationId, EvidenceRef reference, long uploaderId, CancellationToken ct) =>
        new EvidenceService(database, new MongoUnitOfWork((MongoClient)database.Client), new IdAllocator(database), new SystemClock())
            .SubmissionOriginalAsync(tx, target, targetRevision, reservationId, reference, uploaderId, ct);
}
