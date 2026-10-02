using System.Collections.Immutable;
using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Billing;

public sealed record PaymentEvidenceStorageOptions(string Root, string StoreId, IReadOnlyCollection<string> TrustedOperatorSids);

// Payment-owned admission and current-authority original reads. Neither creates a financial effect.
public sealed partial class PaymentEvidenceService
{
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly PaymentDraftService declarations;
    private readonly EvidenceService evidence;
    private readonly EvidenceService reviewEvidence;
    private readonly IClock clock;
    private readonly EvidenceStoreRegistration registration;
    internal EvidenceReadObserver? ReadObserver { get; set; }
    public PaymentEvidenceService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, PaymentDraftService declarations,
        PaymentEvidenceStorageOptions? storage = null) : this(database, unit, ids, clock, declarations, storage is null ? EvidenceStoreRegistration.Unavailable() :
            EvidenceStoreRegistration.OpenLocal(storage.Root, storage.StoreId, storage.TrustedOperatorSids)) { }
    internal PaymentEvidenceService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, PaymentDraftService declarations, EvidenceStoreRegistration registration) {
        this.database = database; this.unit = unit; this.declarations = declarations; this.clock = clock; this.registration = registration;
        evidence = new(database, unit, ids, clock, registration, declarations);
        reviewEvidence = new(database, unit, ids, clock, registration, new PaymentReviewTarget(database, declarations));
    }
    private static EvidenceTargetRef Target(long id) => new(EvidenceTargetKind.PaymentDeclaration, id);
    private async Task StorageAsync(CancellationToken ct) {
        await declarations.PaymentEvidenceStorageAsync(ct);
        foreach (var name in EvidenceSchemas.Collections) {
            var definition = await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, ct)).SingleOrDefaultAsync(ct);
            var options = definition?.GetValue("options", new BsonDocument()).AsBsonDocument;
            var validator = PaymentEvidenceSchemas.Collections.Contains(name) ? PaymentEvidenceSchemas.Validator(name) : EvidenceSchemas.Validator(name);
            if (definition is null || definition.GetValue("type", "") != "collection" || options is null ||
                options.GetValue("validationLevel", "") != "strict" || options.GetValue("validationAction", "") != "error" ||
                options.GetValue("capped", false).AsBoolean || options.Contains("timeseries") || options.Contains("expireAfterSeconds") || options.Contains("encryptedFields") ||
                options.TryGetValue("collation", out var collation) && !InvoiceStorage.Simple(collation) ||
                !RefundPayoutEvidenceSchemas.Supports(name, options.GetValue("validator", new BsonDocument()).AsBsonDocument, validator)) throw Unavailable();
            try { PaymentEvidenceMigration.RequireIndexes(name, await (await database.GetCollection<BsonDocument>(name).Indexes.ListAsync(ct)).ToListAsync(ct)); }
            catch (InvalidOperationException) { throw Unavailable(); }
        }
    }
    private static PersistenceConflictException Unavailable() => new("payment_evidence_storage_unavailable", "Payment evidence storage is not ready");
    public async Task<ReservationResult> ReserveAsync(ActorContext actor, long declarationId, long expectedRevision, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        if (expectedRevision <= 0) throw PaymentDraftRecords.Changed(); await StorageAsync(ct);
        return await unit.ExecuteAsync((tx, token) => evidence.ReserveImageAsync(tx, actor, new(Target(declarationId), EvidencePurpose.PaymentImage, expectedRevision), metadata, token), ct);
    }
    public async Task<ReservationResult?> RecoverReservationAsync(ActorContext actor, long declarationId, string key, CancellationToken ct = default) {
        await StorageAsync(ct); return await unit.ExecuteAsync((tx, token) => evidence.RecoverReservationAsync(tx, actor, Target(declarationId), key, token), ct);
    }
    public async Task<SafeStatus> StatusAsync(ActorContext actor, long declarationId, long reservationId, CancellationToken ct = default) {
        await StorageAsync(ct);
        return await unit.ExecuteAsync(async (tx, token) => {
            await evidence.RequireBindingAsync(tx, actor, Target(declarationId), reservationId, token);
            return await evidence.GetStatusAsync(tx, actor, reservationId, new("evidence-status", "payment-evidence-status"), token);
        }, ct);
    }
    public async Task<ImmutableArray<SafeStatus>> ListAsync(ActorContext actor, long declarationId, long? beforeId = null, CancellationToken ct = default) {
        if (beforeId is <= 0) throw PaymentDraftBson.Invalid(); await StorageAsync(ct);
        var ids = await unit.ExecuteAsync((tx, token) => ((IPaymentEvidenceUploadTarget)declarations).WithReadTargetAsync(tx, actor, Target(declarationId), null, null, async (target, inner) => {
            var filter = new BsonDocument { { "target.kind", "PaymentDeclaration" }, { "target.id", declarationId }, { "purpose", "PaymentImage" }, { "uploader.userId", actor.UserId } };
            if (target.SubmittedReservationId.HasValue) {
                filter.Add("_id", new BsonDocument("$eq", target.SubmittedReservationId.Value));
                if (beforeId.HasValue) filter["_id"].AsBsonDocument.Add("$lt", beforeId.Value);
            } else if (beforeId.HasValue) filter.Add("_id", new BsonDocument("$lt", beforeId.Value));
            return await database.GetCollection<BsonDocument>("evidenceUploadReservations").Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(25).Project(new BsonDocument("_id", 1)).ToListAsync(inner);
        }, token), ct);
        var result = ImmutableArray.CreateBuilder<SafeStatus>();
        foreach (var id in ids) result.Add(await StatusAsync(actor, declarationId, id["_id"].AsInt64, ct));
        return result.ToImmutable();
    }
    public async Task<UploadOutcome> UploadAsync(ActorContext actor, long declarationId, long reservationId, long expectedRevision, Stream source, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        // Checks the URL's exact owning declaration before any source bytes are consumed. The
        // evidence owner reauthorizes/fences again at each durable transition and after slow I/O.
        _ = await StatusAsync(actor, declarationId, reservationId, ct);
        return await evidence.UploadAsync(actor, reservationId, expectedRevision, source, metadata, ct);
    }
    public async Task<UploadOutcome> RecoverUploadAsync(ActorContext actor, long declarationId, long reservationId, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        _ = await StatusAsync(actor, declarationId, reservationId, ct);
        return await evidence.RecoverAsync(actor, reservationId, metadata, ct);
    }
    public async Task<ProtectedEvidenceRead> OpenReadAsync(ActorContext actor, long declarationId, long reservationId, long expectedRevision,
        EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) =>
        await OpenBoundReadAsync(evidence, actor, declarationId, reservationId, expectedRevision, reference, metadata, ct);
    public async Task<ProtectedEvidenceRead> OpenReviewReadAsync(ActorContext actor, long declarationId, long reservationId, long expectedRevision,
        EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct = default) =>
        await OpenBoundReadAsync(reviewEvidence, actor, declarationId, reservationId, expectedRevision, reference, metadata, ct);
    private async Task<ProtectedEvidenceRead> OpenBoundReadAsync(EvidenceService reader, ActorContext actor, long declarationId, long reservationId, long expectedRevision,
        EvidenceRef reference, AuthorizationReadMetadata metadata, CancellationToken ct) {
        await StorageAsync(ct);
        foreach (var name in EvidenceAccessSchemas.Collections)
            await RefundPayoutEvidenceSchemas.RequireCompatibleStorageAsync(database, name, EvidenceAccessSchemas.Validator(name), EvidenceAccessSchemas.Key(name), ct);
        if (registration.Store is null) throw Unavailable();
        var started = Stopwatch.GetTimestamp();
        var claim = await unit.ExecuteAsync((tx, token) => reader.AcquirePaymentReadAsync(tx, actor, Target(declarationId), expectedRevision, reservationId, reference, metadata, token), ct);
        VerifiedOriginalHandle? handle = null;
        async Task ReleaseAsync() {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await unit.ExecuteAsync(async (tx, token) => { await reader.ReleaseReadAsync(tx, claim, token); return true; }, cleanup.Token);
        }
        try {
            var opened = await registration.Store.OpenVerifiedAsync(claim.Original, claim.VerificationBudget, ct);
            handle = opened is OpenOutcome.Opened success ? success.Handle : throw new PersistenceConflictException("evidence_read_integrity_unavailable", "The exact original cannot be safely read");
            if (ReadObserver is { } observer) await observer.Verified();
            var window = await unit.ExecuteAsync((tx, token) => reader.AuthorizePaymentReadAsync(tx, actor, claim, handle, false, token), ct);
            var held = handle;
            var content = new EvidenceReadStream(held, clock, window, claim.RequestDuration - Stopwatch.GetElapsedTime(started),
                token => unit.ExecuteAsync((tx, inner) => reader.AuthorizePaymentReadAsync(tx, actor, claim, held, true, inner), token), ReleaseAsync, ct);
            handle = null; return new(content, claim.Mime, claim.Original.Content.Length, claim.Reference);
        } catch {
            if (handle is not null) await handle.DisposeAsync();
            // No bytes have been returned. A failed/unknown release leaves only a bounded lease;
            // cleanup cannot infer object deletion from this path or from request cancellation.
            try { await ReleaseAsync(); } catch { }
            throw;
        }
    }
}
