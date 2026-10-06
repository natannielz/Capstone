using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.PurchaseOrderBson;
using I = UnitToko.Platform.Procurement.PurchaseOrderInboundBindingBson;

namespace UnitToko.Platform.Procurement;

// These records are part of the existing PO command graph. They cannot bless a raw
// order, substitute today's aliases, or attach an inbound destination to old history.
internal sealed class PurchaseOrderInboundBindingRecords(IMongoDatabase database, IClock clock, PurchaseOrderSourceReadCapture? capture = null)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    private async Task<BsonDocument?> OneAsync(IClientSessionHandle tx, string name, BsonDocument filter, CancellationToken ct) =>
        capture is null ? await Rows(name).Find(tx, filter).SingleOrDefaultAsync(ct) : await capture.OneAsync(database, tx, name, filter, ct);
    private void Context(IClientSessionHandle tx) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || database.Client is not MongoClient) throw B.Broken();
    }
    private async Task SourcesAsync(IClientSessionHandle tx, PurchaseOrderInput input, PurchaseOrderInboundPoolSelection selection,
        BsonArray sources, DateTimeOffset at, CancellationToken ct) {
        var expected = selection.Allocations.GroupBy(row => row.PoolId).OrderBy(group => group.Key).ToArray();
        if (sources.Count != expected.Length || at > clock.UtcNow) throw B.Broken();
        var owner = new StockSourceSetupService(database, new((MongoClient)database.Client), new(database), clock);
        if (capture is not null) owner = owner.CaptureHistory(capture);
        for (var index = 0; index < sources.Count; index++) {
            var source = sources[index].AsBsonDocument; Equal(source["poolId"], expected[index].Key);
            Equal(source["requestedLocation"], ProcurementRequestBson.Location(input.Location));
            Equal(source["configuration"]["source"]["poolRevision"], expected[index].First().PoolRevision);
            await owner.RequireHeldReceiptHistoryAsync(tx, source, at, ct);
            // Mutable pool state is checked fresh, never added to the immutable
            // source cache. A later legitimate fence may advance; it cannot regress.
            var pool = await Rows("inventoryPools").Find(tx, new BsonDocument("_id", expected[index].Key)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            InventoryGraph.RequirePool(pool);
            if (pool["revision"].AsInt64 < source["configuration"]["source"]["poolRevision"].AsInt64 || pool["fence"].AsInt64 < source["poolFence"].AsInt64) throw B.Broken();
        }
    }
    internal async Task<BsonDocument?> SelectionAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct) {
        Context(tx);
        var row = await OneAsync(tx, I.Selections, new BsonDocument("proposalId", proposal["_id"]), ct);
        if (row is null) return null;
        var valid = await OneAsync(tx, I.Selections, new BsonDocument { { "_id", row["_id"] }, { "$and", new BsonArray {
            PurchaseOrderInboundBindingSchemas.Validator(I.Selections) } } }, ct) ?? throw B.Broken(); Equal(row, valid);
        Equal(row["snapshotHash"], B.Hash(row)); Equal(row["_id"], proposal["_id"]); Equal(row["proposalHash"], proposal["snapshotHash"]);
        foreach (var field in new[] { "location", "actorId", "actorGrantVersion", "bodyHash", "auditId", "requestKey", "recordedAt" }) Equal(row[field], proposal[field]);
        var input = B.Input(proposal["input"]); var selection = I.Selection(input, row["allocations"]); Equal(row["allocations"], I.Selection(input, selection));
        await SourcesAsync(tx, input, selection, row["receivingSources"].AsBsonArray, new(row["recordedAt"].ToUniversalTime()), ct);
        return row;
    }
    internal async Task<BsonDocument?> DecisionAsync(IClientSessionHandle tx, BsonDocument proposal, BsonDocument decision,
        BsonDocument? selection, CancellationToken ct) {
        Context(tx); var row = await OneAsync(tx, I.Decisions, new BsonDocument("proposalId", proposal["_id"]), ct);
        if (selection is null) { if (row is not null) throw B.Broken(); return null; }
        if (row is null) throw B.Broken();
        Equal(row, await OneAsync(tx, I.Decisions, new BsonDocument { { "_id", row["_id"] }, { "$and", new BsonArray {
            PurchaseOrderInboundBindingSchemas.Validator(I.Decisions) } } }, ct) ?? throw B.Broken());
        Equal(row["snapshotHash"], B.Hash(row)); Equal(row["_id"], decision["_id"]); Equal(row["selectionHash"], selection["snapshotHash"]);
        Equal(row["proposalHash"], proposal["snapshotHash"]); Equal(row["decisionHash"], decision["snapshotHash"]);
        foreach (var field in new[] { "proposalId", "location", "actorId", "actorGrantVersion", "bodyHash", "auditId", "requestKey", "recordedAt", "decision", "orderId" }) Equal(row[field], decision[field]);
        if (decision["decision"] == "approve") {
            var input = B.Input(proposal["input"]);
            await SourcesAsync(tx, input, I.Selection(input, selection["allocations"]), row["receivingSources"].AsBsonArray, new(row["recordedAt"].ToUniversalTime()), ct);
            // The approver reviews the same intended pool configuration/policy. Any
            // revision/policy change requires a fresh explicit proposal; fences alone may advance.
            Equal(I.StableSources(row["receivingSources"].AsBsonArray), I.StableSources(selection["receivingSources"].AsBsonArray));
        } else if (row["receivingSources"].AsBsonArray.Count != 0) throw B.Broken();
        return row;
    }
    internal async Task<(BsonDocument Selection, BsonDocument Decision)?> ApprovedAsync(IClientSessionHandle tx, BsonDocument source, CancellationToken ct) {
        Context(tx);
        // Caller source must be the actual immutable persisted PO tuple. Semantic
        // ancestry/command receipts remain authenticated by the owning PO reader.
        foreach (var (name, field) in new[] { (B.Orders, "order"), (B.Proposals, "proposal"), (B.Decisions, "approval") })
            Equal(await OneAsync(tx, name, new BsonDocument("_id", source[field]["_id"]), ct) ?? throw B.Broken(), source[field]);
        var proposal = source["proposal"].AsBsonDocument; var selection = await SelectionAsync(tx, proposal, ct);
        var decision = await DecisionAsync(tx, proposal, source["approval"].AsBsonDocument, selection, ct);
        if (selection is null) return null;
        if (decision is null || decision["decision"] != "approve" || decision["orderId"] != source["order"]["_id"]) throw B.Broken();
        return (selection, decision);
    }
}
