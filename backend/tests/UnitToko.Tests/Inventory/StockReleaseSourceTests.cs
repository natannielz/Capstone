using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Contracts;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using UnitToko.Platform.Mongo;
using UnitToko.Tests.Documents;
using UnitToko.Tests.Procurement;
using UnitToko.Platform.Procurement;
using Xunit.Abstractions;

namespace UnitToko.Tests.Inventory;

/// <summary>Real private inspection/invoice/quality/posted-value owners in isolated
/// synthetic Mongo. This proof must not release stock or consume financial capacities.</summary>
[Collection("Mongo")]
[SupportedOSPlatform("windows")]
public sealed class StockReleaseSourceTests(MongoFixture fixture, ITestOutputHelper output)
{
    [WindowsStorageFact]
    public async Task Exact_posted_mixed_quality_origin_is_defensive_transaction_bound_and_changes_no_original_capacity_or_stock() {
        using var timing = new StockReleaseTestDiagnostics(output);
        await using var data = await ProcurementStockValuationTestData.CreateAsync(fixture, mixedQuality: true);
        timing.Stage("fixture");
        var proposed = await data.ProposeAsync("release-source-posted-origin");
        var accepted = await data.DecideAsync(proposed["proposalId"].AsInt64, "release-source-posted-origin-approved");
        timing.Stage("value-posting");
        var selected = new StockReleaseSelection(data.ReceiptId, data.Quality!["allocationId"].AsInt64, accepted["allocationId"].AsInt64);
        var reader = new StockReleaseSource(data.Database, data.Unit, data.Clock, data.Inspection.Receiving.Procurement.Catalog,
            data.Inspection.Receiving.Orders.Service, data.Sources, data.Service);
        var original = await data.PreservedAsync(); var valuation = await data.StateAsync();
        StockReleaseSourceProof? saved = null;
        timing.Restart();
        await data.Unit.ExecuteAsync(async (tx, ct) => {
            var proof = await reader.ReadCurrentAsync(tx, selected, ct); saved = proof; proof.Require(data.Database, tx);
            Assert.Equal(selected, proof.Selection); Assert.Equal(data.Receipt["balanceId"].AsInt64, proof.BalanceId);
            Assert.Equal(2L, proof.AcceptedPackages); Assert.Equal(1L, proof.ValuedPackages); Assert.Equal("11250.00", proof.CapitalizedValue.ToString());
            Assert.False(proof.CleanValuationJoin); Assert.Equal("valuation-quality-bound", proof.Snapshot["qualityJoin"].AsString);
            var receiptCopy = proof.Receipt; receiptCopy["snapshotHash"] = "tampered-copy";
            var valueCopy = proof.ValuationAllocation; valueCopy["capitalizedAmount"] = "0.00";
            var keyCopy = proof.Snapshot; keyCopy["stockKey"] = new BsonDocument();
            proof.Require(data.Database, tx); Assert.NotEqual("tampered-copy", proof.Receipt["snapshotHash"].AsString);
            Assert.Equal("11250.00", proof.ValuationAllocation["capitalizedAmount"].AsString);
            Assert.Equal(4, proof.Snapshot["stockKey"].AsBsonDocument.ElementCount); return true;
        });
        timing.Stage("source-read");
        Assert.NotNull(saved);
        await data.Unit.ExecuteAsync((tx, ct) => {
            Assert.Throws<PersistenceConflictException>(() => saved.Require(data.Database, tx)); return Task.FromResult(true);
        });
        Assert.Equal(original, await data.PreservedAsync()); Assert.Equal(valuation, await data.StateAsync()); await data.NoPaymentOrReleaseAsync();
        using var originTx = await fixture.Client.StartSessionAsync(); originTx.StartTransaction();
        var origin = await HeldReceiptOriginProof.CaptureAsync(data.Database, data.Unit, data.Clock, data.Inspection.Receiving.Procurement.Catalog,
            data.Inspection.Receiving.Orders.Service, data.Sources, originTx, data.ReceiptId, default);
        var valueOwner = data.Service; var paired = valueOwner.CreateReleaseOriginReader(originTx, origin);
        await paired.ReadAsync(valueOwner, data.Database, originTx, origin, selected.QualityAllocationId, selected.ValuationAllocationId, default);
        await paired.ReadAsync(valueOwner, data.Database, originTx, origin, selected.QualityAllocationId, selected.ValuationAllocationId, default);
        Assert.Equal("procurement_stock_valuation_integrity_unavailable", Assert.Throws<AuthorizationDeniedException>(() => paired.Require(data.Service, data.Database, originTx, origin)).Code);
        await origin.RequireAsync(data.Database, originTx, default); await origin.RequireAsync(data.Database, originTx, default);
        var custody = data.Rows(ProcurementReceivingBson.Heads);
        var head = await custody.Find(originTx, new BsonDocument("noticeId", data.Inspection.NoticeId)).SingleAsync();
        Assert.Equal(1L, (await custody.UpdateOneAsync(originTx, head, Builders<BsonDocument>.Update.Inc("version", 1L))).ModifiedCount);
        Assert.Equal("procurement_quality_acceptance_integrity_unavailable", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            origin.RequireAsync(data.Database, originTx, default))).Code);
        await originTx.AbortTransactionAsync();
        Assert.Throws<DomainValidationException>(() => origin.Require(data.Database, originTx));
        Assert.Equal("procurement_stock_valuation_integrity_unavailable", Assert.Throws<AuthorizationDeniedException>(() => paired.Require(valueOwner, data.Database, originTx, origin)).Code);
        originTx.StartTransaction();
        Assert.Throws<DomainValidationException>(() => origin.Require(data.Database, originTx));
        Assert.Equal("procurement_stock_valuation_integrity_unavailable", Assert.Throws<AuthorizationDeniedException>(() => paired.Require(valueOwner, data.Database, originTx, origin)).Code);
        await originTx.AbortTransactionAsync();
        originTx.StartTransaction();
        var pinGuard = await HeldReceiptOriginProof.CaptureAsync(data.Database, data.Unit, data.Clock, data.Inspection.Receiving.Procurement.Catalog,
            data.Inspection.Receiving.Orders.Service, data.Sources, originTx, data.ReceiptId, default);
        var pinReader = valueOwner.CreateReleaseOriginReader(originTx, pinGuard);
        await pinReader.ReadAsync(valueOwner, data.Database, originTx, pinGuard, selected.QualityAllocationId, selected.ValuationAllocationId, default);
        await pinGuard.RequireAsync(data.Database, originTx, default);
        Assert.Equal(1L, (await data.Rows("evidencePins").DeleteOneAsync(originTx, new BsonDocument("_id", pinGuard.Receipt["inspection"]["pinId"]))).DeletedCount);
        Assert.Equal("evidence_validation_integrity", (await Assert.ThrowsAsync<PersistenceConflictException>(() =>
            pinReader.ReadAsync(valueOwner, data.Database, originTx, pinGuard, selected.QualityAllocationId, selected.ValuationAllocationId, default))).Code);
        await originTx.AbortTransactionAsync();
        originTx.StartTransaction();
        var decisionGuard = await HeldReceiptOriginProof.CaptureAsync(data.Database, data.Unit, data.Clock, data.Inspection.Receiving.Procurement.Catalog,
            data.Inspection.Receiving.Orders.Service, data.Sources, originTx, data.ReceiptId, default);
        var decisionReader = valueOwner.CreateReleaseOriginReader(originTx, decisionGuard);
        var valid = await decisionReader.ReadAsync(valueOwner, data.Database, originTx, decisionGuard, selected.QualityAllocationId, selected.ValuationAllocationId, default);
        Assert.Equal(1L, (await data.Rows(ProcurementQualityAcceptanceBson.Decisions).UpdateOneAsync(originTx, new BsonDocument("_id", valid.Quality.Decision["_id"]),
            Builders<BsonDocument>.Update.Set("reason", "Synthetic same-transaction quality decision tampering"))).ModifiedCount);
        Assert.Equal("procurement_quality_acceptance_integrity_unavailable", (await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            decisionReader.ReadAsync(valueOwner, data.Database, originTx, decisionGuard, selected.QualityAllocationId, selected.ValuationAllocationId, default))).Code);
        await originTx.AbortTransactionAsync();
        Assert.Equal(original, await data.PreservedAsync()); Assert.Equal(valuation, await data.StateAsync());
    }
    [WindowsStorageFact]
    public async Task Current_movement_origin_reuse_after_actual_release_rechecks_immutable_owner_and_cannot_cross_attempts() {
        using var timing = new StockReleaseTestDiagnostics(output);
        await using var data = await StockReleaseServiceTests.Data.CreateAsync(fixture);
        var proposed = await data.ProposeAsync("current-origin-proposal");
        await data.DecideAsync(proposed["proposalId"].AsInt64, "current-origin-approved");
        var before = await data.BusinessEffectsAsync();
        using var tx = await data.Database.Client.StartSessionAsync(); tx.StartTransaction();
        var graph = new ProcurementQualityProofGraph(tx);
        var catalog = data.Valuation.Inspection.Receiving.Procurement.Catalog;
        var orders = data.Valuation.Inspection.Receiving.Orders.Service;
        Task<BsonDocument> Read() => graph.HeldReceiptAsync(data.Database, data.Unit, data.Clock, catalog, orders,
            data.Valuation.Sources, tx, data.Valuation.ReceiptId, CancellationToken.None);
        try {
            var first = await Read(); var original = first.DeepClone().AsBsonDocument;
            first["reason"] = "Synthetic caller-only projection change";
            Assert.Equal(original, await Read());
            var audit = await data.Rows("auditEvents").Find(tx, new BsonDocument("_id", original["auditId"])).SingleAsync();
            Assert.Equal(1L, (await data.Rows("auditEvents").UpdateOneAsync(tx, new BsonDocument("_id", audit["_id"]),
                Builders<BsonDocument>.Update.Set("correlationId", "synthetic-changed-current-owner"))).ModifiedCount);
            Assert.Equal("procurement_quality_acceptance_integrity_unavailable",
                (await Assert.ThrowsAsync<AuthorizationDeniedException>(Read)).Code);
        } finally { if (tx.IsInTransaction) await tx.AbortTransactionAsync(); }
        Assert.Equal(before, await data.BusinessEffectsAsync());
        tx.StartTransaction();
        Assert.Throws<AuthorizationDeniedException>(() => graph.Require(tx));
        await tx.AbortTransactionAsync();
    }
}

