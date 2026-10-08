using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Ordering;

public sealed partial class OrderSubmissionService
{
    // Called only after the anomaly owner establishes both source-read and review scope.
    internal async Task<BsonDocument> AnomalyOrderAsync(IClientSessionHandle tx, long orderId, CancellationToken ct) =>
        await GraphAsync(tx, null, orderId, ct, false);
    internal Task FenceAnomalyOrderAsync(IClientSessionHandle tx, BsonDocument order, CancellationToken ct) => TouchAsync(tx, order, ct);
}
