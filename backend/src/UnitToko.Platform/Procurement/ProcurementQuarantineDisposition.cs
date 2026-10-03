using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Procurement;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using B = UnitToko.Platform.Procurement.SupplierClaimBson;

namespace UnitToko.Platform.Procurement;

internal static class ProcurementQuarantineDispositionBson
{
    internal const string PhysicalHeads = "procurementQuarantineDispositionHeads", PhysicalAllocations = "procurementQuarantineDispositions",
        ShortageHeads = "supplierClaimShortageHeads", ShortageAllocations = "supplierClaimShortageAllocations";
    internal static string Heads(SupplierClaimBasis basis) => basis == SupplierClaimBasis.PhysicalReceived ? PhysicalHeads : ShortageHeads;
    internal static string Allocations(SupplierClaimBasis basis) => basis == SupplierClaimBasis.PhysicalReceived ? PhysicalAllocations : ShortageAllocations;
    internal static long Total(SupplierClaimBasis basis, BsonValue observed) => basis == SupplierClaimBasis.PhysicalReceived ? observed["receivedPackages"].AsInt32 :
        Math.Max(0L, (long)observed["declaredPackages"].AsInt32 - observed["receivedPackages"].AsInt32);
    internal static BsonDocument Empty(SupplierClaimBasis basis, long inspectionId, int line, string hash, long total) => new() {
        { "basis", B.Basis(basis) }, { "inspectionId", inspectionId }, { "inspectionLineNumber", line }, { "inspectionHash", hash }, { "headId", BsonNull.Value },
        { "version", 0L }, { "totalPackages", total }, { "occupiedPackages", 0L }, { "remainingPackages", total } };
    internal static void RequireCapacity(BsonDocument row, SupplierClaimBasis basis, long inspectionId, int line, string hash, long total) {
        if (row.ElementCount != 9 || total < 0 || row["basis"] != B.Basis(basis) || row["inspectionId"] != inspectionId || row["inspectionLineNumber"] != line || row["inspectionHash"] != hash ||
            row["totalPackages"] != total || row["version"].AsInt64 is < 0 or >= long.MaxValue - 1 || row["occupiedPackages"].AsInt64 < 0 || row["remainingPackages"].AsInt64 < 0 ||
            row["occupiedPackages"].AsInt64 > total || row["remainingPackages"].AsInt64 != total - row["occupiedPackages"].AsInt64 ||
            (row["version"] == 0L) != row["headId"].IsBsonNull || row["headId"] is { IsBsonNull: false } head && head.AsInt64 <= 0 ||
            (row["version"] == 0L) != (row["occupiedPackages"] == 0L)) throw B.Broken();
    }
    internal static BsonDocument After(BsonDocument before, long headId, int packages) {
        if (headId <= 0 || packages <= 0 || packages > before["remainingPackages"].AsInt64 || !before["headId"].IsBsonNull && before["headId"] != headId) throw B.Changed("supplier_claim_capacity_exceeded");
        var result = before.DeepClone().AsBsonDocument; result["headId"] = headId; result["version"] = checked(before["version"].AsInt64 + 1);
        result["occupiedPackages"] = checked(before["occupiedPackages"].AsInt64 + packages); result["remainingPackages"] = before["remainingPackages"].AsInt64 - packages; return result;
    }
    internal static BsonDocument Allocation(BsonDocument request, int index) {
        var spec = request["allocations"][index]; var row = new BsonDocument { { "_id", spec["allocationId"] }, { "schemaVersion", 1 }, { "kind", "supplier_claim" },
            { "basis", spec["basis"] }, { "claimId", request["_id"] }, { "claimLineNumber", index + 1 }, { "requestHash", request["snapshotHash"] }, { "headId", spec["headId"] },
            { "inspectionId", spec["inspectionId"] }, { "inspectionLineNumber", spec["inspectionLineNumber"] }, { "inspectionHash", spec["inspectionHash"] }, { "location", request["location"] },
            { "packages", spec["packages"] }, { "sequence", spec["after"]["version"] }, { "before", spec["before"] }, { "after", spec["after"] },
            { "previousAllocationId", spec["previousAllocationId"] }, { "previousHash", spec["previousHash"] }, { "recordedAt", request["recordedAt"] } };
        row.Add("snapshotHash", B.Hash(row)); return row;
    }
}

internal sealed record ProcurementQuarantineCapacity(SupplierClaimBasis Basis, BsonDocument? Head, BsonDocument Capacity,
    BsonDocument Location, long OrderId, long SupplierId, IReadOnlyDictionary<string, long> IssuePackages);

