using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform;
using A = UnitToko.Platform.Procurement.PurchaseArrivalRecords;
using B = UnitToko.Platform.Procurement.PurchaseOrderBson;

namespace UnitToko.Platform.Procurement;

internal sealed class PurchaseArrivalReadCapture(IClientSessionHandle session, BsonDocument provenance,
    DateTimeOffset? expectedAt, bool reviewed, IClock clock, Action current, Func<CancellationToken, Task> fence)
{
    private readonly DateTimeOffset capturedAt = clock.UtcNow;
    private DateTimeOffset lastCheckedAt = clock.UtcNow;
    private readonly long attempt = session.WrappedCoreSession.CurrentTransaction.TransactionNumber;
    private readonly string hash = B.Hash(provenance);
    internal DateTimeOffset? ExpectedAt { get; } = expectedAt;
    internal bool Reviewed { get; } = reviewed;
    internal BsonDocument Provenance { get; } = provenance;
    internal void RequireCurrent() {
        var now = clock.UtcNow;
        if (!session.IsInTransaction || session.WrappedCoreSession.CurrentTransaction.TransactionNumber != attempt || B.Hash(Provenance) != hash ||
            now < capturedAt || now < lastCheckedAt) throw A.Broken();
        lastCheckedAt = now;
        current();
    }
    internal async Task FenceAsync(CancellationToken ct) { RequireCurrent(); await fence(ct); RequireCurrent(); ct.ThrowIfCancellationRequested(); }
}

public sealed partial class PurchaseOrderService
{
    internal async Task<PurchaseArrivalReadCapture> CaptureArrivalForReplenishmentAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, long orderId, long skuId, long definitionId, CancellationToken ct)
    {
        await ArrivalStorageAsync(ct);
        var context = await ArrivalContextAsync(actor, authority, tx, orderId, skuId, Permissions.StockRead, ct);
        if (context.Line.DefinitionId != definitionId) throw A.Changed();
        var latest = context.Latest; var approved = latest?["action"] == "approve";
        var expected = approved && latest is not null ? A.Input(latest["input"]).ExpectedAt : null;
        var provenance = new BsonDocument { { "orderId", orderId }, { "orderVersion", context.Source["order"]["version"] },
            { "orderHash", context.Source["order"]["snapshotHash"] }, { "skuId", skuId }, { "definitionId", definitionId },
            { "revision", context.Head["revision"] }, { "eventId", latest is null ? BsonNull.Value : latest["_id"] },
            { "eventHash", latest is null ? BsonNull.Value : latest["eventHash"] },
            { "state", latest is null ? "unrecorded" : latest["action"].AsString },
            { "expectedAt", expected is { } at ? new BsonDateTime(at.UtcDateTime) : BsonNull.Value } };
        // Confirmation references and supplier financial facts remain in the procurement workspace.
        return new(tx, provenance, expected, approved && expected is not null, clock,
            () => Current(context.Owner), token => FenceArrivalAsync(context, token));
    }
}
