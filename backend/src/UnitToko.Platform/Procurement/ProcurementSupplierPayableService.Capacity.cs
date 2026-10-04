using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Platform.Billing;
using B = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;
using T = UnitToko.Platform.Procurement.ProcurementAdvanceReturnBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementSupplierPayableService
{
    private async Task<BsonDocument> PayableRootAsync(IClientSessionHandle tx, long id, CancellationToken ct,
        HistoricalProof? proof = null)
    {
        proof?.Require(tx);
        var payable = await Rows(B.Payables).Find(tx, F.Eq("_id", id)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(payable["snapshotHash"], B.Hash(payable));
        var decision = await Rows(B.Decisions).Find(tx, F.Eq("_id", payable["decisionId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var proposal = await Rows(B.Proposals).Find(tx, F.Eq("_id", decision["proposalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(proposal["snapshotHash"], B.Hash(proposal)); Equal(decision["snapshotHash"], B.Hash(decision));
        if (proposal["input"]["kind"] != "recognize" || decision["decision"] != "approve" ||
            decision["payableId"] != id || !decision["applicationId"].IsBsonNull ||
            proposal["source"]["valuationAllocationId"] != payable["valuationAllocationId"] ||
            proposal["source"]["amount"] != payable["amount"] || decision["proposalHash"] != proposal["snapshotHash"] ||
            payable["journalId"] != decision["journalId"] || payable["journalFingerprint"] != decision["journalFingerprint"])
            throw B.Broken();
        foreach (var field in new[] { "location", "orderId", "supplierId", "supplierName" }) Equal(payable[field], proposal["source"][field]);
        Equal(payable["valuationAllocationHash"], proposal["source"]["valuationAllocationHash"]);
        Equal(payable["accounting"], decision["accounting"]); Equal(payable["recordedAt"], decision["recordedAt"]);
        await PostedAsync(tx, decision, ct); await EffectsAsync(tx, proposal, ct); await EffectsAsync(tx, decision, ct);
        var valuation = proof?.Valuation is { } verified && verified.Allocation["_id"] == payable["valuationAllocationId"]
            ? verified : await valuations.ReadPayableSourceAsync(database, tx, payable["valuationAllocationId"].AsInt64, ct);
        Equal(valuation.Allocation["snapshotHash"], payable["valuationAllocationHash"]);
        Equal(valuation.Allocation["capitalizedAmount"], payable["amount"]);
        Equal(valuation.Receipt["orderId"], payable["orderId"]); Equal(valuation.Receipt["supplierId"], payable["supplierId"]);
        Equal(proposal["source"], RecognizeFacts(valuation));
        var originalInput = B.Input(proposal["input"]);
        if (originalInput.ValuationAllocationId != payable["valuationAllocationId"].AsInt64 ||
            originalInput.PostingDate < AccountingCalendar.BusinessDate(new DateTimeOffset(valuation.Decision["recordedAt"].ToUniversalTime()))) throw B.Broken();
        Equal(payable["commercialReference"], proposal["source"]["commercialReference"]);
        Equal(payable["invoiceDocumentReference"], proposal["source"]["invoiceDocumentReference"]);
        SourceAccounting(proposal["source"].AsBsonDocument, proposal["accounting"].AsBsonDocument);
        return payable;
    }
    private async Task<BsonDocument> CapacityAsync(IClientSessionHandle tx, BsonDocument payable,
        ProcurementPayableAdvanceSource advance, CancellationToken ct)
    {
        var settlement = advance.Settlement;
        var provenPayables = new Dictionary<long, BsonDocument> { [payable["_id"].AsInt64] = payable };
        var provenAdvances = new Dictionary<long, ProcurementPayableAdvanceSource> { [settlement["_id"].AsInt64] = advance };
        async Task<BsonDocument> Payable(long id)
        {
            if (!provenPayables.TryGetValue(id, out var row)) provenPayables[id] = row = await PayableRootAsync(tx, id, ct);
            return row;
        }
        async Task<ProcurementPayableAdvanceSource> Advance(long id)
        {
            if (!provenAdvances.TryGetValue(id, out var row)) provenAdvances[id] = row = await settlements.ReadPayableSourceAsync(database, tx, id, ct);
            return row;
        }
        var provenApplications = new HashSet<long>();
        async Task ProveApplication(BsonDocument allocation)
        {
            if (provenApplications.Add(allocation["_id"].AsInt64)) await ApplicationOwnerAsync(tx, allocation, Payable, Advance, ct);
        }
        var payableState = B.EmptyPayable(payable["_id"].AsInt64, payable);
        var payableAllocations = await Rows(B.Applications).Find(tx, F.Eq("payableId", payable["_id"]))
            .Sort(new BsonDocument("payableSequence", 1)).ToListAsync(ct);
        var payableApprovals = await Rows(B.Decisions).Find(tx, F.Eq("decision", "approve") & F.Eq("source.kind", "apply_advance") &
            F.Eq("source.payableId", payable["_id"])).ToListAsync(ct);
        static void ExactOwners(List<BsonDocument> approvals, List<BsonDocument> allocations)
        {
            if (approvals.Count != allocations.Count || approvals.Select(row => row["applicationId"]).Distinct().Count() != approvals.Count ||
                !approvals.Select(row => row["applicationId"]).ToHashSet().SetEquals(allocations.Select(row => row["_id"]))) throw B.Broken();
        }
        ExactOwners(payableApprovals, payableAllocations);
        foreach (var allocation in payableAllocations)
        {
            await ProveApplication(allocation);
            Equal(allocation["payableBefore"], payableState);
            payableState = B.After(payableState, Money.Parse(allocation["amount"].AsString), allocation["_id"].AsInt64, allocation["proposalHash"].AsString);
            Equal(allocation["payableAfter"], payableState); Equal(allocation["payableSequence"], payableState["version"]);
        }
        var (advanceState, advanceHead) = await AdvanceStateAsync(tx, advance, ProveApplication, ct);
        var payableHead = await Rows(B.PayableHeads).Find(tx, F.Eq("_id", payable["_id"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(HeadState(payableHead), payableState);
        if (payableHead["sourceHash"] != payable["snapshotHash"]) throw B.Broken();
        return new() { { "payable", payableState }, { "advance", advanceState },
            { "payableHead", payableHead }, { "advanceHead", (BsonValue?)advanceHead ?? BsonNull.Value } };
    }
    private static BsonDocument HeadState(BsonDocument head)
    {
        var state = head.DeepClone().AsBsonDocument;
        foreach (var field in new[] { "_id", "schemaVersion", "sourceHash", "createdAt", "updatedAt", "fence" }) state.Remove(field);
        return state;
    }
    private async Task<BsonDocument> PayableOnlyCapacityAsync(IClientSessionHandle tx, BsonDocument payable, CancellationToken ct)
    {
        var state = B.EmptyPayable(payable["_id"].AsInt64, payable);
        var allocations = await Rows(B.Applications).Find(tx, F.Eq("payableId", payable["_id"]))
            .Sort(new BsonDocument("payableSequence", 1)).ToListAsync(ct);
        var approvals = await Rows(B.Decisions).Find(tx, F.Eq("decision", "approve") & F.Eq("source.kind", "apply_advance") &
            F.Eq("source.payableId", payable["_id"])).ToListAsync(ct);
        if (approvals.Count != allocations.Count || !approvals.Select(row => row["applicationId"]).ToHashSet().SetEquals(allocations.Select(row => row["_id"]))) throw B.Broken();
        var payables = new Dictionary<long, BsonDocument> { [payable["_id"].AsInt64] = payable };
        var advances = new Dictionary<long, ProcurementPayableAdvanceSource>();
        async Task<BsonDocument> Payable(long id) => payables.TryGetValue(id, out var row) ? row : payables[id] = await PayableRootAsync(tx, id, ct);
        async Task<ProcurementPayableAdvanceSource> Advance(long id) => advances.TryGetValue(id, out var row) ? row : advances[id] = await settlements.ReadPayableSourceAsync(database, tx, id, ct);
        foreach (var allocation in allocations) {
            await ApplicationOwnerAsync(tx, allocation, Payable, Advance, ct);
            Equal(allocation["payableBefore"], state);
            state = B.After(state, Money.Parse(allocation["amount"].AsString), allocation["_id"].AsInt64, allocation["proposalHash"].AsString);
            Equal(allocation["payableAfter"], state); Equal(allocation["payableSequence"], state["version"]);
        }
        var head = await Rows(B.PayableHeads).Find(tx, F.Eq("_id", payable["_id"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(head["sourceHash"], payable["snapshotHash"]); Equal(HeadState(head), state); return state;
    }
    private async Task<BsonDocument> AdvanceOnlyCapacityAsync(IClientSessionHandle tx, ProcurementPayableAdvanceSource advance,
        CancellationToken ct, ProcurementAdvanceReturnRecords.OwnerProofCache? returnProofs = null)
    {
        var settlement = advance.Settlement;
        var provenApplications = new HashSet<long>();
        var payables = new Dictionary<long, BsonDocument>();
        var advances = new Dictionary<long, ProcurementPayableAdvanceSource> { [settlement["_id"].AsInt64] = advance };
        async Task<BsonDocument> Payable(long id) => payables.TryGetValue(id, out var row) ? row : payables[id] = await PayableRootAsync(tx, id, ct);
        async Task<ProcurementPayableAdvanceSource> Advance(long id) => advances.TryGetValue(id, out var row) ? row : advances[id] = await settlements.ReadPayableSourceAsync(database, tx, id, ct);
        async Task ProveApplication(BsonDocument row) {
            if (provenApplications.Add(row["_id"].AsInt64)) await ApplicationOwnerAsync(tx, row, Payable, Advance, ct);
        }
        return (await AdvanceStateAsync(tx, advance, ProveApplication, ct, returnProofs)).State;
    }
    internal async Task<(BsonDocument State, BsonDocument? Head)> ReadAdvanceCapacityAsync(IClientSessionHandle tx,
        ProcurementPayableAdvanceSource advance, CancellationToken ct,
        ProcurementAdvanceReturnRecords.OwnerProofCache? returnProofs = null)
    {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        // The external return owner uses the same reconstruction and head as payable application.
        var state = await AdvanceOnlyCapacityAsync(tx, advance, ct, returnProofs);
        var head = await Rows(B.AdvanceHeads).Find(tx, F.Eq("_id", advance.Settlement["_id"])).SingleOrDefaultAsync(ct);
        return (state, head);
    }
    private async Task<(BsonDocument State, BsonDocument? Head)> AdvanceStateAsync(IClientSessionHandle tx,
        ProcurementPayableAdvanceSource advance, Func<BsonDocument, Task> proveApplication, CancellationToken ct,
        ProcurementAdvanceReturnRecords.OwnerProofCache? returnProofs = null)
    {
        var settlement = advance.Settlement; var state = B.EmptyAdvance(settlement["_id"].AsInt64, settlement);
        await RequireReturnEvidenceOwnersAsync(tx, settlement["_id"].AsInt64, ct);
        var allocations = await Rows(B.Applications).Find(tx, F.Eq("settlementId", settlement["_id"]))
            .Sort(new BsonDocument("advanceSequence", 1)).ToListAsync(ct);
        var approvals = await Rows(B.Decisions).Find(tx, F.Eq("decision", "approve") & F.Eq("source.kind", "apply_advance") &
            F.Eq("source.settlementId", settlement["_id"])).ToListAsync(ct);
        if (approvals.Count != allocations.Count || !approvals.Select(row => row["applicationId"]).ToHashSet().SetEquals(allocations.Select(row => row["_id"]))) throw B.Broken();
        var returns = await Rows(T.Lines).Find(tx, F.Eq("settlementId", settlement["_id"])).ToListAsync(ct);
        var returnDecisions = await Rows(T.Decisions).Find(tx, F.Eq("decision", "approve") &
            F.Eq("source.lines.settlementId", settlement["_id"])).ToListAsync(ct);
        var purposes = await Rows(PaymentVerificationBson.Parties).Find(tx, F.Eq("schemaVersion", 2) &
            F.Eq("settlementIds", settlement["_id"])).ToListAsync(ct);
        var expectedReturnIds = returnDecisions.Select(decision => {
            var lines = T.Input(decision["input"]).Lines;
            var index = -1;
            for (var i = 0; i < lines.Length; i++) if (lines[i].SettlementId == settlement["_id"].AsInt64) index = i;
            return index >= 0 && decision["returnLineIds"].AsBsonArray.Count == lines.Length
                ? decision["returnLineIds"][index] : throw B.Broken();
        }).ToArray();
        if (returnDecisions.Count != returns.Count || purposes.Count != returnDecisions.Count ||
            expectedReturnIds.Distinct().Count() != expectedReturnIds.Length ||
            !expectedReturnIds.ToHashSet().SetEquals(returns.Select(row => row["_id"]))) throw B.Broken();
        var effects = allocations.Select(row => (Kind: "apply_advance", Row: row)).Concat(returns.Select(row => (Kind: "return", Row: row)))
            .OrderBy(value => value.Row["advanceSequence"].AsInt64).ToArray();
        long sequence = 0;
        foreach (var effect in effects) {
            var row = effect.Row; Equal(row["advanceSequence"], ++sequence); Equal(row["advanceBefore"], state);
            if (effect.Kind == "apply_advance") {
                await proveApplication(row);
                state = B.After(state, Money.Parse(row["amount"].AsString), row["_id"].AsInt64, row["proposalHash"].AsString);
            } else {
                await ProcurementAdvanceReturnRecords.ProveLineAsync(database, tx, row, advance, settlements, ct, returnProofs);
                state = B.AfterReturn(state, Money.Parse(row["amount"].AsString), row["_id"].AsInt64, row["proposalHash"].AsString);
            }
            Equal(row["advanceAfter"], state);
        }
        var head = await Rows(B.AdvanceHeads).Find(tx, F.Eq("_id", settlement["_id"])).SingleOrDefaultAsync(ct);
        if ((head is null) != (effects.Length == 0)) throw B.Broken();
        if (head is not null) { Equal(head["sourceHash"], settlement["snapshotHash"]); Equal(HeadState(head), state); }
        return (state, head);
    }
    private async Task RequireReturnEvidenceOwnersAsync(IClientSessionHandle tx, long settlementId, CancellationToken ct)
    {
        // A removed approved decision, line, purpose and head must not turn its
        // original reviewed proposal and durable command effects into free money.
        // The source-line and operation/result indexes make this a settlement-scoped probe.
        var proposals = await Rows(T.Proposals).Find(tx, F.Eq("source.lines.settlementId", settlementId)).ToListAsync(ct);
        foreach (var proposal in proposals) {
            if (proposal["snapshotHash"] != T.Hash(proposal) ||
                !T.Input(proposal["input"]).Lines.Any(line => line.SettlementId == settlementId)) throw B.Broken();
            var decisions = await Rows(T.Decisions).Find(tx, F.Eq("proposalId", proposal["_id"])).Limit(2).ToListAsync(ct);
            if (decisions.Count > 1) throw B.Broken();
            var decision = decisions.SingleOrDefault();
            foreach (var (table, operationField, resultField) in new[] {
                ("auditEvents", "kind", "result"),
                ("outboxEvents", "kind", "payload"),
                ("idempotencyRecords", "operation", "result") }) {
                var rows = await Rows(table).Find(tx, F.Eq(operationField, T.DecideOperation) &
                    F.Eq(resultField + ".proposalId", proposal["_id"]) & F.Eq(resultField + ".status", "returned"))
                    .Limit(2).ToListAsync(ct);
                if (rows.Count > 1) throw B.Broken();
                if (rows.Count == 1 && (decision is null || decision["decision"] != "approve" ||
                    rows[0][resultField]["decisionId"] != decision["_id"])) throw B.Broken();
            }
        }
    }
    private async Task ApplicationOwnerAsync(IClientSessionHandle tx, BsonDocument allocation,
        Func<long, Task<BsonDocument>> payableRoot, Func<long, Task<ProcurementPayableAdvanceSource>> advanceRoot, CancellationToken ct)
    {
        Equal(allocation["snapshotHash"], B.Hash(allocation));
        var decision = await Rows(B.Decisions).Find(tx, F.Eq("_id", allocation["decisionId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var proposal = await Rows(B.Proposals).Find(tx, F.Eq("_id", decision["proposalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(decision["snapshotHash"], B.Hash(decision)); Equal(proposal["snapshotHash"], B.Hash(proposal));
        if (decision["decision"] != "approve" || proposal["input"]["kind"] != "apply_advance" ||
            decision["applicationId"] != allocation["_id"] || decision["payableId"] != allocation["payableId"] ||
            decision["proposalHash"] != proposal["snapshotHash"] ||
            allocation["proposalId"] != proposal["_id"] || allocation["proposalHash"] != proposal["snapshotHash"] ||
            allocation["journalId"] != decision["journalId"] ||
            allocation["payableId"] != proposal["source"]["payableId"] ||
            allocation["settlementId"] != proposal["source"]["settlementId"] ||
            allocation["amount"] != proposal["source"]["amount"] ||
            allocation["payableBefore"] != decision["payableBefore"] || allocation["payableAfter"] != decision["payableAfter"] ||
            allocation["advanceBefore"] != decision["advanceBefore"] || allocation["advanceAfter"] != decision["advanceAfter"])
            throw B.Broken();
        var input = B.Input(proposal["input"]);
        var payable = await payableRoot(allocation["payableId"].AsInt64);
        var advance = await advanceRoot(allocation["settlementId"].AsInt64);
        var payableHead = await Rows(B.PayableHeads).Find(tx, F.Eq("_id", payable["_id"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        var advanceHead = await Rows(B.AdvanceHeads).Find(tx, F.Eq("_id", advance.Settlement["_id"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        Equal(payableHead["sourceHash"], payable["snapshotHash"]);
        Equal(advanceHead["sourceHash"], advance.Settlement["snapshotHash"]);
        if (payableHead["version"].AsInt64 < allocation["payableSequence"].AsInt64 ||
            advanceHead["version"].AsInt64 < allocation["advanceSequence"].AsInt64) throw B.Broken();
        Equal(proposal["source"], ApplyFacts(payable, advance, Money.Parse(allocation["amount"].AsString), input.PostingDate));
        Equal(allocation["payableId"], input.PayableId!.Value); Equal(allocation["settlementId"], input.SettlementId!.Value);
        Equal(allocation["amount"], input.Amount!.Value.ToString());
        SourceAccounting(proposal["source"].AsBsonDocument, proposal["accounting"].AsBsonDocument);
        await PostedAsync(tx, decision, ct); await EffectsAsync(tx, proposal, ct); await EffectsAsync(tx, decision, ct);
    }
}
