using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using B = UnitToko.Platform.Procurement.PurchaseOrderBson;
using I = UnitToko.Platform.Procurement.PurchaseOrderInboundBindingBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;
using S = UnitToko.Platform.Procurement.ProcurementStockReceiptBson;

namespace UnitToko.Platform.Procurement;

internal sealed record PurchaseOrderRemainingSupply(long OrderId, long ApprovedPackages, long ReceivedPackages, long RemainingPackages);

internal sealed class PurchaseOrderRemainingSupplyCapture(bool complete, ImmutableArray<PurchaseOrderRemainingSupply> lines,
    BsonDocument provenance, Action current, Func<CancellationToken, Task> fence)
{
    private readonly BsonDocument snapshot = provenance.DeepClone().AsBsonDocument;
    internal bool CoverageComplete { get; } = complete;
    internal ImmutableArray<PurchaseOrderRemainingSupply> Lines { get; } = lines;
    internal BsonDocument Provenance => snapshot.DeepClone().AsBsonDocument;
    internal void RequireCurrent() => current();
    internal async Task FenceAsync(CancellationToken ct) { current(); await fence(ct); current(); ct.ThrowIfCancellationRequested(); }
}

public sealed partial class PurchaseOrderService
{
    private const int ReplenishmentMaximumOrders = 1000, ReplenishmentMaximumRows = 4096;

    internal async Task<PurchaseOrderRemainingSupplyCapture> CaptureRemainingForReplenishmentAsync(ActorContext actor,
        AuthorizationSnapshot authority, IClientSessionHandle tx, StockLocation location, long poolId, long skuId,
        long definitionId, CancellationToken ct)
    {
        new InventoryRepository(database).RequireSession(tx); location.Validate();
        if (poolId <= 0 || skuId <= 0 || definitionId <= 0 || database.Client is not MongoClient client) throw B.Broken();
        var unit = new MongoUnitOfWork(client);
        var receiving = new ProcurementReceivingService(database, unit, ids, clock, catalog, this, requests);
        var receiptService = new ProcurementStockReceiptService(database, unit, ids, clock, catalog, this, receiving, InboundSources);
        async Task RequireStorage(CancellationToken token) {
            await StorageAsync(token);
            await receiptService.RequireStockSourcePredecessorStorageAsync(token);
        }
        await RequireStorage(ct);
        var key = new ProcurementLocation(location.OrganizationId.Value, location.ShopId.Value, location.WarehouseId.Value, location.ChannelId.Value);
        var context = await ContextAsync(actor, authority, tx, key, Permissions.StockRead, ct);
        var attempt = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; var at = clock.UtcNow;
        void Require() {
            new InventoryRepository(database).RequireSession(tx);
            if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != attempt || clock.UtcNow < at) throw B.Broken();
            Current(context);
        }
        await FenceAsync(context, ct);
        var filter = new BsonDocument("location", R.Location(key));
        var scope = new Dictionary<string, IReadOnlyList<BsonDocument>>(StringComparer.Ordinal);
        foreach (var table in new[] { B.Proposals, B.Decisions, B.Orders, S.Receipts })
            scope[table] = await Rows(table).Find(tx, filter).Sort(new BsonDocument("_id", 1))
                .Limit((table == B.Orders ? ReplenishmentMaximumOrders : ReplenishmentMaximumRows) + 1).ToListAsync(ct);
        var provenance = new BsonDocument { { "location", R.Location(key) }, { "poolId", poolId }, { "skuId", skuId }, { "definitionId", definitionId } };
        if (scope.Any(pair => pair.Value.Count > (pair.Key == B.Orders ? ReplenishmentMaximumOrders : ReplenishmentMaximumRows)) ||
            scope[B.Proposals].Count + scope[B.Decisions].Count > ReplenishmentMaximumRows) {
            provenance.Add("coverageComplete", false); provenance.Add("coverageReason", "history_limit");
            provenance.Add("ordersObserved", scope[B.Orders].Count); provenance.Add("receiptsObserved", scope[S.Receipts].Count);
            // An incomplete bounded read supplies no quantity. Its exact observed frontier
            // is still bound to this caller transaction and fresh authority.
            return new(false, [], provenance, Require, async token => {
                await FenceAsync(context, token);
                await RequireStorage(token);
                foreach (var pair in scope) {
                    var fresh = await Rows(pair.Key).Find(tx, filter).Sort(new BsonDocument("_id", 1))
                        .Limit((pair.Key == B.Orders ? ReplenishmentMaximumOrders : ReplenishmentMaximumRows) + 1).ToListAsync(token);
                    if (new BsonArray(fresh) != new BsonArray(pair.Value)) throw B.Changed();
                }
                Require();
            });
        }

