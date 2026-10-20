using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Fulfillment;

public sealed partial class ShipmentReceiptService
{
    // Migration-only historical replay after explicit legacy bindings have been staged.
    // No current actor, assignment fallback or newly granted authority is constructed.
    internal async Task ValidateCourierMigrationHistoryAsync(IClientSessionHandle tx, ShipmentDocumentSource source, CancellationToken ct) {
        await DeliveryAttempts.HistoryAsync(tx, source, ct);
        foreach (var challenge in await Rows(ShipmentReceiptRecords.Challenges).Find(tx, F.Eq("shipmentId", source.Snapshot["shipmentId"])).ToListAsync(ct))
            await VerifyChallengeAsync(tx, challenge, source, ct);
        var receipt = await records.FindReceiptAsync(tx, source.Snapshot["shipmentId"].AsInt64, ct);
        if (receipt is not null) await VerifyReceiptAsync(tx, receipt, source, ct);
    }
}
