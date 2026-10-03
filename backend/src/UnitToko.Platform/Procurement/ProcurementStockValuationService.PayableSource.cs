using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Authorization;
using B = UnitToko.Platform.Procurement.ProcurementStockValuationBson;

namespace UnitToko.Platform.Procurement;

internal sealed record ProcurementPayableValuationSource(BsonDocument Proposal, BsonDocument Decision,
    BsonDocument Allocation, BsonDocument Receipt, BsonDocument Invoice, BsonDocument Match);

public sealed partial class ProcurementStockValuationService
{
    internal Task RequirePayableSourceStorageAsync(CancellationToken ct) => StorageAsync(ct);
    internal Task<BsonDocument> PayableNamedTargetsAsync(UnitToko.Modules.Identity.Contracts.ActorContext actor, CancellationToken ct) =>
        receiving.TargetsAsync(actor, ct);
    internal async Task<ProcurementPayableValuationSource> ReadPayableSourceAsync(IMongoDatabase owner,
        IClientSessionHandle tx, long allocationId, CancellationToken ct)
    {
        if (!ReferenceEquals(owner.Client, database.Client) || owner.DatabaseNamespace != database.DatabaseNamespace ||
            !tx.IsInTransaction || allocationId <= 0) throw B.Broken();
        var allocation = await Rows(B.Allocations).Find(tx, new BsonDocument("_id", allocationId)).SingleOrDefaultAsync(ct)
            ?? throw B.Broken();
        var verified = await records.ProposalWithSourcesAsync(tx, allocation["proposalId"].AsInt64, ct);
        var proposal = verified.Proposal;
        var decision = await records.DecisionAsync(tx, proposal, ct) ?? throw B.Broken();
        if (decision["decision"] != "approve" || decision["allocationId"] != allocationId ||
            allocation["decisionId"] != decision["_id"] || allocation["snapshotHash"] != B.Hash(allocation)) throw B.Broken();
        var sources = verified.Sources;
        return new(proposal, decision, allocation, sources.Receipt, sources.Invoice, sources.Match);
    }
}