/// <summary>One physical inspection capacity for genuine supplier claims and fully held inventory receipts.
/// Shortage observations have separate storage and never occupy physical goods.</summary>
internal sealed class ProcurementQuarantineDisposition(IMongoDatabase database, SupplierClaimRecords records)
{
    private IMongoCollection<BsonDocument> Rows(string name) => database.GetCollection<BsonDocument>(name);
    internal async Task<ProcurementQuarantineCapacity> ReadAsync(IClientSessionHandle tx, SupplierClaimBasis basis, long inspectionId, int line,
        string hash, long total, BsonDocument location, long orderId, long supplierId, CancellationToken ct) {
        var filter = new BsonDocument { { "inspectionId", inspectionId }, { "inspectionLineNumber", line } };
        var head = await Rows(ProcurementQuarantineDispositionBson.Heads(basis)).Find(tx, filter).SingleOrDefaultAsync(ct);
        if (head is not null) head = await records.RowAsync(tx, ProcurementQuarantineDispositionBson.Heads(basis), head["_id"].AsInt64, ct);
        var allocations = await Rows(ProcurementQuarantineDispositionBson.Allocations(basis)).Find(tx, filter).Sort(new BsonDocument("sequence", 1)).ToListAsync(ct);
        var claimFilter = new BsonDocument("input.lines", new BsonDocument("$elemMatch", new BsonDocument { { "basis", B.Basis(basis) }, { "inspectionId", inspectionId }, { "inspectionLineNumber", line } }));
        var requests = await Rows(B.Requests).Find(tx, claimFilter).ToListAsync(ct); var expected = new List<BsonDocument>(); var issues = new Dictionary<string, long>(StringComparer.Ordinal);
        // ponytail: Complete owner reconstruction detects deleted allocations/heads. Replace only with an indexed reconciliation proof after measured load warrants it.
        foreach (var raw in requests) {
            var request = await records.RequestAsync(tx, raw["_id"].AsInt64, ct); SupplierClaimRecords.Equal(request["location"], location); SupplierClaimRecords.Equal(request["orderId"], orderId); SupplierClaimRecords.Equal(request["supplierId"], supplierId);
            var input = B.Input(request["input"]); var index = Array.FindIndex(input.Lines.ToArray(), value => value.Basis == basis && value.InspectionId == inspectionId && value.InspectionLineNumber == line);
            if (index < 0) throw B.Broken(); expected.Add(ProcurementQuarantineDispositionBson.Allocation(request, index));
            var reported = input.Lines[index]; issues[reported.Issue] = checked(issues.GetValueOrDefault(reported.Issue) + reported.Packages);
            var condition = reported.Issue switch { "damaged" => "damagedPackages", "expired" => "expiredPackages", "opened" => "openedPackages", _ => null };
            if (condition is not null && issues[reported.Issue] > request["lines"][index]["inspection"]["line"][condition].AsInt32) throw B.Broken();
        }
        if (basis == SupplierClaimBasis.PhysicalReceived) {
            foreach (var raw in await Rows(ProcurementStockReceiptBson.Receipts).Find(tx, filter).ToListAsync(ct)) {
                var receipt = await records.HeldReceipts.ReadAsync(tx, raw["_id"].AsInt64, ct);
                SupplierClaimRecords.Equal(receipt["location"], location); SupplierClaimRecords.Equal(receipt["orderId"], orderId); SupplierClaimRecords.Equal(receipt["supplierId"], supplierId);
                expected.Add(ProcurementStockReceiptBson.Allocation(receipt));
            }
        }
        expected = expected.OrderBy(row => row["sequence"].AsInt64).ToList(); if (expected.Count != allocations.Count || (head is null) != (expected.Count == 0)) throw B.Broken();
        var capacity = ProcurementQuarantineDispositionBson.Empty(basis, inspectionId, line, hash, total); BsonValue previous = BsonNull.Value, previousHash = BsonNull.Value, at = BsonNull.Value;
        for (var i = 0; i < expected.Count; i++) {
            var row = await records.RowAsync(tx, ProcurementQuarantineDispositionBson.Allocations(basis), allocations[i]["_id"].AsInt64, ct);
            SupplierClaimRecords.Equal(row, expected[i]); SupplierClaimRecords.Equal(row["before"], capacity); SupplierClaimRecords.Equal(row["previousAllocationId"], previous); SupplierClaimRecords.Equal(row["previousHash"], previousHash);
            if (!at.IsBsonNull && row["recordedAt"].CompareTo(at) < 0 || row["headId"] != head!["_id"]) throw B.Broken();
            capacity = ProcurementQuarantineDispositionBson.After(capacity, row["headId"].AsInt64, row["packages"].AsInt32); SupplierClaimRecords.Equal(row["after"], capacity);
            previous = row["_id"]; previousHash = row["snapshotHash"]; at = row["recordedAt"];
        }
        ProcurementQuarantineDispositionBson.RequireCapacity(capacity, basis, inspectionId, line, hash, total);
        if (head is not null) {
            foreach (var field in new[] { "basis", "inspectionId", "inspectionLineNumber", "inspectionHash", "version", "totalPackages", "occupiedPackages", "remainingPackages" }) SupplierClaimRecords.Equal(head[field], capacity[field]);
            SupplierClaimRecords.Equal(head["orderId"], orderId); SupplierClaimRecords.Equal(head["supplierId"], supplierId); SupplierClaimRecords.Equal(head["location"], location);
            SupplierClaimRecords.Equal(head["lastAllocationId"], previous); SupplierClaimRecords.Equal(head["lastHash"], previousHash); SupplierClaimRecords.Equal(head["createdAt"], expected[0]["recordedAt"]); SupplierClaimRecords.Equal(head["updatedAt"], at);
        }
        await records.OrphansAsync(tx, ct); return new(basis, head, capacity, location, orderId, supplierId, issues);
    }
    internal static BsonDocument Head(ProcurementQuarantineCapacity capture, BsonDocument allocation) {
        var cap = allocation["after"]; return new() { { "_id", allocation["headId"] }, { "schemaVersion", 1 }, { "basis", cap["basis"] }, { "location", capture.Location }, { "orderId", capture.OrderId }, { "supplierId", capture.SupplierId },
            { "inspectionId", cap["inspectionId"] }, { "inspectionLineNumber", cap["inspectionLineNumber"] }, { "inspectionHash", cap["inspectionHash"] },
            { "version", cap["version"] }, { "totalPackages", cap["totalPackages"] }, { "occupiedPackages", cap["occupiedPackages"] }, { "remainingPackages", cap["remainingPackages"] },
            { "lastAllocationId", allocation["_id"] }, { "lastHash", allocation["snapshotHash"] }, { "fence", capture.Head?["fence"] ?? 0L },
            { "createdAt", capture.Head?["createdAt"] ?? allocation["recordedAt"] }, { "updatedAt", allocation["recordedAt"] } };
    }
    internal async Task OrphansAsync(IClientSessionHandle tx, CancellationToken ct) {
        foreach (var table in new[] { ProcurementQuarantineDispositionBson.PhysicalHeads, ProcurementQuarantineDispositionBson.ShortageHeads,
            ProcurementQuarantineDispositionBson.PhysicalAllocations, ProcurementQuarantineDispositionBson.ShortageAllocations }) {
            var rows = await Rows(table).Aggregate<BsonDocument>(tx, new BsonDocument[] {
                new("$lookup", new BsonDocument { { "from", ProcurementInspectionBson.Submissions }, { "localField", "inspectionId" }, { "foreignField", "_id" }, { "as", "source" } }),
                new("$match", new BsonDocument("source", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct); if (rows.Count != 0) throw B.Broken();
        }
        foreach (var table in new[] { ProcurementQuarantineDispositionBson.PhysicalAllocations, ProcurementQuarantineDispositionBson.ShortageAllocations }) {
            var rows = await Rows(table).Aggregate<BsonDocument>(tx, new BsonDocument[] {
                new("$match", new BsonDocument("kind", "supplier_claim")),
                new("$lookup", new BsonDocument { { "from", B.Requests }, { "localField", "claimId" }, { "foreignField", "_id" }, { "as", "owner" } }),
                new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct); if (rows.Count != 0) throw B.Broken();
        }
        var held = await Rows(ProcurementQuarantineDispositionBson.PhysicalAllocations).Aggregate<BsonDocument>(tx, new BsonDocument[] {
            new("$match", new BsonDocument("kind", "held_receipt")),
            new("$lookup", new BsonDocument { { "from", ProcurementStockReceiptBson.Receipts }, { "localField", "receiptId" }, { "foreignField", "_id" }, { "as", "owner" } }),
            new("$match", new BsonDocument("owner", new BsonDocument("$size", 0))), new("$limit", 1) }).ToListAsync(ct);
        if (held.Count != 0) throw B.Broken(); await records.HeldReceipts.OrphansAsync(tx, ct);
    }
}
