using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Ordering;
using B = UnitToko.Platform.Inventory.StockSourceReconciliationBson;

namespace UnitToko.Platform.Inventory;

internal sealed record ReconciliationStockSourceEvent(string Kind, long Id, long PoolId, BsonDocument Record,
    BsonDocument Audit, BsonValue Time, long? ProposalId)
{
    internal string Hash => CatalogRepository.Digest(new BsonDocument { { "kind", Kind }, { "record", Record } });
}

public sealed partial class StockSourceSetupService
{
    internal async Task<ReconciliationStockSourceEvent> ReadReconciliationStockSourceEventAsync(IClientSessionHandle tx,
        string kind, long sourceId, bool requireReceipt, CancellationToken ct)
    {
        new InventoryRepository(database).RequireSession(tx);
        if (kind == "setup") {
            var setup = await new OrderStockSourceRecords(database).SetupAsync(tx, sourceId, requireReceipt, ct);
            return new(kind, sourceId, setup.PoolIds.Single(), setup.Record, setup.Audit, setup.Time, null);
        }
        var table = kind switch { "proposal" => B.Proposals, "decision" => B.Decisions, _ => throw ReconciliationStockSourceRecords.Unavailable() };
        var row = await Rows(table).Find(tx, F.Eq("_id", sourceId) & StockSourceReconciliationSchemas.Validator(table)).SingleOrDefaultAsync(ct)
            ?? throw ReconciliationStockSourceRecords.Unavailable();
        BsonDocument? proposal = null;
        if (kind == "proposal") {
            if (row["snapshotHash"] != B.Hash(new BsonDocument(row.Where(field => field.Name != "snapshotHash"))) ||
                row["committedPoolFence"] != checked(row["baseline"]["poolFence"].AsInt64 + 1) ||
                row["baseline"]["baselineHash"].AsString != BaselineHash(row["baseline"].AsBsonDocument) ||
                row["baseline"]["binding"].AsString != Binding(row["baseline"].AsBsonDocument) || row["binding"] != row["baseline"]["binding"] ||
                !row["differences"].Equals(Differences(row["baseline"].AsBsonDocument, row["observation"].AsBsonDocument)))
                throw ReconciliationStockSourceRecords.Unavailable();
            if (requireReceipt) row = await ReconciliationProposalAsync(tx, sourceId, ct);
        } else {
            proposal = await ReconciliationProposalAsync(tx, row["proposalId"].AsInt64, ct);
            if (row["snapshotHash"] != B.Hash(new BsonDocument(row.Where(field => field.Name != "snapshotHash"))) ||
                row["proposalHash"] != proposal["snapshotHash"] || row["poolId"] != proposal["poolId"] ||
                row["actorId"] == proposal["actorId"] || (row["decision"] == "confirm") != !row["observation"].IsBsonNull ||
                row["decision"] == "confirm" && (proposal["differences"].AsBsonArray.Count != 0 ||
                    row["baselineHash"] != proposal["baseline"]["baselineHash"] || !row["observation"]["entries"].Equals(proposal["observation"]["entries"])) ||
                row["decision"] == "reject" && !row["baselineHash"].IsBsonNull || row["recordedAt"].CompareTo(proposal["recordedAt"]) < 0)
                throw ReconciliationStockSourceRecords.Unavailable();
            if (requireReceipt) {
                var actual = await ReconciliationDecisionAsync(tx, proposal, ct);
                if (actual is null || actual["_id"] != row["_id"]) throw ReconciliationStockSourceRecords.Unavailable();
                row = actual;
            }
        }
        var operation = kind == "proposal" ? B.ProposeOperation : B.DecideOperation;
        var expectedAudit = B.Audit(row, operation);
        var audit = await Rows("auditEvents").Find(tx, F.Eq("_id", row["auditId"])).SingleOrDefaultAsync(ct);
        var outbox = await Rows("outboxEvents").Find(tx, F.Eq("sourceKey", operation + ":" + sourceId)).SingleOrDefaultAsync(ct);
        if (audit is null || !audit.Equals(expectedAudit) || outbox is null || outbox["kind"] != operation ||
            outbox["poolId"] != row["poolId"] || outbox["actorId"] != row["actorId"] || outbox["requestKey"] != row["requestKey"] ||
            outbox["payload"] != B.Result(row) || outbox["createdAt"] != row["recordedAt"]) throw ReconciliationStockSourceRecords.Unavailable();
        if (requireReceipt) {
            var receipt = await Rows("idempotencyRecords").Find(tx, F.Eq("actorId", row["actorId"]) & F.Eq("operation", operation) &
                F.Eq("key", row["requestKey"])).SingleOrDefaultAsync(ct);
            if (receipt is null || receipt["bodyHash"] != row["bodyHash"] || receipt["result"] != B.Result(row) ||
                receipt["createdAt"].CompareTo(row["recordedAt"]) > 0) throw ReconciliationStockSourceRecords.Unavailable();
            if (kind == "proposal") {
                if (receipt.Contains(PersonApprovalReceipt.Field)) throw ReconciliationStockSourceRecords.Unavailable();
            } else if (PersonApprovalReceipt.Read(receipt, Permissions.StockSourceReconciliationDecide) !=
                new ApprovalRequest(1, 1, ProposedBy: proposal!["actorId"].AsInt64)) throw ReconciliationStockSourceRecords.Unavailable();
        }
        // Observation entries are persisted in canonical order. Never invent the original request
        // order to rehash a legitimate legacy body; immutable row/audit/receipt equality binds it.
        var record = kind == "proposal" ? row.DeepClone().AsBsonDocument : new BsonDocument {
            { "proposal", proposal!.DeepClone() }, { "decision", row.DeepClone() } };
        return new(kind, sourceId, row["poolId"].AsInt64, record, expectedAudit, row["recordedAt"],
            kind == "proposal" ? sourceId : row["proposalId"].AsInt64);
    }
}
