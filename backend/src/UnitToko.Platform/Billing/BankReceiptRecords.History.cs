using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Billing.BankReceiptBson;

namespace UnitToko.Platform.Billing;

internal sealed partial class BankReceiptRecords
{
    // Complete retained source proof for writer-paused forward migration. No current actor
    // is fabricated; application callers still use their scoped live owning readers.
    internal static async Task<BsonDocument> ReadReceiptHistoryAsync(IMongoDatabase database, IClientSessionHandle tx, long id, CancellationToken ct) {
        BankTransactionClaims.RequireTransaction(database, tx);
        IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
        var row = await Rows(B.Receipts).Find(tx, new BsonDocument("$and", new BsonArray {
            new BsonDocument("_id", id), BankReceiptSchemas.Validator(B.Receipts) })).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(row["snapshotHash"], B.Hash(row));
        if (row["postingFence"] != 1L || row["journalId"].IsBsonNull || row["journalFingerprint"].IsBsonNull) throw B.Broken();
        var import = await new BankStatementRecords(database).ImportAsync(tx, row["importId"].AsInt64, ct);
        var accounts = new ReceivingAccountRecords(database); var capturedAccount = import["account"].AsBsonDocument;
        var head = await accounts.HeadAsync(tx, capturedAccount["accountId"].AsInt64, ct);
        var account = await accounts.RowAsync(tx, ReceivingAccountBson.Versions, capturedAccount["_id"].AsInt64, ct);
        ReceivingAccountRecords.Hash(account); Equal(account, capturedAccount); Equal(account["scope"], head["scope"]);
        var line = import["preview"]["rows"].AsBsonArray.SingleOrDefault(value => value["number"] == row["rowNumber"])?.AsBsonDocument ?? throw B.Broken();
        var source = new BankStatementReceiptSource(import, line, null);
        var confirmation = await ReadConfirmationAsync(database, tx, row["confirmationId"].AsInt64, ct);
        ConfirmationSource(confirmation, source); SameTransfer(row, source);
        Equal(confirmation["receiptId"], row["_id"]); Equal(confirmation["createdReceipt"], true);
        foreach (var name in new[] { "actorId", "recordedAt", "policy", "importHash", "rowHash" }) Equal(confirmation[name], row[name]);
        Equal(row["importHash"], import["snapshotHash"]); Equal(row["rowHash"], B.Hash(line));
        var journal = await Rows("journalEntries").Find(tx, new BsonDocument("$and", new BsonArray {
            new BsonDocument("_id", row["journalId"]), AccountingSchemas.Validator("journalEntries") })).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var draft = AccountingBson.ReadDraft(journal); var expected = BankReceiptPostingSource.Snapshot(row);
        if (draft.Source != expected.Source || draft.SourceRevision != expected.Revision || draft.Date != expected.Date || draft.Book != expected.Book ||
            draft.Dimensions != expected.Dimensions || draft.ReversalOf is not null || draft.Fingerprint != row["journalFingerprint"].AsString ||
            journal["actorId"] != row["actorId"] || journal["actorGrantVersion"] != confirmation["actorGrantVersion"] ||
            journal["reason"] != confirmation["reason"] || journal["correlationId"] != confirmation["correlationId"] ||
            journal["createdAt"].ToUniversalTime() < row["recordedAt"].ToUniversalTime() ||
            !draft.Lines.Select(value => new PostingLine(value.MappingKey, value.Debit, value.Credit)).SequenceEqual(expected.Lines)) throw B.Broken();
        var key = "accounting:journal:" + row["journalId"].AsInt64;
        var journalAudit = await Rows("accountingAuditEvents").Find(tx, new BsonDocument("$and", new BsonArray {
            new BsonDocument { { "kind", "journal.posted" }, { "eventKey", key } }, AccountingSchemas.Validator("accountingAuditEvents") })).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(journalAudit["journalId"], journal["_id"]); Equal(journalAudit["bookId"], journal["bookId"]); Equal(journalAudit["fingerprint"], journal["fingerprint"]);
        foreach (var name in new[] { "actorId", "actorGrantVersion", "reason", "correlationId" }) Equal(journalAudit[name], journal[name]);
        if (!journalAudit["periodId"].IsBsonNull || journalAudit["createdAt"].ToUniversalTime() < journal["createdAt"].ToUniversalTime()) throw B.Broken();
        var journalEffect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", key)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(journalEffect["kind"], "journal.posted"); Equal(journalEffect["payload"], new BsonDocument {
            { "bookId", journal["bookId"] }, { "journalId", journal["_id"] }, { "periodId", BsonNull.Value }, { "auditId", journalAudit["_id"] } });
        if (journalEffect["createdAt"].ToUniversalTime() < journalAudit["createdAt"].ToUniversalTime()) throw B.Broken();
        var anchor = await Rows(B.Accounts).Find(tx, new BsonDocument("$and", new BsonArray {
            new BsonDocument("physicalIdentity", row["physicalIdentity"]), BankReceiptSchemas.Validator(B.Accounts) })).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(anchor["snapshotHash"], B.Hash(anchor)); Equal(anchor["scope"], row["scope"]); Equal(anchor["identityNamespace"], row["identityNamespace"]);
        var first = await Rows(B.Receipts).Find(tx, new BsonDocument("_id", anchor["firstReceiptId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(first["snapshotHash"], B.Hash(first)); Equal(first["physicalIdentity"], anchor["physicalIdentity"]); Equal(first["scope"], anchor["scope"]);
        Equal(first["identityNamespace"], anchor["identityNamespace"]); Equal(first["recordedAt"], anchor["createdAt"]);
        if (first["postingFence"] != 1L || first["journalId"].IsBsonNull || anchor["createdAt"].ToUniversalTime() > row["recordedAt"].ToUniversalTime()) throw B.Broken();
        return row;
    }
}
