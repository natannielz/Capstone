using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Procurement.ProcurementDisbursementBson;

namespace UnitToko.Platform.Procurement;

public sealed class ProcurementDisbursementEvidenceService
{
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly ProcurementDisbursementService payouts;
    private readonly EvidenceService evidence;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly EvidenceStoreRegistration registration;
    public ProcurementDisbursementEvidenceService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, ProcurementDisbursementService payouts,
        PaymentEvidenceStorageOptions? storage = null) : this(database, unit, ids, clock, payouts, storage is null ? EvidenceStoreRegistration.Unavailable() : EvidenceStoreRegistration.OpenLocal(storage.Root, storage.StoreId, storage.TrustedOperatorSids)) { }
    internal ProcurementDisbursementEvidenceService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, ProcurementDisbursementService payouts, EvidenceStoreRegistration registration) {
        this.database = database; this.unit = unit; this.payouts = payouts; this.ids = ids; this.clock = clock; this.registration = registration;
        evidence = new(database, unit, ids, clock, registration, procurementDisbursementTargets: payouts);
    }
    private static EvidenceTargetRef Target(long id) => new(EvidenceTargetKind.ProcurementDisbursement, id);
    private async Task StorageAsync(CancellationToken ct) { await payouts.StorageAsync(ct); await ProcurementDisbursementEvidenceSchemas.RequireStorageAsync(database, ct); }
    public async Task<ReservationResult> ReserveAsync(ActorContext actor, long draftId, long expectedRevision, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (expectedRevision != 1) throw B.Changed(); await StorageAsync(ct);
        return await unit.ExecuteAsync((tx, token) => evidence.ReserveImageAsync(tx, actor, new(Target(draftId), EvidencePurpose.ProcurementDisbursementImage, expectedRevision), metadata, token), ct);
    }
    public async Task<ReservationResult?> RecoverReservationAsync(ActorContext actor, long draftId, string key, CancellationToken ct = default) {
        await StorageAsync(ct); return await unit.ExecuteAsync((tx, token) => evidence.RecoverReservationAsync(tx, actor, Target(draftId), key, token), ct);
    }
    public async Task<SafeStatus> StatusAsync(ActorContext actor, long draftId, long reservationId, CancellationToken ct = default) {
        await StorageAsync(ct); return await unit.ExecuteAsync(async (tx, token) => {
            await evidence.RequireBindingAsync(tx, actor, Target(draftId), reservationId, token);
            return await evidence.GetStatusAsync(tx, actor, reservationId, new("evidence-status", "procurement-disbursement-evidence-status"), token);
        }, ct);
    }
    public async Task<ImmutableArray<SafeStatus>> ListAsync(ActorContext actor, long draftId, long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw B.Changed(); await StorageAsync(ct);
        var rows = await unit.ExecuteAsync((tx, token) => ((IProcurementDisbursementEvidenceUploadTarget)payouts).WithReadTargetAsync(tx, actor, Target(draftId), null, null, async (target, inner) => {
            var filter = new BsonDocument { { "target.kind", "ProcurementDisbursement" }, { "target.id", draftId } };
            if (target.DisbursementReservationId is { } reserved) filter.Add("_id", reserved);
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
        await StorageAsync(ct); return await evidence.OpenProcurementDisbursementReadAsync(actor, Target(draftId), expectedRevision, reservationId, reference, metadata, ct);
    }
    public async Task<BsonDocument> RecordAsync(ActorContext actor, long draftId, long expectedRevision, long reservationId, EvidenceRef reference, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(metadata);
        var hash = AuthorizationStore.CommandHash(B.RecordBody(draftId, expectedRevision, reservationId, reference, binding), metadata.Reason);
        var recovered = await payouts.RecoverRecordBodyAsync(actor, draftId, metadata.IdempotencyKey, hash, ct); if (recovered is not null) return recovered;
        await StorageAsync(ct);
        return await evidence.WithProcurementDisbursementPinAsync(actor, Target(draftId), expectedRevision, reservationId, reference, new("procurement-disbursement-evidence-view", metadata.CorrelationId),
            (claim, handle, started, token) => payouts.RecordWithProofAsync(actor, draftId, expectedRevision, reservationId, reference, binding, metadata,
                (tx, id, target, inner) => new EvidenceService(database, unit, ids, clock, registration, procurementDisbursementTargets: target)
                    .PinProcurementDisbursementAsync(tx, actor, id, claim, handle, started, inner), token), ct);
    }
}
