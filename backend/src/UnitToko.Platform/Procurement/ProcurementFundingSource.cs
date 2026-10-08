using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Procurement.ProcurementFundingBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal sealed class ProcurementFundingCapture(object owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
    BsonDocument source, Func<CancellationToken, Task> fence, Action current)
{
    private readonly long transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
    private readonly string hash = B.Hash(source);
    internal BsonDocument Source { get; } = source;
    internal void RequireContext(object service, ActorContext caller, AuthorizationSnapshot auth, IClientSessionHandle session) {
        if (!ReferenceEquals(service, owner) || caller != actor || !ReferenceEquals(auth, authority) || !ReferenceEquals(session, tx) || !session.IsInTransaction ||
            session.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction || B.Hash(Source) != hash) throw B.Broken(); current();
    }
    internal void RequireCurrent() => RequireContext(owner, actor, authority, tx);
    internal async Task FenceAsync(CancellationToken ct) { RequireCurrent(); await fence(ct); RequireCurrent(); ct.ThrowIfCancellationRequested(); }
}

public sealed partial class ProcurementFundingService
{
    internal async Task<BsonDocument> DisbursementSourceAsync(IMongoDatabase owner, IClientSessionHandle tx, long requestId, long decisionId, long authorizationId, CancellationToken ct) {
        return (await DisbursementStateAsync(owner, tx, requestId, decisionId, authorizationId, ct)).Source;
    }
    private async Task<(BsonDocument Source, FundingCapacityState Capacity)> DisbursementStateAsync(IMongoDatabase owner, IClientSessionHandle tx,
        long requestId, long decisionId, long authorizationId, CancellationToken ct, PurchaseOrderFundingCapture? orderProof = null) {
        if (!ReferenceEquals(owner.Client, database.Client) || !owner.DatabaseNamespace.Equals(database.DatabaseNamespace) || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        var request = await records.RequestAsync(tx, requestId, ct, orderProof); var decision = await records.DecisionAsync(tx, decisionId, request, ct);
        ProcurementFundingRecords.Equal(decision["decision"], "approve"); ProcurementFundingRecords.Equal(decision["authorizationId"], authorizationId);
        var allocation = await records.RowAsync(tx, B.Allocations, authorizationId, ct); ProcurementFundingRecords.RequireAllocation(decision, request, allocation);
        var capacity = await CapacityAsync(tx, request["orderSource"].AsBsonDocument, ct, orderProof);
        return (new() { { "request", request }, { "decision", decision }, { "authorization", allocation } }, capacity);
    }
    internal async Task<ProcurementFundingCapture> CaptureDisbursementAsync(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        long requestId, long decisionId, long authorizationId, CancellationToken ct) {
        await StorageAsync(ct); _ = Query(actor, authority, Permissions.FundingRead);
        var route = await Rows(B.Requests).Find(tx, new BsonDocument("_id", requestId)).Project(new BsonDocument { { "location", 1 }, { "input", 1 } }).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        var context = await ContextAsync(actor, authority, tx, R.Location(route["location"]), Permissions.FundingRead, ct);
        var input = B.Input(route["input"]);
        context.Order = await orders.CaptureFundingAsync(database, actor, authority, tx, input.OrderId, input.OrderVersion, input.OrderApprovalId, Permissions.FundingRead, ct);
        context.Order.RequireContext(orders, actor, authority, tx);
        var captured = await DisbursementStateAsync(owner, tx, requestId, decisionId, authorizationId, ct, context.Order);
        var source = captured.Source; var request = source["request"].AsBsonDocument; var decision = source["decision"].AsBsonDocument;
        BindPeople(context, request, decision); await PeopleAsync(context, decision["actorId"].AsInt64, request["actorId"].AsInt64, ct);
        Equal(request["input"], route["input"]);
        var policy = await records.RowAsync(tx, B.Policies, input.PolicyId, ct); Equal(B.PolicySnapshot(policy), request["policy"]); context.Policy = policy;
        context.Capacity = captured.Capacity; Current(context);
        return new(this, actor, authority, tx, source, token => FenceAsync(context, token), () => Current(context));
    }
    internal async Task FenceDisbursementAsync(IMongoDatabase owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementFundingCapture capture, CancellationToken ct) {
        if (!ReferenceEquals(owner.Client, database.Client) || !owner.DatabaseNamespace.Equals(database.DatabaseNamespace)) throw B.Broken();
        capture.RequireContext(this, actor, authority, tx); await capture.FenceAsync(ct);
    }
}
