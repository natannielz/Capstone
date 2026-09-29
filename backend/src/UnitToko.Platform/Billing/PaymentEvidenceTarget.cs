using MongoDB.Driver;
using UnitToko.Modules.Documents;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.Documents;

namespace UnitToko.Platform.Billing;
public sealed partial class PaymentDraftService : IPaymentEvidenceUploadTarget
{
    internal Task PaymentEvidenceStorageAsync(CancellationToken ct) => StorageAsync(ct);
    IMongoDatabase IPaymentEvidenceUploadTarget.Database => database;
    async Task<T> IPaymentEvidenceUploadTarget.WithTargetAsync<T>(IClientSessionHandle tx, ActorContext actor, EvidenceTargetRef target, long? expectedRevision,
        Func<EvidenceUploadTarget, CancellationToken, Task<T>> action, CancellationToken ct) {
        try {
            if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || target.Kind != EvidenceTargetKind.PaymentDeclaration) throw PaymentDraftBson.Invalid();
            var authority = await authorization.LoadAsync(actor, tx, ct);
            var head = await HeadAsync(actor, authority, tx, target.Id, ct);
            if (head["state"] != "draft" || expectedRevision.HasValue && head["revision"] != expectedRevision.Value) throw PaymentDraftRecords.Changed();
            var party = head["party"];
            var owner = new GrantScope(GrantScopeKind.FulfillmentShop, party["fulfillmentOrganizationId"].AsInt64, ShopId: party["shopId"].AsInt64, ChannelId: party["channelId"].AsInt64);
            var result = await action(new(target, owner, head["revision"].AsInt64, null, null), ct);
            await records.TouchAsync(tx, head, ct); await FenceAsync(actor, authority, tx, head["scopes"].AsBsonArray, ct);
            return result;
        } catch {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { if (tx.IsInTransaction) await tx.AbortTransactionAsync(cleanup.Token); if (tx.IsInTransaction) tx.Dispose(); }
            catch { try { tx.Dispose(); } catch { } }
            throw;
        }
    }
}
