using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using C = UnitToko.Platform.Billing.PaymentAllocationCorrectionBson;

namespace UnitToko.Platform.Billing;

internal sealed partial class PaymentAllocationRecords
{
    private static BsonDocument FinancialReceipt(BsonDocument row, long id) =>
        row["receipts"].AsBsonArray.SingleOrDefault(value => value["receiptId"] == id)?.AsBsonDocument ?? throw B.Broken();
    private static BsonValue EventBefore(BsonDocument row, bool receipt, long id) {
        if (row["operation"] == A.Operation) {
            if (!receipt) { Equal(row["invoiceId"], id); return row["invoiceBefore"]; }
            return FinancialReceipt(row, id)["before"];
        }
        if (row["operation"] != B.Operation && row["operation"] != C.Operation) throw B.Broken();
        return receipt ? row["receiptBefore"] : row["lines"].AsBsonArray.Single(line => line["invoiceId"] == id)["before"];
    }
    private static long EventVersion(BsonDocument row, bool receipt, long id) {
        if (row["operation"] == A.Operation) {
            if (!receipt) { Equal(row["invoiceId"], id); return row["invoiceVersion"].AsInt64; }
            return FinancialReceipt(row, id)["version"].AsInt64;
        }
        if (row["operation"] != B.Operation && row["operation"] != C.Operation) throw B.Broken();
        return (receipt ? row["receiptVersion"] : row["lines"].AsBsonArray.Single(line => line["invoiceId"] == id)["version"]).AsInt64;
    }
    private static BsonValue EventSourceHash(BsonDocument row, bool receipt, long id) => row["operation"] == A.Operation
        ? receipt ? FinancialReceipt(row, id)["receiptHash"] : row["invoice"]["snapshotHash"]
        : receipt ? row["receiptHash"] : row["lines"].AsBsonArray.Single(line => line["invoiceId"] == id)["source"]["snapshotHash"];
    private static BsonValue EventTotal(BsonDocument row, bool receipt, long id) => row["operation"] == A.Operation
        ? receipt ? FinancialReceipt(row, id)["receiptTotal"] : row["invoice"]["total"]
        : receipt ? row["receiptTotal"] : row["lines"].AsBsonArray.Single(line => line["invoiceId"] == id)["source"]["total"];
    private static BsonDocument AfterFinancial(BsonDocument row, bool receipt, long id) {
        var before = EventBefore(row, receipt, id).AsBsonDocument; var next = before.DeepClone().AsBsonDocument;
        next["version"] = EventVersion(row, receipt, id);
        var release = (receipt ? FinancialReceipt(row, id)["released"] : row["reservedRefund"]).AsDecimal;
        next[receipt ? "allocated" : "paid"] = new BsonDecimal128(checked(before[receipt ? "allocated" : "paid"].AsDecimal - release));
        next[receipt ? "reservedForRefund" : "credited"] = new BsonDecimal128(checked(before[receipt ? "reservedForRefund" : "credited"].AsDecimal + (receipt ? release : row["credit"].AsDecimal)));
        next["lastEventId"] = row["_id"]; next["lastEventHash"] = row["snapshotHash"]; return next;
    }
    private Task<BsonDocument> ReadEventAsync(IClientSessionHandle tx, BsonDocument candidate, CancellationToken ct) => candidate["operation"].AsString switch {
        B.Operation => RawAsync(tx, candidate["_id"].AsInt64, ct),
        C.Operation => RawEventAsync(tx, true, candidate["_id"].AsInt64, ct),
        A.Operation => RawApprovalAsync(tx, candidate["_id"].AsInt64, ct),
        _ => throw B.Broken()
    };

