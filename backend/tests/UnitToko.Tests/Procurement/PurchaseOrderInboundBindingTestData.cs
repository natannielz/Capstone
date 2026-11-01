using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Authorization;

namespace UnitToko.Tests.Procurement;

// Real existing PO, independent source configuration/policy and private unboxing
// producers. Every account, commercial document and scanner approval is synthetic
// local input. The actual reviewed pool remains INACTIVE throughout this fixture.
[SupportedOSPlatform("windows")]
internal sealed class PurchaseOrderInboundBindingTestData(ProcurementStockReceiptTestData data) : IAsyncDisposable
{
    internal ProcurementStockReceiptTestData Data { get; } = data;
    internal PurchaseOrderTestData Orders => Data.Inspection.Receiving.Orders;
    internal PurchaseOrderService Service => Orders.Service;
    internal ActorContext Maker = null!, Checker = null!;
    internal PurchaseOrderInput Input => Orders.Input(2);
    internal PurchaseOrderInboundPoolSelection Selection => new([new(Input.Lines[0].RequestSkuId, 2, Data.PoolId, 1)]);
    internal IMongoCollection<BsonDocument> Rows(string name) => Data.Rows(name);
    internal static async Task<PurchaseOrderInboundBindingTestData> CreateAsync(MongoFixture fixture) {
        // The existing PO stays a genuine legacy order (no inbound binding) beside the reviewed inactive pool.
        var data = await ProcurementStockReceiptTestData.CreateAsync(fixture, unboundOrder: true);
        try {
            var result = new PurchaseOrderInboundBindingTestData(data);
            result.Maker = await data.Base.AddActorAsync(8810, ProcurementRequestTestData.Scope,
                Permissions.ProcurementRead, Permissions.ProcurementPrepare, Permissions.ProcurementContainerReceive,
                Permissions.ProcurementInspect, Permissions.ProcurementInspectionEvidenceRead, Permissions.StockRead, Permissions.StockReceive);
            result.Checker = await data.Base.AddActorAsync(8811, ProcurementRequestTestData.Scope,
                Permissions.ProcurementRead, Permissions.ProcurementApprove, Permissions.StockRead);
            await PersonTestData.SeedDistinctPeopleAsync(data.Database, data.Clock, result.Maker.UserId, result.Checker.UserId);
            return result;
        } catch { await data.DisposeAsync(); throw; }
    }
    internal async Task<BsonDocument> ProposeAsync(string key) {
        var preview = await Service.PreviewInboundAsync(Maker, Input, Selection);
        return await Service.ProposeInboundAsync(Maker, Input, Selection, preview["binding"].AsString, PurchaseOrderTestData.Meta(key));
    }
    internal async Task<BsonDocument> ApproveAsync(BsonDocument proposal, string key) {
        var preview = await Service.PreviewDecisionAsync(Checker, proposal["proposalId"].AsInt64, PurchaseOrderDecision.Approve);
        return await Service.DecideAsync(Checker, proposal["proposalId"].AsInt64, 1, PurchaseOrderDecision.Approve, preview["binding"].AsString, PurchaseOrderTestData.Meta(key));
    }
    public ValueTask DisposeAsync() => Data.DisposeAsync();
    // Fresh real bound PO and private custody, never attaching a later receipt
    // to an old unbound order. Approvals/scanner/accounts stay synthetic local.
    internal async Task<ProcurementStockReceiptInput> CreateBoundReceiptInputAsync(string prefix) {
        var proposal = await ProposeAsync(prefix + ".po"); var approved = await ApproveAsync(proposal, prefix + ".approval");
        var receiving = Data.Inspection.Receiving;
        var noticeInput = receiving.NoticeInput("SYNTHETIC-" + prefix + "-NOTICE") with {
            OrderId = approved["orderId"].AsInt64, OrderApprovalId = approved["decisionId"].AsInt64 };
        var noticePreview = await receiving.Service.PreviewNoticeAsync(Maker, noticeInput);
        var notice = await receiving.Service.RecordNoticeAsync(Maker, noticeInput, noticePreview["binding"].AsString, ProcurementReceivingTestData.Meta(prefix + ".notice"));
        var intakeInput = receiving.IntakeInput("SYNTHETIC-" + prefix + "-CONTAINERS");
        var intakePreview = await receiving.Service.PreviewIntakeAsync(Maker, notice["noticeId"].AsInt64, 0, intakeInput);
        var intake = await receiving.Service.ReceiveAsync(Maker, notice["noticeId"].AsInt64, 0, intakeInput, intakePreview["binding"].AsString, ProcurementReceivingTestData.Meta(prefix + ".intake"));
        var inspection = Data.Inspection; inspection.Actor = Maker;
        var input = inspection.Input("SYNTHETIC-" + prefix + "-UNBOXING") with { NoticeId = notice["noticeId"].AsInt64, IntakeId = intake["receiptId"].AsInt64 };
        inspection.Draft = await inspection.DraftAsync(prefix + ".inspection-draft", input);
        await inspection.UploadAsync(); await inspection.ValidateAsync();
        var submitted = await inspection.SubmitAsync(await inspection.PreviewSubmitAsync(), prefix + ".inspection-submission");
        return Data.Input("SYNTHETIC-" + prefix + "-RECEIPT", 2) with {
            OrderId = approved["orderId"].AsInt64, OrderApprovalId = approved["decisionId"].AsInt64, InspectionId = submitted["inspectionId"].AsInt64 };
    }
}
