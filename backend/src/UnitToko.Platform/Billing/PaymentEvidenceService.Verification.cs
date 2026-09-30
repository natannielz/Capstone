using System.Diagnostics;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Storage;

namespace UnitToko.Platform.Billing;

internal delegate Task<T> PaymentVerificationConsumer<T>(IClientSessionHandle tx,
    Func<PaymentExtractionView, Action, CancellationToken, Task<T>> consume, CancellationToken ct);

public sealed partial class PaymentEvidenceService
{
    internal IMongoDatabase VerificationDatabase => database;
    internal Task<PaymentExtractionView> AcceptedVerificationExtractionAsync(IClientSessionHandle tx, ActorContext actor, long id,
        PaymentCorrectionBinding binding, CancellationToken ct) => reviewEvidence.AcceptedPaymentExtractionAsync(tx, actor, Target(id),
            binding.DeclarationRevision, binding.Evidence, binding.ExtractionId, ct);
    // The exact original is opened and hash-verified outside the financial transaction.
    // Its held handle, pin, current validation/read policies and lease are fenced inside it.
    internal async Task<T> WithVerificationOriginalAsync<T>(ActorContext actor, long id, long revision, long reservationId, EvidenceRef reference,
        Func<PaymentVerificationConsumer<T>, CancellationToken, Task<T>> action, CancellationToken ct) {
        await StorageAsync(ct); await DocumentProcessingStorage.RequireAsync(database, ct); await declarations.CorrectionStorageAsync(ct);
        if (registration.Store is null) throw Unavailable();
        var started = Stopwatch.GetTimestamp();
        var claim = await unit.ExecuteAsync((tx, token) => reviewEvidence.AcquirePaymentReadAsync(tx, actor, Target(id), revision,
            reservationId, reference, new("payment-evidence-view", "payment-verification-original"), token), ct);
        VerifiedOriginalHandle? handle = null;
        try {
            var opened = await registration.Store.OpenVerifiedAsync(claim.Original, claim.VerificationBudget, ct);
            handle = opened is OpenOutcome.Opened success ? success.Handle : throw new PersistenceConflictException("evidence_read_integrity_unavailable", "The exact original is unavailable for verification");
            if (ReadObserver is { } observer) await observer.Verified();
            return await action(async (tx, consume, token) => {
                var window = await reviewEvidence.AuthorizePaymentReadAsync(tx, actor, claim, handle, false, token);
                return await reviewEvidence.WithPaymentExtractionAsync(tx, actor, Target(id), revision, reference,
                    new("payment-ocr-view", "payment-verification-original"), new PaymentOcrSource(database), async (view, eligible, inner) => {
                        void Check() {
                            eligible(); inner.ThrowIfCancellationRequested();
                            if (!handle.Stream.CanRead || clock.UtcNow >= window.Until || clock.UtcNow >= window.Deadline ||
                                Stopwatch.GetElapsedTime(started) >= claim.RequestDuration) throw new PersistenceConflictException("evidence_read_lease_expired", "The original review window expired");
                        }
                        Check(); var result = await consume(view, Check, inner); Check(); return result;
                    }, token);
            }, ct);
        } finally {
            if (handle is not null) await handle.DisposeAsync();
            try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await unit.ExecuteAsync(async (tx, token) => { await reviewEvidence.ReleaseReadAsync(tx, claim, token); return true; }, cleanup.Token);
            } catch { /* A failed release leaves a bounded lease; it never deletes the original or repeats a decision. */ }
        }
    }
}
