using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Billing;
using B = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;
using R = UnitToko.Platform.Procurement.ProcurementRequestBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementSupplierPayableService
{
    private async Task<ProcurementSupplierPayableSource> SourceAsync(IClientSessionHandle tx,
        ProcurementSupplierPayableInput input, CancellationToken ct, HistoricalProof? proof = null)
    {
        proof?.Require(tx, input);
        if (input.Kind == ProcurementSupplierPayableKind.Recognize)
        {
            var source = proof?.Valuation ?? await valuations.ReadPayableSourceAsync(database, tx, input.ValuationAllocationId!.Value, ct);
            var allocation = source.Allocation; var receipt = source.Receipt; var decision = source.Decision;
            if (receipt["location"] != R.Location(input.Location) || allocation["receiptId"] != receipt["_id"] ||
                allocation["journalId"] != decision["journalId"] || decision["decision"] != "approve" ||
                source.Invoice["input"]["kind"] != "invoice" ||
                source.Match["lines"][allocation["matchLineNumber"].AsInt32 - 1]["state"] != "matched") throw B.Broken();
            var amount = Money.Parse(allocation["capitalizedAmount"].AsString);
            if (amount.Value <= 0 || amount != Money.Add(Money.Add(Money.Parse(allocation["netAmount"].AsString),
                    Money.Parse(allocation["taxAmount"].AsString)), Money.Parse(allocation["chargeAmount"].AsString)) ||
                input.PostingDate < AccountingCalendar.BusinessDate(new DateTimeOffset(decision["recordedAt"].ToUniversalTime())))
                throw B.Changed("procurement_supplier_payable_valuation_unavailable");
            var facts = RecognizeFacts(source);
            if (proof is not null) Equal(facts, proof.Facts);
            var capacity = await RecognizeCapacityAsync(tx, source, ct);
            return new(facts, allocation, null, null, capacity);
        }
        var payable = proof?.Payable ?? await PayableRootAsync(tx, input.PayableId!.Value, ct);
        var advance = proof?.Advance ?? await settlements.ReadPayableSourceAsync(database, tx, input.SettlementId!.Value, ct);
        if (payable["location"] != R.Location(input.Location)) throw B.Changed("procurement_supplier_payable_source_mismatch");
        var factsApply = ApplyFacts(payable, advance, input.Amount!.Value, input.PostingDate);
        if (proof is not null) Equal(factsApply, proof.Facts);
        var capacityApply = await CapacityAsync(tx, payable, advance, ct);
        return new(factsApply, null, advance.Settlement, payable, capacityApply);
    }
    private async Task<BsonDocument> RecognizeCapacityAsync(IClientSessionHandle tx, ProcurementPayableValuationSource source, CancellationToken ct)
    {
            var allocation = source.Allocation; var amount = Money.Parse(allocation["capitalizedAmount"].AsString);
            var used = await Rows(B.Payables).Find(tx, F.Eq("valuationAllocationId", allocation["_id"])).Limit(2).ToListAsync(ct);
            var approvals = await Rows(B.Decisions).Find(tx, F.Eq("decision", "approve") & F.Eq("source.kind", "recognize") &
                F.Eq("source.valuationAllocationId", allocation["_id"])).Limit(2).ToListAsync(ct);
            if (used.Count > 1 || approvals.Count != used.Count || approvals.Count == 1 && approvals[0]["payableId"] != used[0]["_id"])
                throw B.Broken();
            if (used.Count == 1) {
                var root = await PayableRootAsync(tx, used[0]["_id"].AsInt64, ct,
                    Proof(tx, null, RecognizeFacts(source), valuation: source));
                Equal(root["valuationAllocationId"], allocation["_id"]);
            }
            var capacity = new BsonDocument { { "valuationAllocationId", allocation["_id"] },
                { "amount", amount.ToString() }, { "recognized", used.Count == 1 },
                { "payableId", used.Count == 1 ? used[0]["_id"] : BsonNull.Value } };
            return capacity;
    }
    private static BsonDocument RecognizeFacts(ProcurementPayableValuationSource source)
    {
        var allocation = source.Allocation; var receipt = source.Receipt; var decision = source.Decision;
        var amount = Money.Parse(allocation["capitalizedAmount"].AsString);
        var supplier = PurchaseOrderBson.SupplierView(receipt["source"]["proposal"]["supplier"].AsBsonDocument);
        return new BsonDocument { { "kind", "recognize" }, { "valuationAllocationId", allocation["_id"] },
                { "valuationAllocationHash", allocation["snapshotHash"] }, { "valuationDecisionId", decision["_id"] },
                { "valuationJournalId", decision["journalId"] }, { "receiptId", receipt["_id"] },
                { "invoiceDocumentId", source.Invoice["_id"] }, { "invoiceMatchId", source.Match["_id"] },
                { "matchLineNumber", allocation["matchLineNumber"] }, { "packages", allocation["packages"] },
                { "orderId", receipt["orderId"] }, { "commercialReference", receipt["source"]["proposal"]["input"]["commercialReference"]["reference"] },
                { "invoiceDocumentReference", source.Invoice["input"]["documentReference"] },
                { "supplierId", receipt["supplierId"] }, { "supplierName", supplier["displayName"] },
                { "location", receipt["location"] }, { "amount", amount.ToString() },
                { "netAmount", allocation["netAmount"] }, { "taxAmount", allocation["taxAmount"] },
                { "chargeAmount", allocation["chargeAmount"] }, { "sourceAccount", decision["accounting"]["receiptAccrual"] },
                { "sourceBookId", decision["accounting"]["bookId"] } };
    }
    private static BsonDocument ApplyFacts(BsonDocument payable, ProcurementPayableAdvanceSource advance, Money amount, DateOnly postingDate)
    {
        var settlement = advance.Settlement; var disbursement = advance.Disbursement;
        var funding = disbursement["source"]["funding"]; var order = funding["request"]["orderSource"]["order"];
        var supplier = funding["request"]["orderSource"]["proposal"]["supplier"];
        if (settlement["location"] != payable["location"] || order["_id"] != payable["orderId"] ||
            supplier["supplierId"] != payable["supplierId"] || disbursement["input"]["supplierId"] != payable["supplierId"] ||
            postingDate < AccountingCalendar.BusinessDate(new DateTimeOffset(settlement["recordedAt"].ToUniversalTime())) ||
            postingDate < AccountingCalendar.BusinessDate(new DateTimeOffset(payable["recordedAt"].ToUniversalTime())))
            throw B.Changed("procurement_supplier_payable_source_mismatch");
        return new BsonDocument { { "kind", "apply_advance" }, { "payableId", payable["_id"] },
            { "payableHash", payable["snapshotHash"] }, { "settlementId", settlement["_id"] },
            { "settlementHash", settlement["snapshotHash"] }, { "disbursementId", disbursement["_id"] },
            { "settlementJournalId", settlement["journalId"] }, { "valuationAllocationId", payable["valuationAllocationId"] },
            { "orderId", payable["orderId"] }, { "commercialReference", payable["commercialReference"] },
            { "invoiceDocumentReference", payable["invoiceDocumentReference"] },
            { "supplierId", payable["supplierId"] }, { "supplierName", payable["supplierName"] },
            { "location", payable["location"] }, { "amount", amount.ToString() },
            { "sourcePayableAccount", payable["accounting"]["supplierPayable"] },
            { "sourceAdvanceAccount", settlement["accounting"]["supplierAdvance"] },
            { "sourceBookId", payable["accounting"]["bookId"] }, { "advanceBookId", settlement["accounting"]["bookId"] } };
    }
    private static void SourceAccounting(BsonDocument facts, BsonDocument accounting)
    {
        if (facts["kind"] == "recognize")
        {
            Equal(facts["sourceBookId"], accounting["bookId"]);
            Equal(facts["sourceAccount"], accounting["receiptAccrual"]);
        }
        else
        {
            Equal(facts["sourceBookId"], accounting["bookId"]); Equal(facts["advanceBookId"], accounting["bookId"]);
            Equal(facts["sourcePayableAccount"], accounting["supplierPayable"]);
            Equal(facts["sourceAdvanceAccount"], accounting["supplierAdvance"]);
        }
    }
}
