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

/// <summary>F-030: a draft whose packages have all become orders is closed. It leaves the draft list (and so the keranjang),
/// refuses saves, and stays readable by id for the checkout that placed it. A draft ordered in part (one channel group at a time)
/// stays the keranjang, so the packages not yet ordered are not lost. The link is written by OrderSubmissionService in the order's own
/// transaction, so an order and its source draft's closing commit or roll back together.</summary>
public sealed partial class OrderDraftService
{
    internal static Task<bool> SubmittedAsync(IMongoDatabase database, IClientSessionHandle tx, long draftId, CancellationToken ct) =>
        database.GetCollection<BsonDocument>(L.Collection).Find(tx, new BsonDocument { { "draftId", draftId }, { "closesDraft", true } }).Limit(1).AnyAsync(ct);

    /// <summary>The draft an order is being submitted from, read and fenced inside the order's transaction: owned by the actor,
    /// in a branch they still order for, not discarded. A concurrent save of the same draft conflicts with the submission.</summary>
    internal async Task<(OrderDraftInput Input, long Revision)> SourceAsync(ActorContext actor, AuthorizationSnapshot snapshot, IClientSessionHandle tx,
        long draftId, CancellationToken ct)
    {
        if (draftId <= 0) throw B.Invalid("order_draft_unavailable");
        var head = await HeadAsync(actor, snapshot, tx, draftId, ct);
        if (await DiscardRowAsync(tx, head, ct) is not null) throw Discarded();
        var version = await VersionAsync(tx, head["versionId"].AsInt64, ct);
        await TouchAsync(tx, head, ct);
        return (B.Input(version["input"].AsBsonDocument), head["revision"].AsInt64);
    }

    internal static PersistenceConflictException Submitted() =>
        new("order_draft_submitted", "Draf ini sudah diajukan menjadi pesanan; tambahkan barang ke keranjang untuk pesanan berikutnya");
}
