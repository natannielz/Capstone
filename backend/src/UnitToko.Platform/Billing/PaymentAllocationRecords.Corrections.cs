using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Identity;
using B = UnitToko.Platform.Billing.PaymentAllocationBson;
using C = UnitToko.Platform.Billing.PaymentAllocationCorrectionBson;
using A = UnitToko.Platform.Billing.ReturnFinancialApprovalBson;

namespace UnitToko.Platform.Billing;
internal sealed partial class PaymentAllocationRecords
{
    private async Task RequireCorrectionOriginalAsync(IClientSessionHandle tx, BsonDocument row, CancellationToken ct) {
        C.PolicyAt(row["correctionPolicy"].AsBsonDocument, new(row["recordedAt"].ToUniversalTime()));
        var original = await RawAsync(tx, row["originalAllocationId"].AsInt64, ct);
        Equal(row["originalAllocationHash"], original["snapshotHash"]); Equal(row["originalJournalId"], original["journalId"]); Equal(row["originalJournalFingerprint"], original["journalFingerprint"]);
        foreach (var name in new[] { "verificationId", "verificationHash", "declarationId", "declarationRevision", "recordedBy", "receiptId", "receiptHash", "receiptTotal", "party", "suspenseAccount" }) Equal(row[name], original[name]);
        Equal(row["correctionPolicy"]["scope"], row["policy"]["scope"]); Equal(row["correctionPolicy"]["suspenseMapping"], row["policy"]["suspenseMapping"]); Equal(row["correctionPolicy"]["arMapping"], row["policy"]["arMapping"]);
        if (row["recordedAt"].ToUniversalTime() < original["recordedAt"].ToUniversalTime() || row["receiptBefore"]["version"].AsInt64 < original["receiptVersion"].AsInt64) throw B.Broken();
        foreach (var line in row["lines"].AsBsonArray) {
            var prior = original["lines"].AsBsonArray.SingleOrDefault(value => value["invoiceId"] == line["invoiceId"]) ?? throw B.Broken();
            Equal(line["originallyAllocated"], prior["amount"]); Equal(line["source"], prior["source"]);
            if (line["before"]["version"].AsInt64 < prior["version"].AsInt64) throw B.Broken();
        }
        var people = row["people"].AsBsonArray.Select(ReceivingAccountBson.Person).ToArray(); var users = new[] { row["actorId"].AsInt64, original["actorId"].AsInt64, original["recordedBy"].AsInt64 }.Distinct().Order().ToArray();
        if (!people.Select(person => person.UserId).Order().SequenceEqual(users) || !PersonIndependencePolicy.AreIndependent(people.Single(person => person.UserId == row["actorId"].AsInt64),
            people.Single(person => person.UserId == original["actorId"].AsInt64), people.Single(person => person.UserId == original["recordedBy"].AsInt64))) throw B.Broken();
        Equal(row["binding"], C.Binding(original, row["correctionPolicy"].AsBsonDocument, row["policy"].AsBsonDocument, row["receiptBefore"].AsBsonDocument, row["lines"].AsBsonArray));
    }
    internal async Task<Dictionary<long, decimal>> CorrectionTotalsAsync(IClientSessionHandle tx, long allocationId, CancellationToken ct) {
        var original = await RawAsync(tx, allocationId, ct); var totals = original["lines"].AsBsonArray.ToDictionary(line => line["invoiceId"].AsInt64, _ => 0m); var seen = new HashSet<long>(); var version = original["receiptVersion"].AsInt64;
        var candidates = await Rows(C.Events).Find(tx, new BsonDocument("originalAllocationId", allocationId)).ToListAsync(ct);
        candidates.AddRange(await Rows(A.Events).Find(tx, new BsonDocument("links.originalAllocationId", allocationId)).ToListAsync(ct));
        foreach (var candidate in candidates.OrderBy(row => EventVersion(row, true, original["receiptId"].AsInt64))) {
            var row = await ReadEventAsync(tx, candidate, ct); var nextVersion = EventVersion(row, true, original["receiptId"].AsInt64);
            if (!seen.Add(row["_id"].AsInt64) || nextVersion <= version) throw B.Broken(); version = nextVersion;
            var lines = row["operation"] == C.Operation ? row["lines"].AsBsonArray : new BsonArray(row["links"].AsBsonArray.Where(link => link["originalAllocationId"] == allocationId));
            foreach (var line in lines) {
                var id = row["operation"] == C.Operation ? line["invoiceId"].AsInt64 : row["invoiceId"].AsInt64;
                if (!totals.ContainsKey(id)) throw B.Broken();
                Equal(line[row["operation"] == C.Operation ? "previouslyReversed" : "previouslyReleased"], new BsonDecimal128(totals[id]));
                totals[id] = checked(totals[id] + line["amount"].AsDecimal); if (totals[id] > line["originallyAllocated"].AsDecimal) throw B.Broken();
            }
        }
        foreach (var (table, field, payload) in new[] { ("idempotencyRecords", "operation", "result"), ("auditEvents", "kind", "result"), ("outboxEvents", "kind", "payload") }) {
            using var effects = await Rows(table).Find(tx, new BsonDocument { { field, C.Operation }, { payload + ".originalAllocationId", allocationId } }).ToCursorAsync(ct);
            while (await effects.MoveNextAsync(ct)) foreach (var effect in effects.Current) if (!seen.Contains(effect[payload]["correctionId"].AsInt64)) throw B.Broken();
        }
        await RequireFinancialReleaseEffectsAsync(tx, allocationId, seen, ct);
        return totals;
    }
    internal async Task<BsonDocument?> CorrectionForKeyAsync(IClientSessionHandle tx, long actor, string key, CancellationToken ct) {
        var row = await Rows(C.Events).Find(tx, new BsonDocument { { "actorId", actor }, { "requestKey", key } }).SingleOrDefaultAsync(ct);
        if (row is not null) { var saved = await RawEventAsync(tx, true, row["_id"].AsInt64, ct); await CorrectionTotalsAsync(tx, saved["originalAllocationId"].AsInt64, ct); return saved; }
        foreach (var (table, field) in new[] { ("idempotencyRecords", "operation"), ("auditEvents", "kind"), ("outboxEvents", "kind") })
            if (await Rows(table).Find(tx, new BsonDocument { { "actorId", actor }, { field, C.Operation }, { table == "idempotencyRecords" ? "key" : "requestKey", key } }).AnyAsync(ct)) throw B.Broken();
        return null;
    }
}