        var membership = new PurchaseOrderSourceReadCapture();
        _ = await membership.AnyAsync(database, tx, B.Orders, filter, ct); // Bind even a genuinely empty scope.
        foreach (var pair in scope) membership.Record(pair.Key, filter, pair.Value);
        var historical = new PurchaseOrderRecords(database, catalog, clock, membership);
        var approved = new Dictionary<long, BsonDocument>(); var decisions = 0;
        foreach (var raw in scope[B.Proposals]) {
            var proposal = await historical.ProposalAsync(tx, raw["_id"].AsInt64, ct);
            if (proposal != raw) throw B.Broken();
            var decision = await historical.DecisionForAsync(tx, proposal, ct);
            if (decision is null) continue;
            decisions++;
            if (decision["decision"] != "approve") continue;
            var source = await FundingSourceCoreAsync(database, tx, decision["orderId"].AsInt64,
                decision["orderVersion"].AsInt64, decision["_id"].AsInt64, ct, membership);
            if (!approved.TryAdd(source["order"]["_id"].AsInt64, source)) throw B.Broken();
        }
        if (decisions != scope[B.Decisions].Count || approved.Count != scope[B.Orders].Count) throw B.Broken();
        async Task CountEffects(string table, BsonDocument selected, long expected) {
            var count = await Rows(table).CountDocumentsAsync(tx, selected, cancellationToken: ct);
            if (count != expected) throw B.Broken(); membership.RecordCount(table, selected, count);
        }
        foreach (var table in new[] { "auditEvents", "outboxEvents" }) {
            await CountEffects(table, new BsonDocument { { "location", R.Location(key) },
                { "kind", new BsonRegularExpression(@"^procurement\.purchase\.order\.") } }, scope[B.Proposals].Count + decisions);
            await CountEffects(table, new BsonDocument { { "location", R.Location(key) }, { "kind", S.Operation } }, scope[S.Receipts].Count);
        }

        var receiptOwner = new ProcurementStockReceiptRecords(database, unit, clock, catalog, this, InboundSources);
        await receiptOwner.OrphansAsync(tx, ct);
        var origins = new List<HeldReceiptOriginProof>();
        foreach (var raw in scope[S.Receipts]) {
            // Incoming remainder needs the genuine physical receipt genesis, not
            // today's remaining stock. Dispatching received stock cannot reopen a PO.
            var origin = await HeldReceiptOriginProof.CaptureAsync(database, unit, clock, catalog, this, InboundSources, tx, raw["_id"].AsInt64, ct);
            var receipt = origin.Receipt;
            if (receipt != raw || !approved.TryGetValue(receipt["orderId"].AsInt64, out var source) || receipt["source"] != source) throw B.Broken();
            origins.Add(origin);
        }
        var lines = ImmutableArray.CreateBuilder<PurchaseOrderRemainingSupply>(); var observations = new BsonArray(); var complete = true;
        var inbound = new PurchaseOrderInboundBindingRecords(database, clock, membership);
        foreach (var (orderId, source) in approved.OrderBy(pair => pair.Key)) {
            var input = B.Input(source["proposal"]["input"]);
            var requested = input.Lines.SingleOrDefault(line => line.RequestSkuId == skuId);
            if (requested is null) continue;
            var binding = await inbound.ApprovedAsync(tx, source, ct);
            if (requested.DefinitionId != definitionId || binding is null) {
                complete = false;
                observations.Add(new BsonDocument { { "orderId", orderId }, { "state", binding is null ? "inbound_unbound" : "definition_mismatch" } });
                continue;
            }
            var allocation = I.Selection(input, binding.Value.Selection["allocations"]).Allocations
                .SingleOrDefault(value => value.RequestSkuId == skuId && value.PoolId == poolId);
            if (allocation is null) continue;
            long received = 0; var receipts = new BsonArray();
            foreach (var origin in origins.Where(value => value.Receipt["orderId"] == orderId)) {
                var receipt = origin.Receipt; var lot = InventoryGraph.ReadLot(InventoryHeldReceiptWriter.Lot(receipt));
                if (receipt["poolId"] != poolId || lot.SkuId != skuId) continue;
                if (lot.DefinitionId != definitionId) throw B.Broken();
                received = checked(received + receipt["input"]["packages"].AsInt32);
                receipts.Add(new BsonDocument { { "receiptId", origin.ReceiptId }, { "receiptHash", receipt["snapshotHash"] }, { "packages", receipt["input"]["packages"] } });
            }
            if (received > allocation.Packages) throw B.Broken();
            var remaining = checked(allocation.Packages - received);
            lines.Add(new(orderId, allocation.Packages, received, remaining));
            observations.Add(new BsonDocument { { "orderId", orderId }, { "orderHash", source["order"]["snapshotHash"] },
                { "approvalId", source["approval"]["_id"] }, { "approvalHash", source["approval"]["snapshotHash"] },
                { "inboundSelectionHash", binding.Value.Selection["snapshotHash"] }, { "inboundDecisionHash", binding.Value.Decision["snapshotHash"] },
                { "approvedPackages", allocation.Packages }, { "receivedPackages", received }, { "remainingPackages", remaining }, { "receipts", receipts } });
        }
        provenance.Add("coverageComplete", complete); provenance.Add("orders", observations);
        membership.Seal();
        var result = new PurchaseOrderRemainingSupplyCapture(complete, lines.ToImmutable(), provenance, Require, async token => {
            await FenceAsync(context, token);
            await RequireStorage(token);
            foreach (var origin in origins) await origin.RequireAsync(database, tx, token);
            await receiptOwner.OrphansAsync(tx, token);
            await membership.RequireAsync(database, tx, token);
            Require();
        });
        await result.FenceAsync(ct); return result;
    }
}
