using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;

namespace UnitToko.Platform.Billing;

public sealed record PaymentEvidenceStorageOptions(string Root, string StoreId, IReadOnlyCollection<string> TrustedOperatorSids);

// Admission only: every successful original remains quarantined, with no financial effect.
public sealed class PaymentEvidenceService
{
    private readonly IMongoDatabase database;
    private readonly MongoUnitOfWork unit;
    private readonly PaymentDraftService declarations;
    private readonly EvidenceService evidence;
    public PaymentEvidenceService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, PaymentDraftService declarations,
        PaymentEvidenceStorageOptions? storage = null) : this(database, unit, ids, clock, declarations, storage is null ? EvidenceStoreRegistration.Unavailable() :
            EvidenceStoreRegistration.OpenLocal(storage.Root, storage.StoreId, storage.TrustedOperatorSids)) { }
    internal PaymentEvidenceService(IMongoDatabase database, MongoUnitOfWork unit, IdAllocator ids, IClock clock, PaymentDraftService declarations, EvidenceStoreRegistration registration) {
        this.database = database; this.unit = unit; this.declarations = declarations; evidence = new(database, unit, ids, clock, registration, declarations);
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
                !InventorySchemas.Contains(options.GetValue("validator", new BsonDocument()).AsBsonDocument, validator)) throw Unavailable();
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
        var ids = await unit.ExecuteAsync((tx, token) => ((IPaymentEvidenceUploadTarget)declarations).WithTargetAsync(tx, actor, Target(declarationId), null, async (_, inner) => {
            var filter = new BsonDocument { { "target.kind", "PaymentDeclaration" }, { "target.id", declarationId }, { "purpose", "PaymentImage" }, { "uploader.userId", actor.UserId } };
            if (beforeId.HasValue) filter.Add("_id", new BsonDocument("$lt", beforeId.Value));
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
}
