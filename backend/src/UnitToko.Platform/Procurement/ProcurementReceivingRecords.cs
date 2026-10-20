using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementReceivingBson;
using P = UnitToko.Platform.Procurement.PurchaseOrderBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal sealed record ProcurementContainerHistory(BsonDocument Head, BsonDocument Capacity, IReadOnlyList<BsonDocument> Intakes);
internal sealed class ProcurementReceivingRecords(IMongoDatabase database, IClock clock, CatalogAuthoringService catalog, PurchaseOrderService orders,
    ProcurementQualityProofGraph? qualityGraph = null)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) =>
        await TransactionReadMemo.RowAsync(Rows(name), tx, "receiving-schema:" + id,
            () => new BsonDocument { { "_id", id }, { "$and", new BsonArray { ProcurementReceivingSchemas.Validator(name) } } }, ct) ?? throw B.Broken();
    internal static void NoticeFacts(ProcurementArrivalNoticeInput input, BsonDocument source, DateTimeOffset now) {
        Equal(source["order"]["_id"], input.OrderId); Equal(source["order"]["version"], input.OrderVersion); Equal(source["order"]["approvalId"], input.OrderApprovalId);
        Equal(source["order"]["location"], R.Location(input.Location));
        ProcurementReceivingRules.Route(input, R.Route(source["request"]["input"]["route"].AsString));
        if (input.NoticeDate < InvoiceBson.Date(source["proposal"]["input"]["businessDate"]) || input.NoticeDate > AccountingCalendar.BusinessDate(now) ||
            source["order"]["recordedAt"].ToUniversalTime() > now.UtcDateTime) throw B.Changed();
    }
    internal static void IntakeFacts(BsonDocument notice, ProcurementContainerIntakeInput input, DateTimeOffset now) {
        ProcurementReceivingRules.Copy(input);
        if (input.ReceivedDate < B.NoticeInput(notice["input"]).NoticeDate || input.ReceivedDate > AccountingCalendar.BusinessDate(now) ||
            notice["recordedAt"].ToUniversalTime() > now.UtcDateTime) throw B.Changed();
    }
    private static void ActorProof(BsonDocument row) {
        var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray();
        if (people.Length != 1 || people[0] is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 } || people[0].UserId != row["actorId"].AsInt64) throw B.Broken();
    }
    internal async Task<BsonDocument> NoticeAsync(IClientSessionHandle tx, long id, CancellationToken ct, PurchaseOrderReceivingCapture? captured = null) {
        var row = await RowAsync(tx, B.Notices, id, ct); Equal(row["snapshotHash"], B.Hash(row)); var input = B.NoticeInput(row["input"]); Equal(row["input"], B.Input(input));
        var at = new DateTimeOffset(row["recordedAt"].ToUniversalTime()); if (at > clock.UtcNow) throw B.Broken(); ActorProof(row);
        BsonDocument source;
        if (captured is null) source = await orders.FundingSourceAsync(database, tx, input.OrderId, input.OrderVersion, input.OrderApprovalId, ct, qualityGraph);
        else { captured.RequireCurrent(); source = captured.Source; }
        Equal(row["source"], source); NoticeFacts(input, source, at);
        foreach (var field in new[] { "orderId", "orderVersion", "orderApprovalId", "location" }) Equal(row[field], row["input"][field]);
        Equal(row["supplierId"], source["proposal"]["input"]["supplierId"]); Equal(row["route"], source["request"]["input"]["route"]);
        Equal(row["sourceKey"], B.NoticeKey(input, row["supplierId"].AsInt64));
        await new ProcurementRequestRecords(database, catalog).SourcesAsync(tx, input.Location, row["sources"].AsBsonDocument, ct);
        Equal(row["binding"], B.NoticeBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, input, source, row["sources"].AsBsonDocument));
        await EffectsAsync(tx, row, B.NoticeBody(input, row["binding"].AsString), ct); return row;
    }
    internal async Task<ProcurementContainerHistory> HistoryAsync(IClientSessionHandle tx, BsonDocument notice, CancellationToken ct) {
        var head = await RowAsync(tx, B.Heads, notice["_id"].AsInt64, ct); Equal(head["noticeId"], notice["_id"]); Equal(head["noticeHash"], notice["snapshotHash"]);
        var capacity = B.Capacity(notice["input"]["declaredContainers"].AsInt32); BsonValue previousId = BsonNull.Value, previousHash = BsonNull.Value;
        var at = notice["recordedAt"]; var receipts = new List<BsonDocument>();
        // ponytail: reconstruct complete immutable custody proof before the bounded page; a
        // measured high-volume owner can add indexed reconciliation proofs without dropping it.
        using var cursor = await Rows(B.Intakes).Find(tx, new BsonDocument("noticeId", notice["_id"])).Sort(new BsonDocument("version", 1)).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var raw in cursor.Current) {
            var row = await RowAsync(tx, B.Intakes, raw["_id"].AsInt64, ct); Equal(row["snapshotHash"], B.Hash(row)); ActorProof(row);
            foreach (var field in new[] { "location", "orderId" }) Equal(row[field], notice[field]); Equal(row["noticeHash"], notice["snapshotHash"]);
            Equal(row["previousIntakeId"], previousId); Equal(row["previousHash"], previousHash); Equal(row["before"], capacity);
            if (row["recordedAt"].CompareTo(at) < 0 || row["recordedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw B.Broken();
            var input = B.IntakeInput(row["input"]); Equal(row["input"], B.Input(input)); IntakeFacts(notice, input, new(row["recordedAt"].ToUniversalTime()));
            Equal(row["binding"], B.IntakeBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, notice, capacity, input));
            await EffectsAsync(tx, row, B.IntakeBody(notice["_id"].AsInt64, capacity["version"].AsInt64, input, row["binding"].AsString), ct);
            capacity = B.After(capacity, input); Equal(row["after"], capacity); Equal(row["version"], capacity["version"]);
            previousId = row["_id"]; previousHash = row["snapshotHash"]; at = row["recordedAt"]; receipts.Add(row);
        }
        Equal(head["version"], capacity["version"]); Equal(head["lastIntakeId"], previousId); Equal(head["lastHash"], previousHash);
        Equal(head["createdAt"], notice["recordedAt"]); Equal(head["updatedAt"], at);
        foreach (var field in new[] { "matchedContainers", "excessContainers", "heldContainers", "receivedContainers" }) Equal(head[field], capacity[field]);
        await OrphansAsync(tx, ct); return new(head, capacity, receipts);
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, object body, CancellationToken ct) {
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, row["reason"].AsString));
        Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (receipt.ElementCount != 8 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.EffectKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var field in new[] { "actorId", "requestKey", "location" }) Equal(effect[field], row[field]);
        Equal(effect["kind"], row["operation"]); Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]);
    }
    internal async Task<BsonDocument?> FindKeyAsync(IClientSessionHandle tx, long actor, string operation, string key, CancellationToken ct) {
        _ = B.Permission(operation); var row = await Rows(operation == B.NoticeOperation ? B.Notices : B.Intakes).Find(tx,
            new BsonDocument { { "actorId", actor }, { "operation", operation }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is null) foreach (var (name, field, keyField) in new[] { ("idempotencyRecords", "operation", "key"), ("auditEvents", "kind", "requestKey"), ("outboxEvents", "kind", "requestKey") })
            if (await Rows(name).Find(tx, new BsonDocument { { "actorId", actor }, { field, operation }, { keyField, key } }).AnyAsync(ct)) throw B.Broken();
        return row;
    }
    internal async Task OrphansAsync(IClientSessionHandle tx, CancellationToken ct) {
        foreach (var (owner, operation, resultField) in new[] { (B.Notices, B.NoticeOperation, "noticeId"), (B.Intakes, B.IntakeOperation, "receiptId") })
        foreach (var (table, field, local) in new[] { ("auditEvents", "kind", "result." + resultField), ("outboxEvents", "kind", "payload." + resultField), ("idempotencyRecords", "operation", "result." + resultField) }) {
            var rows = await Rows(table).Aggregate<BsonDocument>(tx, new BsonDocument[] {
                new("$match", new BsonDocument(field, operation)), new("$lookup", new BsonDocument { { "from", owner }, { "localField", local }, { "foreignField", "_id" }, { "as", "owner" } }),
                new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct);
            if (rows.Count > 0) throw B.Broken();
        }
        foreach (var name in new[] { B.Heads, B.Intakes }) {
            var rows = await Rows(name).Aggregate<BsonDocument>(tx, new BsonDocument[] {
                new("$lookup", new BsonDocument { { "from", B.Notices }, { "localField", "noticeId" }, { "foreignField", "_id" }, { "as", "notice" } }),
                new("$match", new BsonDocument("notice", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct);
            if (rows.Count > 0) throw B.Broken();
        }
    }
}
