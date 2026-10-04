using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentVerificationBson;

namespace UnitToko.Platform.Billing;

// Permanent purpose ownership is separate from remaining money. Reversals and
// refunds never release this receipt for attribution to a different purpose.
internal sealed class PaymentReceiptPurposeRecords(IMongoDatabase database)
{
    private const string ReturnDecisions = "procurementAdvanceReturnDecisions", ReturnLines = "procurementAdvanceReturnLines";
    private const string ReturnOperation = "procurement.advancereturn.decide";
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    private static UnitToko.Platform.Mongo.PersistenceConflictException Conflict() => B.Changed("bank_receipt_purpose_conflict");

    private async Task<BsonDocument?> ReadAsync(IClientSessionHandle tx, long receiptId, BsonValue receiptHash, CancellationToken ct) {
        BankTransactionClaims.RequireTransaction(database, tx);
        var raw = await Rows(B.Parties).Find(tx, new BsonDocument("_id", receiptId)).SingleOrDefaultAsync(ct);
        if (raw is null) return null;
        var row = await Rows(B.Parties).Find(tx, new BsonDocument("$and", new BsonArray {
            new BsonDocument("_id", receiptId), PaymentReceiptPurposeSchemas.Validator()
        })).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(row["snapshotHash"], B.Hash(row)); Equal(row["receiptHash"], receiptHash); return row;
    }

    internal async Task<BsonDocument?> CustomerAsync(IClientSessionHandle tx, long receiptId, BsonValue receiptHash,
        BsonDocument party, bool required, CancellationToken ct) {
        var row = await ReadAsync(tx, receiptId, receiptHash, ct);
        if (row is null) {
            await RequireNoCustomerConsumersAsync(tx, receiptId, ct);
            await RequireNoSupplierConsumersAsync(tx, receiptId, null, ct);
            await RequireNoOrphanJournalsAsync(tx, receiptId, receiptHash, ct);
            if (required) throw B.Broken();
            return null;
        }
        if (row["schemaVersion"] != 1) throw Conflict();
        await RequireNoSupplierConsumersAsync(tx, receiptId, null, ct);
        var first = await new PaymentVerificationRecords(database).RawCoreAsync(tx, row["firstVerificationId"].AsInt64, ct);
        Equal(first["receiptId"], row["_id"]); Equal(first["source"]["party"], row["party"]);
        Equal(first["receiptHash"], row["receiptHash"]); Equal(first["recordedAt"], row["recordedAt"]);
        if (!row["party"].Equals(party)) throw B.Changed("payment_receipt_payer_conflict");
        return row;
    }

    internal async Task RequireUnclaimedForSupplierAsync(IClientSessionHandle tx, BsonDocument receipt, CancellationToken ct) {
        var id = receipt["_id"].AsInt64;
        if (await ReadAsync(tx, id, receipt["snapshotHash"], ct) is not null) throw Conflict();
        await RequireNoCustomerConsumersAsync(tx, id, ct);
        await RequireNoSupplierConsumersAsync(tx, id, null, ct);
        await RequireNoOrphanJournalsAsync(tx, id, receipt["snapshotHash"], ct);
    }

    private static BsonDocument Supplier(BsonDocument receipt, BsonDocument decision) {
        if (decision["decision"] != "approve" || decision["receiptId"] != receipt["_id"] ||
            decision["receiptHash"] != receipt["snapshotHash"] || decision["snapshotHash"] != B.Hash(decision) ||
            decision["postingFence"] != 1L || decision["journalId"].IsBsonNull || decision["journalFingerprint"].IsBsonNull) throw B.Broken();
        var row = new BsonDocument {
            { "_id", receipt["_id"] }, { "schemaVersion", 2 }, { "kind", "supplier_advance_return" },
            { "receiptHash", receipt["snapshotHash"] }, { "supplierId", decision["supplierId"] }, { "orderId", decision["orderId"] },
            { "settlementIds", new BsonArray(decision["source"]["lines"].AsBsonArray.Select(line => line["settlementId"].AsInt64).Order()) },
            { "firstReturnId", decision["_id"] }, { "firstReturnHash", decision["snapshotHash"] },
            { "recordedAt", decision["recordedAt"] }, { "fence", 0L }
        };
        row.Add("snapshotHash", B.Hash(row)); return row;
    }

