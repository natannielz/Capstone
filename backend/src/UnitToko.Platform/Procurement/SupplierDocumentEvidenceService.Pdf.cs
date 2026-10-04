using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;

namespace UnitToko.Platform.Procurement;

// Separate original format; no supplier invoice/SPH matching, stock effect or money authorization.
public sealed partial class SupplierDocumentEvidenceService
{
    public async Task<ReservationResult> ReservePdfAsync(ActorContext actor, long draftId, long expectedRevision,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (expectedRevision != 1) throw B.Changed(); await StorageAsync(ct);
        return await unit.ExecuteAsync((tx, token) => evidence.ReservePdfAsync(tx, actor,
            new(Target(draftId), EvidencePurpose.SupplierDocumentPdf, expectedRevision), metadata, token), ct);
    }
    public async Task<ReservationResult?> RecoverPdfReservationAsync(ActorContext actor, long draftId, string key, CancellationToken ct = default) {
        await StorageAsync(ct); await PdfEvidenceAdmissionSchemas.RequireAsync(database, ct);
        return await unit.ExecuteAsync((tx, token) => evidence.RecoverReservationAsync(tx, actor, Target(draftId), EvidencePurpose.SupplierDocumentPdf, key, token), ct);
    }
    public async Task<SafeStatus> PdfStatusAsync(ActorContext actor, long draftId, long reservationId, CancellationToken ct = default) {
        await StorageAsync(ct); await PdfEvidenceAdmissionSchemas.RequireAsync(database, ct);
        return await unit.ExecuteAsync((tx, token) => evidence.GetStatusAsync(tx, actor, reservationId,
            new("evidence-status", "supplier-document-pdf-evidence-status"), token, Target(draftId), EvidencePurpose.SupplierDocumentPdf), ct);
    }
    public async Task<ImmutableArray<SafeStatus>> ListPdfsAsync(ActorContext actor, long draftId, long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw B.Changed(); await StorageAsync(ct); await PdfEvidenceAdmissionSchemas.RequireAsync(database, ct);
        var rows = await unit.ExecuteAsync((tx, token) => ((ISupplierDocumentEvidenceUploadTarget)documents).WithReadTargetAsync(tx, actor,
            Target(draftId), null, null, async (target, inner) => {
                var filter = new BsonDocument { { "target.kind", "SupplierDocument" }, { "target.id", draftId }, { "purpose", "SupplierDocumentPdf" } };
                if (target.SupplierDocumentReservationId is { } reserved) {
                    if (beforeId.HasValue && reserved >= beforeId.Value) return new List<BsonDocument>();
                    filter.Add("_id", reserved);
                } else if (beforeId.HasValue) filter.Add("_id", new BsonDocument("$lt", beforeId.Value));
                return await database.GetCollection<BsonDocument>("evidenceUploadReservations").Find(tx, filter)
                    .Sort(new BsonDocument("_id", -1)).Limit(25).Project(new BsonDocument("_id", 1)).ToListAsync(inner);
            }, token), ct);
        var result = ImmutableArray.CreateBuilder<SafeStatus>();
        foreach (var row in rows) result.Add(await PdfStatusAsync(actor, draftId, row["_id"].AsInt64, ct));
        return result.ToImmutable();
    }
    public async Task<UploadOutcome> UploadPdfAsync(ActorContext actor, long draftId, long reservationId, long expectedRevision,
        Stream source, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        _ = await PdfStatusAsync(actor, draftId, reservationId, ct);
        return await evidence.UploadAsync(actor, reservationId, expectedRevision, source, metadata, ct);
    }
    public async Task<UploadOutcome> RecoverPdfUploadAsync(ActorContext actor, long draftId, long reservationId,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        _ = await PdfStatusAsync(actor, draftId, reservationId, ct);
        return await evidence.RecoverAsync(actor, reservationId, metadata, ct);
    }
    public async Task<ProtectedEvidenceRead> OpenPdfReadAsync(ActorContext actor, long draftId, long reservationId, long expectedRevision,
        EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) {
        await StorageAsync(ct); await PdfEvidenceAdmissionSchemas.RequireAsync(database, ct);
        await unit.ExecuteAsync((tx, token) => evidence.RequireFormatBindingAsync(tx, actor, Target(draftId), reservationId, EvidencePurpose.SupplierDocumentPdf, token), ct);
        return await evidence.OpenSupplierDocumentReadAsync(actor, Target(draftId), expectedRevision, reservationId, reference, metadata, ct);
    }
}
