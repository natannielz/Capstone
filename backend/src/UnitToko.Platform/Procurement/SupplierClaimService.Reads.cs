using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Procurement.SupplierClaimBson;
using D = UnitToko.Platform.Procurement.SupplierDocumentBson;
using I = UnitToko.Platform.Procurement.ProcurementInspectionBson;
using P = UnitToko.Platform.Procurement.PurchaseOrderBson;
using Q = UnitToko.Platform.Procurement.ProcurementQuarantineDispositionBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class SupplierClaimService
{
    public async Task<BsonDocument> TargetsAsync(ActorContext actor, CancellationToken ct = default) { await StorageAsync(ct); return await receiving.TargetsAsync(actor, ct); }
    public async Task<BsonDocument> OrdersAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) { await StorageAsync(ct); return await receiving.OrdersAsync(actor, location, beforeId, ct); }
    private async Task<Context> SelectedOrderAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementLocation location, long id, CancellationToken ct) {
        _ = ContextFor(actor, authority, tx, location, Permissions.ProcurementRead);
        var row = await Rows(P.Orders).Find(tx, new BsonDocument { { "_id", id }, { "location", R.Location(location) } }).SingleOrDefaultAsync(ct) ?? throw new AuthorizationDeniedException("resource_unavailable");
        return await OrderContextAsync(actor, authority, tx, location, id, row["version"].AsInt64, row["approvalId"].AsInt64, Permissions.ProcurementRead, ct);
    }
    public async Task<BsonDocument> InspectionsAsync(ActorContext actor, ProcurementLocation location, long orderId, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (orderId <= 0 || beforeId is <= 0) throw SupplierClaimRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await SelectedOrderAsync(actor, authority, tx, location, orderId, token);
            var filter = new BsonDocument { { "location", R.Location(location) }, { "source.notice.source.order._id", orderId } }; if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(I.Submissions).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray();
            foreach (var raw in rows.Take(25)) {
                var history = await records.InspectionAsync(tx, raw["_id"].AsInt64, token); var inspection = history.Submissions.Single(value => value["_id"] == raw["_id"]);
                SupplierClaimRecords.Equal(history.Source["notice"]["source"], context.Order!.Source);
                AddFence(context, ProcurementReceivingBson.Heads, history.CustodyHead); AddFence(context, I.Heads, history.Head ?? throw B.Broken());
                foreach (var draft in history.Drafts) AddFence(context, I.Drafts, draft);
                var lines = new BsonArray();
                for (var index = 0; index < inspection["input"]["lines"].AsBsonArray.Count; index++) {
                    var line = inspection["input"]["lines"][index]; var value = new BsonDocument { { "lineNumber", index + 1 }, { "input", line } };
                    foreach (var basis in new[] { SupplierClaimBasis.PhysicalReceived, SupplierClaimBasis.ShortageObserved }) {
                        var capacity = await dispositions.ReadAsync(tx, basis, inspection["_id"].AsInt64, index + 1, inspection["snapshotHash"].AsString, Q.Total(basis, line),
                            R.Location(location), orderId, context.Order.Source["proposal"]["input"]["supplierId"].AsInt64, token);
                        if (capacity.Head is { } head) AddFence(context, Q.Heads(basis), head); value.Add(basis == SupplierClaimBasis.PhysicalReceived ? "physical" : "shortage", capacity.Capacity);
                    }
                    lines.Add(value);
                }
                items.Add(new BsonDocument { { "inspectionId", inspection["_id"] }, { "version", 1L }, { "inspectionReference", inspection["input"]["inspectionReference"] },
                    { "inspectionDate", inspection["input"]["inspectionDate"] }, { "noticeId", inspection["noticeId"] }, { "intakeId", inspection["intakeId"] }, { "lines", lines } });
            }
            await dispositions.OrphansAsync(tx, token); await FenceAsync(context, token);
            return new BsonDocument { { "location", R.Location(location) }, { "orderId", orderId }, { "items", items }, { "nextInspectionId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    private async Task<SupplierDocumentHistory> DocumentSourceAsync(Context context, long id, CancellationToken ct) {
        var fences = new List<(string Table, BsonDocument Row)>(); var history = await records.DocumentAsync(context.Tx, id, ct, fences);
        SupplierClaimRecords.Equal(history.Document!["location"], R.Location(context.Location)); SupplierClaimRecords.Equal(history.Document["supplierId"], context.Order!.Source["proposal"]["input"]["supplierId"]);
        AddFence(context, D.Heads, history.Head); AddFence(context, D.Identities, history.Identity); AddFence(context, D.Drafts, history.Draft);
        foreach (var (table, row) in fences) AddFence(context, table, row); return history;
    }
    public async Task<BsonDocument> InvoicesAsync(ActorContext actor, ProcurementLocation location, long orderId, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (orderId <= 0 || beforeId is <= 0) throw SupplierClaimRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await SelectedOrderAsync(actor, authority, tx, location, orderId, token);
            using var linkedCursor = await Rows(D.Matches).DistinctAsync<long>(tx, "documentId", new BsonDocument { { "location", R.Location(location) }, { "links", new BsonDocument("$elemMatch", new BsonDocument { { "kind", "inspection" }, { "orderId", orderId } }) } }, cancellationToken: token);
            var linked = await linkedCursor.ToListAsync(token);
            var filter = new BsonDocument { { "location", R.Location(location) }, { "supplierId", context.Order!.Source["proposal"]["input"]["supplierId"] }, { "input.kind", "invoice" },
                { "$or", new BsonArray { new BsonDocument("input.anchorOrderId", orderId), new BsonDocument("_id", new BsonDocument("$in", new BsonArray(linked))) } } };
            if (beforeId is { } before) filter.Add("_id", new BsonDocument("$lt", before));
            var rows = await Rows(D.Submissions).Find(tx, filter).Sort(new BsonDocument("_id", -1)).Limit(26).ToListAsync(token); var items = new BsonArray();
            foreach (var row in rows.Take(25)) {
                var history = await DocumentSourceAsync(context, row["_id"].AsInt64, token);
                if (history.Document!["input"]["anchorOrderId"] != orderId && !history.Matches.Any(match => match["links"].AsBsonArray.Any(link => !link.IsBsonNull && link["kind"] == "inspection" && link["orderId"] == orderId))) throw B.Broken();
                items.Add(new BsonDocument { { "documentId", history.Document["_id"] }, { "documentRevision", 1L }, { "input", history.Document["input"] } });
            }
            await FenceAsync(context, token); return new BsonDocument { { "location", R.Location(location) }, { "orderId", orderId }, { "items", items }, { "nextDocumentId", rows.Count > 25 ? rows[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument> InvoiceMatchesAsync(ActorContext actor, long documentId, long? beforeId = null, CancellationToken ct = default) {
        if (documentId <= 0 || beforeId is <= 0) throw SupplierClaimRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var raw = await Rows(D.Submissions).Find(tx, new BsonDocument("_id", documentId)).Project(new BsonDocument { { "location", 1 }, { "input", 1 } }).SingleOrDefaultAsync(token) ?? throw new AuthorizationDeniedException("resource_unavailable");
            var input = D.Input(raw["input"]); var context = await OrderContextAsync(actor, authority, tx, input.Location, input.AnchorOrderId, input.AnchorOrderVersion, input.AnchorOrderApprovalId, Permissions.ProcurementRead, token);
            var history = await DocumentSourceAsync(context, documentId, token); var selected = history.Matches.Where(row => beforeId is null || row["_id"].AsInt64 < beforeId).OrderByDescending(row => row["_id"].AsInt64).Take(26).ToArray();
            var items = new BsonArray(selected.Take(25).Select(row => new BsonDocument { { "matchId", row["_id"] }, { "recordedAt", row["recordedAt"] },
                { "lines", new BsonArray(row["lines"].AsBsonArray.Select((line, index) => (line, index)).Where(value => value.line["state"] == "matched").Select(value => new BsonDocument {
                    { "lineNumber", value.index + 1 }, { "documentLineNumber", value.line["documentLineNumber"] }, { "inspectionId", value.line["inspectionId"] }, { "inspectionLineNumber", value.line["inspectionLineNumber"] },
                    { "packages", value.line["packages"] }, { "sourceInvoiceAmount", new Money(new[] { "netAmount", "taxAmount", "chargeAmount" }.Sum(field => Money.Parse(value.line[field].AsString).Value)).ToString() } })) } }));
            await FenceAsync(context, token); return new BsonDocument { { "documentId", documentId }, { "items", items }, { "nextMatchId", selected.Length > 25 ? selected[24]["_id"] : BsonNull.Value } };
        }, ct);
    }
    private static BsonDocument ReadView(Context context) => new() { { "request", B.RequestView(context.Request!) }, { "decision", context.Decision is null ? BsonNull.Value : B.DecisionView(context.Decision) },
        { "capacity", context.Capacity }, { "state", context.Decision is null ? "requested" : B.State(context.Decision) } };
    public async Task<BsonDocument> ReadAsync(ActorContext actor, long claimId, CancellationToken ct = default) {
        if (claimId <= 0) throw SupplierClaimRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await ClaimContextAsync(actor, authority, tx, claimId, Permissions.ProcurementRead, token); var result = ReadView(context); await FenceAsync(context, token); return result;
        }, ct);
    }
    public async Task<BsonDocument> ListAsync(ActorContext actor, ProcurementLocation location, long? beforeId = null, CancellationToken ct = default) {
        ProcurementRequestRules.Location(location); if (beforeId is <= 0) throw SupplierClaimRules.Invalid(); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            _ = ContextFor(actor, authority, tx, location, Permissions.ProcurementRead);
            var contexts = new List<Context>();
            // ponytail: Complete scoped reconstruction prevents removed owner/decision/capacity history from looking like an empty page.
            using var cursor = await Rows(B.Requests).Find(tx, new BsonDocument("location", R.Location(location))).Sort(new BsonDocument("_id", -1)).ToCursorAsync(token);
            while (await cursor.MoveNextAsync(token)) foreach (var row in cursor.Current) {
                var context = await ClaimContextAsync(actor, authority, tx, row["_id"].AsInt64, Permissions.ProcurementRead, token);
                await FenceAsync(context, token); contexts.Add(context);
            }
            await records.OrphansAsync(tx, token); await dispositions.OrphansAsync(tx, token);
            var page = contexts.Where(value => beforeId is null || value.Request!["_id"].AsInt64 < beforeId).Take(26).ToArray();
            var items = new BsonArray(page.Take(25).Select(value => { var item = ReadView(value); item.Remove("capacity"); return item; }));
            var scope = await orders.CaptureReceivingLocationAsync(actor, authority, tx, location, false, token);
            await FenceQueryAsync(actor, authority, tx, Permissions.ProcurementRead, token); await scope.Fence(token);
            foreach (var context in contexts) Current(context); scope.Current(); token.ThrowIfCancellationRequested();
            return new BsonDocument { { "location", R.Location(location) }, { "items", items }, { "nextClaimId", page.Length > 25 ? page[24].Request!["_id"] : BsonNull.Value } };
        }, ct);
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        var permission = B.Permission(operation); Key(key); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            _ = Query(actor, authority, permission); var row = await records.ForKeyAsync(tx, actor.UserId, operation, key, token);
            if (row is null) { await FenceQueryAsync(actor, authority, tx, permission, token); return null; }
            var context = await ClaimContextAsync(actor, authority, tx, row["claimId"].AsInt64, permission, token);
            var actual = operation == B.Prefix + "request" ? context.Request! : context.Decision ?? throw B.Broken(); SupplierClaimRecords.Equal(actual["_id"], row["_id"]);
            await FenceAsync(context, token); return B.Result(actual);
        }, ct);
    }
}