    internal async Task ClaimSupplierReturnAsync(IClientSessionHandle tx, BsonDocument receipt, BsonDocument decision, CancellationToken ct) {
        var id = receipt["_id"].AsInt64;
        if (await ReadAsync(tx, id, receipt["snapshotHash"], ct) is not null) throw Conflict();
        var claim = Supplier(receipt, decision);
        Equal(await Rows(ReturnDecisions).Find(tx, new BsonDocument("_id", decision["_id"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), decision);
        await RequireNoCustomerConsumersAsync(tx, id, ct);
        await RequireNoSupplierConsumersAsync(tx, id, decision["_id"].AsInt64, ct);
        await RequireNoOrphanJournalsAsync(tx, id, receipt["snapshotHash"], ct);
        await Rows(B.Parties).InsertOneAsync(tx, claim, cancellationToken: ct);
    }

    // Caller supplies its fully authenticated owning decision; this comparison does
    // not call the return owner again and cannot recurse through advance capacity.
    internal async Task RequireSupplierReturnAsync(IClientSessionHandle tx, BsonDocument receipt, BsonDocument decision, CancellationToken ct) {
        var id = receipt["_id"].AsInt64;
        var row = await ReadAsync(tx, id, receipt["snapshotHash"], ct) ?? throw B.Broken();
        if (row["schemaVersion"] != 2) throw Conflict();
        Equal(B.Snapshot(row), B.Snapshot(Supplier(receipt, decision)));
        await RequireNoCustomerConsumersAsync(tx, id, ct);
        await RequireNoSupplierConsumersAsync(tx, id, decision["_id"].AsInt64, ct);
        await new PaymentVerificationRecords(database).TouchAsync(tx, B.Parties, row, ct);
    }

    private async Task RequireNoCustomerConsumersAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        foreach (var (name, field) in new[] {
            (B.Decisions, "receiptId"), (PaymentAllocationBson.Receipts, "_id"), (PaymentAllocationBson.Events, "receiptId"),
            (PaymentAllocationCorrectionBson.Events, "receiptId"), (ReturnFinancialApprovalBson.Events, "receipts.receiptId"),
            (RefundSettlementBson.Events, "receipts.receiptId")
        }) if (await Rows(name).Find(tx, new BsonDocument(field, id)).AnyAsync(ct)) throw B.Broken();
        var operations = new BsonArray { B.Operation, PaymentAllocationBson.Operation, PaymentAllocationCorrectionBson.Operation,
            ReturnFinancialApprovalBson.Operation, RefundSettlementBson.Operation };
        foreach (var (table, kind, payload) in new[] { ("idempotencyRecords", "operation", "result"), ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload") })
            if (await Rows(table).Find(tx, new BsonDocument { { kind, new BsonDocument("$in", operations) },
                { "$or", new BsonArray { new BsonDocument(payload + ".receiptId", id), new BsonDocument(payload + ".receipts.receiptId", id) } }
            }).AnyAsync(ct)) throw B.Broken();
    }

    private async Task RequireNoSupplierConsumersAsync(IClientSessionHandle tx, long id, long? allowedDecision, CancellationToken ct) {
        var decisions = new BsonDocument { { "receiptId", id }, { "decision", "approve" } };
        var lines = new BsonDocument("receiptId", id);
        if (allowedDecision is { } allowed) { decisions.Add("_id", new BsonDocument("$ne", allowed)); lines.Add("decisionId", new BsonDocument("$ne", allowed)); }
        if (await Rows(ReturnDecisions).Find(tx, decisions).AnyAsync(ct) || await Rows(ReturnLines).Find(tx, lines).AnyAsync(ct)) throw B.Broken();
        foreach (var (table, kind, payload) in new[] { ("idempotencyRecords", "operation", "result"), ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload") }) {
            var filter = new BsonDocument { { kind, ReturnOperation }, { payload + ".receiptId", id }, { payload + ".status", "returned" } };
            if (allowedDecision is { } current) filter.Add(payload + ".decisionId", new BsonDocument("$ne", current));
            if (await Rows(table).Find(tx, filter).AnyAsync(ct)) throw B.Broken();
        }
    }

    private async Task RequireNoOrphanJournalsAsync(IClientSessionHandle tx, long receiptId, BsonValue receiptHash, CancellationToken ct) {
        // Authenticate the actual receipt's retained bank journal before deriving its
        // book. Existing ix_book_date_id bounds this scan to that book. Journal sources
        // retain owner IDs, not receipt IDs: a deleted consumer therefore requires a
        // conservative book-level denial, never a database-wide denial or reassignment.
        var receipt = await BankReceiptRecords.ReadReceiptHistoryAsync(database, tx, receiptId, ct);
        Equal(receipt["snapshotHash"], receiptHash);
        var bankJournal = await Rows("journalEntries").Find(tx, new BsonDocument {
            { "_id", receipt["journalId"] }, { "fingerprint", receipt["journalFingerprint"] }
        }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var (owner, source, eventKind) in new[] {
            (ReturnDecisions, ReturnOperation, "supplier-advance-return"),
            (PaymentAllocationBson.Events, "billing.payment-allocation", "allocated"),
            (PaymentAllocationCorrectionBson.Events, "billing.payment-allocation", "unallocated"),
            (ReturnFinancialApprovalBson.Events, "billing.return-financial", "approved"),
            (RefundSettlementBson.Events, RefundSettlementBson.Operation, "refund.bank-debit-settled")
        }) {
            var orphan = await Rows("journalEntries").Aggregate<BsonDocument>(tx, new BsonDocument[] {
                new("$match", new BsonDocument { { "bookId", bankJournal["bookId"] }, { "source.namespace", source }, { "source.eventKind", eventKind } }),
                new("$lookup", new BsonDocument { { "from", owner }, { "localField", "source.documentId" }, { "foreignField", "_id" }, { "as", "retainedReceiptConsumer" } }),
                new("$match", new BsonDocument("retainedReceiptConsumer", new BsonDocument("$size", 0))), new("$limit", 1)
            }).ToListAsync(ct);
            if (orphan.Count != 0) throw B.Broken();
        }
    }
}
