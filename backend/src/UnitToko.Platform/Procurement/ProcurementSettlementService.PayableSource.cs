using MongoDB.Bson;
using MongoDB.Driver;
using B = UnitToko.Platform.Procurement.ProcurementSettlementBson;

namespace UnitToko.Platform.Procurement;

internal sealed record ProcurementPayableAdvanceSource(BsonDocument Settlement, BsonDocument Disbursement);

public sealed partial class ProcurementSettlementService
{
    internal Task RequirePayableSourceStorageAsync(CancellationToken ct) => StorageAsync(ct);
    internal async Task<ProcurementPayableAdvanceSource> ReadPayableSourceAsync(IMongoDatabase owner,
        IClientSessionHandle tx, long settlementId, CancellationToken ct)
    {
        if (!ReferenceEquals(owner.Client, database.Client) || owner.DatabaseNamespace != database.DatabaseNamespace ||
            !tx.IsInTransaction || settlementId <= 0) throw B.Broken();
        var verified = await records.ReadWithSourceAsync(tx, settlementId, ct);
        var settlement = verified.Settlement;
        var disbursement = verified.Disbursement;
        if (settlement["disbursementHash"] != disbursement["snapshotHash"] || settlement["amount"] != disbursement["input"]["amount"])
            throw B.Broken();
        return new(settlement, disbursement);
    }
}
