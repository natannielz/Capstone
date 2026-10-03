using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Documents;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementInspectionBson;
using C = UnitToko.Platform.Procurement.ProcurementReceivingBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

internal sealed record ProcurementInspectionHistory(BsonDocument Source, BsonDocument CustodyHead, BsonDocument? Head,
    BsonDocument Capacity, IReadOnlyList<BsonDocument> Drafts, IReadOnlyList<BsonDocument> Submissions);
internal sealed class ProcurementInspectionRecords(IMongoDatabase database, MongoUnitOfWork unit, IClock clock,
    CatalogAuthoringService catalog, PurchaseOrderService orders)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string table, long id, CancellationToken ct) =>
        await Rows(table).Find(tx, new BsonDocument { { "_id", id }, { "$and", new BsonArray { ProcurementInspectionSchemas.Validator(table) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    internal static void Review(ProcurementInspectionInput input, BsonDocument source, DateTimeOffset at) {
        input = ProcurementInspectionRules.Copy(input); var notice = source["notice"]; var intake = source["intake"];
        Equal(input.NoticeId, notice["_id"]); Equal(input.IntakeId, intake["_id"]); Equal(R.Location(input.Location), notice["location"]);
        if (input.InspectionDate < InvoiceBson.Date(intake["input"]["receivedDate"]) || input.InspectionDate > AccountingCalendar.BusinessDate(at) || intake["recordedAt"].ToUniversalTime() > at.UtcDateTime) throw B.Changed();
        var lines = notice["source"]["proposal"]["input"]["lines"].AsBsonArray;
        foreach (var line in input.Lines) {
            var actual = lines.SingleOrDefault(value => value["requestSkuId"] == line.RequestSkuId) ?? throw B.Changed();
            Equal(actual["definitionId"], line.DefinitionId); Equal(actual["definitionVersion"], line.DefinitionVersion);
        }
    }
    internal async Task<ProcurementInspectionHistory> HistoryAsync(IClientSessionHandle tx, long noticeId, long intakeId, CancellationToken ct) {
        var receiving = new ProcurementReceivingRecords(database, clock, catalog, orders);
        var notice = await receiving.NoticeAsync(tx, noticeId, ct); var custody = await receiving.HistoryAsync(tx, notice, ct);
        var intake = custody.Intakes.SingleOrDefault(row => row["_id"] == intakeId) ?? throw B.Broken();
        var source = new BsonDocument { { "notice", notice }, { "intake", intake } };
        var received = checked((long)intake["input"]["matchedContainers"].AsInt32 + intake["input"]["excessContainers"].AsInt32);
        var drafts = await Rows(B.Drafts).Find(tx, new BsonDocument("intakeId", intakeId)).Sort(new BsonDocument("_id", 1)).ToListAsync(ct);
        var submissions = await Rows(B.Submissions).Find(tx, new BsonDocument("intakeId", intakeId)).Sort(new BsonDocument("sequence", 1)).ToListAsync(ct);
        var rawHead = await Rows(B.Heads).Find(tx, new BsonDocument("_id", intakeId)).SingleOrDefaultAsync(ct);
        BsonDocument? head = rawHead is null ? null : await RowAsync(tx, B.Heads, intakeId, ct);
        if (head is null && (drafts.Count != 0 || submissions.Count != 0) || head is not null && drafts.Count == 0) throw B.Broken();
        var capacity = B.Capacity(received); BsonValue previousId = BsonNull.Value, previousHash = BsonNull.Value;
        var capacities = new Dictionary<long, BsonDocument> { [0] = capacity }; var times = new Dictionary<long, DateTime> { [0] = intake["recordedAt"].ToUniversalTime() };
        var at = head is null ? intake["recordedAt"] : head["createdAt"];
        // ponytail: complete intake-local claims are reconstructed before pagination; a future measured reconciliation proof must retain missing-owner/effect detection.
        foreach (var raw in submissions) {
            var row = await RowAsync(tx, B.Submissions, raw["_id"].AsInt64, ct);
            var draft = drafts.SingleOrDefault(value => value["_id"] == row["draftId"]) ?? throw B.Broken();
            await SubmissionAsync(tx, row, draft, source, false, ct);
            Equal(row["before"], capacity); Equal(row["previousInspectionId"], previousId); Equal(row["previousHash"], previousHash);
            if (row["recordedAt"].CompareTo(at) < 0) throw B.Broken();
            capacity = B.After(capacity, row["input"]["inspectedContainers"].AsInt32); Equal(row["after"], capacity); Equal(row["sequence"], capacity["version"]);
            capacities.Add(row["sequence"].AsInt64, capacity); times.Add(row["sequence"].AsInt64, row["recordedAt"].ToUniversalTime());
            previousId = row["_id"]; previousHash = row["snapshotHash"]; at = row["recordedAt"];
        }
        foreach (var raw in drafts) {
            var row = await RowAsync(tx, B.Drafts, raw["_id"].AsInt64, ct); await DraftAsync(tx, row, source, ct);
            if (!capacities.TryGetValue(row["capacity"]["version"].AsInt64, out var frozen)) throw B.Broken(); Equal(frozen, row["capacity"]);
            if (times[row["capacity"]["version"].AsInt64] > row["recordedAt"].ToUniversalTime()) throw B.Broken();
            var claim = submissions.SingleOrDefault(value => value["draftId"] == row["_id"]);
            Equal(row["submittedInspectionId"], claim is null ? BsonNull.Value : claim["_id"]);
        }
        if (head is not null) {
            Equal(head["intakeId"], intakeId); Equal(head["intakeHash"], intake["snapshotHash"]); Equal(head["noticeId"], noticeId); Equal(head["location"], notice["location"]);
            Equal(head["createdAt"], drafts.MinBy(row => row["recordedAt"])!["recordedAt"]); Equal(head["updatedAt"], at);
            Equal(head["lastInspectionId"], previousId); Equal(head["lastHash"], previousHash);
            foreach (var field in new[] { "version", "receivedContainers", "inspectedContainers", "remainingContainers" }) Equal(head[field], capacity[field]);
        }
        await OrphansAsync(tx, ct); return new(source, custody.Head, head, capacity, drafts, submissions);
    }
    private static void Common(BsonDocument row, BsonDocument source) {
        Equal(row["source"], source); Equal(row["noticeId"], source["notice"]["_id"]); Equal(row["intakeId"], source["intake"]["_id"]); Equal(row["location"], source["notice"]["location"]);
        var input = B.Input(row["input"]); Equal(row["input"], B.Input(input)); Review(input, source, new(row["recordedAt"].ToUniversalTime()));
        var people = row["people"].AsBsonArray.Select(R.Person).ToArray();
        if (people.Length != 1 || people[0] is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 } || people[0].UserId != row["actorId"].AsInt64) throw B.Broken();
    }
    internal async Task DraftAsync(IClientSessionHandle tx, BsonDocument row, BsonDocument source, CancellationToken ct) {
        Common(row, source); Equal(row["snapshotHash"], B.DraftHash(row)); Equal(row["draftId"], row["_id"]);
        if (row["recordedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw B.Broken();
        var input = B.Input(row["input"]); _ = B.After(row["capacity"].AsBsonDocument, input.InspectedContainers);
        Equal(row["binding"], B.Binding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, input, source, row["capacity"].AsBsonDocument));
        await EffectsAsync(tx, row, B.DraftBody(input, row["binding"].AsString), false, ct);
    }
    internal async Task SubmissionAsync(IClientSessionHandle tx, BsonDocument row, BsonDocument draft, BsonDocument source, bool pending, CancellationToken ct, bool completedReceipt = false) {
        Common(row, source); Equal(row["snapshotHash"], B.Hash(row)); Equal(draft["snapshotHash"], B.DraftHash(draft));
        Equal(row["draftHash"], draft["snapshotHash"]); Equal(row["draftId"], draft["_id"]); Equal(row["actorId"], draft["actorId"]); Equal(row["input"], draft["input"]);
        if (row["recordedAt"].CompareTo(draft["recordedAt"]) < 0 || row["recordedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw B.Broken();
        var original = R.Person(draft["people"][0]); var current = R.Person(row["people"][0]); if (original.PersonId != current.PersonId) throw B.Broken();
        var reference = PaymentSubmissionBson.Reference(row["evidence"].AsBsonDocument);
        Equal(row["binding"], B.SubmitBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, row["draftHash"].AsString, row["reservationId"].AsInt64, reference, row["before"].AsBsonDocument));
        Equal(row["after"], B.After(row["before"].AsBsonDocument, row["input"]["inspectedContainers"].AsInt32));
        await EffectsAsync(tx, row, B.SubmitBody(row["draftId"].AsInt64, 1, row["reservationId"].AsInt64, reference, row["binding"].AsString), pending && !completedReceipt, ct);
        if (!pending) {
            Equal(draft["submittedInspectionId"], row["_id"]);
            await RequirePinAsync(tx, row, ct);
        }
    }
    internal async Task RequirePinAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
            var pin = await EvidencePinRecords.ReadAsync(database, tx, row["pinId"].AsInt64, ct);
            await new EvidenceService(database, unit, new(database), clock).ValidateHistoricalPinAsync(tx, pin, ct);
            foreach (var (field, value) in new (string, BsonValue)[] { ("associationKind", "ProcurementInspection"), ("associationId", row["_id"]),
                ("procurementInspectionDraftId", row["draftId"]), ("procurementInspectionDraftRevision", 1L), ("reference", row["evidence"]), ("reservationId", row["reservationId"]),
                ("actorId", row["actorId"]), ("actorGrantVersion", row["actorGrantVersion"]), ("associationHash", row["snapshotHash"]), ("createdAt", row["recordedAt"]),
                ("reason", row["reason"]), ("correlationId", row["correlationId"]) }) Equal(pin[field], value);
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, object body, bool pending, CancellationToken ct) {
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(body, row["reason"].AsString));
        Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", row["operation"] }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (receipt.ElementCount != 8 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); if (pending) { if (receipt["result"].AsBsonDocument.ElementCount != 0) throw B.Broken(); } else Equal(receipt["result"], B.Result(row));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.EffectKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var field in new[] { "actorId", "requestKey", "location" }) Equal(effect[field], row[field]);
        Equal(effect["kind"], row["operation"]); Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]);
    }
    internal async Task OrphansAsync(IClientSessionHandle tx, CancellationToken ct) {
        foreach (var (owner, operation, resultField) in new[] { (B.Drafts, B.DraftOperation, "draftId"), (B.Submissions, B.SubmitOperation, "inspectionId") })
        foreach (var (table, field, result) in new[] { ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload"), ("idempotencyRecords", "operation", "result") }) {
            var rows = await Rows(table).Aggregate<BsonDocument>(tx, new BsonDocument[] {
                new("$match", new BsonDocument(field, operation)), new("$lookup", new BsonDocument { { "from", owner }, { "localField", result + "." + resultField }, { "foreignField", "_id" }, { "as", "owner" } }),
                new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct);
            if (rows.Count != 0) throw B.Broken();
        }
        foreach (var table in new[] { B.Drafts, B.Submissions, B.Heads }) {
            var rows = await Rows(table).Aggregate<BsonDocument>(tx, new BsonDocument[] {
                new("$lookup", new BsonDocument { { "from", C.Intakes }, { "localField", "intakeId" }, { "foreignField", "_id" }, { "as", "intake" } }),
                new("$match", new BsonDocument("intake", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct);
            if (rows.Count != 0) throw B.Broken();
        }
    }
}
