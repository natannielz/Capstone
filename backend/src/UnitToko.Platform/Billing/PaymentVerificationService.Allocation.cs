using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Identity;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Platform.Billing;

internal sealed record PaymentAllocationSource(BsonDocument Decision, BankReceiptPaymentSource Bank, BsonDocument SuspenseAccount, Action RequireCurrent);

public sealed partial class PaymentVerificationService
{
    internal IMongoDatabase AllocationDatabase => database;
    internal Task AllocationStorageAsync(CancellationToken ct) => StorageAsync(ct);
    // Owning-session consumer of an accepted match. It cannot create a verification or cash.
    internal async Task<PaymentAllocationSource> AllocationSourceAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long verificationId, bool newEffect, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        var raw = await records.RawAsync(tx, verificationId, ct);
        var context = await SourceAsync(new(actor, authority, tx, false), raw["declarationId"].AsInt64, ct);
        var saved = await SavedAsync(context, raw, ct);
        var bank = await receipts.PaymentSourceAsync(actor, authority, tx, saved["receiptId"].AsInt64, ct);
        PersonAuthorityProof? approval = null; PersonAuthorityProof? bankApproval = null;
        if (newEffect) {
            var maker = saved["source"]["recordedBy"].AsInt64; var checker = saved["actorId"].AsInt64;
            approval = await new PersonRegistryService(database, clock).ResolveProofsAsync(tx, [.. new[] { maker, checker, actor.UserId }.Distinct()], ct);
            var makerProof = approval.Evidence.Single(person => person.UserId == maker);
            if (!PersonIndependencePolicy.AreIndependent(approval.Evidence.Single(person => person.UserId == checker), makerProof) ||
                !PersonIndependencePolicy.AreIndependent(approval.Evidence.Single(person => person.UserId == actor.UserId), makerProof))
                throw new AuthorizationDeniedException("independent_person_required");
            await new PersonAuthorizationFence(database, clock).TouchAsync(tx, approval, ct);
            bankApproval = await receipts.RequireAllocationApprovalAsync(actor, authority, tx, bank.Receipt, ct);
        }
        await FenceAsync(context, ct);
        void Current() { _ = Require(context); approval?.RequireCurrent(tx, clock.UtcNow); bankApproval?.RequireCurrent(tx, clock.UtcNow); }
        var suspense = await receipts.AllocationSuspenseAsync(tx, bank.Receipt, ct);
        Current(); return new(saved, bank, suspense, Current);
    }
}
