using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Procurement.ProcurementDisbursementBson;

namespace UnitToko.Platform.Procurement;

internal sealed class ProcurementDisbursementSettlementCapture(object owner, ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
    BsonDocument record, Func<CancellationToken, Task> fence, Action current)
{
    private readonly long transaction = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
    private readonly string hash = B.Hash(record);
    internal BsonDocument Record { get; } = record;
    internal void RequireContext(object caller, ActorContext value, AuthorizationSnapshot snapshot, IClientSessionHandle session) {
        if (!ReferenceEquals(owner, caller) || actor != value || !ReferenceEquals(authority, snapshot) || !ReferenceEquals(session, tx) || !session.IsInTransaction ||
            session.WrappedCoreSession.CurrentTransaction.TransactionNumber != transaction || B.Hash(Record) != hash) throw B.Broken(); current();
    }
    internal void RequireCurrent() => RequireContext(owner, actor, authority, tx);
    internal void RequireSource(object caller, IClientSessionHandle session, long id) { RequireContext(caller, actor, authority, session); if (Record["_id"] != id) throw B.Broken(); }
    internal async Task FenceAsync(CancellationToken ct) { RequireCurrent(); await fence(ct); RequireCurrent(); ct.ThrowIfCancellationRequested(); }
}

public sealed partial class ProcurementDisbursementService
{
    internal async Task<ProcurementDisbursementSettlementCapture> CaptureSettlementAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx,
        long disbursementId, string permission, CancellationToken ct) {
        if (permission is not (Permissions.DisbursementVerify or Permissions.ProcurementDisbursementRead) || !tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        var route = await Rows(B.Records).Find(tx, new BsonDocument("_id", disbursementId)).Project(new BsonDocument { { "draftId", 1 }, { "location", 1 } }).SingleOrDefaultAsync(ct)
            ?? throw new AuthorizationDeniedException("resource_unavailable");
        _ = ContextFor(actor, authority, tx, ProcurementRequestBson.Location(route["location"]), permission);
        var context = await HistoricalContextAsync(actor, authority, tx, route["draftId"].AsInt64, permission, ct);
        var record = context.Capacity?.Claims.GetValueOrDefault(disbursementId) ?? throw B.Broken();
        ProcurementDisbursementRecords.Equal(record["draftId"], context.Draft!["_id"]);
        return new(this, actor, authority, tx, record, token => FenceAsync(context, token), () => Current(context));
    }
    internal async Task<BsonDocument> ReadSettlementHistoryAsync(IClientSessionHandle tx, long disbursementId, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        var history = new ProcurementDisbursementRecords.HistoricalSources(records, tx, () => {
            if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        });
        var record = await records.RecordAsync(tx, disbursementId, false, ct, history: history);
        var capacity = await CapacityAsync(tx, record["source"]["funding"].AsBsonDocument, ct, history: history);
        if (!capacity.Claims.ContainsKey(disbursementId)) throw B.Broken(); return record;
    }
}
