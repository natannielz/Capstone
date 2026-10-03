using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using B = UnitToko.Platform.Procurement.ProcurementDisbursementBson;

namespace UnitToko.Platform.Procurement;

public sealed partial class ProcurementDisbursementService
{
    private sealed class DisbursementCapacityState(BsonDocument funding, BsonDocument? head, BsonDocument? previous, Money recorded) {
        internal BsonDocument Funding = funding;
        internal BsonDocument? Head = head, Previous = previous;
        internal Dictionary<long, BsonDocument> Claims = new();
        internal Money Recorded = recorded;
        internal BsonDocument View() {
            var total = Money.Parse(Funding["decision"]["authorizedAmount"].AsString); if (Recorded.Value > total.Value) throw B.Broken();
            return new() { { "authorizationId", Funding["authorization"]["_id"] }, { "authorizedAmount", total.ToString() }, { "recordedAmount", Recorded.ToString() },
                { "remainingAmount", new Money(total.Value - Recorded.Value).ToString() }, { "version", Head?["version"] ?? new BsonInt64(0) } };
        }
    }
    private async Task<DisbursementCapacityState> CapacityAsync(IClientSessionHandle tx, BsonDocument source, CancellationToken ct, ProcurementDisbursementProof? proof = null, ProcurementDisbursementRecords.HistoricalSources? history = null) {
        var id = source["authorization"]["_id"]; var filter = new BsonDocument("authorizationId", id);
        var raw = await Rows(B.Heads).Find(tx, filter).SingleOrDefaultAsync(ct); var head = raw is null ? null : await records.RowAsync(tx, B.Heads, raw["_id"].AsInt64, ct);
        var allocations = await Rows(B.Allocations).Find(tx, filter).Sort(new BsonDocument("sequence", 1)).ToListAsync(ct);
        var claims = await Rows(B.Records).Find(tx, filter).ToListAsync(ct);
        if (allocations.Count != claims.Count || (head is null) != (allocations.Count == 0)) throw B.Broken();
        foreach (var (table, operation, selection) in new[] { ("auditEvents", "kind", "authorizationId"), ("outboxEvents", "kind", "authorizationId"), ("idempotencyRecords", "operation", "result.authorizationId") })
            if (await Rows(table).CountDocumentsAsync(tx, new BsonDocument { { operation, B.RecordOperation }, { selection, id } }, cancellationToken: ct) != claims.Count) throw B.Broken();
        var state = new DisbursementCapacityState(source, head, null, new(0m)); long sequence = 0;
        foreach (var allocation in allocations) {
            var row = await records.RowAsync(tx, B.Allocations, allocation["_id"].AsInt64, ct); ProcurementDisbursementRecords.Equal(row["snapshotHash"], B.Hash(row));
            ProcurementDisbursementRecords.Equal(row["authorizationHash"], source["authorization"]["snapshotHash"]); ProcurementDisbursementRecords.Equal(row["sequence"], ++sequence);
            ProcurementDisbursementRecords.Equal(row["previousAllocationId"], state.Previous?["_id"] ?? BsonNull.Value); ProcurementDisbursementRecords.Equal(row["previousHash"], state.Previous?["snapshotHash"] ?? BsonNull.Value);
            var candidates = claims.Where(claim => claim["_id"] == row["disbursementId"]).ToArray(); if (candidates.Length != 1) throw B.Broken();
            var claim = await records.RecordAsync(tx, row["disbursementId"].AsInt64, false, ct, proof, history);
            state.Claims.Add(claim["_id"].AsInt64, claim);
            ProcurementDisbursementRecords.Equal(row["disbursementHash"], claim["snapshotHash"]); ProcurementDisbursementRecords.Equal(row["amount"], claim["input"]["amount"]);
            ProcurementDisbursementRecords.Equal(row["recordedAt"], claim["recordedAt"]); ProcurementDisbursementRecords.Equal(row["_id"], claim["allocationId"]);
            var reviewed = state.View(); reviewed["version"] = sequence - 1; ProcurementDisbursementRecords.Equal(claim["capacity"], reviewed);
            if (row["recordedAt"].ToUniversalTime() < source["decision"]["recordedAt"].ToUniversalTime() || state.Previous is not null && row["recordedAt"].ToUniversalTime() < state.Previous["recordedAt"].ToUniversalTime()) throw B.Broken();
            state.Recorded = Money.Add(state.Recorded, Money.Parse(row["amount"].AsString)); state.Previous = row; _ = state.View();
        }
        if (head is not null) {
            ProcurementDisbursementRecords.Equal(head["authorizationHash"], source["authorization"]["snapshotHash"]); ProcurementDisbursementRecords.Equal(head["authorizedAmount"], source["decision"]["authorizedAmount"]);
            ProcurementDisbursementRecords.Equal(head["recordedAmount"], state.Recorded.ToString()); ProcurementDisbursementRecords.Equal(head["version"], sequence);
            ProcurementDisbursementRecords.Equal(head["createdAt"], allocations[0]["recordedAt"]); ProcurementDisbursementRecords.Equal(head["updatedAt"], state.Previous!["recordedAt"]);
        }
        return state;
    }
    private async Task ConsumeAsync(Context context, BsonDocument record, CancellationToken ct) {
        var state = context.Capacity ?? throw B.Broken(); var input = B.Input(record["input"]); ProcurementDisbursementRecords.Equal(record["capacity"], state.View());
        ProcurementDisbursementRecords.Review(input, record["source"].AsBsonDocument, state.View(), new(record["recordedAt"].ToUniversalTime()));
        var at = record["recordedAt"]; if (state.Head is not null && state.Head["updatedAt"].ToUniversalTime() > at.ToUniversalTime()) throw B.Changed();
        var allocation = new BsonDocument { { "_id", record["allocationId"] }, { "schemaVersion", 1 }, { "authorizationId", input.AuthorizationId },
            { "authorizationHash", state.Funding["authorization"]["snapshotHash"] }, { "disbursementId", record["_id"] }, { "disbursementHash", record["snapshotHash"] }, { "amount", input.Amount.ToString() },
            { "sequence", checked(state.View()["version"].AsInt64 + 1) }, { "previousAllocationId", state.Previous?["_id"] ?? BsonNull.Value }, { "previousHash", state.Previous?["snapshotHash"] ?? BsonNull.Value }, { "recordedAt", at } };
        allocation.Add("snapshotHash", B.Hash(allocation)); var total = Money.Add(state.Recorded, input.Amount); BsonDocument head;
        if (state.Head is null) {
            head = new() { { "_id", (await ids.NextAsync(B.Heads, ct)).Value }, { "schemaVersion", 1 }, { "authorizationId", input.AuthorizationId }, { "authorizationHash", allocation["authorizationHash"] },
                { "authorizedAmount", state.View()["authorizedAmount"] }, { "recordedAmount", total.ToString() }, { "version", 1L }, { "fence", 0L }, { "createdAt", at }, { "updatedAt", at } };
            await Rows(B.Heads).InsertOneAsync(context.Tx, head, cancellationToken: ct);
        } else {
            head = state.Head.DeepClone().AsBsonDocument; head["recordedAmount"] = total.ToString(); head["version"] = allocation["sequence"]; head["updatedAt"] = at;
            head["fence"] = checked(head["fence"].AsInt64 + 1);
            if ((await Rows(B.Heads).ReplaceOneAsync(context.Tx, state.Head, head, cancellationToken: ct)).ModifiedCount != 1) throw B.Changed();
        }
        await Rows(B.Allocations).InsertOneAsync(context.Tx, allocation, cancellationToken: ct); state.Head = head; state.Previous = allocation; state.Recorded = total;
    }
}
