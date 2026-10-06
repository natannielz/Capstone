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
    internal async Task<(ProcurementApprovedQualitySource Quality, ProcurementPayableValuationSource Value, HeldReceiptOriginProof? Origin)> ReadCurrentReleaseSourcesAsync(
        IMongoDatabase owner, IClientSessionHandle tx, long qualityAllocationId, long valuationAllocationId, CancellationToken ct) {
        if (!ReferenceEquals(owner.Client, database.Client) || owner.DatabaseNamespace != database.DatabaseNamespace ||
            !ReferenceEquals(tx.Client, database.Client) || !tx.IsInTransaction || qualityAllocationId <= 0 || valuationAllocationId <= 0) throw B.Broken();
        var graph = new ProcurementQualityProofGraph(tx);
        var quality = await new ProcurementQualityAcceptanceSourceReader(database, unit, clock, catalog, orders, sources, graph)
            .ReadAsync(tx, qualityAllocationId, ct);
        var reader = new ProcurementStockValuationRecords(database, unit, clock, catalog, orders, sources, graph);
        var value = await ReadApprovedSourceAsync(owner, tx, valuationAllocationId, reader, ct);
        graph.Require(tx); return (quality, value, graph.CurrentMovementOrigin(database, tx, quality.Receipt["_id"].AsInt64));
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
        private ReleaseOriginReader(ProcurementStockValuationService owner, IClientSessionHandle tx, HeldReceiptOriginProof origin) {
            this.owner = owner; transaction = tx; number = tx.WrappedCoreSession.CurrentTransaction.TransactionNumber; this.origin = origin;
            origin.Require(owner.database, tx); graph = new(tx, origin);
        }
        internal static ReleaseOriginReader Create(ProcurementStockValuationService owner, IClientSessionHandle tx, HeldReceiptOriginProof origin) => new(owner, tx, origin);
        internal void Require(ProcurementStockValuationService expectedOwner, IMongoDatabase database, IClientSessionHandle tx, HeldReceiptOriginProof expectedOrigin) {
            if (!ReferenceEquals(owner, expectedOwner) || !ReferenceEquals(tx, transaction) || !ReferenceEquals(origin, expectedOrigin) ||
                !ReferenceEquals(database.Client, owner.database.Client) || database.DatabaseNamespace != owner.database.DatabaseNamespace ||
                !tx.IsInTransaction || tx.WrappedCoreSession.CurrentTransaction.TransactionNumber != number) throw B.Broken();
            origin.Require(database, tx); graph.Require(tx);
        }
        internal async Task<(ProcurementApprovedQualitySource Quality, ProcurementPayableValuationSource Value)> ReadAsync(
            ProcurementStockValuationService expectedOwner, IMongoDatabase database, IClientSessionHandle tx, HeldReceiptOriginProof expectedOrigin,
            long qualityAllocationId, long valuationAllocationId, CancellationToken ct) {
            Require(expectedOwner, database, tx, expectedOrigin); await origin.RequireAsync(database, tx, ct);
            var quality = await new ProcurementQualityAcceptanceSourceReader(owner.database, owner.unit, owner.clock, owner.catalog, owner.orders, owner.sources, graph)
                .ReadAsync(tx, qualityAllocationId, ct);
            var records = new ProcurementStockValuationRecords(owner.database, owner.unit, owner.clock, owner.catalog, owner.orders, owner.sources, graph);
            var value = await owner.ReadApprovedSourceAsync(database, tx, valuationAllocationId, records, ct);
            if (quality.Receipt != origin.Receipt || value.Receipt != origin.Receipt) throw B.Broken();
            Require(expectedOwner, database, tx, expectedOrigin); return (quality, value);
        }
    }
    private async Task<ProcurementPayableValuationSource> ReadApprovedSourceAsync(IMongoDatabase owner,
        IClientSessionHandle tx, long allocationId, ProcurementStockValuationRecords reader, CancellationToken ct)
    {
        if (!ReferenceEquals(owner.Client, database.Client) || owner.DatabaseNamespace != database.DatabaseNamespace ||
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
