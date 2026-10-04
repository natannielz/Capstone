using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Identity;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Billing;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementAdvanceReturnBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

// Historical owner verification deliberately stops before shared advance-capacity reconstruction.
// The capacity reader supplies exact before/after and sequence proof without recursion.
internal sealed class ProcurementAdvanceReturnRecords(IMongoDatabase database, ProcurementSettlementService settlements)
{
    internal sealed class OwnerProofCache(IClientSessionHandle transaction)
    {
        private readonly long number = transaction.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        private readonly Dictionary<long, (BsonDocument Proposal, BsonDocument Decision, BsonDocument Receipt)> owners = [];
        internal void Require(IClientSessionHandle tx)
        {
            if (!ReferenceEquals(tx, transaction) || !tx.IsInTransaction ||
                tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw B.Broken();
        }
        internal bool TryGet(IClientSessionHandle tx, long decisionId,
            out (BsonDocument Proposal, BsonDocument Decision, BsonDocument Receipt) proof)
        {
            Require(tx); return owners.TryGetValue(decisionId, out proof);
        }
        internal void Add(IClientSessionHandle tx, BsonDocument proposal, BsonDocument decision, BsonDocument receipt)
        {
            Require(tx);
            owners.Add(decision["_id"].AsInt64, (proposal.DeepClone().AsBsonDocument,
                decision.DeepClone().AsBsonDocument, receipt.DeepClone().AsBsonDocument));
        }
    }
    private static readonly FilterDefinitionBuilder<BsonDocument> F = Builders<BsonDocument>.Filter;
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private static void Equal(BsonValue actual, BsonValue expected) { if (!actual.Equals(expected)) throw B.Broken(); }
    internal static async Task ProveLineAsync(IMongoDatabase database, IClientSessionHandle tx, BsonDocument line,
        ProcurementPayableAdvanceSource advance, ProcurementSettlementService settlements, CancellationToken ct,
        OwnerProofCache? cache = null) =>
        await new ProcurementAdvanceReturnRecords(database, settlements).LineAsync(tx, line, advance, ct, cache);

