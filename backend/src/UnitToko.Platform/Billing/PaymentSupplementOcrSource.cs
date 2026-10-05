using MongoDB.Bson;
using MongoDB.Driver;

namespace UnitToko.Platform.Billing;

internal sealed class PaymentSupplementOcrSource(IMongoDatabase database, IPaymentSupplementEvidenceHistory evidence) : IPaymentSupplementOcrSource
{
    public IMongoDatabase Database => database;
    public async Task<PaymentSupplementOcrSourceSnapshot> RequireAsync(IClientSessionHandle tx, BsonDocument job, CancellationToken ct) {
        try {
        if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || job.GetValue("kind", "") != PaymentSupplementBson.JobKind ||
            !job.TryGetValue("payload", out var payload) || !payload.IsBsonDocument || !payload.AsBsonDocument.TryGetValue("supplementId", out var id) || !id.IsInt64 || id.AsInt64 <= 0)
            throw PaymentSupplementRecords.Broken();
        var history = await new PaymentSupplementRecords(database, evidence).HistoryAsync(tx, id.AsInt64, ct);
        var attachment = history.Attachment ?? throw PaymentSupplementRecords.Broken();
        var actual = await database.GetCollection<BsonDocument>("outboxEvents").Find(tx, new BsonDocument("_id", job["_id"])).SingleOrDefaultAsync(ct);
        if (actual is null || !actual.Equals(job) || job["sourceKey"] != PaymentSupplementBson.JobKey(id.AsInt64) || !payload.Equals(PaymentSupplementBson.JobPayload(attachment))) throw PaymentSupplementRecords.Broken();
        return new(id.AsInt64, 2, PaymentSupplementBson.Primary(attachment["primary"].AsBsonDocument), attachment["primarySubmissionHash"].AsString,
            attachment["reservationId"].AsInt64, attachment["pinId"].AsInt64, attachment["uploaderId"].AsInt64,
            PaymentSubmissionBson.Reference(attachment["evidence"].AsBsonDocument), attachment["snapshotHash"].AsString,
            PaymentSupplementBson.Publication(attachment["original"].AsBsonDocument), attachment["scope"].AsBsonDocument);
        } catch {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { if (tx.IsInTransaction) await tx.AbortTransactionAsync(cleanup.Token); if (tx.IsInTransaction) tx.Dispose(); } catch { try { tx.Dispose(); } catch { } }
            throw;
        }
    }
}
