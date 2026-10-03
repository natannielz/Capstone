using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Organizations;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Procurement.ProcurementStockReceiptBson;
using I = UnitToko.Platform.Procurement.ProcurementInspectionBson;
using P = UnitToko.Platform.Procurement.PurchaseOrderBson;
using Q = UnitToko.Platform.Procurement.ProcurementQuarantineDispositionBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementStockReceiptService
{
    private async Task FenceQueryAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, string permission, CancellationToken ct) {
        var clauses = new[] { Permissions.ProcurementRead, permission }.Distinct().SelectMany(value => Query(actor, authority, value).Clauses).ToArray();
        await new HistoricalAuthorizationFence(database, authorization).FenceAsync(actor, clauses.SelectMany(clause => clause.RequiredNodes).Distinct(), [],
            authority.Grants.Where(grant => clauses.Any(clause => clause.GrantId == grant.Id)), authority.Memberships.Where(member => clauses.Any(clause => clause.MembershipId == member.Id)), tx, ct);
        var expiry = await SessionAsync(actor, tx, ct); foreach (var value in new[] { Permissions.ProcurementRead, permission }.Distinct()) _ = Query(actor, authority, value);
        if (expiry is { } until && clock.UtcNow >= until) throw new AuthorizationDeniedException("stale_or_inactive_session"); ct.ThrowIfCancellationRequested();
    }
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, CancellationToken ct = default) {
        await StorageAsync(ct); var named = await receiving.TargetsAsync(actor, ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var read = Query(actor, authority, Permissions.StockRead); var procurement = Query(actor, authority, Permissions.ProcurementRead); var items = new BsonArray();
            var captures = new List<(Action Current, Func<CancellationToken, Task> Fence)>();
            foreach (var item in named["items"].AsBsonArray) {
                var location = R.Location(item["location"]); var resource = Resource(location);
                if (!ScopePolicy.IsResourceActive(resource, authority.Directory) || !read.Matches(resource) || !procurement.Matches(resource)) continue;
                var capture = await orders.CaptureReceivingLocationAsync(actor, authority, tx, location, false, token); await capture.Fence(token); captures.Add(capture); items.Add(item);
            }
            await FenceQueryAsync(actor, authority, tx, Permissions.StockRead, token); foreach (var capture in captures) capture.Current(); token.ThrowIfCancellationRequested();
            return new BsonDocument("items", items);
        }, ct);
    }
    private async Task<Context> SelectedOrderAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementLocation location, long id, CancellationToken ct) {
        _ = Require(new(actor, authority, tx, location, Permissions.StockRead));
        var row = await Rows(P.Orders).Find(tx, new BsonDocument { { "_id", id }, { "location", R.Location(location) } }).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        return await OrderAsync(actor, authority, tx, location, id, row["version"].AsInt64, row["approvalId"].AsInt64, Permissions.StockRead, ct);
    }
    public async Task<BsonDocument> OrdersAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw ProcurementStockReceiptRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            _ = Require(new(actor, authority, tx, location, Permissions.StockRead));
            var scope = await orders.CaptureReceivingLocationAsync(actor, authority, tx, location, true, token); await scope.Fence(token);
            var filter = new BsonDocument("location", R.Location(location)); if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(P.Orders).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray(); var contexts = new List<Context>();
            foreach (var row in rows.Take(25)) {
                var context = await OrderAsync(actor, authority, tx, location, row["_id"].AsInt64, row["version"].AsInt64, row["approvalId"].AsInt64, Permissions.StockRead, token);
                items.Add(PurchaseOrderService.FundingOrderView(context.Order!.Source)); await FenceAsync(context, token); contexts.Add(context);
            }
            await FenceQueryAsync(actor, authority, tx, Permissions.StockRead, token); scope.Current(); foreach (var context in contexts) Current(context); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "location", R.Location(location) }, { "items", items }, { "nextOrderId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> InspectionsAsync(ActorContext actor, ProcurementLocation location, long orderId, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (orderId <= 0 || beforeId is <= 0) throw ProcurementStockReceiptRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await SelectedOrderAsync(actor, authority, tx, location, orderId, token);
            var filter = new BsonDocument { { "location", R.Location(location) }, { "source.notice.source.order._id", orderId } }; if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(I.Submissions).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray();
            foreach (var raw in rows.Take(25)) {
                var history = await records.InspectionAsync(tx, raw["_id"].AsInt64, token); var inspection = history.Submissions.Single(value => value["_id"] == raw["_id"]);
                ProcurementStockReceiptRecords.Equal(history.Source["notice"]["source"], context.Order!.Source);
                AddFence(context, ProcurementReceivingBson.Heads, history.CustodyHead); AddFence(context, I.Heads, history.Head ?? throw B.Broken()); foreach (var draft in history.Drafts) AddFence(context, I.Drafts, draft);
                var lines = new BsonArray();
                for (var index = 0; index < inspection["input"]["lines"].AsBsonArray.Count; index++) {
                    var line = inspection["input"]["lines"][index]; var capacity = await dispositions.ReadAsync(tx, SupplierClaimBasis.PhysicalReceived, inspection["_id"].AsInt64, index + 1,
                        inspection["snapshotHash"].AsString, line["receivedPackages"].AsInt32, R.Location(location), orderId, context.Order.Source["proposal"]["input"]["supplierId"].AsInt64, token);
                    if (capacity.Head is { } head) AddFence(context, Q.PhysicalHeads, head); lines.Add(new BsonDocument { { "lineNumber", index + 1 }, { "input", line }, { "capacity", capacity.Capacity } });
                }
                items.Add(new BsonDocument { { "inspectionId", inspection["_id"] }, { "version", 1L }, { "inspectionReference", inspection["input"]["inspectionReference"] },
                    { "inspectionDate", inspection["input"]["inspectionDate"] }, { "noticeId", inspection["noticeId"] }, { "intakeId", inspection["intakeId"] }, { "lines", lines } });
            }
            await dispositions.OrphansAsync(tx, token); await records.OrphansAsync(tx, token); await FenceAsync(context, token);
            return new BsonDocument { { "location", R.Location(location) }, { "orderId", orderId }, { "items", items }, { "nextInspectionId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> PoolsAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw ProcurementStockReceiptRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            _ = Require(new(actor, authority, tx, location, Permissions.StockRead));
            var scope = await orders.CaptureReceivingLocationAsync(actor, authority, tx, location, false, token); await scope.Fence(token);
            var read = Query(actor, authority, Permissions.StockRead); var filter = new BsonDocument { { "location.organizationId", location.OrganizationId }, { "location.shopId", location.ShopId }, { "location.warehouseId", location.WarehouseId } };
            if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before)); var items = new BsonArray(); var captures = new List<HeldReceiptPoolCapture>();
            // Bounded output, streaming candidates. Unconfigured/external sources are not fabricated as eligible choices.
            using var cursor = await Rows("inventoryPools").Find(tx, filter).Sort(new BsonDocument("_id", -1)).ToCursorAsync(token);
            while (items.Count < 26 && await cursor.MoveNextAsync(token)) foreach (var row in cursor.Current) {
                var owner = Resource(R.Location(row["location"])); if (!ScopePolicy.IsResourceActive(owner, authority.Directory) || !read.Matches(owner)) continue;
                HeldReceiptPoolCapture capture;
                try { capture = await sources.CaptureHeldReceiptAsync(actor, authority, tx, Location(location), row["_id"].AsInt64, row["revision"].AsInt64, Permissions.StockRead, token); }
                catch (PersistenceConflictException error) when (error.Code == "procurement_stock_receipt_pool_ineligible") { continue; }
                items.Add(StockSourceSetupService.HeldReceiptPoolView(capture.Source)); await capture.FenceAsync(token); captures.Add(capture); if (items.Count == 26) break;
            }
            await FenceQueryAsync(actor, authority, tx, Permissions.StockRead, token); scope.Current(); foreach (var capture in captures) capture.RequireCurrent(); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "location", R.Location(location) }, { "items", new BsonArray(items.Take(25)) }, { "nextPoolId", items.Count > 25 ? items[24]["id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> ReadAsync(ActorContext actor, long receiptId, CancellationToken ct = default) {
        if (receiptId <= 0) throw ProcurementStockReceiptRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ReceiptAsync(actor, authority, tx, receiptId, Permissions.StockRead, token); var result = B.View(context.Receipt!); await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> ListAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw ProcurementStockReceiptRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            _ = Require(new(actor, authority, tx, location, Permissions.StockRead)); var contexts = new List<Context>();
            // ponytail: Complete scoped owner reconstruction prevents deleted capacity/effects from appearing as an empty page. An indexed reconciliation proof can replace it after measured load warrants it.
            using var cursor = await Rows(B.Receipts).Find(tx, new BsonDocument("location", R.Location(location))).Sort(new BsonDocument("_id", -1)).ToCursorAsync(token);
            while (await cursor.MoveNextAsync(token)) foreach (var row in cursor.Current) { var context = await ReceiptAsync(actor, authority, tx, row["_id"].AsInt64, Permissions.StockRead, token); await FenceAsync(context, token); contexts.Add(context); }
            await records.OrphansAsync(tx, token); await dispositions.OrphansAsync(tx, token);
            var scope = await orders.CaptureReceivingLocationAsync(actor, authority, tx, location, false, token); await scope.Fence(token); await FenceQueryAsync(actor, authority, tx, Permissions.StockRead, token);
            scope.Current(); foreach (var context in contexts) Current(context); token.ThrowIfCancellationRequested();
            var page = contexts.Where(value => beforeId is null || value.Receipt!["_id"].AsInt64 < beforeId).Take(26).ToArray();
            return new BsonDocument { { "location", R.Location(location) }, { "items", new BsonArray(page.Take(25).Select(value => B.View(value.Receipt!))) }, { "nextReceiptId", page.Length > 25 ? page[24].Receipt!["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        if (operation != B.Operation) throw ProcurementStockReceiptRules.Invalid(); Key(key); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            _ = Query(actor, authority, Permissions.StockReceive); var row = await records.ForKeyAsync(tx, actor.UserId, key, token);
            if (row is null) { await FenceQueryAsync(actor, authority, tx, Permissions.StockReceive, token); return null; }
            var context = await ReceiptAsync(actor, authority, tx, row["_id"].AsInt64, Permissions.StockReceive, token); await FenceAsync(context, token); return B.Result(context.Receipt!);
        }, ct);
    }
}
