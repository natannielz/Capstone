using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;

namespace UnitToko.Platform.Billing;

// PDF routes use an explicit durable purpose. They grant private original reads only after M83 proof;
// payment submission still refuses PDF until its page-aware extraction/review owner is connected.
public sealed partial class PaymentEvidenceService
{
    public async Task<ReservationResult> ReservePdfAsync(ActorContext actor, long declarationId, long expectedRevision,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (expectedRevision <= 0) throw PaymentDraftRecords.Changed(); await StorageAsync(ct);
        return await unit.ExecuteAsync((tx, token) => evidence.ReservePdfAsync(tx, actor,
            new(Target(declarationId), EvidencePurpose.PaymentPdf, expectedRevision), metadata, token), ct);
    }
    public async Task<ReservationResult?> RecoverPdfReservationAsync(ActorContext actor, long declarationId, string key, CancellationToken ct = default) {
        await StorageAsync(ct); await PdfEvidenceAdmissionSchemas.RequireAsync(database, ct);
        return await unit.ExecuteAsync((tx, token) => evidence.RecoverReservationAsync(tx, actor, Target(declarationId), EvidencePurpose.PaymentPdf, key, token), ct);
    }
    public async Task<SafeStatus> PdfStatusAsync(ActorContext actor, long declarationId, long reservationId, CancellationToken ct = default) {
        await StorageAsync(ct); await PdfEvidenceAdmissionSchemas.RequireAsync(database, ct);
        return await unit.ExecuteAsync(async (tx, token) => {
            await evidence.RequireBindingAsync(tx, actor, Target(declarationId), reservationId, token);
            return await evidence.GetStatusAsync(tx, actor, reservationId, new("evidence-status", "payment-pdf-evidence-status"),
                token, Target(declarationId), EvidencePurpose.PaymentPdf);
        }, ct);
    }
    public async Task<ImmutableArray<SafeStatus>> ListPdfsAsync(ActorContext actor, long declarationId, long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw PaymentDraftBson.Invalid(); await StorageAsync(ct); await PdfEvidenceAdmissionSchemas.RequireAsync(database, ct);
        var ids = await unit.ExecuteAsync((tx, token) => ((IPaymentEvidenceUploadTarget)declarations).WithReadTargetAsync(tx, actor,
            Target(declarationId), null, null, async (target, inner) => {
                var filter = new BsonDocument { { "target.kind", "PaymentDeclaration" }, { "target.id", declarationId },
                    { "purpose", "PaymentPdf" }, { "uploader.userId", actor.UserId } };
                if (target.SubmittedReservationId.HasValue) {
                    filter.Add("_id", new BsonDocument("$eq", target.SubmittedReservationId.Value));
                    if (beforeId.HasValue) filter["_id"].AsBsonDocument.Add("$lt", beforeId.Value);
                } else if (beforeId.HasValue) filter.Add("_id", new BsonDocument("$lt", beforeId.Value));
                return await database.GetCollection<BsonDocument>("evidenceUploadReservations").Find(tx, filter)
                    .Sort(new BsonDocument("_id", -1)).Limit(25).Project(new BsonDocument("_id", 1)).ToListAsync(inner);
            }, token), ct);
        var result = ImmutableArray.CreateBuilder<SafeStatus>();
        foreach (var row in ids) result.Add(await PdfStatusAsync(actor, declarationId, row["_id"].AsInt64, ct));
        return result.ToImmutable();
    }
    public async Task<UploadOutcome> UploadPdfAsync(ActorContext actor, long declarationId, long reservationId, long expectedRevision,
        Stream source, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        _ = await PdfStatusAsync(actor, declarationId, reservationId, ct);
        return await evidence.UploadAsync(actor, reservationId, expectedRevision, source, metadata, ct);
    }
    public async Task<UploadOutcome> RecoverPdfUploadAsync(ActorContext actor, long declarationId, long reservationId,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        _ = await PdfStatusAsync(actor, declarationId, reservationId, ct);
        return await evidence.RecoverAsync(actor, reservationId, metadata, ct);
    }
    public async Task<ProtectedEvidenceRead> OpenPdfReadAsync(ActorContext actor, long declarationId, long reservationId, long expectedRevision,
        EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) {
        await StorageAsync(ct); await PdfEvidenceAdmissionSchemas.RequireAsync(database, ct);
        await unit.ExecuteAsync((tx, token) => evidence.RequireFormatBindingAsync(tx, actor, Target(declarationId), reservationId, EvidencePurpose.PaymentPdf, token), ct);
        return await OpenBoundReadAsync(evidence, actor, declarationId, reservationId, expectedRevision, reference, metadata, ct);
    }
}
