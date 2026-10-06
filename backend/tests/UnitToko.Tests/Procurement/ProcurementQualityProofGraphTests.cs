using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Procurement;
using UnitToko.Tests.Documents;

namespace UnitToko.Tests.Procurement;

[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class ProcurementQualityProofGraphTests(MongoFixture fixture)
{
    [WindowsStorageFact]
    public async Task Aborted_graph_cleanup_preserves_original_failure_but_cannot_reuse_or_cross_attempts()
    {
        using var tx = await fixture.Client.StartSessionAsync(); tx.StartTransaction();
        var graph = new ProcurementQualityProofGraph(tx);
        var original = new InvalidOperationException("Synthetic original reader failure");
        var caught = await Assert.ThrowsAsync<InvalidOperationException>(async () => {
            try { await tx.AbortTransactionAsync(); throw original; }
            finally { graph.Leave(tx, 1); }
        });
        Assert.Same(original, caught);
        Assert.Throws<AuthorizationDeniedException>(() => graph.Require(tx));
        using var other = await fixture.Client.StartSessionAsync();
        Assert.Throws<AuthorizationDeniedException>(() => graph.Leave(other, 1));
        tx.StartTransaction();
        Assert.Throws<AuthorizationDeniedException>(() => graph.Leave(tx, 1));
        Assert.Throws<AuthorizationDeniedException>(() => graph.Require(tx));
        await tx.AbortTransactionAsync();
    }

    [WindowsStorageFact]
    public async Task Completed_actual_quality_graph_rechecks_changed_sources_and_cannot_cross_transaction_or_missing_private_pin()
    {
        await using var fixtureData = await SupplierDocumentPdfMatchTestData.CreateAsync(fixture, quality: true);
        var quality = Assert.IsType<ProcurementQualityAcceptanceTestData>(fixtureData.Quality);
        var data = fixtureData.Data;
        var catalog = quality.Inspection.Receiving.Procurement.Catalog;
        var orders = quality.Inspection.Receiving.Orders.Service;
        var sources = quality.Stock.Sources;
        var inspectionId = quality.Stock.InspectionId;
        var inspection = await data.Rows(ProcurementInspectionTestData.Submissions)
            .Find(new BsonDocument("_id", inspectionId)).SingleAsync();
        var receipt = await data.Rows(ProcurementStockReceiptBson.Receipts)
            .Find(new BsonDocument("_id", quality.ReceiptId)).SingleAsync();
        var pinFilter = new BsonDocument("_id", inspection["pinId"]);
        var pin = await data.Rows("evidencePins").Find(pinFilter).SingleAsync();
        Assert.Equal("ProcurementInspection", pin["associationKind"]);
        Assert.Equal(inspection["_id"], pin["associationId"]);
        var before = await PreservedAsync(data);

        using var tx = await data.Database.Client.StartSessionAsync();
        tx.StartTransaction();
        var graph = new ProcurementQualityProofGraph(tx);
        Task<ProcurementInspectionHistory> InspectionAsync(ProcurementQualityProofGraph selected) =>
            selected.InspectionAsync(data.Database, data.Unit, data.Clock, catalog, orders, tx, inspectionId, CancellationToken.None);
        Task<BsonDocument> ReceiptAsync(ProcurementQualityProofGraph selected) =>
            selected.HeldReceiptAsync(data.Database, data.Unit, data.Clock, catalog, orders, sources, tx, quality.ReceiptId, CancellationToken.None);
        try {
            var firstInspection = await InspectionAsync(graph);
            var firstReceipt = await ReceiptAsync(graph);
            Assert.True(firstInspection.Submissions.Single(row => row["_id"] == inspectionId).Equals(inspection),
                "The graph must first authenticate the actual submitted inspection.");
            Assert.True(firstReceipt.Equals(receipt), "The graph must first authenticate the actual held receipt.");

            // Caller projections are copies, never a seed or a mutable replacement for the completed proof.
            firstReceipt["reason"] = "Synthetic caller-only projection mutation";
            firstInspection.Submissions.Single(row => row["_id"] == inspectionId)["reason"] = "Synthetic caller-only projection mutation";
            Assert.True((await ReceiptAsync(graph)).Equals(receipt), "A mutated receipt projection must not poison completed proof.");
            Assert.True((await InspectionAsync(graph)).Submissions.Single(row => row["_id"] == inspectionId).Equals(inspection),
                "A mutated inspection projection must not poison completed proof.");

            // Derive parent identities only from the genuinely authenticated approved-PO source.
            var approvedSource = firstInspection.Source["notice"]["source"].AsBsonDocument;
            var approvedOrder = approvedSource["order"].AsBsonDocument;
            var approvedOrderFilter = new BsonDocument("_id", approvedOrder["_id"]);
            var originalProposalHash = approvedOrder["proposalHash"].AsString;
            var changedProposalHash = (originalProposalHash[0] == '0' ? "1" : "0") + originalProposalHash[1..];
            Assert.NotEqual(originalProposalHash, changedProposalHash);
            // A schema-valid immutable hash-byte edit must also invalidate both completed reuse paths.
            Assert.Equal(1L, (await data.Rows(PurchaseOrderBson.Orders).UpdateOneAsync(tx, approvedOrderFilter,
                Builders<BsonDocument>.Update.Set("proposalHash", changedProposalHash))).ModifiedCount);
            try {
                Assert.Equal("procurement_quality_acceptance_integrity_unavailable",
                    (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => InspectionAsync(graph))).Code);
                Assert.Equal("procurement_quality_acceptance_integrity_unavailable",
                    (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => ReceiptAsync(graph))).Code);
            } finally {
                Assert.Equal(1L, (await data.Rows(PurchaseOrderBson.Orders)
                    .ReplaceOneAsync(tx, approvedOrderFilter, approvedOrder)).ModifiedCount);
            }
            Assert.True((await ReceiptAsync(graph)).Equals(receipt),
                "Restoring the exact purchase order bytes must restore the same authenticated held receipt.");

            // Deleting either immutable ancestor within this transaction must invalidate both reuse paths.
            foreach (var (table, field) in new[] {
                (PurchaseOrderBson.Orders, "order"), (ProcurementRequestBson.Requests, "request") }) {
                var expectedParent = approvedSource[field].AsBsonDocument;
                var parentFilter = new BsonDocument("_id", expectedParent["_id"]);
                var parent = await data.Rows(table).Find(tx, parentFilter).SingleAsync();
                Assert.True(parent.Equals(expectedParent), $"{table} must be the actual authenticated source parent.");
                Assert.Equal(1L, (await data.Rows(table).DeleteOneAsync(tx, parentFilter)).DeletedCount);
                try {
                    Assert.Equal("procurement_quality_acceptance_integrity_unavailable",
                        (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => InspectionAsync(graph))).Code);
                    Assert.Equal("procurement_quality_acceptance_integrity_unavailable",
                        (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => ReceiptAsync(graph))).Code);
                } finally { await data.Rows(table).InsertOneAsync(tx, parent); }
                Assert.True((await ReceiptAsync(graph)).Equals(receipt),
                    $"Restoring the exact {table} parent must restore the same authenticated held receipt.");
            }

            var receiptFilter = new BsonDocument("_id", receipt["_id"]);
            Assert.Equal(1L, (await data.Rows(ProcurementStockReceiptBson.Receipts).UpdateOneAsync(tx, receiptFilter,
                Builders<BsonDocument>.Update.Set("reason", "Synthetic changed receipt inside the aborted transaction")
                    .Set("input.reason", "Synthetic changed receipt inside the aborted transaction"))).ModifiedCount);
            Assert.Equal("procurement_quality_acceptance_integrity_unavailable",
                (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => ReceiptAsync(graph))).Code);
            Assert.Equal(1L, (await data.Rows(ProcurementStockReceiptBson.Receipts).ReplaceOneAsync(tx, receiptFilter, receipt)).ModifiedCount);

            var inspectionFilter = new BsonDocument("_id", inspection["_id"]);
            Assert.Equal(1L, (await data.Rows(ProcurementInspectionTestData.Submissions).UpdateOneAsync(tx, inspectionFilter,
                Builders<BsonDocument>.Update.Set("reason", "Synthetic changed inspection inside the aborted transaction"))).ModifiedCount);
            Assert.Equal("procurement_quality_acceptance_integrity_unavailable",
                (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => InspectionAsync(graph))).Code);
            Assert.Equal(1L, (await data.Rows(ProcurementInspectionTestData.Submissions).ReplaceOneAsync(tx, inspectionFilter, inspection)).ModifiedCount);

            var balanceFilter = new BsonDocument("_id", receipt["balanceId"]);
            Assert.Equal(1L, (await data.Rows("stockBalances").UpdateOneAsync(tx, balanceFilter,
                Builders<BsonDocument>.Update.Inc("version", 1L))).ModifiedCount);
            Assert.Equal("procurement_quality_acceptance_integrity_unavailable",
                (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => ReceiptAsync(graph))).Code);
        } finally { if (tx.IsInTransaction) await tx.AbortTransactionAsync(); }

        // The same handle is insufficient: a restarted transaction must reject every old completed proof.
        tx.StartTransaction();
        try {
            Assert.Equal("procurement_quality_acceptance_integrity_unavailable",
                (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => InspectionAsync(graph))).Code);
            Assert.Equal("procurement_quality_acceptance_integrity_unavailable",
                (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => ReceiptAsync(graph))).Code);
        } finally { if (tx.IsInTransaction) await tx.AbortTransactionAsync(); }

        Assert.Equal(1L, (await data.Rows("evidencePins").DeleteOneAsync(pinFilter)).DeletedCount);
        try {
            tx.StartTransaction();
            try {
                var fresh = new ProcurementQualityProofGraph(tx);
                Assert.Equal("evidence_validation_integrity",
                    (await Assert.ThrowsAsync<UnitToko.Platform.Mongo.PersistenceConflictException>(() => ReceiptAsync(fresh))).Code);
            } finally { if (tx.IsInTransaction) await tx.AbortTransactionAsync(); }
        } finally { await data.Rows("evidencePins").InsertOneAsync(pin); }

        var after = await PreservedAsync(data);
        foreach (var table in before.Names)
            Assert.True(before[table].Equals(after[table]), $"{table} changed after an aborted proof refusal.");
        Assert.True(fixtureData.Baseline.Equals(await fixtureData.PhysicalAndMoneyAsync()),
            "Proof inspection/refusal must not change stock, invoice, matching or financial state.");
    }

    private static async Task<BsonDocument> PreservedAsync(SupplierDocumentTestData data)
    {
        var result = new BsonDocument();
        foreach (var table in new[] {
            PurchaseOrderBson.Orders, PurchaseOrderBson.Proposals, PurchaseOrderBson.Decisions,
            ProcurementRequestBson.Requests, ProcurementRequestBson.Decisions,
            ProcurementRequestPurchaseOrderSchemas.Commitments, ProcurementRequestPurchaseOrderSchemas.Allocations,
            "procurementInspectionSubmissions", "procurementInspectionHeads", ProcurementReceivingBson.Heads,
            "procurementStockReceipts", "procurementQuarantineDispositions", "procurementQuarantineDispositionHeads",
            "procurementQualityAcceptanceProposals", "procurementQualityAcceptanceDecisions", "procurementQualityAcceptanceAllocations",
            "procurementQualityAcceptanceReceiptHeads", "lots", "stockBalances", "movements", "inventoryPools",
            "evidencePins", "evidencePinAudits", "auditEvents", "outboxEvents", "idempotencyRecords", "idCounters",
            "journalEntries", "cashMovements", "procurementDisbursements", "procurementSettlements" })
            result[table] = new BsonArray(await data.Rows(table).Find(new BsonDocument()).Sort(new BsonDocument("_id", 1)).ToListAsync());
        return result;
    }
}
