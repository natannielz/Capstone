using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using C = UnitToko.Platform.Billing.PaymentAllocationCorrectionBson;
using D = UnitToko.Platform.Billing.RefundSettlementBson;
using R = UnitToko.Platform.Billing.PaymentAllocationRecords;

namespace UnitToko.Platform.Billing;

internal static class RefundSettlementRecords
{
    // Immutable owner provenance only. Receipt-chain reconstruction stays outside this
    // reader so a settlement cannot recursively validate itself through aggregate money.
    internal static async Task<BsonDocument> ReadAsync(IMongoDatabase database, IClientSessionHandle tx, long id, CancellationToken ct, IClock? clock = null) {
        BankTransactionClaims.RequireTransaction(database, tx);
        if (id <= 0 || database.Client is not MongoClient client) throw B.Broken();
        clock ??= new SystemClock();
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
        var row = await Rows(D.Events).Find(tx, new BsonDocument("$and", new BsonArray { new BsonDocument("_id", id), RefundSettlementSchemas.Validator(D.Events) })).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        R.Equal(row["operation"], D.Operation); R.Equal(row["snapshotHash"], D.Hash(row));
        foreach (var table in new[] { B.Events, C.Events, A.Events }) if (await Rows(table).Find(tx, new BsonDocument("_id", id)).AnyAsync(ct)) throw B.Broken();
        var payout = await new RefundPayoutRecords(database, clock, new MongoUnitOfWork(client)).PayoutAsync(tx, row["payoutId"].AsInt64, false, ct);
        var approval = await new R(database, clock).RawApprovalAsync(tx, row["financialApprovalId"].AsInt64, ct);
        R.Equal(payout["source"]["approval"], RefundPayoutBson.ApprovalSnapshot(approval, new BsonDocument {
            { "accountId", approval["policy"]["refundLiabilityAccountId"] }, { "accountCode", approval["policy"]["refundLiabilityAccountCode"] } }));
        var originalDraft = await new RefundPayoutRecords(database, clock, new MongoUnitOfWork(client)).DraftAsync(tx, payout["draftId"].AsInt64, ct);
        _ = await BankReceiptService.ReadPayoutBankHistoryAsync(database, tx, payout["source"]["payingBank"].AsBsonDocument, new(originalDraft["createdAt"].ToUniversalTime()), ct);
        R.Equal(row["payoutHash"], payout["snapshotHash"]); R.Equal(row["approvalHash"], approval["snapshotHash"]);
        R.Equal(payout["financialApprovalId"], approval["_id"]); R.Equal(payout["approvalHash"], approval["snapshotHash"]);
        foreach (var field in new[] { "caseId", "returnId", "invoiceId", "party" }) { R.Equal(row[field], payout[field]); R.Equal(row[field], approval[field]); }
        R.Equal(row["invoice"], approval["invoice"]); R.Equal(row["releases"], D.Releases(approval));
        R.Equal(row["amount"], approval["reservedRefund"]); R.Equal(row["amount"], payout["input"]["amount"]);
        if (approval["action"] != "refund" || row["amount"].AsDecimal <= 0 || row["recordedAt"].CompareTo(payout["recordedAt"]) < 0 ||
            row["recordedAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime || !row["attested"].AsBoolean ||
            InvoiceBson.Date(row["postingDate"]) != AccountingCalendar.BusinessDate(new(row["recordedAt"].ToUniversalTime()))) throw B.Broken();
        D.PolicyAt(row["policy"].AsBsonDocument, new(row["recordedAt"].ToUniversalTime()));
        var debit = row["debit"].AsBsonDocument; R.Equal(row["canonicalIdentity"], debit["canonicalIdentity"]); R.Equal(row["amount"], debit["amount"]);
        R.Equal(debit["accountId"], payout["input"]["payingAccountId"]); R.Equal(debit["accountVersionId"], payout["input"]["payingAccountVersionId"]);
        foreach (var field in new[] { "fulfillmentOrganizationId", "shopId", "channelId" }) {
            R.Equal(row["party"][field], debit["scope"][field]); R.Equal(row["party"][field], row["policy"]["scope"][field]);
        }
        if (!debit["scope"]["warehouseId"].IsBsonNull || !row["policy"]["scope"]["warehouseId"].IsBsonNull ||
            InvoiceBson.Date(debit["bookedDate"]) < InvoiceBson.Date(payout["input"]["transferDate"]) ||
            InvoiceBson.Date(debit["bookedDate"]) > InvoiceBson.Date(row["postingDate"])) throw B.Broken();
        var bankCashAccount = await BankReceiptService.ReadSettlementDebitHistoryAsync(database, tx, debit, ct);
        await new BankTransactionClaims(database).RequireDebitAsync(tx, row, ct);
        var input = D.Input(row["input"].AsBsonDocument); R.Equal(row["input"], D.Input(input));
        R.Equal(input.BankImportId, debit["importId"]); R.Equal(input.BankRowNumber, debit["rowNumber"]);
        R.Equal(row["binding"], D.Binding(payout, debit, row["policy"].AsBsonDocument, row["receipts"].AsBsonArray, row["releases"].AsBsonArray, input));
        R.Equal(row["bodyHash"], AuthorizationStore.CommandHash(D.Body(payout["_id"].AsInt64, row["binding"].AsString, input, true), row["reason"].AsString));
        var expectedReceiptIds = row["releases"].AsBsonArray.Select(line => line["receiptId"].AsInt64).Distinct().Order().ToArray();
        if (!row["receipts"].AsBsonArray.Select(line => line["receiptId"].AsInt64).SequenceEqual(expectedReceiptIds)) throw B.Broken();
        foreach (var value in row["receipts"].AsBsonArray) {
            var receipt = value.AsBsonDocument; var before = receipt["before"]; var amount = row["releases"].AsBsonArray.Where(line => line["receiptId"] == receipt["receiptId"]).Sum(line => line["amount"].AsDecimal);
            var original = approval["receipts"].AsBsonArray.Single(line => line["receiptId"] == receipt["receiptId"]);
            R.Equal(receipt["receiptHash"], original["receiptHash"]); R.Equal(receipt["receiptTotal"], original["receiptTotal"]);
            R.Equal(receipt["version"], checked(before["version"].AsInt64 + 1));
            if (before["version"].AsInt64 < original["version"].AsInt64) throw B.Broken();
            R.Equal(receipt["paidAfter"], new BsonDecimal128(checked(receipt["paidBefore"].AsDecimal + amount)));
            if (receipt["paidAfter"].AsDecimal > before["reservedForRefund"].AsDecimal) throw B.Broken();
            R.Equal(receipt["pending"], new BsonDecimal128(checked(before["reservedForRefund"].AsDecimal - receipt["paidAfter"].AsDecimal)));
            R.Equal(receipt["available"], new BsonDecimal128(checked(receipt["receiptTotal"].AsDecimal - before["allocated"].AsDecimal - before["reservedForRefund"].AsDecimal)));
        }
        var actor = row["actorId"].AsInt64; var recorder = payout["recordedBy"].AsInt64; var importer = debit["importedBy"].AsInt64;
        var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray();
        if (!people.Select(person => person.UserId).Order().SequenceEqual(new[] { actor, recorder, importer }.Distinct().Order()) ||
            people.Any(person => person.Verification != PersonVerification.Verified || person.PersonId is null) ||
            !PersonIndependencePolicy.AreIndependent(people.Single(person => person.UserId == actor), people.Single(person => person.UserId == recorder), people.Single(person => person.UserId == importer))) throw B.Broken();
        var originalRecorder = payout["people"].AsBsonArray.Select(ReceivingAccountBson.Person).Single(person => person.UserId == recorder);
        R.Equal(people.Single(person => person.UserId == recorder).PersonId ?? throw B.Broken(), originalRecorder.PersonId ?? throw B.Broken());
        var originalImport = await new BankStatementRecords(database).ImportAsync(tx, debit["importId"].AsInt64, ct);
        var originalImporter = ReceivingAccountBson.Person(originalImport["personAtImport"]);
        if (originalImporter.Verification == PersonVerification.Verified)
            R.Equal(people.Single(person => person.UserId == importer).PersonId ?? throw B.Broken(), originalImporter.PersonId ?? throw B.Broken());
        var result = D.Result(row);
        R.Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), D.Audit(row));
        var operation = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", actor }, { "operation", D.Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (operation.ElementCount != 9 || !operation["_id"].IsObjectId || operation["schemaVersion"] != 1 || !operation["createdAt"].IsBsonDateTime || operation["createdAt"].CompareTo(row["recordedAt"]) > 0) throw B.Broken();
        R.Equal(operation["bodyHash"], row["bodyHash"]); R.Equal(operation["result"], result);
        R.Equal(operation[PersonApprovalReceipt.Field], PersonApprovalReceipt.Create(Permissions.DisbursementVerify, new(1, 1, RecordedBy: recorder, SubmittedBy: importer)));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", D.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var (field, expected) in new (string, BsonValue)[] { ("kind", D.Operation), ("actorId", actor), ("requestKey", row["requestKey"]), ("payload", result), ("createdAt", row["recordedAt"]) }) R.Equal(effect[field], expected);
        var journal = await Rows("journalEntries").Find(tx, new BsonDocument("_id", row["journalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var draft = AccountingBson.ReadDraft(journal); var snapshot = RefundSettlementPostingSource.Snapshot(row); RefundSettlementPostingSource.RequireAccounts(row, draft);
        var liability = approval["policy"];
        R.Equal(row["liabilityAccount"]["mappingKey"], liability["refundLiabilityMapping"]); R.Equal(row["liabilityAccount"]["accountId"], liability["refundLiabilityAccountId"]); R.Equal(row["liabilityAccount"]["accountCode"], liability["refundLiabilityAccountCode"]);
        R.Equal(row["cashAccount"], bankCashAccount);
        R.Equal(row["cashAccount"]["accountId"], payout["source"]["payingBank"]["cashAccountId"]); R.Equal(row["cashAccount"]["accountCode"], payout["source"]["payingBank"]["cashAccountCode"]);
        if (row["postingFence"] != 1L || draft.Source != snapshot.Source || draft.SourceRevision != 1 || draft.Date != snapshot.Date || draft.Book != snapshot.Book ||
            draft.Dimensions != snapshot.Dimensions || draft.ReversalOf is not null || draft.Fingerprint != row["journalFingerprint"].AsString || journal["actorId"] != actor ||
            !draft.Lines.Select(line => new PostingLine(line.MappingKey, line.Debit, line.Credit)).SequenceEqual(snapshot.Lines)) throw B.Broken();
        foreach (var field in new[] { "actorGrantVersion", "reason", "correlationId" }) R.Equal(journal[field], row[field]);
        if (journal["createdAt"].ToUniversalTime() < row["recordedAt"].ToUniversalTime() || journal["createdAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw B.Broken();
        if (await Rows("journalEntries").CountDocumentsAsync(tx, new BsonDocument("sourceEvent", draft.Source.EventKey), cancellationToken: ct) != 1) throw B.Broken();
        var book = await Rows("accountingBooks").Find(tx, new BsonDocument("_id", journal["bookId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        R.Equal(book["key"], AccountingBson.Key(draft.Book));
        var eventKey = "accounting:journal:" + row["journalId"].AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var accountingAudit = await Rows("accountingAuditEvents").Find(tx, new BsonDocument("eventKey", eventKey)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var (field, expected) in new (string, BsonValue)[] { ("schemaVersion", 1), ("kind", "journal.posted"), ("bookId", journal["bookId"]), ("journalId", row["journalId"]),
            ("periodId", BsonNull.Value), ("fingerprint", row["journalFingerprint"]), ("actorId", row["actorId"]), ("actorGrantVersion", row["actorGrantVersion"]), ("reason", row["reason"]), ("correlationId", row["correlationId"]) }) R.Equal(accountingAudit[field], expected);
        if (!accountingAudit["_id"].IsInt64 || accountingAudit["_id"].AsInt64 <= 0 || accountingAudit["createdAt"].ToUniversalTime() < journal["createdAt"].ToUniversalTime() || accountingAudit["createdAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw B.Broken();
        var accountingOutbox = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", eventKey)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        R.Equal(accountingOutbox["kind"], "journal.posted"); R.Equal(accountingOutbox["schemaVersion"], 1);
        R.Equal(accountingOutbox["payload"], new BsonDocument { { "bookId", journal["bookId"] }, { "journalId", row["journalId"] }, { "periodId", BsonNull.Value }, { "auditId", accountingAudit["_id"] } });
        if (accountingOutbox["createdAt"].ToUniversalTime() < accountingAudit["createdAt"].ToUniversalTime() || accountingOutbox["createdAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw B.Broken();
        return row;
    }
}
