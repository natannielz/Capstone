using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Procurement.ProcurementReceivingBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementReceivingService
{
    private async Task<Context> PrepareNoticeAsync(ActorContext actor, AuthorizationSnapshot authority, IClientSessionHandle tx, ProcurementArrivalNoticeInput input, CancellationToken ct) {
        var context = await OrderAsync(actor, authority, tx, input.Location, input.OrderId, input.OrderVersion, input.OrderApprovalId, Permissions.ProcurementPrepare, ct);
        ProcurementReceivingRecords.NoticeFacts(input, context.Order!.Source, Now); await records.OrphansAsync(tx, ct);
        var key = B.NoticeKey(input, context.Order.Source["proposal"]["input"]["supplierId"].AsInt64);
        if (await Rows(B.Notices).Find(tx, new BsonDocument("sourceKey", key)).AnyAsync(ct)) throw B.Changed("procurement_notice_already_recorded");
        return context;
    }
    public async Task<BsonDocument> PreviewNoticeAsync(ActorContext actor, ProcurementArrivalNoticeInput input, CancellationToken ct = default) {
        input = ProcurementReceivingRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await PrepareNoticeAsync(actor, authority, tx, input, token);
            var result = new BsonDocument { { "input", B.Input(input) }, { "order", PurchaseOrderService.FundingOrderView(context.Order!.Source) },
                { "source", ProcurementRequestBson.SourceView(context.Order.Profiles) }, { "binding", B.NoticeBinding(actor.UserId, actor.GrantVersion, input, context.Order.Source, context.Order.Profiles) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); ProcurementReceivingRecords.NoticeFacts(input, context.Order.Source, Now); return result;
        }, ct);
    }
    public async Task<BsonDocument> RecordNoticeAsync(ActorContext actor, ProcurementArrivalNoticeInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        input = ProcurementReceivingRules.Copy(input); Binding(binding); Metadata(metadata); await StorageAsync(ct); var body = B.NoticeBody(input, binding);
        Context? context = null; BsonDocument? replay = null;
        return await authorization.ExecuteCommandAsync(actor, B.NoticeOperation, metadata, body, async (authority, tx, token) => {
            replay = await records.FindKeyAsync(tx, actor.UserId, B.NoticeOperation, metadata.IdempotencyKey, token);
            context = replay is null ? await PrepareNoticeAsync(actor, authority, tx, input, token) : await NoticeAsync(actor, authority, tx, replay["_id"].AsInt64, Permissions.ProcurementPrepare, token);
            return Command(context);
        }, async (tx, token) => {
            var current = context ?? throw B.Broken(); var order = current.Order ?? throw B.Broken();
            if (replay is not null) throw B.Broken();
            if (binding != B.NoticeBinding(actor.UserId, actor.GrantVersion, input, order.Source, order.Profiles)) throw B.Changed();
            await FenceAsync(current, token); ProcurementReceivingRecords.NoticeFacts(input, order.Source, Now);
            var row = await HeaderAsync(current, B.NoticeOperation, body, binding, metadata, token);
            var supplier = order.Source["proposal"]["input"]["supplierId"].AsInt64;
            row.AddRange(new BsonDocument { { "version", 1L }, { "orderId", input.OrderId }, { "orderVersion", input.OrderVersion }, { "orderApprovalId", input.OrderApprovalId },
                { "supplierId", supplier }, { "route", order.Source["request"]["input"]["route"] }, { "input", B.Input(input) }, { "source", order.Source.DeepClone() },
                { "sources", order.Profiles.DeepClone() }, { "sourceKey", B.NoticeKey(input, supplier) } }); row.Add("snapshotHash", B.Hash(row));
            await Rows(B.Notices).InsertOneAsync(tx, row, cancellationToken: token);
            await Rows(B.Heads).InsertOneAsync(tx, new BsonDocument { { "_id", row["_id"] }, { "schemaVersion", 1 }, { "noticeId", row["_id"] }, { "noticeHash", row["snapshotHash"] },
                { "version", 0L }, { "lastIntakeId", BsonNull.Value }, { "lastHash", BsonNull.Value }, { "matchedContainers", 0 }, { "excessContainers", 0L }, { "heldContainers", 0L },
                { "receivedContainers", 0L }, { "createdAt", row["recordedAt"] }, { "updatedAt", row["recordedAt"] }, { "fence", 0L } }, cancellationToken: token);
            await EffectsAsync(tx, row, token); Current(current); return B.Result(row);
        }, ct, async (authority, tx, token) => {
            var current = context ?? throw B.Broken(); var row = await records.FindKeyAsync(tx, actor.UserId, B.NoticeOperation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            current.Notice = await records.NoticeAsync(tx, row["_id"].AsInt64, token, current.Order); current.History = await records.HistoryAsync(tx, current.Notice, token);
            await FenceAsync(current, token);
            if (replay is null) await new ProcurementStockSourceCoverage(database, ids, clock).RecordAsync(tx, "notice", row, token);
        }, validateCurrent: () => { if (context is null) throw B.Broken(); Current(context); });
    }
    private static BsonDocument IntakeAfter(Context context, long revision, ProcurementContainerIntakeInput input, DateTimeOffset now) {
        var history = context.History ?? throw B.Broken(); var notice = context.Notice ?? throw B.Broken();
        if (revision != history.Capacity["version"].AsInt64) throw B.Changed(); ProcurementReceivingRecords.IntakeFacts(notice, input, now);
        if (history.Intakes.Any(row => row["input"]["intakeReference"] == input.IntakeReference)) throw B.Changed("procurement_intake_already_recorded");
        return B.After(history.Capacity, input);
    }
    public async Task<BsonDocument> PreviewIntakeAsync(ActorContext actor, long noticeId, long expectedRevision, ProcurementContainerIntakeInput input, CancellationToken ct = default) {
        Revision(noticeId, expectedRevision); input = ProcurementReceivingRules.Copy(input); await StorageAsync(ct);
        return await authorization.ReadAsync(actor, async (authority, tx, token) => {
            var context = await NoticeAsync(actor, authority, tx, noticeId, Permissions.ProcurementContainerReceive, token);
            var after = IntakeAfter(context, expectedRevision, input, Now);
            var result = new BsonDocument { { "notice", B.NoticeView(context.Notice!) }, { "capacity", context.History!.Capacity }, { "input", B.Input(input) }, { "after", after },
                { "binding", B.IntakeBinding(actor.UserId, actor.GrantVersion, context.Notice!, context.History.Capacity, input) }, { "observedAt", Now.UtcDateTime } };
            await FenceAsync(context, token); ProcurementReceivingRecords.IntakeFacts(context.Notice!, input, Now); return result;
        }, ct);
    }
    public async Task<BsonDocument> ReceiveAsync(ActorContext actor, long noticeId, long expectedRevision, ProcurementContainerIntakeInput input, string binding, AuthorizationCommandMetadata metadata, CancellationToken ct = default) {
        Revision(noticeId, expectedRevision); input = ProcurementReceivingRules.Copy(input); Binding(binding); Metadata(metadata); await StorageAsync(ct);
        var body = B.IntakeBody(noticeId, expectedRevision, input, binding); Context? context = null; BsonDocument? replay = null;
        return await authorization.ExecuteCommandAsync(actor, B.IntakeOperation, metadata, body, async (authority, tx, token) => {
            replay = await records.FindKeyAsync(tx, actor.UserId, B.IntakeOperation, metadata.IdempotencyKey, token);
            if (replay is not null && replay["noticeId"] != noticeId) throw B.Changed();
            context = await NoticeAsync(actor, authority, tx, noticeId, Permissions.ProcurementContainerReceive, token);
            if (replay is null) _ = IntakeAfter(context, expectedRevision, input, Now);
            return Command(context);
        }, async (tx, token) => {
            var current = context ?? throw B.Broken(); if (replay is not null) throw B.Broken();
            var history = current.History ?? throw B.Broken(); var notice = current.Notice ?? throw B.Broken();
            var after = IntakeAfter(current, expectedRevision, input, Now);
            if (binding != B.IntakeBinding(actor.UserId, actor.GrantVersion, notice, history.Capacity, input)) throw B.Changed();
            await FenceAsync(current, token); ProcurementReceivingRecords.IntakeFacts(notice, input, Now);
            var row = await HeaderAsync(current, B.IntakeOperation, body, binding, metadata, token);
            row.AddRange(new BsonDocument { { "noticeId", noticeId }, { "noticeHash", notice["snapshotHash"] }, { "orderId", notice["orderId"] }, { "version", after["version"] },
                { "previousIntakeId", history.Head["lastIntakeId"] }, { "previousHash", history.Head["lastHash"] }, { "input", B.Input(input) }, { "before", history.Capacity.DeepClone() }, { "after", after } });
            row.Add("snapshotHash", B.Hash(row)); await Rows(B.Intakes).InsertOneAsync(tx, row, cancellationToken: token);
            var head = history.Head.DeepClone().AsBsonDocument;
            foreach (var field in new[] { "version", "matchedContainers", "excessContainers", "heldContainers", "receivedContainers" }) head[field] = after[field];
            head["lastIntakeId"] = row["_id"]; head["lastHash"] = row["snapshotHash"]; head["updatedAt"] = row["recordedAt"];
            if ((await Rows(B.Heads).ReplaceOneAsync(tx, history.Head, head, cancellationToken: token)).ModifiedCount != 1) throw B.Changed();
            await EffectsAsync(tx, row, token); Current(current); return B.Result(row);
        }, ct, async (authority, tx, token) => {
            var current = context ?? throw B.Broken(); var row = await records.FindKeyAsync(tx, actor.UserId, B.IntakeOperation, metadata.IdempotencyKey, token) ?? throw B.Broken();
            current.History = await records.HistoryAsync(tx, current.Notice!, token);
            if (!current.History.Intakes.Any(item => item.Equals(row))) throw B.Broken(); await FenceAsync(current, token);
            if (replay is null) await new ProcurementStockSourceCoverage(database, ids, clock).RecordAsync(tx, "intake", row, token);
        }, validateCurrent: () => { if (context is null) throw B.Broken(); Current(context); });
    }
    public async Task<BsonDocument?> LookupAsync(ActorContext actor, string operation, string key, CancellationToken ct = default) {
        var permission = B.Permission(operation); ProcurementReceivingRules.Text(key, 128); await StorageAsync(ct);
        return await authorization.ReadAsync<BsonDocument?>(actor, async (authority, tx, token) => {
            _ = Query(actor, authority, Permissions.ProcurementRead); _ = Query(actor, authority, permission);
            var row = await records.FindKeyAsync(tx, actor.UserId, operation, key, token);
            if (row is null) { await FenceQueryAsync(actor, authority, tx, permission, token); return null; }
            var id = operation == B.NoticeOperation ? row["_id"].AsInt64 : row["noticeId"].AsInt64;
            var context = await NoticeAsync(actor, authority, tx, id, permission, token);
            if (operation == B.IntakeOperation && !context.History!.Intakes.Any(item => item.Equals(row))) throw B.Broken();
            await FenceAsync(context, token); return B.Result(row);
        }, ct);
    }
}
