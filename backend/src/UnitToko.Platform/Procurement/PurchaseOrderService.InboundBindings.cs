using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using I = UnitToko.Platform.Procurement.PurchaseOrderInboundBindingBson;
using B = UnitToko.Platform.Procurement.PurchaseOrderBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class PurchaseOrderService
{
    private StockSourceSetupService InboundSources => inboundSources;
    private PurchaseOrderInboundBindingRecords InboundRecords => new(database, clock);
    private static StockLocation InboundLocation(ProcurementLocation value) =>
        new(new(value.OrganizationId), new(value.ShopId), new(value.WarehouseId), new(value.ChannelId));
    private static BsonArray ReceivingSources(Context context) => new(context.InboundCaptures.Select(capture => capture.Source.DeepClone()));
    private async Task CaptureInboundAsync(Context context, PurchaseOrderInput input, PurchaseOrderInboundPoolSelection? selected, CancellationToken ct) {
        await new StockSourceTopologyFence(database).FenceAsync(context.Tx, input.Location.OrganizationId, ct);
        context.InboundSelection = selected is null ? null : PurchaseOrderInboundRules.Copy(input, selected);
        context.InboundCaptures.Clear();
        if (context.InboundSelection is null) return;
        foreach (var group in context.InboundSelection.Allocations.GroupBy(row => row.PoolId).OrderBy(group => group.Key))
            context.InboundCaptures.Add(await InboundSources.CaptureHeldReceiptAsync(context.Actor, context.Authority, context.Tx,
                InboundLocation(input.Location), group.Key, group.First().PoolRevision, Permissions.StockRead, ct));
        Current(context);
    }
    private void RequireInbound(Context context) {
        foreach (var capture in context.InboundCaptures) capture.RequireContext(InboundSources, context.Actor, context.Authority, context.Tx);
    }
    private async Task FenceInboundAsync(Context context, CancellationToken ct) {
        RequireInbound(context); foreach (var capture in context.InboundCaptures) await capture.FenceAsync(ct); RequireInbound(context);
    }
    private async Task WriteInboundSelectionAsync(Context context, BsonDocument proposal, CancellationToken ct) {
        if (context.InboundSelection is null) return;
        RequireInbound(context);
        var row = new BsonDocument { { "_id", proposal["_id"] }, { "schemaVersion", 1 }, { "proposalId", proposal["_id"] }, { "proposalHash", proposal["snapshotHash"] },
            { "allocations", I.Selection(B.Input(proposal["input"]), context.InboundSelection) }, { "receivingSources", ReceivingSources(context) } };
        foreach (var field in new[] { "location", "actorId", "actorGrantVersion", "bodyHash", "auditId", "requestKey", "recordedAt" }) row.Add(field, proposal[field].DeepClone());
        row.Add("snapshotHash", B.Hash(row));
        await Rows(I.Selections).InsertOneAsync(context.Tx, row, cancellationToken: ct);
    }
    private async Task WriteInboundDecisionAsync(Context context, BsonDocument decision, CancellationToken ct) {
        if (context.InboundRecord is null) return;
        RequireInbound(context);
        var row = new BsonDocument { { "_id", decision["_id"] }, { "schemaVersion", 1 }, { "proposalId", decision["proposalId"] },
            { "proposalHash", decision["proposalHash"] }, { "selectionHash", context.InboundRecord["snapshotHash"] }, { "decisionHash", decision["snapshotHash"] },
            { "receivingSources", ReceivingSources(context) } };
        foreach (var field in new[] { "location", "actorId", "actorGrantVersion", "bodyHash", "auditId", "requestKey", "recordedAt", "decision", "orderId" }) row.Add(field, decision[field].DeepClone());
        row.Add("snapshotHash", B.Hash(row)); await Rows(I.Decisions).InsertOneAsync(context.Tx, row, cancellationToken: ct);
    }
    internal async Task RequireInboundHistoryAsync(IMongoDatabase owner, IClientSessionHandle tx, BsonDocument source, CancellationToken ct) {
        if (!ReferenceEquals(owner.Client, database.Client) || owner.DatabaseNamespace != database.DatabaseNamespace) throw B.Broken();
        _ = await InboundRecords.ApprovedAsync(tx, source, ct);
    }
    // This uses the already authenticated existing FundingSource tuple. Pool IDs
    // come only from its genuine explicit immutable approval; current aliases are
    // used to authorize a NEW write, never to assign historical membership.
    internal async Task<IReadOnlyList<HeldReceiptPoolCapture>> CaptureApprovedInboundAsync(ActorContext actor, AuthorizationSnapshot authority,
        IClientSessionHandle tx, BsonDocument source, CancellationToken ct) {
        await new StockSourceTopologyFence(database).FenceAsync(tx, source["order"]["location"]["organizationId"].AsInt64, ct);
        var binding = await InboundRecords.ApprovedAsync(tx, source, ct);
        if (binding is null) return [];
        var result = new List<HeldReceiptPoolCapture>(); var input = B.Input(source["proposal"]["input"]);
        foreach (var group in I.Selection(input, binding.Value.Selection["allocations"]).Allocations.GroupBy(row => row.PoolId).OrderBy(group => group.Key))
            result.Add(await InboundSources.CaptureHeldReceiptAsync(actor, authority, tx, InboundLocation(input.Location), group.Key, null, Permissions.StockRead, ct));
        return result;
    }
    internal static void RequireApprovedInboundCurrent(IReadOnlyList<HeldReceiptPoolCapture> captures) {
        foreach (var capture in captures) capture.RequireCurrent();
    }
    internal static async Task FenceApprovedInboundAsync(IReadOnlyList<HeldReceiptPoolCapture> captures, CancellationToken ct) {
        RequireApprovedInboundCurrent(captures); foreach (var capture in captures) await capture.FenceAsync(ct); RequireApprovedInboundCurrent(captures);
    }
}
