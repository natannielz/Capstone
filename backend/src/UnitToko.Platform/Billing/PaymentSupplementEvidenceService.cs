using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Storage;
using B = UnitToko.Platform.Billing.PaymentSupplementBson;

namespace UnitToko.Platform.Billing;

public sealed record PaymentSupplementOperationRecovery(string Status, BsonDocument? Result);
public sealed partial class PaymentSupplementEvidenceService
{
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly IdAllocator ids;
    private readonly IClock clock;
    private readonly EvidenceStoreRegistration registration;
    private readonly PaymentSupplementEvidenceHistory history;
    public PaymentSupplementService Domain { get; }
    public PaymentSupplementEvidenceService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        PaymentDraftService declarations, PaymentEvidenceStorageOptions? storage = null) : this(database, unit, ids, clock, declarations,
            storage is null ? EvidenceStoreRegistration.Unavailable() : EvidenceStoreRegistration.OpenLocal(storage.Root, storage.StoreId, storage.TrustedOperatorSids)) { }
    internal PaymentSupplementEvidenceService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock,
        PaymentDraftService declarations, EvidenceStoreRegistration registration)
    {
        var source = ((IPaymentEvidenceUploadTarget)declarations).Database;
        if (!ReferenceEquals(database.Client, source.Client) || !database.DatabaseNamespace.Equals(source.DatabaseNamespace))
            throw new ArgumentException("Supplement and primary payment evidence must join the same database and owning transaction.");
        this.database = database; this.unit = unit; this.ids = ids; this.clock = clock; this.registration = registration;
        var historical = new EvidenceService(database, unit, ids, clock);
        history = new(database, historical); Domain = new(database, unit, ids, clock, declarations, history);
    }
    private static EvidenceTargetRef Target(long id) => new(EvidenceTargetKind.PaymentSupplement, id);
    private EvidenceService For(long declarationId, long supplementId, long ownRevision, bool review = false, bool metadataOnly = false,
        PaymentSupplementAssociation? pending = null)
    {
        PaymentSupplementRules.Id(declarationId); PaymentSupplementRules.Id(supplementId);
        if (ownRevision is not (1 or 2)) throw PaymentDraftRecords.Changed();
        return new(database, unit, ids, clock, registration,
            paymentSupplementTargets: new PaymentSupplementEvidenceTarget(database, Domain, declarationId, supplementId, ownRevision, review, metadataOnly, pending));
    }
    private Task StorageAsync(CancellationToken ct) => PaymentSupplementEvidenceSchemas.RequireAsync(database, ct);
    private static void Draft(long ownRevision) { if (ownRevision != 1) throw PaymentDraftRecords.Changed(); }
    public async Task<ReservationResult> ReservePdfAsync(ActorContext actor, long declarationId, long supplementId, long ownRevision,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        Draft(ownRevision); await StorageAsync(ct); var evidence = For(declarationId, supplementId, ownRevision);
        return await unit.ExecuteAsync((tx, token) => evidence.ReservePdfAsync(tx, actor,
            new(Target(supplementId), EvidencePurpose.PaymentSupplementPdf, ownRevision), metadata, token), ct);
    }
    public async Task<ReservationResult?> RecoverPdfReservationAsync(ActorContext actor, long declarationId, long supplementId, long ownRevision,
        string key, CancellationToken ct = default)
    {
        Draft(ownRevision); await StorageAsync(ct); var evidence = For(declarationId, supplementId, ownRevision, metadataOnly: true);
        return await unit.ExecuteAsync((tx, token) => evidence.RecoverReservationAsync(tx, actor, Target(supplementId), EvidencePurpose.PaymentSupplementPdf, key, token), ct);
    }
    public async Task<SafeStatus> PdfStatusAsync(ActorContext actor, long declarationId, long supplementId, long ownRevision,
        long reservationId, CancellationToken ct = default)
    {
        Draft(ownRevision); PaymentSupplementRules.Id(reservationId); await StorageAsync(ct);
        var evidence = For(declarationId, supplementId, ownRevision, metadataOnly: true);
        return await unit.ExecuteAsync((tx, token) => evidence.GetStatusAsync(tx, actor, reservationId,
            new("evidence-status", "payment-supplement-pdf-status"), token, Target(supplementId), EvidencePurpose.PaymentSupplementPdf), ct);
    }
    public async Task<ImmutableArray<SafeStatus>> ListPdfsAsync(ActorContext actor, long declarationId, long supplementId, long ownRevision,
        long? beforeId = null, CancellationToken ct = default)
    {
        Draft(ownRevision); if (beforeId is { } before) PaymentSupplementRules.Id(before); await StorageAsync(ct);
        var adapter = new PaymentSupplementEvidenceTarget(database, Domain, declarationId, supplementId, ownRevision, metadataOnly: true);
        var selected = await unit.ExecuteAsync((tx, token) => adapter.WithReadTargetAsync(tx, actor, Target(supplementId), ownRevision, null, async (authority, inner) => {
            var filter = new BsonDocument { { "target.kind", "PaymentSupplement" }, { "target.id", supplementId },
                { "purpose", "PaymentSupplementPdf" }, { "uploader.userId", actor.UserId } };
            if (beforeId is { } cursor) filter.Add("_id", new BsonDocument("$lt", cursor));
            return await database.GetCollection<BsonDocument>("evidenceUploadReservations").Find(tx, filter)
                .Sort(new BsonDocument("_id", -1)).Limit(25).Project(new BsonDocument("_id", 1)).ToListAsync(inner);
        }, token), ct);
        var result = ImmutableArray.CreateBuilder<SafeStatus>();
        foreach (var row in selected) result.Add(await PdfStatusAsync(actor, declarationId, supplementId, ownRevision, row["_id"].AsInt64, ct));
        return result.ToImmutable();
    }
    public async Task<UploadOutcome> UploadPdfAsync(ActorContext actor, long declarationId, long supplementId, long reservationId,
        long reservationRevision, Stream source, AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        _ = await PdfStatusAsync(actor, declarationId, supplementId, 1, reservationId, ct);
        return await For(declarationId, supplementId, 1).UploadAsync(actor, reservationId, reservationRevision, source, metadata, ct);
    }
    public async Task<UploadOutcome> RecoverPdfUploadAsync(ActorContext actor, long declarationId, long supplementId, long reservationId,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        _ = await PdfStatusAsync(actor, declarationId, supplementId, 1, reservationId, ct);
        return await For(declarationId, supplementId, 1).RecoverAsync(actor, reservationId, metadata, ct);
    }
    public async Task<BsonDocument> AttachAsync(ActorContext actor, long declarationId, long supplementId, long ownRevision, long reservationId,
        EvidenceRef reference, AuthorizationCommandMetadata metadata, CancellationToken ct = default)
    {
        Draft(ownRevision); var input = new PaymentSupplementAttachInput(supplementId, ownRevision, reservationId, reference).Validate();
        await StorageAsync(ct);
        var primary = await unit.ExecuteAsync((tx, token) => Domain.EvidencePrimaryAsync(tx, actor, declarationId, supplementId, false, token), ct);
        var recovered = await Domain.LookupAsync(actor, declarationId, primary.DeclarationRevision, B.AttachOperation, metadata.IdempotencyKey, supplementId, ct);
        if (recovered is not null) {
            if (recovered.BodyHash != AuthorizationStore.CommandHash(B.AttachBody(input), metadata.Reason))
                throw new PersistenceConflictException("idempotency_key_reused", "Kunci lampiran telah dipakai untuk permintaan lain.");
            return recovered.Result;
        }
        var evidence = For(declarationId, supplementId, ownRevision);
        return await evidence.WithPaymentSupplementPinAsync(actor, Target(supplementId), ownRevision, reservationId, reference,
            new("payment-supplement-pdf-attach", metadata.CorrelationId), async (claim, handle, started, token) => {
                var purpose = await unit.ExecuteAsync((tx, inner) => evidence.PaymentSupplementPurposeAsync(tx, supplementId, reservationId, reference, actor.UserId, inner), token);
                var verified = VerifiedPaymentSupplementOriginal.FromVerifiedClaim(actor, purpose, claim, handle);
                return await Domain.AttachAsync(actor, input, verified,
                    (tx, association, inner) => For(declarationId, supplementId, 1, pending: association)
                        .PinPaymentSupplementAsync(tx, actor, association, claim, handle, started, inner), metadata, token);
            }, ct);
    }
    public Task<ProtectedEvidenceRead> OpenPdfReadAsync(ActorContext actor, long declarationId, long supplementId, long reservationId, long ownRevision,
        EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) =>
        For(declarationId, supplementId, ownRevision).OpenPaymentSupplementReadAsync(actor, Target(supplementId), ownRevision, reservationId, reference, metadata, false, ct);
    public Task<ProtectedEvidenceRead> OpenReviewPdfReadAsync(ActorContext actor, long declarationId, long supplementId, long reservationId, long ownRevision,
        EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) =>
        For(declarationId, supplementId, ownRevision, review: true).OpenPaymentSupplementReadAsync(actor, Target(supplementId), ownRevision, reservationId, reference, metadata, true, ct);
    public async Task<PaymentSupplementOperationRecovery> RecoverCreateAsync(ActorContext actor, long declarationId, long parentRevision, string key,
        CancellationToken ct = default)
    {
        var recovered = await Domain.LookupAsync(actor, declarationId, parentRevision, B.CreateOperation, key, null, ct);
        return new(recovered is null ? "not_confirmed" : "committed", recovered?.Result);
    }
    public async Task<PaymentSupplementOperationRecovery> RecoverAttachAsync(ActorContext actor, long declarationId, long supplementId, string key,
        CancellationToken ct = default)
    {
        var primary = await unit.ExecuteAsync((tx, token) => Domain.EvidencePrimaryAsync(tx, actor, declarationId, supplementId, false, token), ct);
        var recovered = await Domain.LookupAsync(actor, declarationId, primary.DeclarationRevision, B.AttachOperation, key, supplementId, ct);
        return new(recovered is null ? "not_confirmed" : "committed", recovered?.Result);
    }
}
