using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Platform.Accounting;
using UnitToko.Platform.Billing;
using B = UnitToko.Platform.Procurement.ProcurementAdvanceReturnBson;
using P = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementAdvanceReturnService
{
    private async Task PostAndReturnAsync(IClientSessionHandle tx, Context context, BsonDocument proposal,
        BsonDocument row, CancellationToken ct)
    {
        var journal = await new PostJournal(database, clock, [new ProcurementAdvanceReturnPostingSource(database, row)]).PostAsync(
            tx, context.Actor, ProcurementAdvanceReturnPostingSource.Source(row["_id"].AsInt64), 1,
            new(row["reason"].AsString, row["correlationId"].AsString), ct);
        var actual = await Rows("journalEntries").Find(tx, F.Eq("_id", journal.Id)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        ProcurementAdvanceReturnPostingSource.RequireAccounts(row, AccountingBson.ReadDraft(actual));
        Equal(actual["bookId"], context.Accounting["bookId"]);
        var pending = row.DeepClone().AsBsonDocument;
        pending["postingFence"] = 1L;
        row["postingFence"] = 1L;
        row["journalId"] = journal.Id;
        row["journalFingerprint"] = journal.Fingerprint;
        row["snapshotHash"] = B.Hash(row);
        if ((await Rows(B.Decisions).ReplaceOneAsync(tx, pending, row, cancellationToken: ct)).ModifiedCount != 1)
            throw B.Changed();

        for (var i = 0; i < context.Input.Lines.Length; i++)
        {
            var selected = context.Input.Lines[i];
            var before = context.Capacity["lines"][i]["advance"].AsBsonDocument;
            var lineId = row["returnLineIds"][i].AsInt64;
            var after = P.AfterReturn(before, selected.Amount, lineId, proposal["snapshotHash"].AsString);
            var sourceLine = context.Source["lines"][i].AsBsonDocument;
            var line = new BsonDocument { { "_id", lineId }, { "schemaVersion", 1 },
                { "proposalId", proposal["_id"] }, { "decisionId", row["_id"] },
                { "proposalHash", proposal["snapshotHash"] }, { "receiptId", row["receiptId"] },
                { "settlementId", selected.SettlementId }, { "settlementHash", sourceLine["settlementHash"] },
                { "amount", selected.Amount.ToString() }, { "advanceSequence", after["version"] },
                { "advanceBefore", before.DeepClone() }, { "advanceAfter", after.DeepClone() },
                { "journalId", journal.Id }, { "recordedAt", row["recordedAt"] } };
            line.Add("snapshotHash", B.Hash(line));
            await Rows(B.Lines).InsertOneAsync(tx, line, cancellationToken: ct);
            var prior = context.Heads[selected.SettlementId];
            var head = new BsonDocument { { "_id", selected.SettlementId }, { "schemaVersion", 1 },
                { "sourceHash", sourceLine["settlementHash"] },
                { "createdAt", prior?["createdAt"] ?? row["recordedAt"] },
                { "updatedAt", row["recordedAt"] }, { "fence", prior?["fence"] ?? new BsonInt64(0) } };
            head.AddRange(after);
            if (prior is null) await Rows(P.AdvanceHeads).InsertOneAsync(tx, head, cancellationToken: ct);
            else if ((await Rows(P.AdvanceHeads).ReplaceOneAsync(tx, prior, head, cancellationToken: ct)).ModifiedCount != 1)
                throw B.Changed();
            context.Heads[selected.SettlementId] = head;
        }
        await new PaymentReceiptPurposeRecords(database).ClaimSupplierReturnAsync(tx, context.Bank.Receipt, row, ct);
    }
}
