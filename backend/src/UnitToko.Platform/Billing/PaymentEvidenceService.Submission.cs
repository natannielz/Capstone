using System.Diagnostics;
using MongoDB.Bson;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Billing;

public sealed partial class PaymentEvidenceService
{
    public Task<BsonDocument?> RecoverSubmissionAsync(ActorContext actor, long declarationId, string key, CancellationToken ct = default) =>
        declarations.RecoverSubmissionAsync(actor, declarationId, key, null, ct);
    public async Task<BsonDocument> SubmitAsync(ActorContext actor, long declarationId, long expectedRevision, long reservationId, EvidenceRef reference,
        AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        AuthorizationStore.ValidateMetadata(metadata);
        if (declarationId <= 0 || expectedRevision <= 0 || reservationId <= 0 || reference is null || metadata.IdempotencyKey.Length > 128 ||
            metadata.IdempotencyKey.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or ':'))) throw PaymentDraftBson.Invalid();
        var bodyHash = AuthorizationStore.CommandHash(PaymentSubmissionBson.Body(declarationId, expectedRevision, reservationId, reference), metadata.Reason);
        var recovered = await declarations.RecoverSubmissionAsync(actor, declarationId, metadata.IdempotencyKey, bodyHash, ct);
        if (recovered is not null) return recovered;
        await StorageAsync(ct);
        foreach (var name in EvidenceAccessSchemas.Collections)
            await SupplierDocumentEvidenceSchemas.RequireCompatibleStorageAsync(database, name, EvidenceAccessSchemas.Validator(name), EvidenceAccessSchemas.Key(name), ct);
        if (registration.Store is null) throw Unavailable();
        var started = Stopwatch.GetTimestamp();
        var claim = await unit.ExecuteAsync((tx, token) => evidence.AcquirePaymentReadAsync(tx, actor, Target(declarationId), expectedRevision, reservationId, reference,
            new("payment-evidence-view", metadata.CorrelationId), token), ct);
        VerifiedOriginalHandle? handle = null;
        try {
            var opened = await registration.Store.OpenVerifiedAsync(claim.Original, claim.VerificationBudget, ct);
            handle = opened is OpenOutcome.Opened success ? success.Handle : throw new PersistenceConflictException("evidence_read_integrity_unavailable", "The exact original cannot be safely submitted");
            if (ReadObserver is { } observer) await observer.Verified();
            return await declarations.SubmitAsync(actor, declarationId, expectedRevision, reservationId, reference, metadata,
                (tx, id, token) => evidence.PinPaymentAsync(tx, actor, id, claim, handle, started, token), ct);
        } finally {
            if (handle is not null) await handle.DisposeAsync();
            // Release is best effort after file closure. A file-close/request failure can
            // still leave an unknown outcome; recover through the original operation receipt.
            try {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await unit.ExecuteAsync(async (tx, token) => { await evidence.ReleaseReadAsync(tx, claim, token); return true; }, cleanup.Token);
            } catch { /* A bounded lease remains; pins still prohibit reclamation. */ }
        }
    }
}
