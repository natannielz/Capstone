using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Accounting;
using UnitToko.Modules.Contracts;
using UnitToko.Modules.Inventory;
using UnitToko.Platform.Catalog;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Mongo.Migrations;
using UnitToko.Platform.Procurement;
using Q = UnitToko.Platform.Procurement.ProcurementQualityAcceptanceBson;
using V = UnitToko.Platform.Procurement.ProcurementStockValuationBson;

namespace UnitToko.Platform.Inventory;

internal sealed record StockReleaseSelection(long ReceiptId, long QualityAllocationId, long ValuationAllocationId)
{
    internal void Validate() {
        if (ReceiptId is <= 0 or >= long.MaxValue - 2 || QualityAllocationId is <= 0 or >= long.MaxValue - 2 ||
            ValuationAllocationId is <= 0 or >= long.MaxValue - 2) throw StockReleaseSourceProof.Unavailable();
    }
}

/// <summary>Authenticated selected acquisition/quality origin only. This reader does
/// not consume either entitlement, release held stock, or touch supplier/finance capacity.</summary>
internal sealed class StockReleaseSource(IMongoDatabase database, MongoUnitOfWork unit, IClock clock,
    CatalogAuthoringService catalog, PurchaseOrderService orders, StockSourceSetupService sources,
    ProcurementStockValuationService valuations)
{
    internal Task<StockReleaseSourceProof> ReadCurrentAsync(IClientSessionHandle tx, StockReleaseSelection selected, CancellationToken ct) =>
        StockReleaseSourceProof.ReadCurrentAsync(database, unit, clock, catalog, orders, sources, valuations, tx, selected, ct);
    internal Task<StockReleaseSourceProof> ReadOriginAsync(IClientSessionHandle tx, StockReleaseSelection selected, HeldReceiptOriginProof origin, CancellationToken ct) =>
        StockReleaseSourceProof.ReadOriginAsync(database, unit, clock, catalog, orders, sources, valuations, tx, selected, origin, ct);
    internal Task<StockReleaseSourceProof> ReadOriginAsync(IClientSessionHandle tx, StockReleaseSelection selected, HeldReceiptOriginProof origin,
        ProcurementStockValuationService.ReleaseOriginReader reader, CancellationToken ct) =>
        StockReleaseSourceProof.ReadOriginAsync(database, unit, clock, catalog, orders, sources, valuations, tx, selected, origin, ct, reader);
    internal ProcurementStockValuationService.ReleaseOriginReader CreateOriginReader(IClientSessionHandle tx, HeldReceiptOriginProof origin) =>
        valuations.CreateReleaseOriginReader(tx, origin);
    internal Task<HeldReceiptOriginProof> CaptureOriginAsync(IClientSessionHandle tx, long receiptId, CancellationToken ct) =>
        HeldReceiptOriginProof.CaptureAsync(database, unit, clock, catalog, orders, sources, tx, receiptId, ct);
}

