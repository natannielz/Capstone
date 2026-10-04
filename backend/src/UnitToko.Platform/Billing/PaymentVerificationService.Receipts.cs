using MongoDB.Bson;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity.Contracts;

namespace UnitToko.Platform.Billing;

public sealed record PaymentVerificationReceiptPage(IReadOnlyList<BsonDocument> Items, long? NextAfterId);

public sealed partial class PaymentVerificationService
{
    public async Task<PaymentVerificationReceiptPage> ReceiptCandidatesAsync(ActorContext actor, long declarationId,
        long? afterId = null, CancellationToken ct = default) {
        if (declarationId <= 0 || afterId is <= 0) throw PaymentVerificationPolicy.Invalid();
        await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await SourceAsync(new(actor, authority, tx, true), declarationId, token);
            await records.RequireUnverifiedAsync(tx, declarationId, token);
            var proof = await PeopleAsync(context, token);
            var page = await receipts.PaymentCandidatesAsync(actor, authority, tx, context.Source!["party"].AsBsonDocument, afterId, token);
            await AuditReadAsync(context, "receipt-candidates", declarationId, token); await FenceAsync(context, token);
            proof.RequireCurrent(tx, clock.UtcNow); return new PaymentVerificationReceiptPage(page.Items, page.NextAfterId);
        }, ct);
    }
}
