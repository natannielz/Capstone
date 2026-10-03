using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Billing;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementSettlementBson;

namespace UnitToko.Platform.Procurement;

internal sealed class ProcurementSettlementRecords(IMongoDatabase database, IClock clock, ProcurementDisbursementService disbursements)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal async Task<BsonDocument> RowAsync(IClientSessionHandle tx, string name, long id, CancellationToken ct) => await Rows(name).Find(tx,
        new BsonDocument("$and", new BsonArray { new BsonDocument("_id", id), ProcurementSettlementSchemas.Validator(name) })).SingleOrDefaultAsync(ct) ?? throw B.Broken();
    internal static void Review(BsonDocument record, ProcurementSettlementInput input, BsonDocument debit, BsonDocument policy, DateTimeOffset at) {
        input = ProcurementSettlementRules.Copy(input); B.PolicyAt(policy, at);
        Equal(policy["_id"], input.PolicyId); Equal(policy["revision"], input.PolicyRevision); Equal(policy["location"], record["location"]);
        Equal(debit["accountId"], record["input"]["payingAccountId"]); Equal(debit["accountVersionId"], record["input"]["payingAccountVersionId"]);
        foreach (var name in new[] { "payingAccountId", "payingAccountVersionId" }) Equal(policy[name], record["input"][name]);
        Equal(debit["importId"], input.BankImportId); Equal(debit["rowNumber"], input.BankRowNumber);
        Equal(debit["physicalIdentity"], record["source"]["payingBank"]["physicalIdentity"]); Equal(debit["identityNamespace"], record["source"]["payingBank"]["identityNamespace"]);
        if (InvoiceBson.Money(debit["amount"]) != Money.Parse(record["input"]["amount"].AsString) || InvoiceBson.Money(debit["amount"]).Value <= 0 ||
            InvoiceBson.Date(debit["bookedDate"]) < InvoiceBson.Date(record["input"]["transferDate"]) || InvoiceBson.Date(debit["bookedDate"]) > AccountingCalendar.BusinessDate(at)) throw B.Changed("procurement_settlement_bank_mismatch");
        ReceivingAccountPolicy.RequireMatch(ReceivingAccountBson.Details(record["source"]["beneficiary"]["details"]), input.ObservedBeneficiary);
        if (record["recordedAt"].ToUniversalTime() > at.UtcDateTime) throw B.Broken();
    }
    internal async Task<BsonDocument?> ForDisbursementAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        var row = await Rows(B.Events).Find(tx, new BsonDocument("disbursementId", id)).SingleOrDefaultAsync(ct);
        if (row is null) foreach (var (table, field, result) in new[] { ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload"), ("idempotencyRecords", "operation", "result") })
            if (await Rows(table).Find(tx, new BsonDocument { { field, B.Operation }, { result + ".disbursementId", id } }).AnyAsync(ct)) throw B.Broken();
        await OrphansAsync(tx, ct); return row;
    }
    internal async Task<BsonDocument?> ForKeyAsync(IClientSessionHandle tx, long actor, string key, CancellationToken ct) {
        var row = await Rows(B.Events).Find(tx, new BsonDocument { { "actorId", actor }, { "operation", B.Operation }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is null) foreach (var (table, field) in new[] { ("auditEvents", "kind"), ("outboxEvents", "kind"), ("idempotencyRecords", "operation") })
            if (await Rows(table).Find(tx, new BsonDocument { { field, B.Operation }, { "actorId", actor }, { table == "idempotencyRecords" ? "key" : "requestKey", key } }).AnyAsync(ct)) throw B.Broken();
        await OrphansAsync(tx, ct); return row;
    }
    internal async Task OrphansAsync(IClientSessionHandle tx, CancellationToken ct) {
        foreach (var (table, field, local) in new[] { ("auditEvents", "kind", "result.id"), ("outboxEvents", "kind", "payload.id"), ("idempotencyRecords", "operation", "result.id"), ("journalEntries", "source.namespace", "source.documentId") }) {
            var orphan = await Rows(table).Aggregate<BsonDocument>(tx, new BsonDocument[] {
                new("$match", new BsonDocument(field, B.Operation)), new("$lookup", new BsonDocument { { "from", B.Events }, { "localField", local }, { "foreignField", "_id" }, { "as", "owner" } }),
                new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct);
            if (orphan.Count > 0) throw B.Broken();
        }
    }
    internal async Task<BsonDocument> ReadAsync(IClientSessionHandle tx, long id, CancellationToken ct, ProcurementDisbursementSettlementCapture? captured = null) {
        BankTransactionClaims.RequireTransaction(database, tx); var row = await RowAsync(tx, B.Events, id, ct); Equal(row["snapshotHash"], B.Hash(row));
        BsonDocument record;
        if (captured is null) record = await disbursements.ReadSettlementHistoryAsync(tx, row["disbursementId"].AsInt64, ct);
        else { captured.RequireSource(disbursements, tx, row["disbursementId"].AsInt64); record = captured.Record; }
        Equal(row["disbursementHash"], record["snapshotHash"]); foreach (var name in new[] { "draftId", "authorizationId", "location" }) Equal(row[name], record[name]);
        Equal(row["amount"], record["input"]["amount"]); Equal(row["canonicalIdentity"], row["debit"]["canonicalIdentity"]);
        var at = new DateTimeOffset(row["recordedAt"].ToUniversalTime()); if (at > clock.UtcNow || row["postingDate"] != InvoiceBson.Date(AccountingCalendar.BusinessDate(at)) || !row["attested"].AsBoolean) throw B.Broken();
        var input = B.Input(row["input"]); Equal(row["input"], B.Input(input)); var policy = row["policy"].AsBsonDocument; var debit = row["debit"].AsBsonDocument;
        Review(record, input, debit, policy, at); var cash = await BankReceiptService.ReadSettlementDebitHistoryAsync(database, tx, debit, ct);
        await new BankTransactionClaims(database).RequireProcurementDebitAsync(tx, row, ct);
        var import = await new BankStatementRecords(database).ImportAsync(tx, input.BankImportId, ct);
        var sourceRow = import["preview"]["rows"].AsBsonArray.Single(value => value["number"] == input.BankRowNumber); Equal(row["debitReference"], sourceRow["reference"]);
        Equal(row["binding"], B.Binding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64, record, input, debit, policy, row["accounting"].AsBsonDocument, row["postingDate"].AsString));
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(B.Body(record["_id"].AsInt64, 1, input, row["binding"].AsString, true), row["reason"].AsString));
        var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray(); var actor = row["actorId"].AsInt64; var recorder = record["actorId"].AsInt64; var importer = debit["importedBy"].AsInt64;
        if (!people.Select(person => person.UserId).SequenceEqual(new[] { actor, recorder, importer }.Distinct().Order()) || people.Any(person => person is not { Verification: PersonVerification.Verified, PersonId: > 0, Version: > 0 }) ||
            !PersonIndependencePolicy.AreIndependent(people.Single(person => person.UserId == actor), people.Single(person => person.UserId == recorder), people.Single(person => person.UserId == importer))) throw B.Broken();
        var recordedPerson = record["people"].AsBsonArray.Select(ReceivingAccountBson.Person).Single(person => person.UserId == recorder); var importedPerson = ReceivingAccountBson.Person(import["personAtImport"]);
        if (people.Single(person => person.UserId == recorder).PersonId != recordedPerson.PersonId || importedPerson.Verification != PersonVerification.Verified || people.Single(person => person.UserId == importer).PersonId != importedPerson.PersonId) throw B.Broken();
        Equal(await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", actor }, { "operation", B.Operation }, { "key", row["requestKey"] } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (receipt.ElementCount != 9 || !receipt["_id"].IsObjectId || receipt["schemaVersion"] != 1 || receipt["createdAt"].ToUniversalTime() > at.UtcDateTime) throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        Equal(receipt[PersonApprovalReceipt.Field], PersonApprovalReceipt.Create(Permissions.DisbursementVerify, new(1, 1, RecordedBy: recorder, SubmittedBy: importer)));
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var field in new[] { "actorId", "requestKey", "location", "disbursementId" }) Equal(effect[field], row[field]); Equal(effect["kind"], B.Operation); Equal(effect["payload"], B.Result(row)); Equal(effect["createdAt"], row["recordedAt"]);
        await JournalAsync(tx, row, record, cash, ct); return row;
    }
    private async Task JournalAsync(IClientSessionHandle tx, BsonDocument row, BsonDocument record, BsonDocument cash, CancellationToken ct) {
        var accounting = row["accounting"]; var policy = row["policy"]; Equal(accounting["cash"], cash);
        foreach (var name in new[] { "bookId", "chartVersionId" }) Equal(accounting[name], policy[name]);
        foreach (var prefix in new[] { "supplierAdvance", "cash" }) foreach (var (suffix, field) in new[] { ("Mapping", "mappingKey"), ("AccountId", "accountId"), ("AccountCode", "accountCode") }) Equal(accounting[prefix][field], policy[prefix + suffix]);
        Equal(cash["accountId"], record["source"]["payingBank"]["cashAccountId"]); Equal(cash["accountCode"], record["source"]["payingBank"]["cashAccountCode"]);
        var journal = await Rows("journalEntries").Find(tx, new BsonDocument("_id", row["journalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var draft = AccountingBson.ReadDraft(journal); var snapshot = ProcurementSettlementPostingSource.Snapshot(row); ProcurementSettlementPostingSource.RequireAccounts(row, draft);
        if (row["postingFence"] != 1L || draft.Source != snapshot.Source || draft.SourceRevision != 1 || draft.Date != snapshot.Date || draft.Book != snapshot.Book || draft.Dimensions != snapshot.Dimensions ||
            draft.ReversalOf is not null || draft.Fingerprint != row["journalFingerprint"].AsString || !draft.Lines.Select(line => new PostingLine(line.MappingKey, line.Debit, line.Credit)).SequenceEqual(snapshot.Lines)) throw B.Broken();
        Equal(journal["bookId"], accounting["bookId"]); foreach (var field in new[] { "actorId", "actorGrantVersion", "reason", "correlationId" }) Equal(journal[field], row[field]);
        if (journal["createdAt"].ToUniversalTime() < row["recordedAt"].ToUniversalTime() || journal["createdAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime ||
            await Rows("journalEntries").CountDocumentsAsync(tx, new BsonDocument("sourceEvent", draft.Source.EventKey), cancellationToken: ct) != 1) throw B.Broken();
        var book = await Rows("accountingBooks").Find(tx, new BsonDocument("_id", accounting["bookId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(); Equal(book["key"], AccountingBson.Key(draft.Book));
        var chart = await Rows("accountingChartVersions").Find(tx, new BsonDocument("_id", accounting["chartVersionId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var actualChart = AccountingBson.ReadChart(chart); if (actualChart.BookId != book["_id"].AsInt64 || actualChart.Version != draft.ChartVersion) throw B.Broken();
        foreach (var line in draft.Lines) if (!actualChart.Mappings.Any(mapping => mapping.MappingKey == line.MappingKey && mapping.AccountId == line.AccountId && mapping.AccountCode == line.AccountCode)) throw B.Broken();
        var period = await Rows("periods").Find(tx, new BsonDocument("_id", accounting["periodId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(); Equal(period["bookId"], book["_id"]);
        Equal(period["month"], accounting["periodMonth"]); Equal(accounting["periodMonth"], AccountingCalendar.Month(draft.Date));
        var eventKey = "accounting:journal:" + row["journalId"].AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var audit = await Rows("accountingAuditEvents").Find(tx, new BsonDocument("eventKey", eventKey)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var (field, expected) in new (string, BsonValue)[] { ("schemaVersion", 1), ("kind", "journal.posted"), ("bookId", journal["bookId"]), ("journalId", row["journalId"]), ("periodId", BsonNull.Value),
            ("fingerprint", row["journalFingerprint"]), ("actorId", row["actorId"]), ("actorGrantVersion", row["actorGrantVersion"]), ("reason", row["reason"]), ("correlationId", row["correlationId"]) }) Equal(audit[field], expected);
        if (!audit["_id"].IsInt64 || audit["_id"].AsInt64 <= 0 || audit["createdAt"].ToUniversalTime() < journal["createdAt"].ToUniversalTime() || audit["createdAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw B.Broken();
        var effect = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", eventKey)).SingleOrDefaultAsync(ct) ?? throw B.Broken(); Equal(effect["kind"], "journal.posted"); Equal(effect["schemaVersion"], 1);
        Equal(effect["payload"], new BsonDocument { { "bookId", journal["bookId"] }, { "journalId", row["journalId"] }, { "periodId", BsonNull.Value }, { "auditId", audit["_id"] } });
        if (effect["createdAt"].ToUniversalTime() < audit["createdAt"].ToUniversalTime() || effect["createdAt"].ToUniversalTime() > clock.UtcNow.UtcDateTime) throw B.Broken();
    }
}
