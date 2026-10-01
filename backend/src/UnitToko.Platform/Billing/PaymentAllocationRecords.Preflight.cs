using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;

namespace UnitToko.Platform.Billing;
internal sealed partial class PaymentAllocationRecords
{
    internal async Task PreflightSourceAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        var verification = await new PaymentVerificationRecords(database).RawAsync(tx, row["verificationId"].AsInt64, ct);
        Equal(verification["snapshotHash"], row["verificationHash"]); Equal(verification["receiptId"], row["receiptId"]); Equal(verification["receiptHash"], row["receiptHash"]);
        Equal(verification["declarationId"], row["declarationId"]); Equal(verification["source"]["revision"], row["declarationRevision"]); Equal(verification["source"]["recordedBy"], row["recordedBy"]); Equal(verification["source"]["party"], row["party"]);
        var bank = await Rows(BankReceiptBson.Receipts).Find(tx, Builders<BsonDocument>.Filter.Eq("_id", row["receiptId"]) & BankReceiptSchemas.Validator(BankReceiptBson.Receipts)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(bank["snapshotHash"], BankReceiptBson.Hash(bank)); Equal(bank["snapshotHash"], row["receiptHash"]); Equal(bank["amount"], row["receiptTotal"]); Equal(row["policy"]["scope"], bank["scope"]); Equal(row["policy"]["suspenseMapping"], bank["policy"]["suspenseMapping"]);
        var import = await new BankStatementRecords(database).ImportAsync(tx, bank["importId"].AsInt64, ct); var transfer = import["preview"]["rows"].AsBsonArray.SingleOrDefault(line => line["number"] == bank["rowNumber"])?.AsBsonDocument ?? throw B.Broken();
        var bankSource = new BankStatementReceiptSource(import, transfer, null); var confirmation = await BankReceiptRecords.ReadConfirmationAsync(database, tx, bank["confirmationId"].AsInt64, ct);
        BankReceiptRecords.ConfirmationSource(confirmation, bankSource); BankReceiptRecords.SameTransfer(bank, bankSource);
        foreach (var field in new[] { "receiptId", "actorId", "recordedAt", "policy" }) Equal(confirmation[field], field == "receiptId" ? bank["_id"] : bank[field]);
        Equal(confirmation["createdReceipt"], true); Equal(bank["importHash"], import["snapshotHash"]); Equal(bank["rowHash"], BankReceiptBson.Hash(transfer));
        if (bank["postingFence"] != 1L || bank["journalId"].IsBsonNull || bank["journalFingerprint"].IsBsonNull) throw B.Broken();
        var bankJournal = await Rows("journalEntries").Find(tx, new BsonDocument("_id", bank["journalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(); var draft = AccountingBson.ReadDraft(bankJournal); var expected = BankReceiptPostingSource.Snapshot(bank);
        if (draft.Source != expected.Source || draft.SourceRevision != 1 || draft.Book != expected.Book || draft.Date != expected.Date || draft.Dimensions != expected.Dimensions || draft.ReversalOf is not null || draft.Fingerprint != bank["journalFingerprint"].AsString || bankJournal["actorId"] != bank["actorId"] || !draft.Lines.Select(line => new PostingLine(line.MappingKey, line.Debit, line.Credit)).SequenceEqual(expected.Lines)) throw B.Broken();
        var suspense = draft.Lines.Single(line => line.MappingKey == row["policy"]["suspenseMapping"].AsString); Equal(row["suspenseAccount"], new BsonDocument { { "accountId", suspense.AccountId }, { "accountCode", suspense.AccountCode } });
        var anchor = await Rows(BankReceiptBson.Accounts).Find(tx, Builders<BsonDocument>.Filter.Eq("physicalIdentity", bank["physicalIdentity"]) & BankReceiptSchemas.Validator(BankReceiptBson.Accounts)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(anchor["snapshotHash"], BankReceiptBson.Hash(anchor)); Equal(anchor["scope"], bank["scope"]); Equal(anchor["identityNamespace"], bank["identityNamespace"]);
        foreach (var line in row["lines"].AsBsonArray) {
            var invoice = await Rows(InvoiceBson.Invoices).Find(tx, new BsonDocument("_id", line["invoiceId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            await InvoiceRecords.VerifyPersistedAsync(database, tx, invoice, ct); await InvoiceService.CaseDispatchStockAsync(database, tx, InvoiceService.CaseSnapshot(invoice), ct);
            Equal(line["source"], await InvoiceService.AllocationSnapshotAsync(database, tx, invoice, ct));
        }
    }
}
