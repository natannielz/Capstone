using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using UnitToko.Platform.Inventory;
using B = UnitToko.Platform.Procurement.ProcurementStockValuationBson;

namespace UnitToko.Platform.Procurement;

internal sealed record ProcurementPayableValuationSource(BsonDocument Proposal, BsonDocument Decision,
    BsonDocument Allocation, BsonDocument Receipt, BsonDocument Invoice, BsonDocument Match);

public sealed partial class ProcurementStockValuationService
{
    internal Task RequirePayableSourceStorageAsync(CancellationToken ct) => StorageAsync(ct);
    internal Task<BsonDocument> PayableNamedTargetsAsync(UnitToko.Modules.Identity.Contracts.ActorContext actor, CancellationToken ct) =>
        receiving.TargetsAsync(actor, ct);
    internal Task<ProcurementPayableValuationSource> ReadPayableSourceAsync(IMongoDatabase owner,
        IClientSessionHandle tx, long allocationId, CancellationToken ct) => ReadApprovedSourceAsync(owner, tx, allocationId, records, ct);
    // Graphs retain exact owning PO proof instances. Both reads must originate
    // under this actual service context, even when caller service factories use
    // different equivalent instances for their other dependencies.
    internal async Task<(ProcurementApprovedQualitySource Quality, ProcurementPayableValuationSource Value, HeldReceiptOriginProof? Origin, ReleaseOriginReader? Reader)> ReadCurrentReleaseSourcesAsync(
        IMongoDatabase owner, IClientSessionHandle tx, long qualityAllocationId, long valuationAllocationId, CancellationToken ct) {
        if (!ReferenceEquals(owner.Client, database.Client) || !owner.DatabaseNamespace.Equals(database.DatabaseNamespace) ||
            !ReferenceEquals(tx.Client, database.Client) || !tx.IsInTransaction || qualityAllocationId <= 0 || valuationAllocationId <= 0) throw B.Broken();
        var graph = new ProcurementQualityProofGraph(tx);
        var quality = await new ProcurementQualityAcceptanceSourceReader(database, unit, clock, catalog, orders, sources, graph)
            .ReadAsync(tx, qualityAllocationId, ct);
        var reader = new ProcurementStockValuationRecords(database, unit, clock, catalog, orders, sources, graph);
        var value = await ReadApprovedSourceAsync(owner, tx, valuationAllocationId, reader, ct);
        graph.Require(tx);
        var origin = graph.CurrentMovementOrigin(database, tx, quality.Receipt["_id"].AsInt64);
        var retained = origin is null ? null : ReleaseOriginReader.CreateFromCurrent(this, tx, origin, graph);
        return (quality, value, origin, retained);
    }
    internal async Task<ProcurementPayableValuationSource> ReadReleaseSourceAsync(IMongoDatabase owner,
        IClientSessionHandle tx, long allocationId, HeldReceiptOriginProof origin, CancellationToken ct)
    {
        await origin.RequireAsync(owner, tx, ct);
        var graph = new ProcurementQualityProofGraph(tx, origin);
        var historical = new ProcurementStockValuationRecords(database, unit, clock, catalog, orders, sources, graph);
        var result = await ReadApprovedSourceAsync(owner, tx, allocationId, historical, ct);
        if (result.Receipt != origin.Receipt) throw B.Broken();
        return result;
    }
    internal ReleaseOriginReader CreateReleaseOriginReader(IClientSessionHandle tx, HeldReceiptOriginProof origin) => ReleaseOriginReader.Create(this, tx, origin);
    // Retains only the existing ancestor graph, never quality, claim, invoice,
    // valuation decisions or current quantity. Each read runs their genuine owners.
    internal sealed class ReleaseOriginReader {
        private readonly ProcurementStockValuationService owner;
        private readonly IClientSessionHandle transaction;
        private readonly long number;
        private readonly HeldReceiptOriginProof origin;
        private readonly ProcurementQualityProofGraph graph;
        private readonly IClock clock;
        private readonly DateTimeOffset capturedAt;
        private ReleaseOriginReader(ProcurementStockValuationService owner, IClientSessionHandle tx, HeldReceiptOriginProof origin,
            ProcurementQualityProofGraph? ancestors = null) {
            this.owner = owner; transaction = tx; number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; this.origin = origin;
            clock = owner.clock; capturedAt = clock.UtcNow;
            if (capturedAt == default || capturedAt.Offset != TimeSpan.Zero) throw B.Broken();
            origin.Require(owner.database, tx);
            graph = ancestors is null ? new(tx, origin) : ancestors.CopyOriginAncestors(owner.database, clock, tx, origin);
        }
        internal static ReleaseOriginReader Create(ProcurementStockValuationService owner, IClientSessionHandle tx, HeldReceiptOriginProof origin) => new(owner, tx, origin);
        internal static ReleaseOriginReader CreateFromCurrent(ProcurementStockValuationService owner, IClientSessionHandle tx,
            HeldReceiptOriginProof origin, ProcurementQualityProofGraph current) => new(owner, tx, origin, current);
        internal ReleaseOriginReader ForOrigin(IMongoDatabase database, IClientSessionHandle tx, HeldReceiptOriginProof first) {
            Require(owner, database, tx, origin); first.Require(database, tx);
            if (first.Receipt != origin.Receipt) throw B.Broken();
            // Keep the command's first exact origin. Copy only completed semantic
            // ancestors; the donor reader and its active/mutable state stay private.
            var result = new ReleaseOriginReader(owner, tx, first, graph);
            Require(owner, database, tx, origin); result.Require(owner, database, tx, first);
            return result;
        }
        internal void Require(ProcurementStockValuationService expectedOwner, IMongoDatabase database, IClientSessionHandle tx, HeldReceiptOriginProof expectedOrigin) {
            if (!ReferenceEquals(owner, expectedOwner) || !ReferenceEquals(tx, transaction) || !ReferenceEquals(origin, expectedOrigin) ||
                !ReferenceEquals(database.Client, owner.database.Client) || !database.DatabaseNamespace.Equals(owner.database.DatabaseNamespace) ||
                !ReferenceEquals(clock, expectedOwner.clock) || clock.UtcNow < capturedAt || clock.UtcNow.Offset != TimeSpan.Zero ||
                !tx.IsInTransaction || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw B.Broken();
            origin.Require(database, tx); graph.Require(tx);
        }
        internal async Task<(ProcurementApprovedQualitySource Quality, ProcurementPayableValuationSource Value)> ReadAsync(
            ProcurementStockValuationService expectedOwner, IMongoDatabase database, IClientSessionHandle tx, HeldReceiptOriginProof expectedOrigin,
            long qualityAllocationId, long valuationAllocationId, CancellationToken ct) {
            Require(expectedOwner, database, tx, expectedOrigin); await origin.RequireAsync(database, tx, ct);
            if (completed.TryGetValue((qualityAllocationId, valuationAllocationId), out var verified)) {
                // Same reader, origin and transaction attempt: the exact quality and valuation
                // allocation, proposal, decision and receipt-head rows keep this reader's full
                // owner proof; its snapshot observes no other writer and no row may differ.
                if (await SourceRowsAsync(tx, verified.Quality, verified.Value, ct) != verified.Rows) throw B.Broken();
                await origin.RequireAsync(database, tx, ct);
                Require(expectedOwner, database, tx, expectedOrigin); ct.ThrowIfCancellationRequested(); return (verified.Quality, verified.Value);
            }
            var token = graph.BeginOriginRead(this, owner, database, owner.clock, tx, origin);
            try {
                var quality = await new ProcurementQualityAcceptanceSourceReader(owner.database, owner.unit, owner.clock, owner.catalog, owner.orders, owner.sources, graph)
                    .ReadAsync(tx, qualityAllocationId, ct);
                var records = new ProcurementStockValuationRecords(owner.database, owner.unit, owner.clock, owner.catalog, owner.orders, owner.sources, graph);
                var value = await owner.ReadApprovedSourceAsync(database, tx, valuationAllocationId, records, ct);
                if (quality.Receipt != origin.Receipt || value.Receipt != origin.Receipt) throw B.Broken();
                await origin.RequireAsync(database, tx, ct);
                Require(expectedOwner, database, tx, expectedOrigin); ct.ThrowIfCancellationRequested();
                if (completed.Count < LotValueStore.MaximumTransitions)
                    completed[(qualityAllocationId, valuationAllocationId)] = (quality, value, await SourceRowsAsync(tx, quality, value, ct));
                return (quality, value);
            } finally { graph.EndOriginRead(token); }
        }
        private readonly Dictionary<(long Quality, long Value), (ProcurementApprovedQualitySource Quality, ProcurementPayableValuationSource Value, BsonArray Rows)> completed = [];
        private async Task<BsonArray> SourceRowsAsync(IClientSessionHandle tx, ProcurementApprovedQualitySource quality,
            ProcurementPayableValuationSource value, CancellationToken ct) {
            var rows = new BsonArray();
            foreach (var (name, id) in new (string Name, BsonValue Id)[] {
                (ProcurementQualityAcceptanceBson.Allocations, quality.Allocation["_id"]), (ProcurementQualityAcceptanceBson.Proposals, quality.Proposal["_id"]),
                (ProcurementQualityAcceptanceBson.Decisions, quality.Decision["_id"]), (ProcurementQualityAcceptanceBson.ReceiptHeads, origin.ReceiptId),
                (B.Allocations, value.Allocation["_id"]), (B.Proposals, value.Proposal["_id"]), (B.Decisions, value.Decision["_id"]), (B.ReceiptHeads, origin.ReceiptId) })
                rows.Add((BsonValue?)await owner.Rows(name).Find(tx, new BsonDocument("_id", id)).SingleOrDefaultAsync(ct) ?? BsonNull.Value);
            return rows;
        }
    }
    private async Task<ProcurementPayableValuationSource> ReadApprovedSourceAsync(IMongoDatabase owner,
        IClientSessionHandle tx, long allocationId, ProcurementStockValuationRecords reader, CancellationToken ct)
    {
        if (!ReferenceEquals(owner.Client, database.Client) || !owner.DatabaseNamespace.Equals(database.DatabaseNamespace) ||
            !tx.IsInTransaction || allocationId <= 0) throw B.Broken();
        var allocation = await Rows(B.Allocations).Find(tx, new BsonDocument("_id", allocationId)).SingleOrDefaultAsync(ct)
            ?? throw B.Broken();
        var verified = await reader.ProposalWithSourcesAsync(tx, allocation["proposalId"].AsInt64, ct);
        var proposal = verified.Proposal;
        var decision = await reader.DecisionAsync(tx, proposal, ct) ?? throw B.Broken();
        if (decision["decision"] != "approve" || decision["allocationId"] != allocationId ||
            allocation["decisionId"] != decision["_id"] || allocation["snapshotHash"] != B.Hash(allocation)) throw B.Broken();
        var sources = verified.Sources;
        return new(proposal, decision, allocation, sources.Receipt, sources.Invoice, sources.Match);
    }
}
