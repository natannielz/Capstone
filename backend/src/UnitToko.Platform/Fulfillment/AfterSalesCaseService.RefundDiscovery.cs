using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Billing;
using R = UnitToko.Platform.Fulfillment.AfterSalesCaseRecords;

namespace UnitToko.Platform.Fulfillment;

internal sealed record RefundDiscoveryCaseSource(BsonDocument Case, BsonDocument Receipt, Action RequireCurrent, Func<CancellationToken, Task> FenceAsync);
public sealed partial class AfterSalesCaseService {
    internal async Task<RefundDiscoveryCaseSource> RefundDiscoverySourceAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        long caseId, long returnId, CancellationToken ct) {
        if (caseId <= 0 || returnId <= 0 || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || actor.UserId != authority.UserId || actor.GrantVersion != authority.GrantVersion) throw R.Broken();
        var root = await AuthorizedAsync(actor, authority, tx, caseId, Permissions.CaseRead, ct);
        var receipt = await InspectionReceiptAsync(actor, authority, tx, root, returnId, Permissions.CaseRead, ct);
        var frozenRoot = root.DeepClone().AsBsonDocument; frozenRoot.Remove("fence"); var frozenReceipt = receipt.DeepClone().AsBsonDocument;
        void Current() { if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw R.Broken(); Require(actor, authority, frozenRoot, Permissions.CaseRead); ct.ThrowIfCancellationRequested(); }
        async Task Fence(CancellationToken token) {
            Current(); var latest = await AuthorizedAsync(actor, authority, tx, caseId, Permissions.CaseRead, token);
            var currentReceipt = await InspectionReceiptAsync(actor, authority, tx, latest, returnId, Permissions.CaseRead, token);
            var stable = latest.DeepClone().AsBsonDocument; stable.Remove("fence");
            if (!InvoiceRecords.Equal(stable, frozenRoot) || !InvoiceRecords.Equal(currentReceipt, frozenReceipt)) throw R.Broken();
            await FenceAsync(actor, authority, tx, latest, Permissions.CaseRead, token); Current();
        }
        Current(); return new(frozenRoot, frozenReceipt, Current, Fence);
    }
}
