using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Ordering;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Ordering.OrderDraftBson;
using L = UnitToko.Platform.Ordering.OrderDraftSubmissionSchemas;

namespace UnitToko.Platform.Ordering;

/// <summary>F-030: a draft that has become an order is closed. It leaves the draft list (and so the keranjang), refuses saves, and
/// stays readable by id for the checkout that placed it. Whatever the order left unordered continues in a successor draft written
/// in the order's own transaction (same owner, branch, delivery and reference), so the keranjang is never lost and never reopens an
/// ordered package. The link is written by OrderSubmissionService, so an order, the closing of its draft and the successor commit
/// or roll back together.</summary>
public sealed partial class OrderDraftService
{
    internal static Task<bool> SubmittedAsync(IMongoDatabase database, IClientSessionHandle tx, long draftId, CancellationToken ct) =>
        database.GetCollection<BsonDocument>(L.Collection).Find(tx, new BsonDocument("draftId", draftId)).Limit(1).AnyAsync(ct);

    /// <summary>The draft an order is being submitted from, read and fenced inside the order's transaction: owned by the actor, in a
    /// branch they still order for, not discarded. A checkout that began on a draft keeps naming it, so a draft already closed by an
    /// earlier group of the same checkout leads on to its successor (the live draft); a draft whose packages were all ordered leads
    /// nowhere. A concurrent save of the live draft conflicts with the submission.</summary>
    internal async Task<(OrderDraftInput Input, long Revision, long LiveId)> SourceAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx,
        long draftId, CancellationToken ct)
    {
        if (draftId <= 0) throw B.Invalid("order_draft_unavailable");
        // Ownership first, so another buyer's draft looks exactly like a draft that does not exist.
        _ = await HeadAsync(actor, snapshot, tx, draftId, ct);
        var live = draftId;
        for (var hop = 0; ; hop++) {
            if (hop > 100) throw B.Invalid();
            var link = await Rows(L.Collection).Find(tx, new BsonDocument("draftId", live)).SingleOrDefaultAsync(ct);
            if (link is null) break;
            if (link["successorId"].IsBsonNull) throw Submitted();
            live = link["successorId"].AsInt64;
        }
        var head = await HeadAsync(actor, snapshot, tx, live, ct);
        if (await DiscardRowAsync(tx, head, ct) is not null) throw Discarded();
        var version = await VersionAsync(tx, head["versionId"].AsInt64, ct);
        await TouchAsync(tx, head, ct);
        return (B.Input(version["input"].AsBsonDocument), head["revision"].AsInt64, live);
    }

    /// <summary>The draft that carries what an order left unordered, written through the same version writer as Save and with the
    /// receipt Save's command leaves (built by the same `AuthorizationStore.CommandReceipt` ExecuteCommandAsync uses), inside the order's transaction. Its packages are copied as the source draft held them: the
    /// ordered group's price check is the order's own, and a price that changed for the others is the keranjang's to show.</summary>
    internal async Task<long> WriteSuccessorAsync(IClientSessionHandle tx, ActorContext actor, OrderDraftInput remaining, long orderId, string correlationId,
        DateTimeOffset now, CancellationToken ct)
    {
        var draftId = (await ids.NextAsync(B.Heads, ct)).Value; var versionId = (await ids.NextAsync(B.Versions, ct)).Value;
        var auditId = (await ids.NextAsync("auditEvents", ct)).Value;
        var metadata = new AuthorizationCommandMetadata("order-successor:" + orderId, "Sisa keranjang dilanjutkan setelah pesanan diajukan", correlationId);
        var hash = AuthorizationStore.CommandHash(Body(null, 0, remaining), metadata.Reason);
        var version = await WriteVersionAsync(tx, actor, draftId, versionId, auditId, 1, remaining, metadata, hash, null, now, ct);
        await Rows("idempotencyRecords").InsertOneAsync(tx, AuthorizationStore.CommandReceipt(actor.UserId, B.Operation, metadata.IdempotencyKey, hash,
            clock.UtcNow.UtcDateTime, B.Result(version)), cancellationToken: ct);
        return draftId;
    }

    internal static PersistenceConflictException Submitted() =>
        new("order_draft_submitted", "Draf ini sudah diajukan menjadi pesanan; tambahkan barang ke keranjang untuk pesanan berikutnya");
}
