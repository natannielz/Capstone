using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity.Contracts;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Accounting;
using B = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementSupplierPayableService
{
    private async Task PostAndApplyAsync(IClientSessionHandle tx, Context context, BsonDocument proposal,
        BsonDocument row, BsonValue payableAfter, BsonValue advanceAfter, DateTimeOffset at, CancellationToken ct)
    {
        var journal = await new PostJournal(database, clock, [new ProcurementSupplierPayablePostingSource(database, row)]).PostAsync(tx,
            context.Actor, ProcurementSupplierPayablePostingSource.Source(row["_id"].AsInt64, context.Input.Kind == ProcurementSupplierPayableKind.Recognize ? "recognize" : "apply_advance"),
            1, new(row["reason"].AsString, row["correlationId"].AsString), ct);
        var actual = await Rows("journalEntries").Find(tx, new BsonDocument("_id", journal.Id)).SingleOrDefaultAsync(ct) ?? throw B.Broken();
        ProcurementSupplierPayablePostingSource.RequireAccounts(row, AccountingBson.ReadDraft(actual));
        Equal(actual["bookId"], context.Accounting["bookId"]);
        var pending = row.DeepClone().AsBsonDocument; pending["postingFence"] = 1L;
        row["postingFence"] = 1L; row["journalId"] = journal.Id; row["journalFingerprint"] = journal.Fingerprint;
        row["snapshotHash"] = B.Hash(row);
        if ((await Rows(B.Decisions).ReplaceOneAsync(tx, pending, row, cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        if (context.Input.Kind == ProcurementSupplierPayableKind.Recognize)
        {
            var payable = new BsonDocument { { "_id", row["payableId"] }, { "schemaVersion", 1 },
                { "proposalId", proposal["_id"] }, { "decisionId", row["_id"] },
                { "valuationAllocationId", context.Source.Facts["valuationAllocationId"] },
                { "valuationAllocationHash", context.Source.Facts["valuationAllocationHash"] },
                { "amount", row["amount"] }, { "location", row["location"] },
                { "orderId", context.Source.Facts["orderId"] }, { "commercialReference", context.Source.Facts["commercialReference"] },
                { "invoiceDocumentReference", context.Source.Facts["invoiceDocumentReference"] },
                { "supplierId", context.Source.Facts["supplierId"] }, { "supplierName", context.Source.Facts["supplierName"] },
                { "accounting", context.Accounting }, { "journalId", journal.Id }, { "journalFingerprint", journal.Fingerprint },
                { "recordedAt", at.UtcDateTime } };
            payable.Add("snapshotHash", B.Hash(payable));
            await Rows(B.Payables).InsertOneAsync(tx, payable, cancellationToken: ct);
            var head = Head(B.EmptyPayable(payable["_id"].AsInt64, payable), payable["_id"], payable["snapshotHash"], at, null);
            await Rows(B.PayableHeads).InsertOneAsync(tx, head, cancellationToken: ct);
        }
        else
        {
            var capacity = context.Source.Capacity;
            var application = new BsonDocument { { "_id", row["applicationId"] }, { "schemaVersion", 1 },
                { "proposalId", proposal["_id"] }, { "decisionId", row["_id"] }, { "proposalHash", proposal["snapshotHash"] },
                { "payableId", context.Input.PayableId!.Value }, { "settlementId", context.Input.SettlementId!.Value },
                { "amount", row["amount"] }, { "payableSequence", payableAfter["version"] }, { "advanceSequence", advanceAfter["version"] },
                { "payableBefore", capacity["payable"].DeepClone() }, { "payableAfter", payableAfter.DeepClone() },
                { "advanceBefore", capacity["advance"].DeepClone() }, { "advanceAfter", advanceAfter.DeepClone() },
                { "journalId", journal.Id }, { "recordedAt", at.UtcDateTime } };
            application.Add("snapshotHash", B.Hash(application));
            await Rows(B.Applications).InsertOneAsync(tx, application, cancellationToken: ct);
            var oldPayable = capacity["payableHead"].AsBsonDocument;
            var newPayable = Head(payableAfter.AsBsonDocument, oldPayable["_id"], oldPayable["sourceHash"], at, oldPayable);
            if ((await Rows(B.PayableHeads).ReplaceOneAsync(tx, oldPayable, newPayable, cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
            var oldAdvance = capacity["advanceHead"].IsBsonNull ? null : capacity["advanceHead"].AsBsonDocument;
            var newAdvance = Head(advanceAfter.AsBsonDocument, context.Input.SettlementId!.Value,
                context.Source.Settlement!["snapshotHash"], at, oldAdvance);
            if (oldAdvance is null) await Rows(B.AdvanceHeads).InsertOneAsync(tx, newAdvance, cancellationToken: ct);
            else if ((await Rows(B.AdvanceHeads).ReplaceOneAsync(tx, oldAdvance, newAdvance, cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
            context.Fences.RemoveAll(value => value.Table == B.PayableHeads && value.Row["_id"] == newPayable["_id"] ||
                value.Table == B.AdvanceHeads && value.Row["_id"] == newAdvance["_id"]);
            context.Fences.Add((B.PayableHeads, newPayable)); context.Fences.Add((B.AdvanceHeads, newAdvance));
        }
    }
    private static BsonDocument Head(BsonDocument state, BsonValue id, BsonValue sourceHash, DateTimeOffset at, BsonDocument? prior)
    {
        var row = new BsonDocument { { "_id", id }, { "schemaVersion", 1 }, { "sourceHash", sourceHash },
            { "createdAt", prior?["createdAt"] ?? new BsonDateTime(at.UtcDateTime) },
            { "updatedAt", at.UtcDateTime }, { "fence", prior?["fence"].AsInt64 ?? 0L } };
        row.AddRange(state); return row;
    }
}
