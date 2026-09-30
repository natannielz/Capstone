using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;

namespace UnitToko.Platform.Billing;

internal sealed class PaymentAllocationRecords(IMongoDatabase database)
{
    internal IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static void Equal(BsonValue a, BsonValue b) { if (!a.Equals(b)) throw B.Broken(); }
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) =>
        await Rows(name).Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument("_id", id), PaymentAllocationSchemas.Validator(name) })).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    internal static BsonDocument Empty(bool receipt) => new() { { "version", 0L }, { receipt ? "allocated" : "paid", new BsonDecimal128(0m) },
        { receipt ? "reservedForRefund" : "credited", new BsonDecimal128(0m) }, { "lastEventId", BsonNull.Value }, { "lastEventHash", BsonNull.Value } };
    internal static BsonDocument Balance(BsonDocument head, bool receipt) => new(Empty(receipt).Names.Select(name => new BsonElement(name, head[name])));
    internal static BsonDocument After(BsonDocument row, bool receipt, long id) {
        var line = receipt ? null : row["lines"].AsBsonArray.Single(value => value["invoiceId"] == id).AsBsonDocument;
        var before = (receipt ? row["receiptBefore"] : line!["before"]).AsBsonDocument;
        var next = before.DeepClone().AsBsonDocument;
        next["version"] = receipt ? row["receiptVersion"] : line!["version"];
        next[receipt ? "allocated" : "paid"] = new BsonDecimal128(before[receipt ? "allocated" : "paid"].AsDecimal + (receipt ? row["amount"] : line!["amount"]).AsDecimal);
        next["lastEventId"] = row["_id"]; next["lastEventHash"] = row["snapshotHash"]; return next;
    }
    internal static void PolicyAt(BsonDocument policy, DateTimeOffset at) {
        foreach (var key in new[] { "suspenseMapping", "arMapping", "procedureReference", "operatingOwnerReference", "approvalReference" })
            if (!ReceivingAccountPolicy.Text(policy[key].AsString, key.EndsWith("Mapping", StringComparison.Ordinal) ? 80 : 256)) throw B.Broken();
        if (policy["suspenseMapping"] == policy["arMapping"]) throw B.Broken();
        if (!policy["active"].AsBoolean || policy["approvedAt"].ToUniversalTime() > at.UtcDateTime || policy["expiresAt"].ToUniversalTime() <= at.UtcDateTime) throw B.Changed("payment_allocation_policy_unavailable");
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string key, CancellationToken ct) {
        var row = await Rows(B.Events).Find(tx, new BsonDocument { { "actorId", actor }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is not null) return await RawAsync(tx, row["_id"].AsInt64, ct);
        foreach (var (name, filter) in new (string, BsonDocument)[] {
            ("idempotencyRecords", new() { { "actorId", actor }, { "operation", B.Operation }, { "key", key } }),
            ("auditEvents", new() { { "actorId", actor }, { "kind", B.Operation }, { "requestKey", key } }),
            ("outboxEvents", new() { { "actorId", actor }, { "kind", B.Operation }, { "requestKey", key } })
        }) if (await Rows(name).Find(tx, filter).AnyAsync(ct)) throw B.Broken();
        return null;
    }
    internal async Task<BsonDocument> RawAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await RowAsync(tx, B.Events, id, ct); Equal(row["snapshotHash"], B.Hash(row));
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(B.Body(row["verificationId"].AsInt64, row["binding"].AsString, B.Input(row)), row["reason"].AsString));
        PolicyAt(row["policy"].AsBsonDocument, new(row["recordedAt"].ToUniversalTime()));
        if (row["postingFence"] != 1L || row["journalId"].IsBsonNull || row["journalFingerprint"].IsBsonNull) throw B.Broken();
        var before = row["receiptBefore"]; var party = B.Party(row["party"].AsBsonDocument);
        var result = PaymentAllocationPolicy.Review(new(new(row["receiptId"].AsInt64), before["version"].AsInt64, party, "IDR", InvoiceBson.Money(row["receiptTotal"]),
            InvoiceBson.Money(before["allocated"]), InvoiceBson.Money(before["reservedForRefund"])),
            [.. row["lines"].AsBsonArray.Select(line => new InvoiceFunds(new(line["invoiceId"].AsInt64), line["before"]["version"].AsInt64, B.Party(PaymentDraftBson.Party(new BsonArray { line["source"] })), "IDR",
                InvoiceBson.Money(line["source"]["total"]), InvoiceBson.Money(line["before"]["paid"]), InvoiceBson.Money(line["before"]["credited"])))], B.Input(row));
        Equal(row["amount"], InvoiceBson.Money(result.Total)); Equal(row["unapplied"], InvoiceBson.Money(result.RemainingReceipt));
        Equal(row["receiptVersion"], before["version"].AsInt64 + 1);
        foreach (var line in row["lines"].AsBsonArray) {
            Equal(line["invoiceId"], line["source"]["invoiceId"]); Equal(line["source"]["arMapping"], row["policy"]["arMapping"]);
            Equal(line["version"], line["before"]["version"].AsInt64 + 1);
            Equal(line["remaining"], InvoiceBson.Money(result.Invoices.Single(value => value.InvoiceId.Value == line["invoiceId"].AsInt64).Remaining));
        }
        Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", B.Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (receipt.ElementCount != 9 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || !receipt["createdAt"].IsBsonDateTime || receipt["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        var revision = row["declarationRevision"].AsInt64;
        Equal(receipt[PersonApprovalReceipt.Field], PersonApprovalReceipt.Create(Permissions.PaymentAllocate, new(revision, revision, RecordedBy: row["recordedBy"].AsInt64)));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(effect["kind"], B.Operation); Equal(effect["actorId"], row["actorId"]); Equal(effect["requestKey"], row["requestKey"]); Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]);
        var journal = await Rows("journalEntries").Find(tx, new BsonDocument("_id", row["journalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var draft = AccountingBson.ReadDraft(journal); var expected = PaymentAllocationPostingSource.Snapshot(row);
        var suspense = draft.Lines.Single(line => line.MappingKey == row["policy"]["suspenseMapping"].AsString);
        var receivable = draft.Lines.Single(line => line.MappingKey == row["policy"]["arMapping"].AsString);
        if (suspense.AccountId != row["suspenseAccount"]["accountId"].AsInt64 || suspense.AccountCode != row["suspenseAccount"]["accountCode"].AsString ||
            row["lines"].AsBsonArray.Any(line => line["source"]["arAccountId"].AsInt64 != receivable.AccountId || line["source"]["arAccountCode"].AsString != receivable.AccountCode))
            throw B.Changed("payment_allocation_accounting_mapping_changed");
        if (draft.Source != expected.Source || draft.SourceRevision != 1 || draft.Date != expected.Date || draft.Book != expected.Book || draft.Dimensions != expected.Dimensions || draft.ReversalOf is not null ||
            draft.Fingerprint != row["journalFingerprint"].AsString || journal["actorId"] != row["actorId"] || !draft.Lines.Select(line => new PostingLine(line.MappingKey, line.Debit, line.Credit)).SequenceEqual(expected.Lines)) throw B.Broken();
        return row;
    }
    internal async Task<(BsonDocument Balance, BsonDocument? Head)> BalanceAsync(IClientSessionHandle tx, bool receipt, long id, BsonDocument party,
        BsonValue total, BsonValue sourceHash, Func<BsonDocument, Task> validateSource, CancellationToken ct) {
        var name = receipt ? B.Receipts : B.Invoices; var raw = await Rows(name).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct);
        var expected = Empty(receipt); var seen = new HashSet<long>();
        using var cursor = await Rows(B.Events).Find(tx, new BsonDocument(receipt ? "receiptId" : "lines.invoiceId", id)).Sort(new BsonDocument(receipt ? "receiptVersion" : "recordedAt", 1)).ToCursorAsync(ct);
        // Invoice histories can span receipts. Order by that invoice's version, not the array's minimum.
        var events = new List<BsonDocument>(); while (await cursor.MoveNextAsync(ct)) events.AddRange(cursor.Current);
        foreach (var item in events.OrderBy(row => receipt ? row["receiptVersion"].AsInt64 : row["lines"].AsBsonArray.Single(line => line["invoiceId"] == id)["version"].AsInt64)) {
            var row = await RawAsync(tx, item["_id"].AsInt64, ct); await validateSource(row); seen.Add(row["_id"].AsInt64);
            Equal(row["party"], party); var line = receipt ? null : row["lines"].AsBsonArray.Single(value => value["invoiceId"] == id);
            Equal(receipt ? row["receiptBefore"] : line!["before"], expected);
            Equal(receipt ? row["receiptHash"] : line!["source"]["snapshotHash"], sourceHash);
            Equal(receipt ? row["receiptTotal"] : line!["source"]["total"], total);
            expected = After(row, receipt, id);
        }
        // A deleted event/head must not reset spendable funds while its committed effects survive.
        foreach (var (collection, kind, prefix) in new[] { ("auditEvents", "kind", "result"), ("idempotencyRecords", "operation", "result"), ("outboxEvents", "kind", "payload") }) {
            using var effects = await Rows(collection).Find(tx, new BsonDocument { { kind, B.Operation }, { prefix + (receipt ? ".receiptId" : ".invoices.invoiceId"), id } }).ToCursorAsync(ct);
            while (await effects.MoveNextAsync(ct)) foreach (var effect in effects.Current) if (!seen.Contains(effect[prefix]["allocationId"].AsInt64)) throw B.Broken();
        }
        if (raw is null) { if (seen.Count != 0) throw B.Broken(); return (expected, null); }
        var head = await RowAsync(tx, name, id, ct); Equal(head["snapshotHash"], B.Hash(head)); Equal(Balance(head, receipt), expected);
        Equal(head["party"], party); Equal(head["total"], total); Equal(head["sourceHash"], sourceHash); return (expected, head);
    }
    internal async Task SaveHeadAsync(IClientSessionHandle tx, bool receipt, long id, BsonDocument? previous, BsonDocument row, BsonValue total, BsonValue sourceHash, CancellationToken ct) {
        var head = new BsonDocument { { "_id", id }, { "schemaVersion", 1 } }; head.AddRange(After(row, receipt, id));
        head.Add("sourceHash", sourceHash); head.Add("party", row["party"]); head.Add("total", total); head.Add("fence", 0L); head.Add("snapshotHash", B.Hash(head));
        var collection = Rows(receipt ? B.Receipts : B.Invoices);
        if (previous is null) await collection.InsertOneAsync(tx, head, cancellationToken: ct);
        else if ((await collection.ReplaceOneAsync(tx, previous, head, cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
    }
    internal async Task TouchAsync(IClientSessionHandle tx, string name, BsonDocument row, CancellationToken ct) {
        if (row["fence"].AsInt64 >= long.MaxValue - 1 || (await Rows(name).UpdateOneAsync(tx, row, Builders<BsonDocument>.Update.Inc("fence", 1L), cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
    }
}
