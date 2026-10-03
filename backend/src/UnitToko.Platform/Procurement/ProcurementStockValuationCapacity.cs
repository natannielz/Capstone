using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Accounting;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.ProcurementStockValuationBson;

namespace UnitToko.Platform.Procurement;

internal sealed record ProcurementStockValuationCapacity(BsonDocument Receipt, BsonDocument Match,
    BsonDocument? ReceiptHead, BsonDocument? MatchHead);
internal sealed record ProcurementStockValuationReceiptCapacity(BsonDocument State, BsonDocument? Head,
    IReadOnlyList<BsonDocument> Allocations);

internal sealed class ProcurementStockValuationCapacityReader(IMongoDatabase database)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    private async Task OwnerAsync(IClientSessionHandle tx, BsonDocument allocation, CancellationToken ct) {
        var proposal = await Rows(B.Proposals).Find(tx, new BsonDocument("_id", allocation["proposalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var decision = await Rows(B.Decisions).Find(tx, new BsonDocument("_id", allocation["decisionId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (proposal["snapshotHash"] != B.Hash(proposal) || decision["snapshotHash"] != B.Hash(decision) ||
            proposal["snapshotHash"] != allocation["proposalHash"] || decision["proposalId"] != proposal["_id"] ||
            decision["proposalHash"] != proposal["snapshotHash"] || decision["decision"] != "approve" ||
            decision["postingFence"] != 1L || decision["allocationId"] != allocation["_id"] ||
            decision["journalId"] != allocation["journalId"] || decision["input"] != proposal["input"] ||
            decision["receiptBefore"] != allocation["receiptBefore"] || decision["receiptAfter"] != allocation["receiptAfter"] ||
            decision["matchBefore"] != allocation["matchBefore"] || decision["matchAfter"] != allocation["matchAfter"]) throw B.Broken();
        var input = B.Input(proposal["input"]);
        if (allocation["receiptId"] != input.ReceiptId || allocation["invoiceDocumentId"] != input.InvoiceDocumentId ||
            allocation["invoiceMatchId"] != input.InvoiceMatchId || allocation["matchLineNumber"] != input.MatchLineNumber ||
            allocation["receiptHash"] != proposal["receiptHash"] || allocation["matchHash"] != proposal["matchHash"] ||
            allocation["packages"] != input.Packages || allocation["netAmount"] != input.NetAmount.ToString() ||
            allocation["taxAmount"] != input.TaxAmount.ToString() || allocation["chargeAmount"] != input.ChargeAmount.ToString() ||
            allocation["capitalizedAmount"] != input.CapitalizedAmount.ToString() ||
            allocation["receiptSequence"] != allocation["receiptAfter"]["version"] ||
            allocation["matchSequence"] != allocation["matchAfter"]["version"] || allocation["recordedAt"] != decision["recordedAt"]) throw B.Broken();
        var receipt = await Rows(ProcurementStockReceiptBson.Receipts).Find(tx, new BsonDocument { { "_id", input.ReceiptId },
            { "$and", new BsonArray { ProcurementStockReceiptSchemas.Validator(ProcurementStockReceiptBson.Receipts) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var invoice = await Rows(SupplierDocumentBson.Submissions).Find(tx, new BsonDocument { { "_id", input.InvoiceDocumentId },
            { "$and", new BsonArray { SupplierDocumentSchemas.Validator(SupplierDocumentBson.Submissions) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var match = await Rows(SupplierDocumentBson.Matches).Find(tx, new BsonDocument { { "_id", input.InvoiceMatchId },
            { "$and", new BsonArray { SupplierDocumentSchemas.Validator(SupplierDocumentBson.Matches) } } }).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        if (input.MatchLineNumber <= 0 || input.MatchLineNumber > match["lines"].AsBsonArray.Count ||
            input.MatchLineNumber > match["links"].AsBsonArray.Count || match["links"][input.MatchLineNumber - 1].IsBsonNull) throw B.Broken();
        if (receipt["snapshotHash"] != ProcurementStockReceiptBson.Hash(receipt) ||
            invoice["snapshotHash"] != SupplierDocumentBson.Hash(invoice) || match["snapshotHash"] != SupplierDocumentBson.Hash(match) ||
            receipt["snapshotHash"] != proposal["receiptHash"] || invoice["snapshotHash"] != proposal["invoiceHash"] ||
            match["snapshotHash"] != proposal["matchHash"] || match["documentId"] != invoice["_id"] ||
            invoice["input"]["kind"] != "invoice" || receipt["location"] != invoice["location"] ||
            receipt["supplierId"] != invoice["supplierId"] || match["lines"][input.MatchLineNumber - 1]["state"] != "matched" ||
            match["lines"][input.MatchLineNumber - 1]["inspectionId"] != receipt["inspectionId"] ||
            match["lines"][input.MatchLineNumber - 1]["inspectionLineNumber"] != receipt["inspectionLineNumber"] ||
            match["links"][input.MatchLineNumber - 1]["hash"] != receipt["inspection"]["snapshotHash"] ||
            match["links"][input.MatchLineNumber - 1]["orderId"] != receipt["orderId"] ||
            match["lines"][input.MatchLineNumber - 1] != proposal["matchLine"]) throw B.Broken();
        foreach (var row in new[] { proposal, decision }) {
            var auditRow = await Rows("auditEvents").Find(tx, new BsonDocument("_id", row["auditId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            if (auditRow != B.Audit(row)) throw B.Broken();
            var receipts = await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "actorId", row["actorId"] },
                { "operation", row["operation"] }, { "key", row["requestKey"] } }).Limit(2).ToListAsync(ct);
            if (receipts.Count != 1 || receipts[0]["bodyHash"] != row["bodyHash"] || receipts[0]["result"] != B.Result(row)) throw B.Broken();
            var effects = await Rows("outboxEvents").Find(tx, new BsonDocument("sourceKey", B.SourceKey(row))).Limit(2).ToListAsync(ct);
            if (effects.Count != 1 || effects[0]["kind"] != row["operation"] || effects[0]["actorId"] != row["actorId"] ||
                effects[0]["requestKey"] != row["requestKey"] || effects[0]["payload"] != B.Result(row) ||
                effects[0]["createdAt"] != row["recordedAt"]) throw B.Broken();
        }
        var journal = await Rows("journalEntries").Find(tx, new BsonDocument("_id", allocation["journalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var draft = AccountingBson.ReadDraft(journal); var expectedSource = ProcurementStockValuationPostingSource.Snapshot(decision);
        if (journal["sourceEvent"] != ProcurementStockValuationPostingSource.Source(decision["_id"].AsInt64).EventKey ||
            journal["fingerprint"] != decision["journalFingerprint"] || journal["bookId"] != decision["accounting"]["bookId"] ||
            draft.Source != expectedSource.Source || draft.SourceRevision != 1 || draft.Book != expectedSource.Book ||
            draft.Dimensions != expectedSource.Dimensions || draft.Date != expectedSource.Date || draft.ReversalOf is not null ||
            draft.Total != input.CapitalizedAmount || draft.Lines.Length != 2 ||
            draft.Lines[0].MappingKey != expectedSource.Lines[0].MappingKey || draft.Lines[0].Debit != input.CapitalizedAmount ||
            draft.Lines[0].Credit.Value != 0 || draft.Lines[1].MappingKey != expectedSource.Lines[1].MappingKey ||
            draft.Lines[1].Credit != input.CapitalizedAmount || draft.Lines[1].Debit.Value != 0 ||
            journal["actorId"] != decision["actorId"] || journal["actorGrantVersion"] != decision["actorGrantVersion"] ||
            journal["reason"] != decision["reason"] || journal["correlationId"] != decision["correlationId"]) throw B.Broken();
        ProcurementStockValuationPostingSource.RequireAccounts(decision, draft);
        var audit = await Rows("accountingAuditEvents").Find(tx, new BsonDocument("journalId", journal["_id"])).Limit(2).ToListAsync(ct);
        if (audit.Count != 1 || audit[0]["kind"] != "journal.posted" || audit[0]["fingerprint"] != journal["fingerprint"] ||
            audit[0]["eventKey"] != $"accounting:journal:{journal["_id"].AsInt64}" ||
            audit[0]["bookId"] != journal["bookId"] || audit[0]["actorId"] != journal["actorId"] ||
            audit[0]["actorGrantVersion"] != journal["actorGrantVersion"] || audit[0]["reason"] != journal["reason"] ||
            audit[0]["correlationId"] != journal["correlationId"] || !audit[0]["periodId"].IsBsonNull) throw B.Broken();
    }
    internal static string MatchKey(long matchId, int lineNumber) => $"{matchId}:{lineNumber}";
    internal static BsonDocument EmptyReceipt(long packages) => new() { { "version", 0L }, { "allocatedPackages", 0L },
        { "remainingPackages", packages }, { "lastAllocationId", BsonNull.Value }, { "lastHash", BsonNull.Value } };
    internal static BsonDocument EmptyMatch(BsonDocument line) => new() { { "version", 0L }, { "allocatedPackages", 0L },
        { "remainingPackages", (long)line["packages"].AsInt32 }, { "allocatedNetAmount", "0.00" }, { "allocatedTaxAmount", "0.00" },
        { "allocatedChargeAmount", "0.00" }, { "remainingNetAmount", line["netAmount"] }, { "remainingTaxAmount", line["taxAmount"] },
        { "remainingChargeAmount", line["chargeAmount"] }, { "lastAllocationId", BsonNull.Value }, { "lastHash", BsonNull.Value } };
    internal static BsonDocument AfterReceipt(BsonDocument before, int packages, long allocationId, string hash) {
        if (packages <= 0 || before["remainingPackages"].AsInt64 < packages || before["version"].AsInt64 >= long.MaxValue - 1) throw B.Changed("procurement_stock_valuation_receipt_capacity_exceeded");
        var next = before.DeepClone().AsBsonDocument;
        next["version"] = checked(before["version"].AsInt64 + 1); next["allocatedPackages"] = checked(before["allocatedPackages"].AsInt64 + packages);
        next["remainingPackages"] = checked(before["remainingPackages"].AsInt64 - packages); next["lastAllocationId"] = allocationId; next["lastHash"] = hash;
        return next;
    }
    internal static BsonDocument AfterMatch(BsonDocument before, int packages, Money net, Money tax, Money charge, long allocationId, string hash) {
        if (packages <= 0 || before["remainingPackages"].AsInt64 < packages || before["version"].AsInt64 >= long.MaxValue - 1) throw B.Changed("procurement_stock_valuation_match_capacity_exceeded");
        var next = before.DeepClone().AsBsonDocument;
        next["version"] = checked(before["version"].AsInt64 + 1); next["allocatedPackages"] = checked(before["allocatedPackages"].AsInt64 + packages);
        next["remainingPackages"] = checked(before["remainingPackages"].AsInt64 - packages);
        foreach (var (name, amount) in new[] { ("Net", net), ("Tax", tax), ("Charge", charge) }) {
            var remaining = Money.Parse(before[$"remaining{name}Amount"].AsString);
            if (amount.Value > remaining.Value || next["remainingPackages"].AsInt64 == 0 && amount.Value != remaining.Value)
                throw B.Changed("procurement_stock_valuation_match_components_exceeded");
            next[$"allocated{name}Amount"] = Money.Add(Money.Parse(before[$"allocated{name}Amount"].AsString), amount).ToString();
            next[$"remaining{name}Amount"] = new Money(remaining.Value - amount.Value).ToString();
        }
        next["lastAllocationId"] = allocationId; next["lastHash"] = hash; return next;
    }
    private static BsonDocument State(BsonDocument head, bool match) { var state = head.DeepClone().AsBsonDocument;
        foreach (var name in new[] { "_id", "schemaVersion", "receiptId", "matchId", "matchLineNumber", "sourceHash", "createdAt", "updatedAt", "fence" }) state.Remove(name);
        if (match && !state.Contains("remainingNetAmount")) throw B.Broken(); return state; }
    internal async Task<ProcurementStockValuationReceiptCapacity> ReceiptAsync(IClientSessionHandle tx, BsonDocument receipt, CancellationToken ct) {
        var receiptId = receipt["_id"].AsInt64;
        var head = await Rows(B.ReceiptHeads).Find(tx, new BsonDocument("_id", receiptId)).SingleOrDefaultAsync(ct);
        var state = EmptyReceipt(receipt["input"]["packages"].AsInt32);
        var allocations = await Rows(B.Allocations).Find(tx, new BsonDocument("receiptId", receiptId)).Sort(new BsonDocument("receiptSequence", 1)).ToListAsync(ct);
        var approvals = await Rows(B.Decisions).Find(tx, new BsonDocument { { "decision", "approve" },
            { "input.receiptId", receiptId } }).ToListAsync(ct);
        if (approvals.Count != allocations.Count || approvals.Select(row => row["allocationId"]).Distinct().Count() != approvals.Count ||
            !approvals.Select(row => row["allocationId"]).ToHashSet().SetEquals(allocations.Select(row => row["_id"]))) throw B.Broken();
        foreach (var allocation in allocations) {
            await OwnerAsync(tx, allocation, ct);
            if (allocation["receiptHash"] != receipt["snapshotHash"] || allocation["receiptBefore"] != state || allocation["snapshotHash"] != B.Hash(allocation)) throw B.Broken();
            state = AfterReceipt(state, allocation["packages"].AsInt32, allocation["_id"].AsInt64, allocation["proposalHash"].AsString);
            if (allocation["receiptAfter"] != state || allocation["receiptSequence"] != state["version"]) throw B.Broken();
        }
        if ((head is null) != (allocations.Count == 0) || head is not null && (head["sourceHash"] != receipt["snapshotHash"] || State(head, false) != state)) throw B.Broken();
        return new(state, head, allocations);
    }
    private async Task<(BsonDocument State, BsonDocument? Head, IReadOnlyList<BsonDocument> Allocations)> MatchAsync(
        IClientSessionHandle tx, BsonDocument match, int lineNumber, HashSet<BsonValue> provenAllocations, CancellationToken ct) {
        var matchId = match["_id"].AsInt64;
        if (lineNumber <= 0 || lineNumber > match["lines"].AsBsonArray.Count) throw B.Broken();
        var line = match["lines"][lineNumber - 1].AsBsonDocument;
        var matchHead = await Rows(B.MatchHeads).Find(tx, new BsonDocument("_id", MatchKey(matchId, lineNumber))).SingleOrDefaultAsync(ct);
        var matchState = EmptyMatch(line);
        var matchAllocations = await Rows(B.Allocations).Find(tx, new BsonDocument { { "invoiceMatchId", matchId }, { "matchLineNumber", lineNumber } }).Sort(new BsonDocument("matchSequence", 1)).ToListAsync(ct);
        var approvals = await Rows(B.Decisions).Find(tx, new BsonDocument { { "decision", "approve" },
            { "input.invoiceMatchId", matchId }, { "input.matchLineNumber", lineNumber } }).ToListAsync(ct);
        if (approvals.Count != matchAllocations.Count || approvals.Select(row => row["allocationId"]).Distinct().Count() != approvals.Count ||
            !approvals.Select(row => row["allocationId"]).ToHashSet().SetEquals(matchAllocations.Select(row => row["_id"]))) throw B.Broken();
        foreach (var allocation in matchAllocations) {
            if (!provenAllocations.Contains(allocation["_id"])) await OwnerAsync(tx, allocation, ct);
            if (allocation["matchHash"] != match["snapshotHash"] || allocation["matchBefore"] != matchState || allocation["snapshotHash"] != B.Hash(allocation)) throw B.Broken();
            matchState = AfterMatch(matchState, allocation["packages"].AsInt32, Money.Parse(allocation["netAmount"].AsString),
                Money.Parse(allocation["taxAmount"].AsString), Money.Parse(allocation["chargeAmount"].AsString), allocation["_id"].AsInt64,
                allocation["proposalHash"].AsString);
            if (allocation["matchAfter"] != matchState || allocation["matchSequence"] != matchState["version"]) throw B.Broken();
        }
        if ((matchHead is null) != (matchAllocations.Count == 0)) throw B.Broken();
        if (matchHead is not null && (matchHead["sourceHash"] != match["snapshotHash"] || State(matchHead, true) != matchState)) throw B.Broken();
        return (matchState, matchHead, matchAllocations);
    }
    internal async Task VerifyMatchesAsync(IClientSessionHandle tx, ProcurementStockValuationReceiptCapacity receipt,
        IReadOnlyDictionary<long, BsonDocument> verifiedMatches, CancellationToken ct) {
        var proven = receipt.Allocations.Select(row => row["_id"]).ToHashSet();
        foreach (var group in receipt.Allocations.GroupBy(row => (MatchId: row["invoiceMatchId"].AsInt64, LineNumber: row["matchLineNumber"].AsInt32))) {
            if (!verifiedMatches.TryGetValue(group.Key.MatchId, out var match)) throw B.Broken();
            var capacity = await MatchAsync(tx, match, group.Key.LineNumber, proven, ct);
            if (!group.Select(row => row["_id"]).ToHashSet().IsSubsetOf(capacity.Allocations.Select(row => row["_id"]))) throw B.Broken();
        }
    }
    internal async Task<ProcurementStockValuationCapacity> ReadAsync(IClientSessionHandle tx, BsonDocument receipt, BsonDocument match, int lineNumber, CancellationToken ct) {
        var receiptCapacity = await ReceiptAsync(tx, receipt, ct);
        var matchCapacity = await MatchAsync(tx, match, lineNumber, receiptCapacity.Allocations.Select(row => row["_id"]).ToHashSet(), ct);
        return new(receiptCapacity.State, matchCapacity.State, receiptCapacity.Head, matchCapacity.Head);
    }
    internal static BsonDocument Head(BsonDocument state, BsonValue id, BsonDocument source, DateTimeOffset now, BsonDocument? previous,
        long? receiptId = null, long? matchId = null, int? lineNumber = null) {
        var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "sourceHash", source["snapshotHash"] },
            { "createdAt", previous?["createdAt"] ?? now.UtcDateTime }, { "updatedAt", now.UtcDateTime }, { "fence", previous?["fence"] ?? 0L } };
        if (receiptId is { } receipt) row.Add("receiptId", receipt);
        if (matchId is { } match) { row.Add("matchId", match); row.Add("matchLineNumber", lineNumber!.Value); }
        foreach (var element in state.Elements) row.Add(element.DeepClone()); return row;
    }
}