    internal async Task<(BsonDocument Facts, Dictionary<long, ProcurementPayableAdvanceSource> Advances)> FactsAsync(
        IClientSessionHandle tx, ProcurementAdvanceReturnInput input, BankReceiptPaymentSource bank,
        BsonDocument suspense, CancellationToken ct)
    {
        var receipt = bank.Receipt; var scope = receipt["scope"];
        if (scope["fulfillmentOrganizationId"] != input.Location.OrganizationId || scope["shopId"] != input.Location.ShopId ||
            scope["channelId"] != input.Location.ChannelId || !scope["warehouseId"].IsBsonNull ||
            receipt["_id"] != input.ReceiptId || InvoiceBson.Money(receipt["amount"]) != ProcurementAdvanceReturnRules.Total(input) ||
            input.PostingDate < InvoiceBson.Date(receipt["bookedDate"])) throw B.Changed("procurement_advance_return_receipt_mismatch");
        var journal = await Rows("journalEntries").Find(tx, F.Eq("_id", receipt["journalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var bookId = journal["bookId"]; var advances = new Dictionary<long, ProcurementPayableAdvanceSource>();
        var lines = new BsonArray(); BsonValue? supplierId = null, orderId = null, supplierName = null, commercialReference = null;
        foreach (var selected in input.Lines) {
            var advance = await settlements.ReadPayableSourceAsync(database, tx, selected.SettlementId, ct);
            advances.Add(selected.SettlementId, advance);
            var settlement = advance.Settlement; var disbursement = advance.Disbursement;
            var orderSource = disbursement["source"]["funding"]["request"]["orderSource"];
            var supplier = PurchaseOrderBson.SupplierView(orderSource["proposal"]["supplier"].AsBsonDocument);
            var currentSupplier = disbursement["input"]["supplierId"];
            if (settlement["location"] != R.Location(input.Location) ||
                orderSource["proposal"]["input"]["supplierId"] != currentSupplier ||
                supplier["id"] != currentSupplier || settlement["accounting"]["bookId"] != bookId ||
                input.PostingDate < AccountingCalendar.BusinessDate(new DateTimeOffset(settlement["recordedAt"].ToUniversalTime())))
                throw B.Changed("procurement_advance_return_source_mismatch");
            supplierId ??= currentSupplier; orderId ??= orderSource["order"]["_id"];
            supplierName ??= supplier["displayName"];
            commercialReference ??= orderSource["proposal"]["input"]["commercialReference"]["reference"];
            if (supplierId != currentSupplier || orderId != orderSource["order"]["_id"] ||
                selected.Amount.Value > Money.Parse(settlement["amount"].AsString).Value) throw B.Changed("procurement_advance_return_source_mismatch");
            lines.Add(new BsonDocument { { "settlementId", settlement["_id"] }, { "settlementHash", settlement["snapshotHash"] },
                { "disbursementId", disbursement["_id"] }, { "settlementAmount", settlement["amount"] },
                { "returnAmount", selected.Amount.ToString() }, { "sourceAdvanceAccount", settlement["accounting"]["supplierAdvance"] } });
        }
        var facts = new BsonDocument { { "receiptId", receipt["_id"] }, { "receiptHash", receipt["snapshotHash"] },
            { "receiptAmount", InvoiceBson.Money(receipt["amount"]).ToString() }, { "bookedDate", receipt["bookedDate"] },
            { "bankReference", bank.BankRow["reference"] }, { "bankAccountId", bank.Account["accountId"] },
            { "bankAccountVersionId", bank.Account["_id"] }, { "location", R.Location(input.Location) },
            { "supplierId", supplierId ?? throw B.Broken() }, { "supplierName", supplierName ?? throw B.Broken() },
            { "orderId", orderId ?? throw B.Broken() }, { "commercialReference", commercialReference ?? throw B.Broken() },
            { "bookId", bookId },
            { "suspenseAccount", new BsonDocument {
                { "mappingKey", receipt["policy"]["suspenseMapping"] },
                { "accountId", suspense["accountId"] },
                { "accountCode", suspense["accountCode"] }
            } },
            { "lines", lines }
        };
        return (facts, advances);
    }

    internal async Task<BsonDocument> ProposalAsync(IClientSessionHandle tx, long id, CancellationToken ct)
    {
        var row = await Rows(B.Proposals).Find(tx, F.Eq("_id", id) & ProcurementAdvanceReturnSchemas.Validator(B.Proposals))
            .SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(row["snapshotHash"], B.Hash(row)); var input = B.Input(row["input"]);
        Equal(row["input"], B.Input(input)); Equal(row["location"], R.Location(input.Location));
        Equal(row["policy"]["_id"], input.PolicyId); Equal(row["policy"]["revision"], input.PolicyRevision);
        Equal(row["binding"], B.Binding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64,
            row["input"].AsBsonDocument, row["source"].AsBsonDocument, row["policy"].AsBsonDocument,
            row["accounting"].AsBsonDocument, row["capacity"].AsBsonDocument));
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(B.ProposeBody(input, row["binding"].AsString), row["reason"].AsString));
        HistoryPeople(row, null); await SourceAsync(tx, row, ct); await EffectsAsync(tx, row, ct); return row;
    }
    internal async Task<BsonDocument?> DecisionAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct)
    {
        var rows = await Rows(B.Decisions).Find(tx, F.Eq("proposalId", proposal["_id"])).Limit(2).ToListAsync(ct);
        if (rows.Count > 1) throw B.Broken();
        if (rows.Count == 0) {
            await RequireNoMissingDecisionAsync(tx, proposal, ct);
            return null;
        }
        var row = rows[0]; Equal(row["snapshotHash"], B.Hash(row)); Equal(row["proposalHash"], proposal["snapshotHash"]);
        Equal(row["proposalMakerId"], proposal["actorId"]); HistoryPeople(proposal, row);
        foreach (var field in new[] { "location", "input", "source", "capacity", "policy", "accounting", "amount" }) Equal(row[field], proposal[field]);
        var choice = row["decision"] == "approve" ? ProcurementAdvanceReturnDecision.Approve : ProcurementAdvanceReturnDecision.Reject;
        Equal(row["binding"], B.DecisionBinding(row["actorId"].AsInt64, row["actorGrantVersion"].AsInt64,
            proposal, choice, row["source"].AsBsonDocument, row["policy"].AsBsonDocument,
            row["accounting"].AsBsonDocument, row["capacity"].AsBsonDocument));
        Equal(row["bodyHash"], AuthorizationStore.CommandHash(B.DecideBody(proposal["_id"].AsInt64, 1, choice, row["binding"].AsString), row["reason"].AsString));
        Equal(row["receiptId"], proposal["source"]["receiptId"]);
        if (row["recordedAt"].ToUniversalTime() < proposal["recordedAt"].ToUniversalTime()) throw B.Broken();
        if (choice == ProcurementAdvanceReturnDecision.Approve) {
            await PostedAsync(tx, row, ct);
            var expectedIds = row["returnLineIds"].AsBsonArray;
            if (expectedIds.Count != B.Input(row["input"]).Lines.Length || expectedIds.Distinct().Count() != expectedIds.Count) throw B.Broken();
            var lines = await Rows(B.Lines).Find(tx, F.Eq("decisionId", row["_id"])).ToListAsync(ct);
            if (lines.Count != expectedIds.Count || !expectedIds.ToHashSet().SetEquals(lines.Select(line => line["_id"]))) throw B.Broken();
            var (bank, _) = await BankReceiptService.ReadSupplierReturnHistoryAsync(database, tx, row["receiptId"].AsInt64, ct);
            await new PaymentReceiptPurposeRecords(database).RequireSupplierReturnAsync(tx, bank.Receipt, row, ct);
        } else if (row["returnLineIds"].AsBsonArray.Count != 0 || !row["journalId"].IsBsonNull ||
            !row["journalFingerprint"].IsBsonNull || row["postingFence"] != 0L) throw B.Broken();
        await EffectsAsync(tx, row, ct); return row;
    }
    private async Task RequireNoMissingDecisionAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct)
    {
        var proposalId = proposal["_id"];
        if (await Rows(B.Lines).Find(tx, F.Eq("proposalId", proposalId)).AnyAsync(ct)) throw B.Broken();
        foreach (var (table, operationField, resultField) in new[] {
            ("auditEvents", "kind", "result"),
            ("outboxEvents", "kind", "payload"),
            ("idempotencyRecords", "operation", "result") })
            if (await Rows(table).Find(tx, F.Eq(operationField, B.DecideOperation) &
                F.Eq(resultField + ".proposalId", proposalId) & F.Eq(resultField + ".status", "returned")).AnyAsync(ct))
                throw B.Broken();
        var purpose = await Rows(PaymentVerificationBson.Parties)
            .Find(tx, F.Eq("_id", proposal["source"]["receiptId"])).SingleOrDefaultAsync(ct);
        if (purpose is not null && purpose.GetValue("schemaVersion", BsonNull.Value) == 2) {
            var owner = await Rows(B.Decisions).Find(tx, F.Eq("_id", purpose["firstReturnId"])).SingleOrDefaultAsync(ct)
                ?? throw B.Broken();
            if (owner["proposalId"] == proposalId) throw B.Broken();
        }
    }
    private async Task LineAsync(IClientSessionHandle tx, BsonDocument line, ProcurementPayableAdvanceSource advance,
        CancellationToken ct, OwnerProofCache? cache)
    {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        var stored = await Rows(B.Lines).Find(tx, F.Eq("_id", line["_id"]) & ProcurementAdvanceReturnSchemas.Validator(B.Lines)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(stored, line); Equal(line["snapshotHash"], B.Hash(line));
        BsonDocument proposal, decision;
        if (cache is not null && cache.TryGet(tx, line["decisionId"].AsInt64, out var proof)) {
            proposal = proof.Proposal; decision = proof.Decision;
            Equal(proposal["snapshotHash"], B.Hash(proposal)); Equal(decision["snapshotHash"], B.Hash(decision));
            var current = await Rows(B.Decisions).Find(tx, F.Eq("_id", decision["_id"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            Equal(current, decision);
            await new PaymentReceiptPurposeRecords(database).RequireSupplierReturnAsync(tx, proof.Receipt, decision, ct);
        } else {
            proposal = await ProposalAsync(tx, line["proposalId"].AsInt64, ct);
            decision = await DecisionAsync(tx, proposal, ct) ?? throw B.Broken();
            if (cache is not null) {
                var (bank, _) = await BankReceiptService.ReadSupplierReturnHistoryAsync(database, tx, decision["receiptId"].AsInt64, ct);
                cache.Add(tx, proposal, decision, bank.Receipt);
            }
        }
        if (decision["decision"] != "approve" || line["decisionId"] != decision["_id"] ||
            line["proposalId"] != proposal["_id"] ||
            line["proposalHash"] != proposal["snapshotHash"] || line["receiptId"] != decision["receiptId"] ||
            !decision["returnLineIds"].AsBsonArray.Contains(line["_id"]) || line["journalId"] != decision["journalId"] ||
            line["settlementHash"] != advance.Settlement["snapshotHash"] || line["recordedAt"] != decision["recordedAt"])
            throw B.Broken();
        var input = B.Input(proposal["input"]); var selected = input.Lines.SingleOrDefault(value => value.SettlementId == line["settlementId"].AsInt64)
            ?? throw B.Broken();
        Equal(line["amount"], selected.Amount.ToString());
        var source = proposal["source"]["lines"].AsBsonArray.Single(value => value["settlementId"] == line["settlementId"]);
        Equal(source["settlementHash"], advance.Settlement["snapshotHash"]); Equal(source["returnAmount"], line["amount"]);
    }
    private async Task SourceAsync(IClientSessionHandle tx, BsonDocument proposal, CancellationToken ct)
    {
        var input = B.Input(proposal["input"]); var (bank, suspense) = await BankReceiptService.ReadSupplierReturnHistoryAsync(database, tx, input.ReceiptId, ct);
        var (facts, _) = await FactsAsync(tx, input, bank, suspense, ct); Equal(proposal["source"], facts);
    }
    private static void HistoryPeople(BsonDocument proposal, BsonDocument? decision)
    {
        var originals = proposal["people"].AsBsonArray.Select(R.Person).ToArray();
        if (originals.Length is < 1 or > 4 || !originals.Any(person => person.UserId == proposal["actorId"].AsInt64) ||
            originals.Any(person => person.Verification != PersonVerification.Verified || person.PersonId <= 0)) throw B.Broken();
        if (decision is null) return;
        var people = decision["people"].AsBsonArray.Select(R.Person).ToArray();
        if (people.Length is < 2 or > 4 || !people.Any(person => person.UserId == decision["actorId"].AsInt64) ||
            !people.Any(person => person.UserId == proposal["actorId"].AsInt64) ||
            !PersonIndependencePolicy.AreIndependent(people.Single(person => person.UserId == decision["actorId"].AsInt64),
                people.Single(person => person.UserId == proposal["actorId"].AsInt64))) throw B.Broken();
    }
    private async Task EffectsAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct)
    {
        Equal(await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken(), B.Audit(row));
        var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", row["actorId"]) & F.Eq("operation", row["operation"]) &
            F.Eq("key", row["requestKey"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(receipt["bodyHash"], row["bodyHash"]); Equal(receipt["result"], B.Result(row));
        if (row["operation"] == B.DecideOperation)
            Equal(receipt[PersonApprovalReceipt.Field], PersonApprovalReceipt.Create(Permissions.ProcurementAdvanceReturnDecide,
                new ApprovalRequest(1, 1, RequestedBy: row["proposalMakerId"].AsInt64)));
        var effect = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", B.SourceKey(row))).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        foreach (var field in new[] { "kind", "actorId", "requestKey", "location", "payload", "createdAt" }) Equal(effect[field], B.Outbox(row)[field]);
    }
    private async Task PostedAsync(IClientSessionHandle tx, BsonDocument decision, CancellationToken ct)
    {
        if (decision["postingFence"] != 1L || decision["journalId"].IsBsonNull || decision["journalFingerprint"].IsBsonNull) throw B.Broken();
        var journal = await Rows("journalEntries").Find(tx, F.Eq("_id", decision["journalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var draft = AccountingBson.ReadDraft(journal); var snapshot = ProcurementAdvanceReturnPostingSource.Snapshot(decision);
        ProcurementAdvanceReturnPostingSource.RequireAccounts(decision, draft);
        if (draft.Fingerprint != decision["journalFingerprint"].AsString || draft.Source != snapshot.Source || draft.SourceRevision != 1 ||
            journal["bookId"] != decision["accounting"]["bookId"] ||
            await Rows("journalEntries").CountDocumentsAsync(tx, F.Eq("sourceEvent", draft.Source.EventKey), cancellationToken: ct) != 1) throw B.Broken();
        foreach (var field in new[] { "actorId", "actorGrantVersion", "reason", "correlationId" }) Equal(journal[field], decision[field]);
        var key = "accounting:journal:" + decision["journalId"].AsInt64.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var audit = await Rows("accountingAuditEvents").Find(tx, F.Eq("eventKey", key)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(audit["journalId"], decision["journalId"]); Equal(audit["fingerprint"], decision["journalFingerprint"]);
        var outbox = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", key)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(outbox["kind"], "journal.posted"); Equal(outbox["payload"]["journalId"], decision["journalId"]);
    }
}
