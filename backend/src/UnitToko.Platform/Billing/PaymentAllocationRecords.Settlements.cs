using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Billing;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using D = UnitToko.Platform.Billing.RefundSettlementBson;

namespace UnitToko.Platform.Billing;

internal sealed partial class PaymentAllocationRecords
{
    private static BsonDocument SettlementReceipt(BsonDocument row, bool receipt, long id) {
        if (!receipt) throw B.Broken();
        return row["receipts"].AsBsonArray.SingleOrDefault(value => value["receiptId"] == id)?.AsBsonDocument ?? throw B.Broken();
    }
    private static BsonDocument AfterSettlement(BsonDocument row, bool receipt, long id) {
        var source = SettlementReceipt(row, receipt, id); var next = source["before"].DeepClone().AsBsonDocument;
        // Paid refunds remain part of TOTAL blocked funds. Neither original received cash
        // nor its allocation/reservation amounts change when settlement is verified.
        next["version"] = source["version"]; next["lastEventId"] = row["_id"]; next["lastEventHash"] = row["snapshotHash"]; return next;
    }
    internal Task<BsonDocument> RawSettlementAsync(IClientSessionHandle tx, long id, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || id <= 0) throw B.Broken();
        // The settlement owner validates actual payout/private pin, approval, bank claim,
        // permanent person receipt, effects and its one journal without aggregate recursion.
        return RefundSettlementRecords.ReadAsync(database, tx, id, ct, clock);
    }
    private static decimal RequireSettlementPaidReceipt(BsonDocument row, long id, decimal paidBefore) {
        var receipt = SettlementReceipt(row, true, id); var before = receipt["before"];
        var releases = row["releases"].AsBsonArray.Where(line => line["receiptId"] == id).ToArray();
        if (releases.Length == 0 || releases.Any(line => line["amount"].AsDecimal <= 0)) throw B.Broken();
        var paidAfter = checked(paidBefore + releases.Sum(line => line["amount"].AsDecimal));
        var blocked = before["reservedForRefund"].AsDecimal;
        var available = checked(receipt["receiptTotal"].AsDecimal - before["allocated"].AsDecimal - blocked);
        if (paidBefore < 0 || paidAfter > blocked || available < 0) throw B.Broken();
        Equal(receipt["version"], checked(before["version"].AsInt64 + 1));
        Equal(receipt["paidBefore"], new BsonDecimal128(paidBefore)); Equal(receipt["paidAfter"], new BsonDecimal128(paidAfter));
        Equal(receipt["pending"], new BsonDecimal128(blocked - paidAfter)); Equal(receipt["available"], new BsonDecimal128(available));
        return paidAfter;
    }
    // Return actual fully validated events, not a synthetic paid balance or a mutable head
    // extension. The command owner still obtains current canonical balances and source fences.
    internal async Task<BsonArray> PaidRefundsAsync(IClientSessionHandle tx, long[] receiptIds, BsonDocument party, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || receiptIds is null || party is null ||
            receiptIds.Length > ReturnFinancialApprovalPolicy.MaximumReleases || receiptIds.Any(id => id <= 0) || receiptIds.Distinct().Count() != receiptIds.Length) throw B.Broken();
        receiptIds = [.. receiptIds.Order()]; var rows = new List<BsonDocument>(); var seen = new HashSet<long>();
        using var cursor = await Rows(D.Events).Find(tx, new BsonDocument("receipts.receiptId", new BsonDocument("$in", new BsonArray(receiptIds)))).ToCursorAsync(ct);
        while (await cursor.MoveNextAsync(ct)) foreach (var candidate in cursor.Current) {
            var row = await RawSettlementAsync(tx, candidate["_id"].AsInt64, ct); Equal(row["party"], party);
            if (!seen.Add(row["_id"].AsInt64)) throw B.Broken(); rows.Add(row);
        }
        foreach (var id in receiptIds) {
            var paid = 0m; var version = 0L;
            var history = rows.Where(row => row["receipts"].AsBsonArray.Any(receipt => receipt["receiptId"] == id)).OrderBy(row => EventVersion(row, true, id)).ToArray();
            foreach (var row in history) {
                var nextVersion = EventVersion(row, true, id); if (nextVersion <= version) throw B.Broken(); version = nextVersion;
                paid = RequireSettlementPaidReceipt(row, id, paid);
            }
            await RequireSettlementEffectsAsync(tx, id, history.Select(row => row["_id"].AsInt64).ToHashSet(), ct);
        }
        return new BsonArray(rows.OrderBy(row => row["_id"].AsInt64).Select(row => row.DeepClone()));
    }
    private async Task RequireSettlementEffectsAsync(IClientSessionHandle tx, long receiptId, HashSet<long> seen, CancellationToken ct) {
        foreach (var (table, field, payload) in new[] { ("idempotencyRecords", "operation", "result"), ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload") }) {
            using var effects = await Rows(table).Find(tx, new BsonDocument { { field, D.Operation }, { payload + ".receipts.receiptId", receiptId } }).ToCursorAsync(ct);
            while (await effects.MoveNextAsync(ct)) foreach (var effect in effects.Current)
                if (!seen.Contains(effect[payload]["settlementId"].AsInt64)) throw B.Broken();
        }
    }
}
