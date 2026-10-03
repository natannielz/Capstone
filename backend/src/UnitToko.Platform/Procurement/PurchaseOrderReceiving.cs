using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Procurement.PurchaseOrderBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal sealed class PurchaseOrderReceivingCapture(object owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
    BsonDocument source, BsonDocument profiles, PersonEvidence person, Action current, Func<CancellationToken, Task> fence)
{
    private readonly long transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
    private readonly string hash = B.Hash(new() { { "source", source }, { "profiles", profiles } });
    internal BsonDocument Source { get; } = source;
    internal BsonDocument Profiles { get; } = profiles;
    internal PersonEvidence Person { get; } = person;
    internal void RequireContext(object caller, ActorContext value, AuthorizationSnapshot snapshot, IClientSessionHandle session) {
        if (!ReferenceEquals(caller, owner) || value != actor || !ReferenceEquals(snapshot, authority) || !ReferenceEquals(session, tx) || !tx.IsInTransaction ||
            tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction || B.Hash(new() { { "source", Source }, { "profiles", Profiles } }) != hash) throw B.Broken();
        current();
    }
    internal void RequireCurrent() => RequireContext(owner, actor, authority, tx);
    internal async Task FenceAsync(CancellationToken ct) { RequireCurrent(); await fence(ct); RequireCurrent(); ct.ThrowIfCancellationRequested(); }
}

public sealed partial class PurchaseOrderService
{
    internal Task RequireReceivingStorageAsync(CancellationToken ct) => StorageAsync(ct);
    internal async Task<(Action Current, Func<CancellationToken, Task> Fence)> CaptureReceivingLocationAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, ProcurementLocation location, bool orderHistory, CancellationToken ct) {
        var context = await ContextAsync(actor, authority, tx, location, Permissions.ProcurementRead, ct);
        await PeopleAsync(context, null, null, ct);
        if (orderHistory) await HistoryAsync(context, ct);
        var transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        void Require() {
            if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction) throw B.Broken();
            Current(context);
        }
        async Task Fence(CancellationToken token) { Require(); await FenceAsync(context, token); Require(); token.ThrowIfCancellationRequested(); }
        Require(); return (Require, Fence);
    }
    internal async Task<PurchaseOrderReceivingCapture> CaptureReceivingAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        long orderId, long version, long approvalId, string permission, CancellationToken ct) {
        if (permission is not (Permissions.ProcurementRead or Permissions.ProcurementPrepare or Permissions.ProcurementContainerReceive)) throw B.Broken();
        var route = await Rows(B.Orders).Find(tx, new BsonDocument("_id", orderId)).Project(new BsonDocument("location", 1)).SingleOrDefaultAsync(ct)
            ?? throw new AuthorizationDeniedException("resource_unavailable");
        var context = await ContextAsync(actor, authority, tx, R.Location(route["location"]), permission, ct);
        // This historical owner proof also verifies the original demand allocations. Receiving
        // does not consume them again or require an advance, a live quote or current SKU revision.
        var source = await FundingSourceAsync(database, tx, orderId, version, approvalId, ct);
        var proposal = source["proposal"].AsBsonDocument; var approval = source["approval"].AsBsonDocument;
        BindPeople(context, proposal, approval); await PeopleAsync(context, approval["actorId"].AsInt64, proposal["actorId"].AsInt64, ct);
        async Task Fence(CancellationToken token) {
            Current(context);
            foreach (var (name, row) in new[] { (B.Orders, source["order"].AsBsonDocument), (B.Proposals, proposal), (B.Decisions, approval) })
                if ((await Rows(name).UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Set("version", row["version"]), cancellationToken: token)).MatchedCount != 1) throw B.Changed();
            await FenceAsync(context, token); Current(context);
        }
        Current(context); return new(this, actor, authority, tx, source, context.Sources,
            context.People!.Evidence.Single(person => person.UserId == actor.UserId), () => Current(context), Fence);
    }
}
