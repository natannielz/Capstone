using MongoDB.Bson;
using UnitToko.Platform.Accounting;

namespace UnitToko.Platform.Billing;

internal sealed partial class OrderTimelineHistory
{
    private readonly HashSet<long> capturedAccounts = [];
    private readonly HashSet<long> capturedReceipts = [];

    // These methods retain dependencies of the already authorized historical owner
    // proofs. They neither expose bank data nor manufacture a finance permission.
    private async Task CaptureReceivingAccountAsync(long accountId, CancellationToken ct) {
        if (!capturedAccounts.Add(accountId)) return;
        var owner = new ReceivingAccountRecords(database);
        var head = await owner.HeadAsync(tx, accountId, ct);
        Hold(ReceivingAccountBson.Heads, head);
        var root = await owner.ProposalAsync(tx, head["rootProposalId"].AsInt64, ct);
        Hold(ReceivingAccountBson.Proposals, root);
        await EffectsAsync(root, root["operation"].AsString, "actorId", ct, ReceivingAccountBson.SourceKey(root));
        var versions = await CaptureAsync(ReceivingAccountBson.Versions, new("accountId", accountId), ct);
        var decisions = await CaptureAsync(ReceivingAccountBson.Decisions, new() { { "accountId", accountId }, { "decision", "approve" } }, ct);
        if (!decisions.Select(row => row["_id"]).Order().SequenceEqual(versions.Select(row => row["decisionId"]).Order())) throw Broken();
        foreach (var version in versions) {
            var proposal = await owner.ProposalAsync(tx, version["proposalId"].AsInt64, ct);
            var decision = await owner.DecisionAsync(tx, version["decisionId"].AsInt64, proposal, ct);
            var observation = await owner.ObservationAsync(tx, version["observationId"].AsInt64, proposal, ct);
            Hold(ReceivingAccountBson.Versions, version);
            foreach (var (collection, row) in new[] { (ReceivingAccountBson.Proposals, proposal),
                (ReceivingAccountBson.Decisions, decision), (ReceivingAccountBson.Observations, observation) }) {
                Hold(collection, row);
                await EffectsAsync(row, row["operation"].AsString, "actorId", ct, ReceivingAccountBson.SourceKey(row));
            }
        }
    }

    private async Task CaptureImportAsync(long importId, CancellationToken ct) {
        var import = await new BankStatementRecords(database).ImportAsync(tx, importId, ct);
        Hold(BankStatementBson.Imports, import);
        await EffectsAsync(import, BankStatementBson.Operation, "actorId", ct, BankStatementBson.SourceKey(import));
        await CaptureReceivingAccountAsync(import["account"]["accountId"].AsInt64, ct);
        var account = await HoldAsync(ReceivingAccountBson.Versions, new("_id", import["account"]["_id"]), ct);
        Equal(account, import["account"]);
    }

    private async Task CaptureBankJournalAsync(long journalId, bool requireBook, CancellationToken ct) {
        var journal = await HoldAsync("journalEntries", new("_id", journalId), ct);
        var key = "accounting:journal:" + journalId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await HoldAsync("accountingAuditEvents", new("eventKey", key), ct);
        await HoldAsync("outboxEvents", new("sourceKey", key), ct);
        var siblings = await CaptureAsync("journalEntries", new("sourceEvent", journal["sourceEvent"]), ct);
        if (siblings.Count != 1 || siblings[0]["_id"] != journalId) throw Broken();
        if (requireBook) {
            var book = await HoldAsync("accountingBooks", new("_id", journal["bookId"]), ct);
            Equal(book["key"], AccountingBson.Key(AccountingBson.ReadDraft(journal).Book));
        }
    }

    internal async Task CaptureBankReceiptAsync(BsonDocument expected, CancellationToken ct) {
        var receipt = await BankReceiptRecords.ReadReceiptHistoryAsync(database, tx, expected["_id"].AsInt64, ct);
        Equal(receipt, expected);
        Hold(BankReceiptBson.Receipts, receipt);
        if (!capturedReceipts.Add(receipt["_id"].AsInt64)) return;
        await new BankTransactionClaims(database).RequireCreditAsync(tx, receipt, ct);
        await HoldAsync(BankTransactionClaims.Collection, new("_id", receipt["canonicalIdentity"]), ct);
        await CaptureImportAsync(receipt["importId"].AsInt64, ct);
        var confirmation = await HoldAsync(BankReceiptBson.Confirmations, new("_id", receipt["confirmationId"]), ct);
        await EffectsAsync(confirmation, BankReceiptBson.Operation, "actorId", ct, BankReceiptBson.SourceKey(confirmation));
        await CaptureBankJournalAsync(receipt["journalId"].AsInt64, false, ct);
        var anchor = await HoldAsync(BankReceiptBson.Accounts, new("physicalIdentity", receipt["physicalIdentity"]), ct);
        if (anchor["firstReceiptId"] != receipt["_id"]) {
            var first = await BankReceiptRecords.ReadReceiptHistoryAsync(database, tx, anchor["firstReceiptId"].AsInt64, ct);
            await CaptureBankReceiptAsync(first, ct);
        }
    }

    internal async Task CapturePayoutBankAsync(BsonDocument compact, CancellationToken ct) {
        // ReadPayoutBankHistoryAsync has already authenticated this compact source.
        await CaptureReceivingAccountAsync(compact["accountId"].AsInt64, ct);
        await HoldAsync(ReceivingAccountBson.Versions, new("_id", compact["versionId"]), ct);
        var anchor = await HoldAsync(BankReceiptBson.Accounts, new("physicalIdentity", compact["physicalIdentity"]), ct);
        Equal(anchor["firstReceiptId"], compact["firstReceiptId"]);
        var first = await BankReceiptRecords.ReadReceiptHistoryAsync(database, tx, anchor["firstReceiptId"].AsInt64, ct);
        await CaptureBankReceiptAsync(first, ct);
    }

    internal async Task CaptureSettlementBankAsync(BsonDocument settlement, CancellationToken ct) {
        // RefundSettlementRecords.ReadAsync has authenticated the complete debit and
        // posting graph. Keep that graph alive through the final timeline return.
        var debit = settlement["debit"].AsBsonDocument;
        await CaptureImportAsync(debit["importId"].AsInt64, ct);
        var anchor = await HoldAsync(BankReceiptBson.Accounts, new("physicalIdentity", debit["physicalIdentity"]), ct);
        var first = await BankReceiptRecords.ReadReceiptHistoryAsync(database, tx, anchor["firstReceiptId"].AsInt64, ct);
        await CaptureBankReceiptAsync(first, ct);
        await CaptureBankJournalAsync(settlement["journalId"].AsInt64, true, ct);
    }
}
