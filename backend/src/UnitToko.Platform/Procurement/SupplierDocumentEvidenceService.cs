using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Procurement.SupplierDocumentBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierDocumentEvidenceService
{
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly SupplierDocumentService documents;
    private readonly EvidenceService evidence;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly EvidenceStoreRegistration registration;
    public SupplierDocumentEvidenceService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, SupplierDocumentService documents,
        PaymentEvidenceStorageOptions? storage = null) : this(database, unit, ids, clock, documents, storage is null ? EvidenceStoreRegistration.Unavailable() : EvidenceStoreRegistration.OpenLocal(storage.Root, storage.StoreId, storage.TrustedOperatorSids)) { }
    internal SupplierDocumentEvidenceService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, SupplierDocumentService documents, EvidenceStoreRegistration registration) {
        this.database = database; this.unit = unit; this.documents = documents; this.ids = ids; this.clock = clock; this.registration = registration;
        evidence = new(database, unit, ids, clock, registration, supplierDocumentTargets: documents);
    }
    private static EvidenceTargetRef Target(long id) => new(EvidenceTargetKind.SupplierDocument, id);
    private async Task StorageAsync(CancellationToken ct) { await documents.StorageAsync(ct); await SupplierDocumentEvidenceSchemas.RequireStorageAsync(database, ct); }
    public async Task<ReservationResult> ReserveAsync(ActorContext actor, long draftId, long expectedRevision, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (expectedRevision != 1) throw B.Changed(); await StorageAsync(ct);
        return await unit.ExecuteAsync((tx, token) => evidence.ReserveImageAsync(tx, actor, new(Target(draftId), EvidencePurpose.SupplierDocumentImage, expectedRevision), metadata, token), ct);
    }
    public async Task<ReservationResult?> RecoverReservationAsync(ActorContext actor, long draftId, string key, CancellationToken ct = default) {
        await StorageAsync(ct); return await unit.ExecuteAsync((tx, token) => evidence.RecoverReservationAsync(tx, actor, Target(draftId), key, token), ct);
    }
    public async Task<SafeStatus> StatusAsync(ActorContext actor, long draftId, long reservationId, CancellationToken ct = default) {
        await StorageAsync(ct);
        // The shared reader validates the requested draft and exact submitted reservation under one owner fence.
        return await unit.ExecuteAsync((tx, token) => evidence.GetStatusAsync(tx, actor, reservationId,
            new("evidence-status", "supplier-document-evidence-status"), token, Target(draftId), EvidencePurpose.SupplierDocumentImage), ct);
    }
    public async Task<ImmutableArray<SafeStatus>> ListAsync(ActorContext actor, long draftId, long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw B.Changed(); await StorageAsync(ct);
        var rows = await unit.ExecuteAsync((tx, token) => ((ISupplierDocumentEvidenceUploadTarget)documents).WithReadTargetAsync(tx, actor, Target(draftId), null, null, async (target, inner) => {
            var filter = new BsonDocument { { "target.kind", "SupplierDocument" }, { "target.id", draftId }, { "purpose", "SupplierDocumentImage" } };
            if (target.SupplierDocumentReservationId is { } reserved) {
                if (beforeId.HasValue && reserved >= beforeId.Value) return new List<BsonDocument>();
                filter.Add("_id", reserved);
            }
            else if (beforeId.HasValue) filter.Add("_id", new BsonDocument("$lt", beforeId.Value));
            return await database.GetCollection<BsonDocument>("evidenceUploadReservations").Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(25).Project(new BsonDocument("_id", 1)).ToListAsync(inner);
        }, token), ct);
        var statuses = ImmutableArray.CreateBuilder<SafeStatus>(); foreach (var row in rows) statuses.Add(await StatusAsync(actor, draftId, row["_id"].AsInt64, ct)); return statuses.ToImmutable();
    }
    public async Task<UploadOutcome> UploadAsync(ActorContext actor, long draftId, long reservationId, long expectedRevision, Stream source, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        _ = await StatusAsync(actor, draftId, reservationId, ct); return await evidence.UploadAsync(actor, reservationId, expectedRevision, source, metadata, ct);
    }
    public async Task<UploadOutcome> RecoverUploadAsync(ActorContext actor, long draftId, long reservationId, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        _ = await StatusAsync(actor, draftId, reservationId, ct); return await evidence.RecoverAsync(actor, reservationId, metadata, ct);
    }
    public async Task<ProtectedEvidenceRead> OpenReadAsync(ActorContext actor, long draftId, long reservationId, long expectedRevision, EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) {
        await StorageAsync(ct);
        await unit.ExecuteAsync((tx, token) => evidence.RequireFormatBindingAsync(tx, actor, Target(draftId), reservationId, EvidencePurpose.SupplierDocumentImage, token), ct);
        return await evidence.OpenSupplierDocumentReadAsync(actor, Target(draftId), expectedRevision, reservationId, reference, metadata, ct);
    }
    public async Task<SupplierDocumentExtractionView> ReadExtractionAsync(ActorContext actor, long draftId, long expectedRevision, EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) {
        await StorageAsync(ct); return await unit.ExecuteAsync((tx, token) => evidence.ReadSupplierDocumentExtractionAsync(tx, actor, Target(draftId), expectedRevision, reference,
            metadata, new SupplierDocumentOcrSource(documents), token), ct);
    }
    public async Task<BsonDocument> SubmitAsync(ActorContext actor, long draftId, long expectedRevision, long reservationId, EvidenceRef reference, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(metadata);
        var hash = AuthorizationStore.CommandHash(B.SubmitBody(draftId, expectedRevision, reservationId, reference, binding), metadata.Reason);
        var recovered = await documents.RecoverSubmitBodyAsync(actor, draftId, metadata.IdempotencyKey, hash, ct); if (recovered is not null) return recovered;
        await StorageAsync(ct);
        return await evidence.WithSupplierDocumentPinAsync(actor, Target(draftId), expectedRevision, reservationId, reference, new("supplier-document-evidence-view", metadata.CorrelationId),
            async (claim, handle, started, token) => {
                var actualOriginal = await unit.ExecuteAsync((tx, inner) => evidence.SubmissionOriginalAsync(tx, Target(draftId), expectedRevision,
                    reservationId, reference, actor.UserId, inner), token);
                VerifiedPdfSubmissionOriginal? pdfOriginal = null;
                if (actualOriginal is not null) {
                    if (actualOriginal != claim.Original) throw B.Broken();
                    pdfOriginal = VerifiedPdfSubmissionOriginal.FromVerifiedClaim(actor, EvidencePurpose.SupplierDocumentPdf, claim, handle);
                }
                return await documents.SubmitWithProofAsync(actor, draftId, expectedRevision, reservationId, reference, binding, metadata,
                    pdfOriginal, (tx, id, target, inner) => new EvidenceService(database, unit, ids, clock, registration, supplierDocumentTargets: target)
                        .PinSupplierDocumentAsync(tx, actor, id, claim, handle, started, inner), token);
            }, ct);
    }
}
