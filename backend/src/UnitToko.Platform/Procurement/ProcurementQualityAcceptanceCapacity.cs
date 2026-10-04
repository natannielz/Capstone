using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Inventory;
using B = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;

namespace UnitToko.Platform.Procurement;

internal sealed record ProcurementQualityAcceptanceCapacity(BsonDocument State, BsonDocument? Head, IReadOnlyList<BsonDocument> Allocations);

/// <summary>Receipt-local physical entitlement. An approval never changes a stock balance.</summary>
internal sealed class ProcurementQualityAcceptanceCapacityReader(IMongoDatabase database, StockSourceSetupService sources)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal static BsonDocument Empty(int packages) => new() { { "version", 0L }, { "acceptedPackages", 0L },
        { "remainingPackages", (long)packages }, { "lastAllocationId", BsonNull.Value }, { "lastHash", BsonNull.Value } };
    internal static BsonDocument After(BsonDocument before, int packages, long allocationId, string proposalHash) {
        if (packages <= 0 || packages > before["remainingPackages"].AsInt64 || before["version"].AsInt64 >= long.MaxValue - 1)
            throw B.Changed("procurement_quality_acceptance_capacity_exceeded");
        var next = before.DeepClone().AsBsonDocument;
        next["version"] = before["version"].AsInt64 + 1; next["acceptedPackages"] = checked(before["acceptedPackages"].AsInt64 + packages);
        next["remainingPackages"] = before["remainingPackages"].AsInt64 - packages;
        next["lastAllocationId"] = allocationId; next["lastHash"] = proposalHash; return next;
    }
    internal static BsonDocument Head(BsonDocument state, BsonDocument receipt, DateTimeOffset at, BsonDocument? previous) {
        var row = new BsonDocument { { "_id", receipt["_id"] }, { "schemaVersion", 1 }, { "receiptId", receipt["_id"] },
            { "sourceHash", receipt["snapshotHash"] }, { "createdAt", previous?["createdAt"] ?? at.UtcDateTime },
            { "updatedAt", at.UtcDateTime }, { "fence", previous?["fence"] ?? 0L } };
        foreach (var element in state.Elements) row.Add(element.DeepClone()); return row;
    }
    private static BsonDocument State(BsonDocument head) { var row = head.DeepClone().AsBsonDocument;
        foreach (var field in new[] { "_id", "schemaVersion", "receiptId", "sourceHash", "createdAt", "updatedAt", "fence" }) row.Remove(field);
        return row;
    }
    internal async Task<ProcurementQualityAcceptanceCapacity> ReadAsync(IClientSessionHandle tx, BsonDocument receipt, CancellationToken ct) {
        var receiptId = receipt["_id"].AsInt64; var head = await Rows(B.ReceiptHeads).Find(tx, new BsonDocument("_id", receiptId)).SingleOrDefaultAsync(ct);
        var allocations = await Rows(B.Allocations).Find(tx, new BsonDocument("receiptId", receiptId)).Sort(new BsonDocument("sequence", 1)).ToListAsync(ct);
        var decisions = await Rows(B.Decisions).Find(tx, new BsonDocument { { "decision", "approve" }, { "input.receiptId", receiptId } }).ToListAsync(ct);
        if (decisions.Count != allocations.Count || !decisions.Select(row => row["allocationId"]).ToHashSet().SetEquals(allocations.Select(row => row["_id"]))) throw B.Broken();
        // A deleted decision, allocation and head must not reopen an already reviewed receipt.
        // The immutable proposal and operation effects retain the decision's existence.
        var proposals = await Rows(B.Proposals).Find(tx, new BsonDocument("input.receiptId", receiptId)).ToListAsync(ct);
        foreach (var proposal in proposals) {
            var proposalId = proposal["_id"];
            var reviewed = await Rows(B.Decisions).Find(tx, new BsonDocument("proposalId", proposalId)).Limit(2).ToListAsync(ct);
            if (reviewed.Count > 1) throw B.Broken();
            if (reviewed.Count == 0 && (
                await Rows("auditEvents").Find(tx, new BsonDocument { { "kind", B.DecideOperation }, { "result.proposalId", proposalId } }).AnyAsync(ct) ||
                await Rows("outboxEvents").Find(tx, new BsonDocument { { "kind", B.DecideOperation }, { "payload.proposalId", proposalId } }).AnyAsync(ct) ||
                await Rows("idempotencyRecords").Find(tx, new BsonDocument { { "operation", B.DecideOperation }, { "result.proposalId", proposalId } }).AnyAsync(ct))) throw B.Broken();
        }
        var state = Empty(receipt["input"]["packages"].AsInt32); BsonValue priorAt = BsonNull.Value;
        foreach (var allocation in allocations) {
            if (allocation["snapshotHash"] != B.Hash(allocation) || allocation["receiptHash"] != receipt["snapshotHash"] ||
                allocation["receiptId"] != receipt["_id"] || allocation["capacityBefore"] != state ||
                !priorAt.IsBsonNull && allocation["recordedAt"].CompareTo(priorAt) < 0) throw B.Broken();
            var proposal = await Rows(B.Proposals).Find(tx, new BsonDocument("_id", allocation["proposalId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            var decision = decisions.SingleOrDefault(row => row["_id"] == allocation["decisionId"]) ?? throw B.Broken();
            if (proposal["snapshotHash"] != B.Hash(proposal) || decision["snapshotHash"] != B.Hash(decision) ||
                proposal["snapshotHash"] != allocation["proposalHash"] || decision["proposalHash"] != proposal["snapshotHash"] ||
                decision["proposalId"] != proposal["_id"] || decision["decision"] != "approve" ||
                decision["allocationId"] != allocation["_id"] || decision["input"] != proposal["input"] ||
                decision["capacityBefore"] != state || allocation["packages"] != proposal["input"]["acceptedPackages"] ||
                allocation["recordedAt"] != decision["recordedAt"] || proposal["receiptHash"] != receipt["snapshotHash"])
                throw B.Broken();
            var policy = await Rows(B.Policies).Find(tx, new BsonDocument("_id", proposal["input"]["policyId"])).SingleOrDefaultAsync(ct) ?? throw B.Broken();
            if (proposal["policy"] != B.PolicySnapshot(policy) || proposal["input"]["policyRevision"] != policy["revision"] ||
                proposal["sourceConfigurationHash"] != B.SourceConfigurationHash(proposal["poolSource"].AsBsonDocument)) throw B.Broken();
            await sources.RequireHeldReceiptHistoryAsync(tx, proposal["poolSource"].AsBsonDocument,
                new DateTimeOffset(proposal["recordedAt"].ToUniversalTime()), ct);
            await ProcurementQualityAcceptanceRecords.RequireEffectsAsync(database, tx, proposal, ct);
            await ProcurementQualityAcceptanceRecords.RequireEffectsAsync(database, tx, decision, ct, proposal);
            state = After(state, allocation["packages"].AsInt32, allocation["_id"].AsInt64, proposal["snapshotHash"].AsString);
            if (allocation["capacityAfter"] != state || decision["capacityAfter"] != state || allocation["sequence"] != state["version"]) throw B.Broken();
            priorAt = allocation["recordedAt"];
        }
        if ((head is null) != (allocations.Count == 0) || head is not null &&
            (head["sourceHash"] != receipt["snapshotHash"] || head["receiptId"] != receipt["_id"] || State(head) != state ||
             head["updatedAt"] != priorAt)) throw B.Broken();
        return new(state, head, allocations);
    }
}
