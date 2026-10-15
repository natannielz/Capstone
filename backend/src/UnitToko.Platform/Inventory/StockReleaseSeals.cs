using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Platform.Integrity;
using UnitToko.Platform.Mongo;
using UnitToko.Platform.Procurement;
using B = UnitToko.Platform.Inventory.StockReleaseBson;

namespace UnitToko.Platform.Inventory;

/// <summary>Inventory-owned seal over a release proposal's historical source proof: the exact held receipt, lot,
/// approved quality allocation/proposal/decision, approved valuation allocation/proposal/decision, supplier invoice
/// and match rows the full procurement proof authenticated. Written once after that full proof inside an owning
/// unit-of-work operation; later reads rebuild the same source proof from the verified rows instead of replaying
/// procurement ancestry. Every row is immutable apart from its freshness fence, which the row hash excludes.</summary>
internal static class StockReleaseSeals
{
    private const string Kind = InventorySealSchemas.ReleaseKind;
    internal static readonly IReadOnlyList<string> SealedCollections = [ProcurementStockReceiptBson.Receipts, "lots",
        ProcurementQualityAcceptanceBson.Allocations, ProcurementQualityAcceptanceBson.Proposals, ProcurementQualityAcceptanceBson.Decisions,
        ProcurementStockValuationBson.Allocations, ProcurementStockValuationBson.Proposals, ProcurementStockValuationBson.Decisions,
        SupplierDocumentBson.Submissions, SupplierDocumentBson.Matches];
    // Full procurement source proofs run (tests assert that a verified seal replaces them).
    internal static long FullProofs;
    private static IMongoCollection<BsonDocument> Seals(IMongoDatabase database) => database.GetCollection<BsonDocument>(InventorySealSchemas.Releases);

    /// <summary>Null when no key ring or no seal exists (the caller runs the full proof). Otherwise one verified row per
    /// sealed collection, keyed by collection. A present seal that fails or has any other shape refuses.</summary>
    internal static async Task<IReadOnlyDictionary<string, BsonDocument>?> VerifyAsync(IMongoDatabase database, IClientSessionHandle tx,
        BsonDocument proposal, CancellationToken ct)
    {
        var ring = ProvenanceSealRing.Current;
        if (ring is null) return null;
        var seal = await Seals(database).Find(tx, new BsonDocument("_id", proposal["_id"])).SingleOrDefaultAsync(ct);
        if (seal is null) return null;
        IReadOnlyList<BsonDocument> verified;
        try { verified = await new SealVerifier(database, ring).VerifyAsync(tx, seal, Kind, proposal["_id"].AsInt64, proposal, ct); }
        catch (SealUnavailableException) { throw B.Broken(); }
        Exception Shape() { SealIntegrityAlerts.Raise(Kind, proposal["_id"].AsInt64, "shape"); return B.Broken(); }
        var rows = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
        foreach (var scope in verified) {
            var name = scope["collection"].AsString; var filter = scope["filter"].AsBsonDocument;
            if (!scope.Contains("rows") || scope["rows"].AsBsonArray.Count != 1 || filter.ElementCount != 1 || !filter.Contains("_id") ||
                !SealedCollections.Contains(name) || !rows.TryAdd(name, scope["rows"][0].AsBsonDocument) || rows[name]["_id"] != filter["_id"]) throw Shape();
        }
        if (seal["dependencies"].AsBsonArray.Count != 0 || rows.Count != SealedCollections.Count) throw Shape();
        return rows;
    }

    /// <summary>Seals after the full source proof succeeded. Only an owning operation seals; caller-supplied sessions never write.</summary>
    internal static async Task SealAsync(IMongoDatabase database, IClock clock, IClientSessionHandle tx, BsonDocument proposal,
        StockReleaseSourceProof proof, CancellationToken ct)
    {
        var ring = ProvenanceSealRing.Current;
        if (ring is null || !MongoUnitOfWork.OwnsOperation(tx) || await Seals(database).Find(tx, new BsonDocument("_id", proposal["_id"])).AnyAsync(ct)) return;
        BsonDocument[] rows = [proof.Receipt, proof.Lot, proof.QualityAllocation, proof.QualityProposal, proof.QualityDecision,
            proof.ValuationAllocation, proof.ValuationProposal, proof.ValuationDecision, proof.Invoice, proof.Match];
        var membership = SealVerifier.Membership(SealedCollections.Zip(rows).Select(pair =>
            (pair.First, new BsonDocument("_id", pair.Second["_id"]), (IReadOnlyList<BsonDocument>)[pair.Second])));
        if (!ProvenanceSeal.Sealable(membership)) return;
        await Seals(database).InsertOneAsync(tx, ProvenanceSeal.Create(ring, Kind, proposal["_id"].AsInt64, ProvenanceSeal.RowHash(proposal),
            membership, [], clock.UtcNow), cancellationToken: ct);
    }
}