    // Structural provenance only. Aggregate history validation happens outside this method,
    // so a refund link cannot recurse through its own canonical invoice/receipt chain.
    internal async Task<BsonDocument> RawApprovalAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || clock is null) throw B.Broken();
        var row = await RowAsync(tx, A.Events, id, ct); Equal(row["operation"], A.Operation); Equal(row["snapshotHash"], B.Hash(row));
        foreach (var other in new[] { B.Events, C.Events }) if (await Rows(other).Find(tx, new BsonDocument("_id", id)).AnyAsync(ct)) throw B.Broken();
        var request = await new ReturnEntitlementRecords(database, clock).RawAsync(tx, row["returnId"].AsInt64, ct);
        Equal(row["request"], request); Equal(row["caseId"], request["caseId"]); Equal(row["invoiceId"], request["invoiceId"]); Equal(row["action"], request["action"]);
        Equal(row["invoice"], request["source"]["invoice"]); Equal(row["party"], PaymentDraftBson.Party(new BsonArray { row["invoice"] }));
        Equal(row["invoiceVersion"], checked(row["invoiceBefore"]["version"].AsInt64 + 1));
        if (row["recordedAt"].CompareTo(request["requestedAt"]) < 0 || row["recordedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime ||
            InvoiceBson.Date(row["postingDate"]) != AccountingCalendar.BusinessDate(new(row["recordedAt"].ToUniversalTime()))) throw B.Broken();
        A.PolicyAt(row["policy"].AsBsonDocument, new(row["recordedAt"].ToUniversalTime()));
        Equal(row["policy"]["arMapping"], row["invoice"]["arMapping"]);
        foreach (var field in new[] { "fulfillmentOrganizationId", "shopId", "channelId" }) Equal(row["policy"]["scope"][field], row["party"][field]);
        if (!row["policy"]["scope"]["warehouseId"].IsBsonNull) throw B.Broken();
        var input = A.Input(row["input"].AsBsonDocument); Equal(row["input"], A.Input(input));
        Equal(row["binding"], A.Binding(request, row["policy"].AsBsonDocument, row["invoiceBefore"].AsBsonDocument, row["receipts"].AsBsonArray, row["links"].AsBsonArray, input));
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(A.Body(row["caseId"].AsInt64, row["returnId"].AsInt64, row["action"].AsString, row["binding"].AsString, input), row["reason"].AsString));
        foreach (var link in row["links"].AsBsonArray) await RequireFinancialOriginalAsync(tx, row, link.AsBsonDocument, ct);
        var result = A.Review(row); Equal(row["credit"], InvoiceBson.Money(result.Credit)); Equal(row["reservedRefund"], InvoiceBson.Money(result.ReservedRefund));
        Equal(row["remainingInvoice"], InvoiceBson.Money(result.RemainingInvoice));
        if (result.Receipts.Length != row["receipts"].AsBsonArray.Count) throw B.Broken();
        foreach (var receipt in row["receipts"].AsBsonArray) {
            var review = result.Receipts.Single(value => value.After.ReceiptId.Value == receipt["receiptId"].AsInt64);
            Equal(receipt["version"], review.After.Version); Equal(receipt["released"], InvoiceBson.Money(review.Released)); Equal(receipt["remainingReceipt"], InvoiceBson.Money(review.Available));
        }
        RequireFinancialPeople(row);
        var savedResult = A.Result(row);
        Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), A.Audit(row));
        var operationReceipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] }, { "operation", A.Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (operationReceipt.ElementCount != 9 || !operationReceipt["_id"].IsObjectId || operationReceipt["schemaVersion"] != 1 || !operationReceipt["createdAt"].IsBsonDateTime || operationReceipt["createdAt"].CompareTo(row["recordedAt"]) > 0) throw B.Broken();
        Equal(operationReceipt["bodyHash"], row["bodyHash"]); Equal(operationReceipt["result"], savedResult);
        Equal(operationReceipt[PersonApprovalReceipt.Field], PersonApprovalReceipt.Create(row["action"] == "credit" ? Permissions.CreditApprove : Permissions.RefundApprove, new(1, 1, RequestedBy: request["actorId"].AsInt64)));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", A.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var (field, value) in new (string, BsonValue)[] { ("kind", A.Operation), ("actorId", row["actorId"]), ("requestKey", row["requestKey"]), ("createdAt", row["recordedAt"]), ("payload", savedResult) }) Equal(effect[field], value);
        var journal = await Rows("journalEntries").Find(tx, new BsonDocument("_id", row["journalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var draft = AccountingBson.ReadDraft(journal); var expected = ReturnFinancialApprovalPostingSource.Snapshot(row);
        ReturnFinancialApprovalPostingSource.RequireAccounts(row, draft);
        if (row["postingFence"] != 1L || draft.Source != expected.Source || draft.SourceRevision != 1 || draft.Date != expected.Date || draft.Book != expected.Book || draft.Dimensions != expected.Dimensions || draft.ReversalOf is not null ||
            draft.Fingerprint != row["journalFingerprint"].AsString || journal["actorId"] != row["actorId"] || !draft.Lines.Select(line => new PostingLine(line.MappingKey, line.Debit, line.Credit)).SequenceEqual(expected.Lines)) throw B.Broken();
        return row;
    }
    private async Task RequireFinancialOriginalAsync(IClientSessionHandle tx, BsonDocument row, BsonDocument link, CancellationToken ct) {
        var original = await RawAsync(tx, link["originalAllocationId"].AsInt64, ct);
        foreach (var (field, originalField) in new[] { ("originalAllocationHash", "snapshotHash"), ("originalJournalId", "journalId"), ("originalJournalFingerprint", "journalFingerprint"), ("allocationActorId", "actorId") }) Equal(link[field], original[originalField]);
        foreach (var field in new[] { "verificationId", "verificationHash", "declarationId", "declarationRevision", "recordedBy", "receiptId", "receiptHash", "receiptTotal", "suspenseAccount" }) Equal(link[field], original[field]);
        Equal(row["party"], original["party"]);
        var line = original["lines"].AsBsonArray.SingleOrDefault(value => value["invoiceId"] == row["invoiceId"]) ?? throw B.Broken();
        Equal(link["source"], line["source"]); Equal(row["invoice"], line["source"]); Equal(link["originallyAllocated"], line["amount"]);
        var receipt = FinancialReceipt(row, original["receiptId"].AsInt64);
        foreach (var field in new[] { "receiptId", "receiptHash", "receiptTotal" }) Equal(receipt[field], original[field]);
        if (row["recordedAt"].CompareTo(original["recordedAt"]) < 0 || receipt["before"]["version"].AsInt64 < original["receiptVersion"].AsInt64 || row["invoiceBefore"]["version"].AsInt64 < line["version"].AsInt64) throw B.Broken();
        var verification = await new PaymentVerificationRecords(database).RawAsync(tx, original["verificationId"].AsInt64, ct);
        Equal(link["verifiedBy"], verification["actorId"]); Equal(link["verificationHash"], verification["snapshotHash"]);
    }
    private static void RequireFinancialPeople(BsonDocument row) {
        var request = row["request"]; var actor = row["actorId"].AsInt64;
        var users = request["source"]["participants"].AsBsonArray.Select(value => value.AsInt64)
            .Append(request["actorId"].AsInt64).Append(actor)
            .Concat(row["links"].AsBsonArray.SelectMany(link => new[] { link["allocationActorId"].AsInt64, link["verifiedBy"].AsInt64, link["recordedBy"].AsInt64 })).Distinct().Order().ToArray();
        var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray();
        var makers = new[] { request["actorId"].AsInt64, request["source"]["requestedBy"].AsInt64 }
            .Concat(row["links"].AsBsonArray.SelectMany(link => new[] { link["allocationActorId"].AsInt64, link["verifiedBy"].AsInt64, link["recordedBy"].AsInt64 })).Distinct().ToArray();
        if (people.Any(person => person.Verification != PersonVerification.Verified) || !people.Select(person => person.UserId).Order().SequenceEqual(users) ||
            !PersonIndependencePolicy.AreIndependent(people.Single(person => person.UserId == actor), makers.Select(id => people.Single(person => person.UserId == id)).ToArray())) throw B.Broken();
    }
    internal async Task<BsonDocument?> FinancialForKeyAsync(IClientSessionHandle tx, long actor, string key, CancellationToken ct) {
        var row = await Rows(A.Events).Find(tx, new BsonDocument { { "actorId", actor }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is not null) return await RawApprovalAsync(tx, row["_id"].AsInt64, ct);
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            if (await Rows(table).Find(tx, new BsonDocument { { "actorId", actor }, { field, A.Operation }, { table == "idempotencyRecords" ? "key" : "requestKey", key } }).AnyAsync(ct)) throw B.Broken();
        return null;
    }
    private async Task RequireFinancialEffectsAsync(IClientSessionHandle tx, bool receipt, long id, HashSet<long> seen, CancellationToken ct) {
        foreach (var (table, field, payload) in new[] { ("idempotencyRecords", "operation", "result"), ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload") }) {
            using var effects = await Rows(table).Find(tx, new BsonDocument { { field, A.Operation }, { payload + (receipt ? ".receipts.receiptId" : ".invoiceId"), id } }).ToCursorAsync(ct);
            while (await effects.MoveNextAsync(ct)) foreach (var effect in effects.Current) if (!seen.Contains(effect[payload]["financialApprovalId"].AsInt64)) throw B.Broken();
        }
    }
    private async Task RequireFinancialReleaseEffectsAsync(IClientSessionHandle tx, long allocationId, HashSet<long> seen, CancellationToken ct) {
        foreach (var (table, field, payload) in new[] { ("idempotencyRecords", "operation", "result"), ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload") }) {
            using var effects = await Rows(table).Find(tx, new BsonDocument { { field, A.Operation }, { payload + ".releases.originalAllocationId", allocationId } }).ToCursorAsync(ct);
            while (await effects.MoveNextAsync(ct)) foreach (var effect in effects.Current) if (!seen.Contains(effect[payload]["financialApprovalId"].AsInt64)) throw B.Broken();
        }
    }
}
