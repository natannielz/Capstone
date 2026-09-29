using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Fulfillment;

namespace UnitToko.Platform.Fulfillment;

public sealed partial class ShipmentReceiptService
{
    internal IMongoDatabase SourceDatabase => database;
    private DeliveryAttemptRecords DeliveryAttempts => new(database);
    internal async Task<bool> HasRecordedReceiptAsync(IClientSessionHandle tx, ShipmentDocumentSource source, CancellationToken ct) {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client)) throw ShipmentReceiptRecords.Invalid();
        var row = await records.FindReceiptAsync(tx, source.Snapshot["shipmentId"].AsInt64, ct);
        if (row is null) return false;
        await VerifyReceiptAsync(tx, row, source, ct); return true;
    }
    internal async Task<long[]> OpenChallengesForFailedVisitAsync(IClientSessionHandle tx, ShipmentDocumentSource source, DateTimeOffset recordedAt, CancellationToken ct) {
        if (await HasRecordedReceiptAsync(tx, source, ct)) throw ShipmentReceiptRecords.Invalid();
        var candidates = await Rows(ShipmentReceiptRecords.Challenges).Find(tx, F.Eq("shipmentId", source.Snapshot["shipmentId"]) & F.Eq("consumedReceiptId", BsonNull.Value) & F.Gt("expiresAt", recordedAt.UtcDateTime))
            .Limit(DeliveryReceiptPolicy.MaximumChallengesPerHour + 1).ToListAsync(ct);
        if (candidates.Count > DeliveryReceiptPolicy.MaximumChallengesPerHour) throw ShipmentReceiptRecords.Invalid();
        var result = new List<long>();
        foreach (var row in candidates) {
            await VerifyChallengeAsync(tx, row, source, ct);
            if (ShipmentReceiptRecords.Time(row["recordedAt"]) > recordedAt) throw ShipmentReceiptRecords.Invalid();
            if (!await Rows(DeliveryAttemptRecords.Revocations).Find(tx, F.Eq("_id", row["_id"])).AnyAsync(ct)) result.Add(row["_id"].AsInt64);
        }
        return result.Order().ToArray();
    }
}
