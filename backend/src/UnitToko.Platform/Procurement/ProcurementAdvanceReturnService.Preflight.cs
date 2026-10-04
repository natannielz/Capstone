using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Billing;
using B = UnitToko.Platform.Procurement.ProcurementAdvanceReturnBson;
using P = UnitToko.Platform.Procurement.ProcurementSupplierPayableBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementAdvanceReturnService
{
    // Called by the additive migration before changing validators. All source owners
    // are reconstructed against the retained data in one transaction snapshot.
    internal async Task PreflightAsync(IClientSessionHandle tx, CancellationToken ct)
    {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw B.Broken();
        var ids = await Rows(B.Proposals).Find(tx, F.Empty).Project(new BsonDocument("_id", 1)).ToListAsync(ct);
        foreach (var raw in ids) {
            var proposal = await records.ProposalAsync(tx, raw["_id"].AsInt64, ct);
            var decision = await records.DecisionAsync(tx, proposal, ct);
            if (decision is null || decision["decision"] != "approve") continue;
            foreach (var selected in B.Input(proposal["input"]).Lines) {
                var advance = await settlements.ReadPayableSourceAsync(database, tx, selected.SettlementId, ct);
                _ = await payables.ReadAdvanceCapacityAsync(tx, advance, ct);
            }
        }
        foreach (var raw in await Rows(B.Decisions).Find(tx, F.Empty).Project(new BsonDocument("proposalId", 1)).ToListAsync(ct))
            if (!ids.Any(id => id["_id"] == raw["proposalId"])) throw B.Broken();
        foreach (var raw in await Rows(B.Lines).Find(tx, F.Empty).Project(new BsonDocument("_id", 1)).ToListAsync(ct)) {
            var line = await Rows(B.Lines).Find(tx, F.Eq("_id", raw["_id"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            var advance = await settlements.ReadPayableSourceAsync(database, tx, line["settlementId"].AsInt64, ct);
            await ProcurementAdvanceReturnRecords.ProveLineAsync(database, tx, line, advance, settlements, ct);
        }
        foreach (var raw in await Rows(P.AdvanceHeads).Find(tx, F.Empty).Project(new BsonDocument("_id", 1)).ToListAsync(ct)) {
            var advance = await settlements.ReadPayableSourceAsync(database, tx, raw["_id"].AsInt64, ct);
            _ = await payables.ReadAdvanceCapacityAsync(tx, advance, ct);
        }
        foreach (var raw in await Rows(PaymentVerificationBson.Parties).Find(tx, F.Eq("schemaVersion", 2))
            .Project(new BsonDocument("_id", 1)).ToListAsync(ct)) {
            var decision = await Rows(B.Decisions).Find(tx, F.Eq("receiptId", raw["_id"]) & F.Eq("decision", "approve"))
                .SingleOrDefaultAsync(ct) ?? throw B.Broken();
            var proposal = await records.ProposalAsync(tx, decision["proposalId"].AsInt64, ct);
            _ = await records.DecisionAsync(tx, proposal, ct) ?? throw B.Broken();
        }
        foreach (var (table, field, owner, idField) in new[] {
            ("auditEvents", "kind", "result", "proposalId"),
            ("outboxEvents", "kind", "payload", "proposalId"),
            ("idempotencyRecords", "operation", "result", "proposalId") }) {
            foreach (var operation in new[] { B.ProposeOperation, B.DecideOperation }) {
                var destination = operation == B.ProposeOperation ? B.Proposals : B.Decisions;
                var lookup = operation == B.ProposeOperation ? owner + "." + idField : owner + ".decisionId";
                var pipeline = new BsonDocument[] { new("$match", new BsonDocument(field, operation)),
                    new("$lookup", new BsonDocument { { "from", destination }, { "localField", lookup },
                        { "foreignField", "_id" }, { "as", "owner" } }),
                    new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) };
                if (await Rows(table).Aggregate<BsonDocument>(tx, pipeline).AnyAsync(ct)) throw B.Broken();
            }
        }
        var journalPipeline = new BsonDocument[] { new("$match", new BsonDocument("source.namespace", B.DecideOperation)),
            new("$lookup", new BsonDocument { { "from", B.Decisions }, { "localField", "source.documentId" },
                { "foreignField", "_id" }, { "as", "owner" } }),
            new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) };
        if (await Rows("journalEntries").Aggregate<BsonDocument>(tx, journalPipeline).AnyAsync(ct)) throw B.Broken();
    }
}
