using MongoDB.Bson;
using MongoDB.Driver;
using UnitToko.Modules.Organizations;
using UnitToko.Platform.DocumentProcessing;

namespace UnitToko.Platform.Billing;

/// <summary>Read only. A worker gets the immutable submitted source, never buyer identity or financial write authority.</summary>
internal sealed class PaymentOcrSource(IMongoDatabase database) : IPaymentOcrSource
{
    public IMongoDatabase Database => database;
    public async Task<PaymentOcrSourceSnapshot> RequireAsync(IClientSessionHandle tx, BsonDocument job, CancellationToken ct) {
        try {
            if (!tx.IsInTransaction || !ReferenceEquals(tx.Client, database.Client) || job.GetValue("kind", "") != "payment.ocr.requested" ||
                !job.TryGetValue("payload", out var payload) || !payload.IsBsonDocument || !payload.AsBsonDocument.TryGetValue("submissionId", out var id) || !id.IsInt64 || id.AsInt64 <= 0)
                throw PaymentDraftBson.Invalid();
            var submission = await new PaymentSubmissionRecords(database).ReadAsync(tx, id.AsInt64, ct);
            if (job.GetValue("sourceKey", "") != PaymentSubmissionBson.OcrKey(submission) || !InvoiceRecords.Equal(payload, PaymentSubmissionBson.OcrPayload(submission))) throw PaymentDraftBson.Invalid();
            var records = new PaymentDraftRecords(database); var head = await records.HeadAsync(tx, submission["recordedBy"].AsInt64, submission["declarationId"].AsInt64, ct);
            if (head["state"] != "submitted" || head["submissionId"] != id || head["reviewRevision"] != submission["reviewRevision"] || head["fence"].AsInt64 >= long.MaxValue) throw PaymentDraftRecords.Changed();
            // Submitted declarations are immutable in this workflow. Processing fences belong to Documents/outbox;
            // the OCR service identity must not acquire update rights on Billing or Accounting collections.
            var party = head["party"];
            return new(id.AsInt64, head["_id"].AsInt64, head["versionId"].AsInt64, head["revision"].AsInt64, submission["reviewRevision"].AsInt64,
                submission["reservationId"].AsInt64, submission["pinId"].AsInt64, submission["uploaderId"].AsInt64,
                PaymentSubmissionBson.Reference(submission["evidence"].AsBsonDocument), submission["snapshotHash"].AsString,
                new(GrantScopeKind.FulfillmentShop, party["fulfillmentOrganizationId"].AsInt64, ShopId: party["shopId"].AsInt64, ChannelId: party["channelId"].AsInt64));
        } catch {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { if (tx.IsInTransaction) await tx.AbortTransactionAsync(cleanup.Token); if (tx.IsInTransaction) tx.Dispose(); } catch { try { tx.Dispose(); } catch { } }
            throw;
        }
    }
}