/// <summary>Private construction follows the actual owning readers. Defensive document
/// copies never permit a caller to manufacture a quality, receipt or posted-value proof.</summary>
internal sealed class StockReleaseSourceProof
{
    private readonly IMongoDatabase database;
    private readonly IClientSessionHandle session;
    private readonly long transactionNumber;
    private readonly BsonDocument receipt, lot, qualityProposal, qualityDecision, qualityAllocation,
        qualitySnapshot, qualityPolicy, valuationProposal, valuationDecision, valuationAllocation, invoice, match, snapshot;
    private readonly BsonDocument? pool, balance;
    private readonly HeldReceiptOriginProof? currentOrigin;
    private readonly ProcurementStockValuationService.ReleaseOriginReader? currentReader;
    private readonly string seal;
    private StockReleaseSourceProof(IMongoDatabase database, IClientSessionHandle tx, StockReleaseSelection selected,
        ProcurementApprovedQualitySource quality, ProcurementPayableValuationSource value, BsonDocument lot,
        BsonDocument? pool, BsonDocument? balance, BsonDocument qualityPolicy, DateTimeOffset observedAt,
        HeldReceiptOriginProof? currentOrigin = null, ProcurementStockValuationService.ReleaseOriginReader? currentReader = null)
    {
        this.database = database; session = tx; transactionNumber = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
        Selection = selected with { }; ObservedAt = observedAt;
        receipt = Copy(quality.Receipt); this.lot = Copy(lot); this.pool = pool is null ? null : Copy(pool); this.balance = balance is null ? null : Copy(balance);
        currentOrigin?.Require(database, tx);
        if (currentOrigin is not null && (pool is null || balance is null || currentOrigin.Receipt != receipt)) throw Unavailable();
        if (currentReader is not null && currentOrigin is null) throw Unavailable();
        this.currentOrigin = currentOrigin; this.currentReader = currentReader;
        qualityProposal = Copy(quality.Proposal); qualityDecision = Copy(quality.Decision); qualityAllocation = Copy(quality.Allocation);
        qualitySnapshot = Copy(quality.Snapshot); this.qualityPolicy = Copy(qualityPolicy);
        valuationProposal = Copy(value.Proposal); valuationDecision = Copy(value.Decision); valuationAllocation = Copy(value.Allocation);
        invoice = Copy(value.Invoice); match = Copy(value.Match);
        var physical = balance is null ? InventoryHeldReceiptWriter.After(receipt) : InventoryBson.ReadBalance(balance);
        BalanceId = physical.Id; PoolId = physical.PhysicalPoolId; Key = physical.Balance.Key;
        AcceptedPackages = quality.Allocation["packages"].AsInt32; ValuedPackages = value.Allocation["packages"].AsInt32;
        CapitalizedValue = Money.Parse(value.Allocation["capitalizedAmount"].AsString);
        CleanValuationJoin = !value.Proposal.Contains("quality");
        snapshot = new BsonDocument { { "receiptId", selected.ReceiptId }, { "receiptHash", receipt["snapshotHash"] },
            { "lotId", lot["_id"] }, { "lotHash", CatalogRepository.Digest(lot) }, { "balanceId", physical.Id }, { "poolId", physical.PhysicalPoolId },
            { "stockKey", new BsonDocument { { "location", StockSourceSetupBson.Location(physical.Balance.Key.Location) }, { "skuId", physical.Balance.Key.SkuId.Value },
                { "definitionId", physical.Balance.Key.DefinitionId.Value }, { "lotId", physical.Balance.Key.LotId.Value } } }, { "qualityAllocationId", selected.QualityAllocationId },
            { "qualityAllocationHash", qualityAllocation["snapshotHash"] }, { "quality", qualitySnapshot.DeepClone() },
            { "valuationAllocationId", selected.ValuationAllocationId }, { "valuationAllocationHash", valuationAllocation["snapshotHash"] },
            { "valuationProposalId", valuationProposal["_id"] }, { "valuationProposalHash", valuationProposal["snapshotHash"] },
            { "valuationDecisionId", valuationDecision["_id"] }, { "valuationDecisionHash", valuationDecision["snapshotHash"] },
            { "journalId", valuationAllocation["journalId"] }, { "acceptedPackages", AcceptedPackages }, { "valuedPackages", ValuedPackages },
            { "capitalizedValue", CapitalizedValue.ToString() }, { "qualityJoin", CleanValuationJoin ? "explicit-clean-release-join" : "valuation-quality-bound" } };
        seal = CatalogRepository.Digest(PrivateFacts());
    }
    internal StockReleaseSelection Selection { get; }
    internal DateTimeOffset ObservedAt { get; }
    internal long BalanceId { get; }
    internal long PoolId { get; }
    internal StockBalanceKey Key { get; }
    internal long AcceptedPackages { get; }
    internal long ValuedPackages { get; }
    internal Money CapitalizedValue { get; }
    internal bool CleanValuationJoin { get; }
    internal bool IsCurrent => balance is not null && pool is not null;
    internal BsonDocument Receipt => Copy(receipt);
    internal BsonDocument Lot => Copy(lot);
    internal BsonDocument Pool => pool is null ? throw Unavailable() : Copy(pool);
    internal BsonDocument Balance => balance is null ? throw Unavailable() : Copy(balance);
    internal BsonDocument QualityProposal => Copy(qualityProposal);
    internal BsonDocument QualityDecision => Copy(qualityDecision);
    internal BsonDocument QualityAllocation => Copy(qualityAllocation);
    internal BsonDocument QualitySnapshot => Copy(qualitySnapshot);
    internal BsonDocument QualityPolicy => Copy(qualityPolicy);
    internal BsonDocument ValuationProposal => Copy(valuationProposal);
    internal BsonDocument ValuationDecision => Copy(valuationDecision);
    internal BsonDocument ValuationAllocation => Copy(valuationAllocation);
    internal BsonDocument Invoice => Copy(invoice);
    internal BsonDocument Match => Copy(match);
    internal BsonDocument Snapshot => Copy(snapshot);
    internal HeldReceiptOriginProof? CurrentOrigin(IMongoDatabase current, IClientSessionHandle tx) {
        Require(current, tx); return currentOrigin;
    }
    internal ProcurementStockValuationService.ReleaseOriginReader? CurrentOriginReader(IMongoDatabase current,
        IClientSessionHandle tx, HeldReceiptOriginProof first) {
        Require(current, tx); first.Require(current, tx);
        if (first.Receipt != receipt) throw Unavailable();
        return currentReader?.ForOrigin(current, tx, first);
    }
    internal void Require(IMongoDatabase current, IClientSessionHandle tx) {
        new InventoryRepository(current).RequireSession(tx);
        if (!ReferenceEquals(current.Client, database.Client) || !current.DatabaseNamespace.Equals(database.DatabaseNamespace) ||
            !ReferenceEquals(session, tx) || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != transactionNumber ||
            CatalogRepository.Digest(PrivateFacts()) != seal) throw Unavailable();
        currentOrigin?.Require(current, tx);
    }
    private BsonDocument PrivateFacts() => new() { { "receipt", receipt }, { "lot", lot }, { "pool", (BsonValue?)pool ?? BsonNull.Value }, { "balance", (BsonValue?)balance ?? BsonNull.Value },
        { "qualityProposal", qualityProposal }, { "qualityDecision", qualityDecision }, { "qualityAllocation", qualityAllocation },
        { "quality", qualitySnapshot }, { "qualityPolicy", qualityPolicy }, { "valuationProposal", valuationProposal },
        { "valuationDecision", valuationDecision }, { "valuationAllocation", valuationAllocation }, { "invoice", invoice }, { "match", match }, { "snapshot", snapshot } };
    private static BsonDocument Copy(BsonDocument row) => row.DeepClone().AsBsonDocument;
    internal static PersistenceConflictException Unavailable() => new("stock_release_source_unavailable",
        "The selected held receipt, approved quality subset and posted acquisition value cannot be authenticated");
    internal static async Task<StockReleaseSourceProof> ReadOriginAsync(IMongoDatabase database, MongoUnitOfWork unit, IClock clock,
        CatalogAuthoringService catalog, PurchaseOrderService orders, StockSourceSetupService sources, ProcurementStockValuationService valuations,
        IClientSessionHandle tx, StockReleaseSelection selected, HeldReceiptOriginProof origin, CancellationToken ct,
        ProcurementStockValuationService.ReleaseOriginReader? retainedReader = null) {
        new InventoryRepository(database).RequireSession(tx); selected.Validate(); origin.Require(database, tx);
        if (origin.ReceiptId != selected.ReceiptId) throw Unavailable();
        var reader = retainedReader ?? valuations.CreateReleaseOriginReader(tx, origin);
        var pair = await reader.ReadAsync(valuations, database, tx, origin, selected.QualityAllocationId, selected.ValuationAllocationId, ct);
        var quality = pair.Quality; var value = pair.Value; RequireSelected(selected, quality, value);
        if (quality.Receipt != origin.Receipt) throw Unavailable();
        var lot = await database.GetCollection<BsonDocument>("lots").Find(tx, new BsonDocument("_id", quality.Receipt["lotId"])).SingleOrDefaultAsync(ct) ?? throw Unavailable();
        if (lot != InventoryHeldReceiptWriter.Lot(quality.Receipt)) throw Unavailable();
        // This proves only immutable acquisition/quality ancestry. It deliberately has
        // no current balance/pool projection and cannot be used as new release authority.
        var proof = new StockReleaseSourceProof(database, tx, selected, quality, value, lot, null, null, quality.Proposal["policy"].AsBsonDocument, clock.UtcNow);
        proof.Require(database, tx); return proof;
    }
    internal static async Task<StockReleaseSourceProof> ReadCurrentAsync(IMongoDatabase database, MongoUnitOfWork unit, IClock clock,
        CatalogAuthoringService catalog, PurchaseOrderService orders, StockSourceSetupService sources, ProcurementStockValuationService valuations,
        IClientSessionHandle tx, StockReleaseSelection selected, CancellationToken ct)
    {
        try {
            var repository = new InventoryRepository(database); repository.RequireSession(tx); var number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber;
            if (selected is null || clock.UtcNow == default || clock.UtcNow.Offset != TimeSpan.Zero) throw Unavailable(); selected.Validate();
            var at = clock.UtcNow;
            // These are genuine maintained owner reads, not descriptive BSON supplied by the caller.
            // Their shared graph is created inside one actual valuation owner so
            // exact PO-owner reference binding is preserved across both readers.
            var pair = await valuations.ReadCurrentReleaseSourcesAsync(database, tx, selected.QualityAllocationId, selected.ValuationAllocationId, ct);
            var quality = pair.Quality; var value = pair.Value;
            RequireSelected(selected, quality, value);
            var input = Q.Input(quality.Proposal["input"]);
            // Historical approval can now have expired; new release preparation applies the
            // current business day and current independently approved quality policy.
            ProcurementQualityAcceptanceRecords.LotFacts(input, quality.Receipt, at);
            var policy = await new ProcurementQualityAcceptanceRecords(database, unit, clock, catalog, orders, sources)
                .PolicyAsync(tx, input, at, true, ct);
            if (Q.PolicySnapshot(policy) != quality.Proposal["policy"]) throw Unavailable();
            var movement = await new HeldReceiptMovementHistory(database, unit, clock).RequireAsync(tx, quality.Receipt, ct, retainedOrigin: pair.Origin);
            var lot = await database.GetCollection<BsonDocument>("lots").Find(tx, new BsonDocument("_id", quality.Receipt["lotId"])).SingleOrDefaultAsync(ct) ?? throw Unavailable();
            if (lot != InventoryHeldReceiptWriter.Lot(quality.Receipt)) throw Unavailable();
            var pool = await database.GetCollection<BsonDocument>("inventoryPools").Find(tx, new BsonDocument("_id", quality.Receipt["poolId"])).SingleOrDefaultAsync(ct) ?? throw Unavailable();
            InventoryGraph.RequirePool(pool);
            var balance = InventoryBson.Balance(movement.Current);
            if (movement.Current.Id != quality.Receipt["balanceId"].AsInt64 || movement.Current.PhysicalPoolId != quality.Receipt["poolId"].AsInt64 ||
                movement.Current.Balance.Key != InventoryHeldReceiptWriter.After(quality.Receipt).Balance.Key ||
                movement.Current.Balance.Held <= 0 || pool["location"] != balance["location"]) throw Unavailable();
            var proof = new StockReleaseSourceProof(database, tx, selected, quality, value, lot, pool, balance, policy, at, pair.Origin, pair.Reader);
            proof.Require(database, tx); repository.RequireSession(tx);
            if (tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number || clock.UtcNow < at) throw Unavailable(); ct.ThrowIfCancellationRequested(); return proof;
        } catch { await InventoryRepository.AbortAsync(tx); throw; }
    }
    private static void RequireSelected(StockReleaseSelection selected, ProcurementApprovedQualitySource quality, ProcurementPayableValuationSource value) {
        if (quality.Receipt["_id"] != selected.ReceiptId || value.Receipt != quality.Receipt ||
            quality.Allocation["_id"] != selected.QualityAllocationId || value.Allocation["_id"] != selected.ValuationAllocationId ||
            quality.Allocation["receiptId"] != selected.ReceiptId || value.Allocation["receiptId"] != selected.ReceiptId ||
            quality.Proposal["lotId"] != quality.Receipt["lotId"] || quality.Proposal["balanceId"] != quality.Receipt["balanceId"] ||
            quality.Proposal["poolId"] != quality.Receipt["poolId"] || quality.Decision["decision"] != "approve" || value.Decision["decision"] != "approve" ||
            // Quality and acquisition value are separate finite entitlements. A clean
            // posted allocation may cover more packages than the explicitly selected
            // healthy subset; the release owner must cap/consume both, never infer equality.
            value.Allocation["packages"].AsInt32 <= 0 || quality.Allocation["packages"].AsInt32 <= 0 ||
            Money.Parse(value.Allocation["capitalizedAmount"].AsString).Value <= 0) throw Unavailable();
        var input = V.Input(value.Proposal["input"]);
        if (input.ReceiptId != selected.ReceiptId || input.MatchLineNumber > value.Match["lines"].AsBsonArray.Count) throw Unavailable();
        var line = value.Match["lines"][input.MatchLineNumber - 1];
        if (value.Proposal.TryGetValue("quality", out var valueQuality)) {
            if (valueQuality != quality.Snapshot || !value.Decision.TryGetValue("quality", out var decisionQuality) || decisionQuality != quality.Snapshot ||
                !line.AsBsonDocument.TryGetValue("quality", out var matchQuality) || matchQuality != quality.Snapshot ||
                line["qualityAllocationId"] != selected.QualityAllocationId) throw Unavailable();
        } else {
            // A completed clean acquisition remains valid. Its later release explicitly
            // joins a separately approved exact-receipt quality allocation; no journal is reposted.
            var observed = quality.Receipt["inspection"]["input"]["lines"][quality.Receipt["inspectionLineNumber"].AsInt32 - 1];
            if (value.Decision.Contains("quality") || line.AsBsonDocument.Contains("quality") || line.AsBsonDocument.Contains("qualityAllocationId") ||
                observed["damagedPackages"].AsInt32 != 0 || observed["expiredPackages"].AsInt32 != 0 || observed["openedPackages"].AsInt32 != 0 ||
                observed["declaredPackages"] != observed["receivedPackages"] || !observed["discrepancy"].IsBsonNull) throw Unavailable();
        }
    }
}
