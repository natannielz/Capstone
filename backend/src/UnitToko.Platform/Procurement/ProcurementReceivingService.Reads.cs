using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using B = UnitToko.Platform.Procurement.ProcurementReceivingBson;
using P = UnitToko.Platform.Procurement.PurchaseOrderBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementReceivingService
{
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, CancellationToken ct = default) {
        await StorageAsync(ct); return await requests.TargetsAsync(actor, ct);
    }
    public async Task<BsonDocument> OrdersAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw ProcurementReceivingRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var scope = await orders.CaptureReceivingLocationAsync(actor, authority, tx, location, true, token);
            var filter = new BsonDocument("location", R.Location(location)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(P.Orders).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token);
            var items = new BsonArray(); var captures = new List<PurchaseOrderReceivingCapture>();
            foreach (var row in rows.Take(25)) {
                var capture = await orders.CaptureReceivingAsync(actor, authority, tx, row["_id"].AsInt64, row["version"].AsInt64, row["approvalId"].AsInt64, Permissions.ProcurementRead, token);
                captures.Add(capture); items.Add(PurchaseOrderService.FundingOrderView(capture.Source));
            }
            foreach (var capture in captures) await capture.FenceAsync(token);
            await FenceQueryAsync(actor, authority, tx, Permissions.ProcurementRead, token); await scope.Fence(token);
            foreach (var capture in captures) capture.RequireCurrent(); scope.Current(); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "location", R.Location(location) }, { "items", items }, { "nextOrderId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> ListAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw ProcurementReceivingRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var scope = await orders.CaptureReceivingLocationAsync(actor, authority, tx, location, false, token);
            var contexts = new List<Context>();
            // ponytail: Full scoped custody reconstruction catches removed heads/effects before pagination. Replace only with a measured, indexed reconciliation proof.
            using var cursor = await Rows(B.Notices).Find(tx, new BsonDocument("location", R.Location(location))).Sort(new BsonDocument("_id", -1)).ToCursorAsync(token);
            while (await cursor.MoveNextAsync(token)) foreach (var row in cursor.Current)
                contexts.Add(await NoticeAsync(actor, authority, tx, row["_id"].AsInt64, Permissions.ProcurementRead, token));
            await records.OrphansAsync(tx, token);
            var page = contexts.Where(context => beforeId is null || context.Notice!["_id"].AsInt64 < beforeId).Take(26).ToArray();
            var items = new BsonArray(page.Take(25).Select(context => new BsonDocument { { "notice", B.NoticeView(context.Notice!) }, { "capacity", context.History!.Capacity } }));
            foreach (var context in contexts) await FenceAsync(context, token); await scope.Fence(token);
            foreach (var context in contexts) Current(context); scope.Current(); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "location", R.Location(location) }, { "items", items }, { "nextNoticeId", page.Length > 25 ? page[24].Notice!["_id"] : BsonNull.Value } };
        }, ct);
    }
    private static (BsonArray Items, BsonValue Next) ReceiptPage(ProcurementContainerHistory history, long? beforeId) {
        var rows = history.Intakes.Where(row => beforeId is null || row["_id"].AsInt64 < beforeId).OrderByDescending(row => row["_id"].AsInt64).Take(26).ToArray();
        return (new BsonArray(rows.Take(25).Select(B.IntakeView)), rows.Length > 25 ? rows[24]["_id"] : BsonNull.Value);
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, long noticeId, CancellationToken ct = default) {
        if (noticeId <= 0) throw ProcurementReceivingRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await NoticeAsync(actor, authority, tx, noticeId, Permissions.ProcurementRead, token); var page = ReceiptPage(context.History!, null);
            var result = new BsonDocument { { "notice", B.NoticeView(context.Notice!) }, { "capacity", context.History!.Capacity }, { "receipts", page.Items }, { "nextReceiptId", page.Next } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> IntakesAsync(ActorContext actor, long noticeId, long? beforeId = null, CancellationToken ct = default) {
        if (noticeId <= 0 || beforeId is <= 0) throw ProcurementReceivingRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await NoticeAsync(actor, authority, tx, noticeId, Permissions.ProcurementRead, token); var page = ReceiptPage(context.History!, beforeId);
            var result = new BsonDocument { { "noticeId", noticeId }, { "items", page.Items }, { "nextReceiptId", page.Next } };
            await FenceAsync(context, token); return result;
        }, ct);
    }
}
