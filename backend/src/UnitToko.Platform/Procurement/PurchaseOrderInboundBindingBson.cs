using MongoDB.Bson;
using UnitToko.Modules.Procurement;
using B = UnitToko.Platform.Procurement.PurchaseOrderBson;

namespace UnitToko.Platform.Procurement;

internal static class PurchaseOrderInboundBindingBson
{
    internal const string Selections = "purchaseOrderInboundSelections", Decisions = "purchaseOrderInboundDecisions";
    internal static BsonArray Selection(PurchaseOrderInput input, PurchaseOrderInboundPoolSelection selection) =>
        new(PurchaseOrderInboundRules.Copy(input, selection).Allocations.Select(row => new BsonDocument {
            { "requestSkuId", row.RequestSkuId }, { "packages", row.Packages }, { "poolId", row.PoolId }, { "poolRevision", row.PoolRevision } }));
    internal static PurchaseOrderInboundPoolSelection Selection(PurchaseOrderInput input, BsonValue value) =>
        PurchaseOrderInboundRules.Copy(input, new([.. value.AsBsonArray.Select(row => new PurchaseOrderInboundAllocation(
            row["requestSkuId"].AsInt64, row["packages"].AsInt32, row["poolId"].AsInt64, row["poolRevision"].AsInt64))]));
    internal static BsonArray StableSources(BsonArray sources) => new(sources.Select(value => {
        var row = value.DeepClone().AsBsonDocument; row.Remove("poolFence"); return row;
    }));
    internal static object Body(PurchaseOrderInput input, string binding, PurchaseOrderInboundPoolSelection? selection) =>
        selection is null ? B.ProposeBody(input, binding) : new { input = PurchaseOrderRules.Copy(input), binding,
            inboundPools = PurchaseOrderInboundRules.Copy(input, selection) };
    internal static string Binding(long actor, long grantVersion, PurchaseOrderInput input, BsonDocument request, BsonDocument decision,
        BsonDocument sources, BsonDocument supplier, BsonArray capacity, PurchaseOrderInboundPoolSelection? selection, BsonArray receivingSources) =>
        selection is null ? B.Binding(actor, grantVersion, input, request, decision, sources, supplier, capacity) : B.Hash(new() {
            { "purchaseOrderBinding", B.Binding(actor, grantVersion, input, request, decision, sources, supplier, capacity) },
            { "inboundAllocations", Selection(input, selection) }, { "receivingSources", StableSources(receivingSources) } });
    internal static string DecisionBinding(long actor, long grantVersion, BsonDocument proposal, PurchaseOrderDecision decision,
        BsonDocument sources, BsonDocument? supplier, BsonArray capacity, BsonDocument? selection, BsonArray receivingSources) =>
        selection is null ? B.DecisionBinding(actor, grantVersion, proposal, decision, sources, supplier, capacity) : B.Hash(new() {
            { "purchaseOrderBinding", B.DecisionBinding(actor, grantVersion, proposal, decision, sources, supplier, capacity) },
            { "selectionHash", selection["snapshotHash"] }, { "receivingSources", StableSources(receivingSources) } });
    internal static BsonDocument Public(BsonDocument selection, BsonDocument? decision) => new() {
        { "selectionId", selection["_id"] }, { "selectionHash", selection["snapshotHash"] }, { "allocations", selection["allocations"].DeepClone() },
        { "receivingPools", new BsonArray(selection["receivingSources"].AsBsonArray.Select(value =>
            UnitToko.Platform.Inventory.StockSourceSetupService.HeldReceiptPoolView(value.AsBsonDocument))) },
        { "decisionId", decision is null ? BsonNull.Value : decision["_id"] },
        { "state", decision is null ? "proposed" : decision["decision"].AsString },
        { "unplannedPackagesBound", false }, { "stockReserved", false }, { "physicalStockReleased", false } };
}