// Test-only aggregate timing. Only fixed phase/stage names, counts and elapsed
// durations are printed; no arguments, identities, private data or proof payloads.
internal sealed class StockReleaseTestDiagnostics : IDisposable {
    private static readonly HashSet<string> Phases = ["fixture", "value-posting", "quality-approval", "fixture-access", "source-read", "concurrent-approvals"];
    private static readonly HashSet<string> Stages = ["quality.receipt", "quality.capacity", "quality.policy", "quality.configuration", "quality.effects", "quality.disposition",
        "release.current-source", "release.activation", "release.available-value", "release.held-value", "release.remaining-quality",
        "receipt.history", "receipt.order", "receipt.inspection", "receipt.configuration", "receipt.stock-effects", "source.inspection-history", "source.quality",
        "matching.verify-sources", "matching.pdf-candidates", "matching.resolve", "matching.quality-heads", "matching.source-capacities", "matching.storage", "matching.context", "matching.fence"];
    private readonly ITestOutputHelper output;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, (long Count, double Milliseconds)> totals = new();
    private readonly ActivityListener listener;
    internal StockReleaseTestDiagnostics(ITestOutputHelper output) {
        this.output = output;
        listener = new ActivityListener {
            ShouldListenTo = source => source.Name == SupplierDocumentMatchDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => Stages.Contains(options.Name) ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            SampleUsingParentId = (ref ActivityCreationOptions<string> options) => Stages.Contains(options.Name) ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStopped = activity => {
                if (Stages.Contains(activity.OperationName)) totals.AddOrUpdate(activity.OperationName, (1, activity.Duration.TotalMilliseconds),
                    (_, previous) => (previous.Count + 1, previous.Milliseconds + activity.Duration.TotalMilliseconds));
            }
        };
        ActivitySource.AddActivityListener(listener);
    }
    internal void Restart() => elapsed.Restart();
    internal void Stage(string name) {
        if (!Phases.Contains(name)) throw new ArgumentException("A fixed release timing phase is required", nameof(name));
        output.WriteLine("Stock release phase=" + name + " durationMs=" + elapsed.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture));
        elapsed.Restart();
    }
    public void Dispose() {
        listener.Dispose();
        foreach (var (stage, total) in totals.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            output.WriteLine("Stock release inclusive-stage=" + stage + " calls=" + total.Count.ToString(CultureInfo.InvariantCulture) +
                " durationMs=" + total.Milliseconds.ToString("F3", CultureInfo.InvariantCulture));
    }
}
